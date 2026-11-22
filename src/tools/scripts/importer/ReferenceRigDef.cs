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
        public ReferenceFingerRigMode FingerMode { get; set; } = ReferenceFingerRigMode.ArticulatedFingers;
        public bool EnableThumb { get; set; } = true;
        public bool EnableIndex { get; set; } = true;
        public bool EnableMiddle { get; set; } = true;
        public bool EnableRing { get; set; } = true;
        public bool EnablePinky { get; set; } = true;
        public int PhalanxCount { get; set; } = 2; // 2 or 3 (2j deforms cleaner on most models)

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

                // Build palm orthonormal frame aligned to actual hand geometry
                Vector3 upRef = MathF.Abs(handFwd.Y) < 0.9f ? Vector3.Up : Vector3.Forward;
                Vector3 palmAcross = handFwd.Cross(upRef);
                palmAcross = palmAcross.LengthSquared() > 1e-6f ? palmAcross.Normalized() : Vector3.Forward;
                if (palmAcross.Z < 0f) palmAcross = -palmAcross; // +across points toward thumb (+Z front)
                Vector3 palmUp = palmAcross.Cross(handFwd).Normalized();

                // Measure actual lateral (across) span of the 4-finger + palm band (f in 0.30..0.92)
                float minA = float.MaxValue, maxA = float.MinValue;
                float sumUp = 0f; int cntBand = 0;
                foreach (var p in handPts)
                {
                    Vector3 rel = p - wrist;
                    float f = rel.Dot(handFwd) / handLen;
                    if (f < 0.26f || f > 0.94f) continue;
                    float a = rel.Dot(palmAcross);
                    if (a < minA) minA = a;
                    if (a > maxA) maxA = a;
                    sumUp += rel.Dot(palmUp);
                    cntBand++;
                }
                if (cntBand < 6 || maxA - minA < 0.01f * H) continue;
                float palmWidth = maxA - minA;
                float avgUp = sumUp / cntBand;

                // 1. Dedicated angled-ray thumb solver (isolated to f <= 0.70 & a >= 0.74 so it NEVER snaps to index fingertip!)
                if (IsFingerEnabled("thumb"))
                {
                    var thumbPts = new List<(float F, float A, Vector3 P)>();
                    float thumbMinA = minA + 0.72f * palmWidth;
                    foreach (var p in handPts)
                    {
                        Vector3 rel = p - wrist;
                        float f = rel.Dot(handFwd) / handLen;
                        float a = rel.Dot(palmAcross);
                        if (f >= 0.14f && f <= 0.68f && a >= thumbMinA)
                            thumbPts.Add((f, a, p));
                    }

                    Vector3 thumbBase = wrist + handFwd * (0.22f * handLen) + palmAcross * (minA + 0.76f * palmWidth) + palmUp * avgUp;
                    Vector3 thumbTip = wrist + handFwd * (0.58f * handLen) + palmAcross * (minA + 0.96f * palmWidth) + palmUp * avgUp;

                    if (thumbPts.Count >= 6)
                    {
                        // Tip = centroid of the most-lateral + distal thumb vertices (score = a_norm*0.65 + f*0.35)
                        thumbPts.Sort((x, y) =>
                        {
                            float sx = ((x.A - minA) / palmWidth) * 0.65f + x.F * 0.35f;
                            float sy = ((y.A - minA) / palmWidth) * 0.65f + y.F * 0.35f;
                            return sx.CompareTo(sy);
                        });
                        int tStart = Math.Max(0, (int)(thumbPts.Count * 0.82f));
                        Vector3 tipSum = Vector3.Zero;
                        for (int i = tStart; i < thumbPts.Count; i++) tipSum += thumbPts[i].P;
                        thumbTip = tipSum / (thumbPts.Count - tStart);

                        // Base = centroid of proximal thumb vertices (f in 0.16..0.34)
                        Vector3 baseSum = Vector3.Zero; int baseCnt = 0;
                        foreach (var tp in thumbPts)
                        {
                            if (tp.F >= 0.16f && tp.F <= 0.34f) { baseSum += tp.P; baseCnt++; }
                        }
                        if (baseCnt >= 2) thumbBase = baseSum / baseCnt;
                    }

                    // Clamp thumb length to realistic anatomical bounds (0.28..0.52 of handLen)
                    Vector3 tDelta = thumbTip - thumbBase;
                    float tLen = tDelta.Length();
                    if (tLen < 0.18f * handLen)
                        thumbTip = thumbBase + (handFwd * 0.65f + palmAcross * 0.75f).Normalized() * (0.36f * handLen);
                    else if (tLen > 0.52f * handLen)
                        thumbTip = thumbBase + (tDelta / tLen) * (0.52f * handLen);

                    for (int j = 0; j <= phalanges; j++)
                    {
                        string key = $"finger_thumb_{j}_{side}";
                        if (!forceRegenerate && Joints.ContainsKey(key)) continue;
                        float frac = j / (float)phalanges;
                        Vector3 target = thumbBase.Lerp(thumbTip, frac);
                        // Gently snap toward local thumb vertices within a tight radius around target
                        float snapRad = MathF.Max(0.008f * H, handLen * 0.12f);
                        Vector3 acc = Vector3.Zero; int hits = 0;
                        foreach (var tp in thumbPts)
                        {
                            if (tp.P.DistanceSquaredTo(target) <= snapRad * snapRad) { acc += tp.P; hits++; }
                        }
                        Joints[key] = hits >= 2 ? target.Lerp(acc / hits, 0.75f) : target;
                    }
                }

                // 2. Four main fingers (index, middle, ring, pinky):
                // Measure lateral span strictly in the distal fingertip zone (f in 0.74..0.98, strictly PAST the thumb tip
                // which ends at f <= 0.68!) so the thumb NEVER enters max4A and all 4 fingers land on their exact fingers!
                float min4A = float.MaxValue, max4A = float.MinValue;
                int cnt4 = 0;
                foreach (var p in handPts)
                {
                    Vector3 rel = p - wrist;
                    float f = rel.Dot(handFwd) / handLen;
                    if (f < 0.74f || f > 0.98f) continue;
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

                // Use a 3% dead-zone gutter between adjacent fingers and per-finger anatomical fCap limits
                // so a longer neighbor (e.g. ring) NEVER leaks into a shorter neighbor's tip (e.g. pinky)!
                (string name, float a0, float a1, float fStart, float fFallbackEnd, float fCap)[] bands =
                {
                    ("index",  0.76f, 1.02f, 0.48f, 0.93f, 0.99f),
                    ("middle", 0.50f, 0.74f, 0.50f, 1.00f, 1.04f),
                    ("ring",   0.24f, 0.48f, 0.48f, 0.91f, 0.97f),
                    ("pinky", -0.02f, 0.21f, 0.44f, 0.78f, 0.85f)
                };

                foreach (var (fName, a0, a1, fStart, fFallbackEnd, fCap) in bands)
                {
                    if (!IsFingerEnabled(fName)) continue;
                    float loA = min4A + a0 * width4;
                    float hiA = min4A + a1 * width4;
                    float midA = (loA + hiA) * 0.5f;

                    var slice = new List<(float F, float A, float U, Vector3 P)>();
                    foreach (var p in handPts)
                    {
                        Vector3 rel = p - wrist;
                        float f = rel.Dot(handFwd) / handLen;
                        float a = rel.Dot(palmAcross);
                        if (f >= fStart * 0.82f && f <= fCap && a >= loA && a <= hiA)
                            slice.Add((f, a, rel.Dot(palmUp), p));
                    }

                    float fMax = fFallbackEnd;
                    if (slice.Count >= 4)
                    {
                        slice.Sort((x, y) => x.F.CompareTo(y.F));
                        fMax = Math.Clamp(slice[(int)((slice.Count - 1) * 0.94f)].F, fStart + 0.16f, fCap);
                    }

                    Vector3 prevPos = Vector3.Zero;
                    float prevA = midA;
                    for (int j = 0; j <= phalanges; j++)
                    {
                        string key = $"finger_{fName}_{j}_{side}";
                        if (!forceRegenerate && Joints.ContainsKey(key))
                        {
                            prevPos = Joints[key];
                            prevA = (prevPos - wrist).Dot(palmAcross);
                            continue;
                        }

                        float frac = j / (float)phalanges;
                        float targetF = Mathf.Lerp(fStart, fMax, frac);
                        float bandHalf = (fMax - fStart) / (phalanges * 1.45f);

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

                        Vector3 chosen = hits >= 2
                            ? snapped / hits
                            : (wrist + handFwd * (targetF * handLen) + palmAcross * midA + palmUp * avgUp);

                        // Lateral continuity guard: prevent a distal phalanx/tip (especially pinky) from drifting
                        // sideways onto an adjacent finger if a few neighbor vertices grazed the band edge.
                        if (j > 0)
                        {
                            Vector3 relC = chosen - wrist;
                            float curA = relC.Dot(palmAcross);
                            float maxStepA = 0.08f * width4;
                            float clampedA = Math.Clamp(curA, MathF.Max(loA, prevA - maxStepA), MathF.Min(hiA, prevA + maxStepA));
                            if (MathF.Abs(clampedA - curA) > 1e-5f)
                                chosen += palmAcross * (clampedA - curA);
                        }

                        // Monotonic progression guard: ensure each phalanx advances along handFwd
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
            float totalLength)
        {
            int segs = Math.Clamp(segmentCount, 1, 6);
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
            // Auto-Rig Pro–style Chin pin (with automatic fallback to legacy Head pin):
            // places the head bone pivot cleanly at the skull base at chin height so the chin/jaw moves solidly with head!
            Vector3 chinPin = Joints.ContainsKey("Chin")
                ? GetJ("Chin", neck + Vector3.Up * 0.045f * H)
                : neck.Lerp(GetJ("Head", neck + Vector3.Up * 0.10f * H), 0.55f);
            Vector3 hipL = GetJ("HipL", pelvis + new Vector3(0.08f * H, -0.04f * H, 0f));
            Vector3 hipR = GetJ("HipR", pelvis + new Vector3(-0.08f * H, -0.04f * H, 0f));
            Vector3 midHip = hipL.Lerp(hipR, 0.5f);

            // 1. Pelvis + Modular Spine (1..4 bones) with clean pelvis/hip/spine_0 separation
            string[] spineNames = GetDeadlockSpineBoneNames(SpineCount);
            Vector3 firstSpineOrigin = spineNames.Length == 1
                ? pelvis.Lerp(neck, 0.48f)
                : pelvis.Lerp(neck, 0.30f);
            AddBone("pelvis", null, ReferenceBoneRole.Core, pelvis, midHip, firstSpineOrigin, 0.074f * H, allowBelow: false, headKey: "Pelvis");

            string prevTorso = "pelvis";
            for (int si = 0; si < spineNames.Length; si++)
            {
                float t0 = spineNames.Length == 1
                    ? 0.48f
                    : Mathf.Lerp(0.30f, 0.80f, si / (float)(spineNames.Length - 1));
                float t1 = si == spineNames.Length - 1
                    ? (HasNeck ? 0.90f : 1.0f)
                    : Mathf.Lerp(0.30f, 0.80f, (si + 1) / (float)(spineNames.Length - 1));
                Vector3 spOrig = pelvis.Lerp(neck, t0);
                Vector3 spEnd = pelvis.Lerp(neck, t1);
                AddBone(spineNames[si], prevTorso, ReferenceBoneRole.Core, spOrig, spOrig, spEnd, 0.098f * H,
                    headKey: si == spineNames.Length - 1 ? "Chest" : "");
                prevTorso = spineNames[si];
            }
            string upperTorso = prevTorso;

            // 2. Optional Neck + Head (derived from Chin landmark: neck spans collar -> skull base with balanced
            // radius so spine_3 / head never swallow neck_0 weights, while head pivots at skull base & owns the jaw/chin!)
            string headParent = upperTorso;
            float chinY = MathF.Max(neck.Y + 0.018f * H, chinPin.Y);
            float skullPivotY = MathF.Max(neck.Y + 0.032f * H, chinY + 0.022f * H);
            Vector3 headOrig = HasNeck
                ? new Vector3(neck.X, skullPivotY, Mathf.Lerp(neck.Z, chinPin.Z, 0.25f))
                : neck;
            if (HasNeck)
            {
                AddBone("neck_0", upperTorso, ReferenceBoneRole.Core, neck, neck, headOrig, 0.058f * H, headKey: "Neck");
                headParent = "neck_0";
            }
            AddBone("head", headParent, ReferenceBoneRole.Core, headOrig, headOrig, headOrig + Vector3.Up * (0.14f * H), 0.075f * H, allowBeyond: true, headKey: "Chin");

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
                Vector3 handEnd = wr + handDir * (hasFingers ? 0.025f * H : 0.11f * H);
                if (hasFingers)
                {
                    // Anchor hand capsule strictly proximal to the metacarpal roots so hand_L/R never steals
                    // palm/knuckle vertices from finger_*_meta or finger_*_0!
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
                        handEnd = wr.Lerp(knuckleSum / knuckleCnt, 0.30f);
                }
                AddBone("hand_" + s, "arm_lower_" + s, ReferenceBoneRole.Core, wr, wr, handEnd, hasFingers ? 0.015f * H : 0.042f * H,
                    allowBelow: true, allowBeyond: !hasFingers, headKey: "Wrist" + s);

                // Individual Articulated Fingers (matching DeadlockRigs finger_{name}_meta_{L|R} + finger_{name}_{0..2}_{L|R})
                if (hasFingers)
                {
                    foreach (string fName in FingerNames)
                    {
                        if (!IsFingerEnabled(fName)) continue;
                        string prevF = "hand_" + s;

                        // Emit metacarpal bone (finger_{name}_meta_{L|R}) for the 4 non-thumb fingers with balanced
                        // palm-width radius so it receives clean mid-palm seeds and bridges hand -> knuckle smoothly!
                        string firstHk = $"finger_{fName}_0_{s}";
                        if (fName != "thumb" && Joints.ContainsKey(swapLeftRight ? (MirrorJointKey(firstHk) ?? firstHk) : firstHk))
                        {
                            Vector3 k0 = GetJ(firstHk, handEnd);
                            Vector3 metaOrig = wr.Lerp(k0, 0.28f);
                            string metaName = $"finger_{fName}_meta_{s}";
                            AddBone(metaName, prevF, ReferenceBoneRole.Finger, metaOrig, metaOrig, k0, MathF.Max(0.007f, 0.011f * H),
                                allowBelow: true, allowBeyond: false, headKey: "Wrist" + s, tailKey: firstHk);
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

                // Legs & optional Toes (balanced hip radius so pelvis & thighs blend smoothly across glute/shorts seams)
                AddBone("leg_upper_" + s, "pelvis", ReferenceBoneRole.Core, hip, hip, kn, 0.058f * H, headKey: "Hip" + s, tailKey: "Knee" + s);
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

