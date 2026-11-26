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
        /// <summary>Maximum 3D &amp; geodesic influence radius in meters (default 0.08m = 8cm) so cloth never bleeds into face/body.</summary>
        public float InfluenceRadius { get; set; } = 0.08f;
        /// <summary>Optional imported submesh name filter; empty = Auto (isolates nearest connected mesh island).</summary>
        public string TargetMeshName { get; set; } = "";
        /// <summary>Ordered joint positions in model space (root -> tip).</summary>
        public List<Vector3> JointPositions { get; set; } = new();

        public ReferenceClothChainDef Clone() => new()
        {
            ChainId = ChainId,
            Name = Name,
            ParentBoneName = ParentBoneName,
            MirrorPairId = MirrorPairId,
            InfluenceRadius = InfluenceRadius,
            TargetMeshName = TargetMeshName ?? "",
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
        public int SpineCount { get; set; } = 3; // 1..4 (3 = spine_1..3, omitting pelvis-adjacent spine_0 by default)
        public bool HasNeck { get; set; } = true;
        public bool HasClavicles { get; set; } = true;
        public bool HasToes { get; set; } = false;

        // Hands & Fingers configuration
        public ReferenceFingerRigMode FingerMode { get; set; } = ReferenceFingerRigMode.ArticulatedFingers;
        public bool EnableThumb { get; set; } = true;
        public bool EnableIndex { get; set; } = true;
        public bool EnableMiddle { get; set; } = true;
        public bool EnableRing { get; set; } = true;
        public bool EnablePinky { get; set; } = true;
        public int PhalanxCount { get; set; } = 3; // 2 or 3 (3j matches Deadlock's native 3-bone finger chains 1:1)

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
            2 => new[] { "spine_1", "spine_3" },
            3 => new[] { "spine_1", "spine_2", "spine_3" },
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
            // Migrate legacy "Head" key to "Chin" if needed
            if (!Joints.ContainsKey("Chin") && Joints.TryGetValue("Head", out var legacyHead))
            {
                if (Joints.TryGetValue("Neck", out var nk))
                    Joints["Chin"] = nk.Lerp(legacyHead, 0.55f);
                else
                    Joints["Chin"] = legacyHead;
            }

            float H = MathF.Max(0.2f, approxHeight);
            if ((Joints.TryGetValue("Chin", out var hp) || Joints.TryGetValue("Head", out hp)) &&
                Joints.TryGetValue("AnkleL", out var al))
            {
                H = MathF.Max(0.2f, MathF.Abs(hp.Y - al.Y) / 0.84f);
            }

            // Optional HandTipL/R (Auto-Rig Pro–style distal middle-finger tip marker)
            foreach (string side in new[] { "L", "R" })
            {
                string tipKey = "HandTip" + side;
                if (!Joints.ContainsKey(tipKey) &&
                    Joints.TryGetValue("Wrist" + side, out var wr) &&
                    Joints.TryGetValue("Elbow" + side, out var el))
                {
                    Vector3 fwd = wr - el;
                    float armLen = fwd.Length();
                    fwd = armLen > 1e-4f ? fwd / armLen : (side == "L" ? Vector3.Right : Vector3.Left);
                    float handLen = MathF.Max(0.075f * H, armLen * 0.48f);
                    Joints[tipKey] = wr + fwd * handLen;
                }
            }

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

                Vector3 handFwd;
                float handLen;
                if (Joints.TryGetValue("HandTip" + side, out var handTip) && handTip.DistanceTo(wrist) > 0.02f * H)
                {
                    Vector3 delta = handTip - wrist;
                    handLen = delta.Length();
                    handFwd = delta / handLen;
                }
                else
                {
                    Vector3 arm = wrist - elbow;
                    float armLen = arm.Length();
                    handFwd = armLen > 1e-4f ? arm / armLen : (side == "L" ? Vector3.Right : Vector3.Left);
                    handLen = MathF.Max(0.075f * H, armLen * 0.48f);
                }

                // Orthonormal palm frame: forward along hand, up = world Y projected, across = pinky-to-thumb
                Vector3 upRef = MathF.Abs(handFwd.Y) < 0.9f ? Vector3.Up : Vector3.Forward;
                Vector3 palmRight = handFwd.Cross(upRef);
                if (palmRight.LengthSquared() < 1e-6f) palmRight = Vector3.Forward;
                else palmRight = palmRight.Normalized();
                // Ensure palmAcross points from pinky toward thumb (+Z front of hand in T/A-pose)
                if (palmRight.Z < 0f) palmRight = -palmRight;
                Vector3 palmUp = palmRight.Cross(handFwd).Normalized();

                float palmWidth = handLen * 0.46f;

                // Offsets for thumb, index, middle, ring, pinky: (fwdFrac, acrossFrac, upFrac, lengthFrac)
                (string name, float fwd, float across, float up, float len)[] layout =
                {
                    ("thumb",  0.22f,  0.48f, -0.08f, 0.38f),
                    ("index",  0.50f,  0.34f,  0.02f, 0.46f),
                    ("middle", 0.52f,  0.10f,  0.03f, 0.50f),
                    ("ring",   0.50f, -0.14f,  0.02f, 0.46f),
                    ("pinky",  0.46f, -0.36f,  0.00f, 0.38f)
                };

                foreach (var (fName, fwd, across, up, len) in layout)
                {
                    if (!IsFingerEnabled(fName)) continue;
                    Vector3 fBase = wrist + handFwd * (handLen * fwd) + palmRight * (palmWidth * across) + palmUp * (palmWidth * up);
                    Vector3 fDir = fName == "thumb"
                        ? (handFwd * 0.68f + palmRight * 0.70f - palmUp * 0.16f).Normalized()
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
        /// Auto-Rig Pro–style mesh-aware finger snapping: uses explicit <c>HandTipL</c>/<c>HandTipR</c>
        /// (when available) or distal extremity to define the exact palm axis &amp; length, isolates the
        /// angled thumb ray (capped at f &lt;= 0.70 so it never jumps to the index fingertip), and projects
        /// every phalanx cleanly inside the real 3D finger geometry.
        /// </summary>
        public void SnapFingersToMesh(Node3D scene, IEnumerable<MeshInstance3D> meshes, bool forceRegenerate = true)
        {
            float H = 1.7f;
            if ((Joints.TryGetValue("Chin", out var hp) || Joints.TryGetValue("Head", out hp)) &&
                Joints.TryGetValue("AnkleL", out var al))
            {
                H = MathF.Max(0.2f, MathF.Abs(hp.Y - al.Y) / 0.84f);
            }

            // Start with clean mathematical defaults as fallback
            EnsureFingerJoints(H, forceRegenerate: forceRegenerate);
            if (scene == null || meshes == null) return;

            var allPts = new List<Vector3>();
            var fallbackSkel = CustomModelAutoRigger.FindSkeleton(scene);
            foreach (var mi in meshes)
            {
                if (mi?.Mesh == null) continue;
                for (int s = 0; s < mi.Mesh.GetSurfaceCount(); s++)
                {
                    var arrays = mi.Mesh.SurfaceGetArrays(s);
                    if (arrays == null || arrays.Count == 0) continue;
                    var verts = CustomModelAutoRigger.GetVector3Array(arrays[(int)Mesh.ArrayType.Vertex]);
                    if (verts == null || verts.Length == 0) continue;
                    var rawBones = arrays.Count > (int)Mesh.ArrayType.Bones ? CustomModelAutoRigger.GetInt32Array(arrays[(int)Mesh.ArrayType.Bones]) : null;
                    var rawWeights = arrays.Count > (int)Mesh.ArrayType.Weights ? CustomModelAutoRigger.GetFloat32Array(arrays[(int)Mesh.ArrayType.Weights]) : null;
                    CustomModelAutoRigger.EvaluateMeshSurfaceInTargetSpace(
                        mi, scene, Transform3D.Identity, fallbackSkel,
                        verts, null, rawBones, rawWeights,
                        out var sceneVerts, out _);
                    allPts.AddRange(sceneVerts);
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

                bool hasExplicitTip = Joints.TryGetValue("HandTip" + side, out var explicitTip) &&
                                      explicitTip.DistanceTo(wrist) > 0.025f * H;
                Vector3 handFwd = hasExplicitTip
                    ? (explicitTip - wrist).Normalized()
                    : (forearm / armLen);
                float explicitHandLen = hasExplicitTip ? explicitTip.DistanceTo(wrist) : 0f;

                // Gather vertices in the hand bounding volume distal to the wrist (tight radius + hip/pelvis exclusion)
                float maxHandReach = hasExplicitTip
                    ? explicitHandLen * 1.15f
                    : MathF.Min(0.16f * H, MathF.Max(0.08f * H, armLen * 0.75f));
                float maxLatRadius = hasExplicitTip
                    ? MathF.Min(0.068f * H, explicitHandLen * 0.62f)
                    : MathF.Min(0.065f * H, MathF.Max(0.035f * H, armLen * 0.42f));
                bool hasHip = Joints.TryGetValue("Hip" + side, out var hipPos);
                bool hasPelvis = Joints.TryGetValue("Pelvis", out var pelPos);
                var handPts = new List<Vector3>();
                foreach (var p in allPts)
                {
                    Vector3 rel = p - wrist;
                    float f = rel.Dot(handFwd);
                    if (f < -0.005f * H || f > maxHandReach) continue;
                    Vector3 perp = rel - handFwd * f;
                    if (perp.LengthSquared() > maxLatRadius * maxLatRadius) continue;
                    Vector3 handMid = wrist + handFwd * (hasExplicitTip ? explicitHandLen * 0.45f : 0.045f * H);
                    float dHandSq = p.DistanceSquaredTo(handMid);
                    if (hasHip && p.DistanceSquaredTo(hipPos) < dHandSq * 0.85f) continue;
                    if (hasPelvis && p.DistanceSquaredTo(pelPos) < dHandSq * 0.85f) continue;
                    handPts.Add(p);
                }
                if (handPts.Count < 12) continue;

                float handLen;
                if (hasExplicitTip)
                {
                    handLen = explicitHandLen;
                }
                else
                {
                    // Refine handFwd using the actual distal hand-tip cluster (top 15% farthest along initial handFwd)
                    handPts.Sort((a, b) => (a - wrist).Dot(handFwd).CompareTo((b - wrist).Dot(handFwd)));
                    int tipStart = Math.Max(0, (int)(handPts.Count * 0.85f));
                    Vector3 tipCentroid = Vector3.Zero;
                    for (int i = tipStart; i < handPts.Count; i++) tipCentroid += handPts[i];
                    tipCentroid /= (handPts.Count - tipStart);
                    Vector3 refinedFwd = tipCentroid - wrist;
                    handLen = refinedFwd.Length();
                    if (handLen < 0.02f * H) continue;
                    handFwd = refinedFwd / handLen;
                    Joints["HandTip" + side] = tipCentroid;
                }

                // Build palm orthonormal frame aligned to actual hand geometry using 2D PCA perpendicular to handFwd
                // so rotated/pronated palms and -Z/+Z-facing models always get the true palm width & thickness axes.
                Vector3 upRef = MathF.Abs(handFwd.Y) < 0.9f ? Vector3.Up : Vector3.Forward;
                Vector3 basisE1 = handFwd.Cross(upRef);
                basisE1 = basisE1.LengthSquared() > 1e-6f ? basisE1.Normalized() : Vector3.Forward;
                Vector3 basisE2 = handFwd.Cross(basisE1).Normalized();

                Vector3 palmAcross = basisE1;
                {
                    // Collect palm-band radial offsets (f in 0.18..0.72) to extract palm width eigenvector via 2x2 PCA
                    float mx = 0f, my = 0f;
                    int pCnt = 0;
                    foreach (var p in handPts)
                    {
                        Vector3 rel = p - wrist;
                        float f = rel.Dot(handFwd) / handLen;
                        if (f < 0.18f || f > 0.72f) continue;
                        mx += rel.Dot(basisE1);
                        my += rel.Dot(basisE2);
                        pCnt++;
                    }
                    if (pCnt >= 10)
                    {
                        mx /= pCnt;
                        my /= pCnt;
                        float cxx = 0f, cxy = 0f, cyy = 0f;
                        foreach (var p in handPts)
                        {
                            Vector3 rel = p - wrist;
                            float f = rel.Dot(handFwd) / handLen;
                            if (f < 0.18f || f > 0.72f) continue;
                            float x = rel.Dot(basisE1) - mx;
                            float y = rel.Dot(basisE2) - my;
                            cxx += x * x;
                            cxy += x * y;
                            cyy += y * y;
                        }
                        float tr = cxx + cyy;
                        float diff = cxx - cyy;
                        float disc = MathF.Sqrt(diff * diff + 4f * cxy * cxy);
                        float lMax = 0.5f * (tr + disc);
                        float lMin = 0.5f * (tr - disc);
                        if (lMax > 1e-6f && lMax > lMin * 1.25f)
                        {
                            float vx = MathF.Abs(cxy) > 1e-7f ? (lMax - cyy) : (cxx >= cyy ? 1f : 0f);
                            float vy = MathF.Abs(cxy) > 1e-7f ? cxy : (cxx >= cyy ? 0f : 1f);
                            Vector3 pcaAxis = basisE1 * vx + basisE2 * vy;
                            if (pcaAxis.LengthSquared() > 1e-6f)
                                palmAcross = pcaAxis.Normalized();
                        }
                    }

                    // Resolve +palmAcross sign toward the thumb side:
                    // Compare lateral protrusion in the thumb zone (f in 0.22..0.64) beyond the distal 4-finger span (f in 0.74..0.98).
                    float midMinA = float.MaxValue, midMaxA = float.MinValue;
                    float distMinA = float.MaxValue, distMaxA = float.MinValue;
                    int midCnt = 0, distCnt = 0;
                    foreach (var p in handPts)
                    {
                        Vector3 rel = p - wrist;
                        float f = rel.Dot(handFwd) / handLen;
                        float a = rel.Dot(palmAcross);
                        if (f >= 0.22f && f <= 0.64f) { if (a < midMinA) midMinA = a; if (a > midMaxA) midMaxA = a; midCnt++; }
                        else if (f >= 0.74f && f <= 0.98f) { if (a < distMinA) distMinA = a; if (a > distMaxA) distMaxA = a; distCnt++; }
                    }
                    bool resolvedSign = false;
                    if (midCnt >= 6 && distCnt >= 6)
                    {
                        float posBulge = midMaxA - distMaxA;
                        float negBulge = distMinA - midMinA;
                        float span = MathF.Max(1e-4f, midMaxA - midMinA);
                        if (MathF.Abs(posBulge - negBulge) > 0.06f * span)
                        {
                            if (negBulge > posBulge) palmAcross = -palmAcross;
                            resolvedSign = true;
                        }
                    }
                    if (!resolvedSign && palmAcross.Z < 0f)
                        palmAcross = -palmAcross;
                }
                Vector3 palmUp = palmAcross.Cross(handFwd).Normalized();

                // Measure actual lateral (across) span of the 4-finger + palm band (f in 0.26..0.94)
                float minA = float.MaxValue, maxA = float.MinValue;
                float minUp = float.MaxValue, maxUp = float.MinValue;
                int cntBand = 0;
                foreach (var p in handPts)
                {
                    Vector3 rel = p - wrist;
                    float f = rel.Dot(handFwd) / handLen;
                    if (f < 0.26f || f > 0.94f) continue;
                    float a = rel.Dot(palmAcross);
                    float u = rel.Dot(palmUp);
                    if (a < minA) minA = a;
                    if (a > maxA) maxA = a;
                    if (u < minUp) minUp = u;
                    if (u > maxUp) maxUp = u;
                    cntBand++;
                }
                if (cntBand < 6 || maxA - minA < 0.01f * H) continue;
                float palmWidth = maxA - minA;
                float avgUp = 0.5f * (minUp + maxUp);

                // Helper: compute geometric bounding-box center of a local point list along (handFwd, palmAcross, palmUp)
                // so dense glove cuffs or fingernail topology never pull joints off a finger's true centerline.
                Vector3 BboxCenter(List<Vector3> pts, Vector3 fallback)
                {
                    if (pts.Count == 0) return fallback;
                    float f0 = float.MaxValue, f1 = float.MinValue;
                    float a0 = float.MaxValue, a1 = float.MinValue;
                    float u0 = float.MaxValue, u1 = float.MinValue;
                    for (int i = 0; i < pts.Count; i++)
                    {
                        Vector3 rel = pts[i] - wrist;
                        float fv = rel.Dot(handFwd);
                        float av = rel.Dot(palmAcross);
                        float uv = rel.Dot(palmUp);
                        if (fv < f0) f0 = fv; if (fv > f1) f1 = fv;
                        if (av < a0) a0 = av; if (av > a1) a1 = av;
                        if (uv < u0) u0 = uv; if (uv > u1) u1 = uv;
                    }
                    return wrist + handFwd * (0.5f * (f0 + f1)) + palmAcross * (0.5f * (a0 + a1)) + palmUp * (0.5f * (u0 + u1));
                }

                // Measure distal 4-finger lateral span strictly PAST the thumb tip (f in 0.72..0.98)
                float min4A = float.MaxValue, max4A = float.MinValue;
                int cnt4 = 0;
                foreach (var p in handPts)
                {
                    Vector3 rel = p - wrist;
                    float f = rel.Dot(handFwd) / handLen;
                    if (f < 0.72f || f > 0.98f) continue;
                    float a = rel.Dot(palmAcross);
                    if (a < min4A) min4A = a;
                    if (a > max4A) max4A = a;
                    cnt4++;
                }
                if (cnt4 < 6 || max4A - min4A < 0.008f * H)
                {
                    min4A = minA;
                    max4A = minA + 0.74f * palmWidth;
                }
                float width4 = max4A - min4A;

                // 1. Dedicated angled-ray thumb solver (isolated to f <= 0.70 & beyond the 4-finger index edge)
                if (IsFingerEnabled("thumb"))
                {
                    var thumbPts = new List<(float F, float A, Vector3 P)>();
                    float thumbMinA = MathF.Max(minA + 0.68f * palmWidth, max4A - 0.10f * width4);
                    foreach (var p in handPts)
                    {
                        Vector3 rel = p - wrist;
                        float f = rel.Dot(handFwd) / handLen;
                        float a = rel.Dot(palmAcross);
                        if (f >= 0.14f && f <= 0.70f && a >= thumbMinA)
                            thumbPts.Add((f, a, p));
                    }

                    Vector3 thumbBase = wrist + handFwd * (0.20f * handLen) + palmAcross * (minA + 0.74f * palmWidth) + palmUp * avgUp;
                    Vector3 thumbTip = wrist + handFwd * (0.58f * handLen) + palmAcross * (minA + 0.96f * palmWidth) + palmUp * avgUp;

                    if (thumbPts.Count >= 6)
                    {
                        thumbPts.Sort((x, y) =>
                        {
                            float sx = ((x.A - minA) / palmWidth) * 0.65f + x.F * 0.35f;
                            float sy = ((y.A - minA) / palmWidth) * 0.65f + y.F * 0.35f;
                            return sx.CompareTo(sy);
                        });
                        int tStart = Math.Max(0, (int)(thumbPts.Count * 0.82f));
                        var tipCluster = new List<Vector3>(thumbPts.Count - tStart);
                        for (int i = tStart; i < thumbPts.Count; i++) tipCluster.Add(thumbPts[i].P);
                        thumbTip = BboxCenter(tipCluster, thumbTip);

                        var baseCluster = new List<Vector3>();
                        foreach (var tp in thumbPts)
                        {
                            if (tp.F >= 0.15f && tp.F <= 0.34f) baseCluster.Add(tp.P);
                        }
                        if (baseCluster.Count >= 2) thumbBase = BboxCenter(baseCluster, thumbBase);
                    }

                    Vector3 tDelta = thumbTip - thumbBase;
                    float tLen = tDelta.Length();
                    if (tLen < 0.18f * handLen)
                        thumbTip = thumbBase + (handFwd * 0.65f + palmAcross * 0.75f).Normalized() * (0.36f * handLen);
                    else if (tLen > 0.54f * handLen)
                        thumbTip = thumbBase + (tDelta / tLen) * (0.54f * handLen);

                    float[] thumbFracs = phalanges == 2
                        ? new[] { 0.0f, 0.50f, 1.0f }
                        : new[] { 0.0f, 0.42f, 0.74f, 1.0f };
                    var localSlice = new List<Vector3>();
                    for (int j = 0; j <= phalanges; j++)
                    {
                        string key = $"finger_thumb_{j}_{side}";
                        if (!forceRegenerate && Joints.ContainsKey(key)) continue;
                        Vector3 target = thumbBase.Lerp(thumbTip, thumbFracs[j]);
                        float snapRad = MathF.Max(0.008f * H, handLen * 0.11f);
                        localSlice.Clear();
                        foreach (var tp in thumbPts)
                        {
                            if (tp.P.DistanceSquaredTo(target) <= snapRad * snapRad) localSlice.Add(tp.P);
                        }
                        Joints[key] = localSlice.Count >= 2 ? target.Lerp(BboxCenter(localSlice, target), 0.75f) : target;
                    }
                }

                // 2. Four main fingers (index, middle, ring, pinky):
                // Detect knuckle-band span (excluding thumb) + fan-aware 1D valley/peak centers so splayed/fanned hands
                // follow each finger's true angled axis instead of slicing diagonally with rigid vertical strips.
                float knuckleMinA = min4A, knuckleMaxA = max4A;
                {
                    float kMin = float.MaxValue, kMax = float.MinValue;
                    int kCnt = 0;
                    float kClipMax = max4A + 0.05f * width4;
                    foreach (var p in handPts)
                    {
                        Vector3 rel = p - wrist;
                        float f = rel.Dot(handFwd) / handLen;
                        if (f < 0.38f || f > 0.54f) continue;
                        float a = rel.Dot(palmAcross);
                        if (a > kClipMax) continue;
                        if (a < kMin) kMin = a;
                        if (a > kMax) kMax = a;
                        kCnt++;
                    }
                    if (kCnt >= 6 && (kMax - kMin) >= 0.45f * width4 && (kMax - kMin) <= 1.15f * width4)
                    {
                        knuckleMinA = kMin;
                        knuckleMaxA = kMax;
                    }
                }
                float knuckleWidth4 = knuckleMaxA - knuckleMinA;

                // 1D lateral density histogram across distal fingers (f in 0.64..0.95) to find finger peaks/gutters
                const int HistBins = 32;
                var hist = new int[HistBins];
                foreach (var p in handPts)
                {
                    Vector3 rel = p - wrist;
                    float f = rel.Dot(handFwd) / handLen;
                    if (f < 0.64f || f > 0.95f) continue;
                    float an = (rel.Dot(palmAcross) - min4A) / MathF.Max(1e-5f, width4);
                    int bin = Math.Clamp((int)(an * HistBins), 0, HistBins - 1);
                    hist[bin]++;
                }
                // Refine each finger's distal lateral center toward its nearest local peak within its anatomical window
                (string name, float a0, float a1, float kFrac, float fStart, float fFallbackEnd, float fCap)[] bands =
                {
                    ("index",  0.75f, 1.02f, 0.86f, 0.47f, 0.93f, 0.99f),
                    ("middle", 0.49f, 0.75f, 0.61f, 0.49f, 1.00f, 1.04f),
                    ("ring",   0.23f, 0.49f, 0.36f, 0.47f, 0.91f, 0.97f),
                    ("pinky", -0.02f, 0.23f, 0.12f, 0.43f, 0.78f, 0.86f)
                };

                float[] phalanxFracs = phalanges == 2
                    ? new[] { 0.0f, 0.52f, 1.0f }
                    : new[] { 0.0f, 0.46f, 0.76f, 1.0f };

                var slicePts = new List<Vector3>();
                foreach (var (fName, a0, a1, kFrac, fStart, fFallbackEnd, fCap) in bands)
                {
                    if (!IsFingerEnabled(fName)) continue;
                    float loA = min4A + a0 * width4;
                    float hiA = min4A + a1 * width4;
                    float defaultTipA = 0.5f * (loA + hiA);

                    // Find peak bin inside this finger's distal window
                    int b0 = Math.Clamp((int)(MathF.Max(0f, a0) * HistBins), 0, HistBins - 1);
                    int b1 = Math.Clamp((int)(MathF.Min(1f, a1) * HistBins), b0, HistBins - 1);
                    int bestBin = -1, bestCnt = 0;
                    for (int bi = b0; bi <= b1; bi++)
                    {
                        if (hist[bi] > bestCnt) { bestCnt = hist[bi]; bestBin = bi; }
                    }
                    float tipA = bestBin >= 0 && bestCnt >= 2
                        ? min4A + ((bestBin + 0.5f) / HistBins) * width4
                        : defaultTipA;
                    float knuckleA = knuckleMinA + kFrac * knuckleWidth4;
                    float halfTubeA = MathF.Max(0.006f * H, 0.115f * width4);

                    // Gather vertices along the fanned finger corridor from (fStart, knuckleA) -> (fCap, tipA)
                    var slice = new List<(float F, float A, Vector3 P)>();
                    foreach (var p in handPts)
                    {
                        Vector3 rel = p - wrist;
                        float f = rel.Dot(handFwd) / handLen;
                        if (f < fStart * 0.84f || f > fCap) continue;
                        float tRay = Mathf.Clamp((f - fStart) / MathF.Max(0.15f, fFallbackEnd - fStart), 0f, 1.1f);
                        float expectedA = Mathf.Lerp(knuckleA, tipA, tRay);
                        float a = rel.Dot(palmAcross);
                        if (MathF.Abs(a - expectedA) <= halfTubeA)
                            slice.Add((f, a, p));
                    }

                    float fMax = fFallbackEnd;
                    if (slice.Count >= 4)
                    {
                        slice.Sort((x, y) => x.F.CompareTo(y.F));
                        fMax = Math.Clamp(slice[(int)((slice.Count - 1) * 0.95f)].F, fStart + 0.16f, fCap);
                    }

                    Vector3 prevPos = Vector3.Zero;
                    float prevA = knuckleA;
                    for (int j = 0; j <= phalanges; j++)
                    {
                        string key = $"finger_{fName}_{j}_{side}";
                        if (!forceRegenerate && Joints.ContainsKey(key))
                        {
                            prevPos = Joints[key];
                            prevA = (prevPos - wrist).Dot(palmAcross);
                            continue;
                        }

                        float frac = phalanxFracs[j];
                        float targetF = Mathf.Lerp(fStart, fMax, frac);
                        float targetA = Mathf.Lerp(knuckleA, tipA, frac);
                        float bandHalf = (fMax - fStart) / (phalanges * 1.65f);

                        slicePts.Clear();
                        foreach (var item in slice)
                        {
                            if (MathF.Abs(item.F - targetF) <= bandHalf)
                                slicePts.Add(item.P);
                        }

                        Vector3 fallback = wrist + handFwd * (targetF * handLen) + palmAcross * targetA + palmUp * avgUp;
                        Vector3 chosen = slicePts.Count >= 2 ? BboxCenter(slicePts, fallback) : fallback;

                        // Lateral continuity guard along the fanned ray so a distal joint never drifts onto a neighbor finger
                        if (j > 0)
                        {
                            Vector3 relC = chosen - wrist;
                            float curA = relC.Dot(palmAcross);
                            float maxDev = 0.08f * width4;
                            float clampedA = Math.Clamp(curA, targetA - maxDev, targetA + maxDev);
                            if (MathF.Abs(clampedA - curA) > 1e-5f)
                                chosen += palmAcross * (clampedA - curA);
                        }

                        // Monotonic progression guard along handFwd
                        if (j > 0 && (chosen - prevPos).Dot(handFwd) < 0.02f * handLen)
                        {
                            chosen = prevPos + handFwd * ((fMax - fStart) * handLen / phalanges);
                        }
                        Joints[key] = chosen;
                        prevPos = chosen;
                        prevA = (chosen - wrist).Dot(palmAcross);
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
            float totalLength,
            float influenceRadius = 0.08f,
            string targetMeshName = "")
        {
            int segs = Math.Clamp(segmentCount, 1, 6);
            Vector3 dir = directionModelSpace.LengthSquared() > 1e-6f ? directionModelSpace.Normalized() : Vector3.Down;
            float len = MathF.Max(0.12f, totalLength);
            var chain = new ReferenceClothChainDef
            {
                ChainId = $"refcloth_{Guid.NewGuid().ToString("N")[..8]}",
                Name = string.IsNullOrWhiteSpace(name) ? "cloth_chain" : name.Trim(),
                ParentBoneName = string.IsNullOrWhiteSpace(parentBoneName) ? "spine_3" : parentBoneName,
                InfluenceRadius = Math.Clamp(influenceRadius, 0.02f, 0.35f),
                TargetMeshName = targetMeshName ?? "",
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
                InfluenceRadius = src.InfluenceRadius,
                TargetMeshName = src.TargetMeshName ?? "",
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
            public string ClothTargetMeshName = "";
        }

        public readonly struct PartialBodyRigMask
        {
            public bool IncludePelvisAnchor { get; init; }
            public bool IncludeSpine { get; init; }
            public bool IncludeCollarSpineAnchor { get; init; }
            public bool IncludeClavicles { get; init; }
            public bool IncludeArms { get; init; }
            public bool IncludeHands { get; init; }
            public bool IncludeLegs { get; init; }
            public bool LowerLegsOnly { get; init; }
            public bool IncludeFeet { get; init; }
            public bool IncludeNeck { get; init; }
            public bool IncludeHead { get; init; }
        }

        /// <summary>
        /// Builds the complete modular bone hierarchy from this <see cref="ReferenceRigDef"/> in
        /// transformed space (pass <see cref="Transform3D.Identity"/> for model-space preview or
        /// <c>options.AlignedTransform</c> for hero-space weight solving).
        /// When <paramref name="partialMask"/> is supplied, emits strictly the bones belonging to the
        /// selected clothing/accessory body region so unrelated limbs never steal geodesic/voxel heat weights.
        /// </summary>
        public List<ResolvedBoneSpec> BuildResolvedBones(
            Transform3D xf,
            float H,
            float footFwdZ,
            bool swapLeftRight = false,
            PartialBodyRigMask? partialMask = null)
        {
            bool isPartial = partialMask.HasValue;
            bool incPelvis = !isPartial || partialMask.Value.IncludePelvisAnchor;
            bool incSpine = !isPartial || partialMask.Value.IncludeSpine;
            bool incCollarSpine = isPartial && partialMask.Value.IncludeCollarSpineAnchor && !incSpine;
            bool incClavicles = (!isPartial || partialMask.Value.IncludeClavicles) && HasClavicles;
            bool incArms = !isPartial || partialMask.Value.IncludeArms || partialMask.Value.IncludeHands;
            bool incHands = !isPartial || partialMask.Value.IncludeHands;
            bool incLegs = !isPartial || partialMask.Value.IncludeLegs;
            bool lowerLegsOnly = isPartial && partialMask.Value.LowerLegsOnly;
            bool incFeet = !isPartial || partialMask.Value.IncludeFeet;
            bool incHead = !isPartial || partialMask.Value.IncludeHead;
            bool incNeck = !isPartial || partialMask.Value.IncludeNeck || incHead;

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
            // Auto-Rig Pro–style Chin pin (with automatic fallback to legacy Head pin):
            // places the head bone pivot cleanly at the skull base at chin height so the chin/jaw moves solidly with head!
            Vector3 chinPin = Joints.ContainsKey("Chin")
                ? GetJ("Chin", neck + Vector3.Up * 0.045f * H)
                : neck.Lerp(GetJ("Head", neck + Vector3.Up * 0.10f * H), 0.55f);
            Vector3 hipL = GetJ("HipL", pelvis + new Vector3(0.08f * H, -0.04f * H, 0f));
            Vector3 hipR = GetJ("HipR", pelvis + new Vector3(-0.08f * H, -0.04f * H, 0f));
            Vector3 midHip = hipL.Lerp(hipR, 0.5f);

            // 1. Pelvis + Modular Spine (1..4 bones) with clean pelvis/glute seeding (allowBelow=true avoids glute gap)
            string[] spineNames = GetDeadlockSpineBoneNames(SpineCount);
            Vector3 firstSpineOrigin = spineNames.Length == 1
                ? pelvis.Lerp(neck, 0.48f)
                : pelvis.Lerp(neck, 0.30f);

            string prevTorso = null;
            if (incPelvis)
            {
                Vector3 pelStart = incLegs ? (midHip - Vector3.Up * (0.025f * H)) : pelvis;
                Vector3 pelEnd = incSpine ? firstSpineOrigin : (pelvis + Vector3.Up * (0.08f * H));
                AddBone("pelvis", null, ReferenceBoneRole.Core, pelvis, pelStart, pelEnd, 0.088f * H,
                    allowBelow: true, allowBeyond: !incSpine, headKey: "Pelvis");
                prevTorso = "pelvis";
            }

            if (incSpine)
            {
                for (int si = 0; si < spineNames.Length; si++)
                {
                    float t0 = spineNames.Length == 1
                        ? 0.48f
                        : Mathf.Lerp(0.30f, 0.80f, si / (float)(spineNames.Length - 1));
                    float t1 = si == spineNames.Length - 1
                        ? ((HasNeck && incHead) ? 0.90f : 1.0f)
                        : Mathf.Lerp(0.30f, 0.80f, (si + 1) / (float)(spineNames.Length - 1));
                    Vector3 spOrig = pelvis.Lerp(neck, t0);
                    Vector3 spEnd = pelvis.Lerp(neck, t1);
                    AddBone(spineNames[si], prevTorso, ReferenceBoneRole.Core, spOrig, spOrig, spEnd, 0.098f * H,
                        allowBelow: !incPelvis && si == 0,
                        allowBeyond: !incHead && si == spineNames.Length - 1,
                        headKey: si == spineNames.Length - 1 ? "Chest" : "",
                        tailKey: (!incHead && si == spineNames.Length - 1) ? "Neck" : "");
                    prevTorso = spineNames[si];
                }
            }
            else if (incCollarSpine)
            {
                AddBone("spine_3", prevTorso, ReferenceBoneRole.Core, chest, chest, neck, 0.088f * H,
                    allowBelow: true, headKey: "Chest", tailKey: "Neck");
                prevTorso = "spine_3";
            }
            string upperTorso = prevTorso;

            // 2. Optional Neck + Head (derived from Chin landmark)
            if (incNeck || incHead)
            {
                string headParent = upperTorso;
                float chinY = MathF.Max(neck.Y + 0.018f * H, chinPin.Y);
                float skullPivotY = MathF.Max(neck.Y + 0.032f * H, chinY + 0.022f * H);
                Vector3 headOrig = (HasNeck || (incNeck && !incHead))
                    ? new Vector3(neck.X, skullPivotY, Mathf.Lerp(neck.Z, chinPin.Z, 0.25f))
                    : neck;
                if (HasNeck || (incNeck && !incHead))
                {
                    AddBone("neck_0", upperTorso, ReferenceBoneRole.Core, neck, neck, headOrig, 0.058f * H,
                        allowBeyond: !incHead, headKey: "Neck");
                    headParent = "neck_0";
                }
                if (incHead)
                {
                    AddBone("head", headParent, ReferenceBoneRole.Core, headOrig, headOrig, headOrig + Vector3.Up * (0.14f * H), 0.075f * H, allowBeyond: true, headKey: "Chin");
                }
            }

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
                if (incClavicles && (incSpine || incArms))
                {
                    Vector3 clavOrig = neck + (sh - neck) * 0.22f;
                    AddBone("clavicle_" + s, upperTorso, ReferenceBoneRole.Core, clavOrig, clavOrig, sh, 0.035f * H,
                        tailKey: !incArms ? ("Shoulder" + s) : "");
                    armParent = "clavicle_" + s;
                }

                if (incArms)
                {
                    AddBone("arm_upper_" + s, armParent, ReferenceBoneRole.Core, sh, sh, el, 0.035f * H, headKey: "Shoulder" + s, tailKey: "Elbow" + s);
                    AddBone("arm_lower_" + s, "arm_upper_" + s, ReferenceBoneRole.Core, el, el, wr, 0.030f * H,
                        allowBeyond: !incHands, headKey: "Elbow" + s, tailKey: "Wrist" + s);

                    Vector3 handDir = (wr - el).LengthSquared() > 1e-6f ? (wr - el).Normalized() : -Vector3.Up;
                    bool hasFingers = incHands && FingerMode == ReferenceFingerRigMode.ArticulatedFingers;
                    Vector3 handEnd = incHands
                        ? wr + handDir * (hasFingers ? 0.042f * H : 0.11f * H)
                        : wr + handDir * (0.022f * H); // Compact wrist cuff anchor so ArmSegmentRegistration knows exact Cs=Wrist without leaking hand weights down sleeves!
                    if (hasFingers)
                    {
                        Vector3 knuckleSum = Vector3.Zero;
                        int knuckleCnt = 0;
                        foreach (string fn in new[] { "index", "middle", "ring", "pinky" })
                        {
                            string kKey = $"finger_{fn}_0_{s}";
                            if (Joints.ContainsKey(swapLeftRight ? (MirrorJointKey(kKey) ?? kKey) : kKey))
                            {
                                knuckleSum += GetJ(kKey, handEnd);
                                knuckleCnt++;
                            }
                        }
                        if (knuckleCnt > 0)
                            handEnd = wr.Lerp(knuckleSum / knuckleCnt, 0.52f);
                    }
                    float handRad = !incHands ? 0.022f * H : (hasFingers ? 0.022f * H : 0.042f * H);
                    AddBone("hand_" + s, "arm_lower_" + s, ReferenceBoneRole.Core, wr, wr, handEnd, handRad,
                        allowBelow: incHands, allowBeyond: incHands && !hasFingers, headKey: "Wrist" + s);

                    // Individual Articulated Fingers (matching DeadlockRigs finger_{name}_meta_{L|R} + finger_{name}_{0..2}_{L|R})
                    if (hasFingers)
                    {
                        foreach (string fName in FingerNames)
                        {
                            if (!IsFingerEnabled(fName)) continue;
                            string prevF = "hand_" + s;

                            string firstHk = $"finger_{fName}_0_{s}";
                            if (fName != "thumb" && Joints.ContainsKey(swapLeftRight ? (MirrorJointKey(firstHk) ?? firstHk) : firstHk))
                            {
                                Vector3 k0 = GetJ(firstHk, handEnd);
                                Vector3 metaOrig = wr.Lerp(k0, 0.35f);
                                string metaName = $"finger_{fName}_meta_{s}";
                                AddBone(metaName, prevF, ReferenceBoneRole.Finger, metaOrig, metaOrig, k0, MathF.Max(0.008f, 0.014f * H),
                                    allowBelow: false, allowBeyond: false, headKey: "Wrist" + s, tailKey: firstHk);
                                prevF = metaName;
                            }

                            for (int j = 0; j < phalanges; j++)
                            {
                                string hk = $"finger_{fName}_{j}_{s}";
                                string tk = $"finger_{fName}_{j + 1}_{s}";
                                if (!Joints.ContainsKey(swapLeftRight ? (MirrorJointKey(hk) ?? hk) : hk) ||
                                    !Joints.ContainsKey(swapLeftRight ? (MirrorJointKey(tk) ?? tk) : tk))
                                    break;
                                Vector3 jHead = GetJ(hk, handEnd);
                                Vector3 jTail = GetJ(tk, jHead + handDir * 0.02f * H);
                                float fRad = MathF.Max(0.005f, (fName == "thumb" ? 0.0095f : 0.0075f) * H);
                                string bName = $"finger_{fName}_{j}_{s}";
                                AddBone(bName, prevF, ReferenceBoneRole.Finger, jHead, jHead, jTail, fRad,
                                    allowBelow: j == 0, allowBeyond: j == phalanges - 1, headKey: hk, tailKey: tk);
                                prevF = bName;
                            }
                        }
                    }
                }

                // Legs & optional Feet / Toes
                if (incLegs)
                {
                    string lowerLegParent = null;
                    if (!lowerLegsOnly)
                    {
                        AddBone("leg_upper_" + s, incPelvis ? "pelvis" : null, ReferenceBoneRole.Core, hip, hip, kn, 0.058f * H,
                            allowBelow: !incPelvis, headKey: "Hip" + s, tailKey: "Knee" + s);
                        lowerLegParent = "leg_upper_" + s;
                    }
                    AddBone("leg_lower_" + s, lowerLegParent, ReferenceBoneRole.Core, kn, kn, ak, 0.040f * H,
                        allowBelow: lowerLegsOnly, allowBeyond: !incFeet, headKey: "Knee" + s, tailKey: "Ankle" + s);

                    bool emitToes = incFeet && HasToes;
                    Vector3 footEnd = incFeet
                        ? (emitToes
                            ? GetJ("Toe" + s, new Vector3(ak.X, ak.Y - 0.035f * H, ak.Z + footFwdZ * 0.10f * H))
                            : new Vector3(ak.X, ak.Y - 0.04f * H, ak.Z + footFwdZ * 0.145f * H))
                        : new Vector3(ak.X, ak.Y - 0.022f * H, ak.Z + footFwdZ * 0.025f * H); // Compact ankle cuff anchor for pants/shorts so LegSegmentRegistration knows exact ankle position without bleeding foot weights!
                    float ankleRad = incFeet ? 0.055f * H : 0.024f * H;
                    AddBone("ankle_" + s, "leg_lower_" + s, ReferenceBoneRole.Core, ak, ak, footEnd, ankleRad,
                        allowBelow: incFeet, allowBeyond: incFeet && !emitToes, headKey: "Ankle" + s, tailKey: emitToes ? "Toe" + s : "");

                    if (emitToes)
                    {
                        Vector3 toeFwd = (footEnd - ak);
                        toeFwd.Y = 0f;
                        toeFwd = toeFwd.LengthSquared() > 1e-6f ? toeFwd.Normalized() : new Vector3(0f, 0f, footFwdZ);
                        AddBone("toe_" + s, "ankle_" + s, ReferenceBoneRole.Core, footEnd, footEnd, footEnd + toeFwd * 0.055f * H, 0.035f * H,
                            allowBelow: true, allowBeyond: true, headKey: "Toe" + s);
                    }
                }
            }

            // Guarantee at least one fallback bone if a Custom partial mask has every region disabled
            if (list.Count == 0)
            {
                AddBone("spine_2", null, ReferenceBoneRole.Core, chest, pelvis, neck, 0.10f * H,
                    allowBelow: true, allowBeyond: true, headKey: "Chest");
                upperTorso = "spine_2";
            }

            // 4. Authored Cloth Bone Chains
            if (ClothChains != null)
            {
                string defaultClothParent = !string.IsNullOrEmpty(upperTorso) && byName.ContainsKey(upperTorso)
                    ? upperTorso
                    : list[0].Name;
                foreach (var cc in ClothChains)
                {
                    if (cc == null || cc.JointPositions == null || cc.JointPositions.Count < 2) continue;
                    string parentBone = byName.ContainsKey(cc.ParentBoneName ?? "") ? cc.ParentBoneName : defaultClothParent;
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
                        float cRad = cc.InfluenceRadius > 0.005f
                            ? Math.Clamp(cc.InfluenceRadius, 0.02f, 0.35f)
                            : MathF.Max(0.04f, MathF.Min(0.12f, segLen * 0.45f));
                        int bIdx = AddBone(bName, prevCloth, ReferenceBoneRole.Cloth, p0, p0, p1, cRad,
                            allowBelow: k == 0, allowBeyond: k == segs - 1,
                            headKey: $"cloth::{cc.ChainId}::{k}",
                            tailKey: $"cloth::{cc.ChainId}::{k + 1}",
                            clothChainId: cc.ChainId);
                        list[bIdx].ClothTargetMeshName = cc.TargetMeshName ?? "";
                        prevCloth = bName;
                    }
                }
            }

            return list;
        }
    }
}

