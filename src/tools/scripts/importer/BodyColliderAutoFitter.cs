using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;

namespace DeadlockPlayground.Importer
{
    /// <summary>
    /// Computes mesh-fitted tapered body collision capsules for core hero bones (head, neck, spine,
    /// pelvis, clavicles, upper arms) so hair/cloth deflects cleanly over the character's actual
    /// silhouette during standing, running, and crouching in both Godot preview and Deadlock export.
    /// </summary>
    public static class BodyColliderAutoFitter
    {
        public const string SkeletonMetaKey = "CustomBodyCollisionCapsulesJson";

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = false,
            PropertyNameCaseInsensitive = true
        };

        private readonly struct BoneCapsuleSpec
        {
            public readonly string BoneName;
            public readonly string DisplayName;
            public readonly string EndChildBone;
            public readonly Vector3 FallbackHeroDir;
            public readonly float FallbackLength;
            public readonly float MinRadius;
            public readonly float MaxRadius;
            public readonly float DefaultRadius;
            public readonly float Percentile;
            public readonly float Margin;
            public readonly bool SpanHipWidth;

            public BoneCapsuleSpec(
                string boneName, string displayName, string endChildBone,
                Vector3 fallbackHeroDir, float fallbackLength,
                float minRadius, float maxRadius, float defaultRadius,
                float percentile, float margin, bool spanHipWidth = false)
            {
                BoneName = boneName;
                DisplayName = displayName;
                EndChildBone = endChildBone;
                FallbackHeroDir = fallbackHeroDir;
                FallbackLength = fallbackLength;
                MinRadius = minRadius;
                MaxRadius = maxRadius;
                DefaultRadius = defaultRadius;
                Percentile = percentile;
                Margin = margin;
                SpanHipWidth = spanHipWidth;
            }
        }

        private static readonly BoneCapsuleSpec[] Specs =
        {
            new("head",        "Head / Skull",      null,          Vector3.Up,    0.16f, 0.088f, 0.155f, 0.112f, 0.65f, 0.010f),
            new("neck_0",      "Neck",              "head",        Vector3.Up,    0.10f, 0.068f, 0.120f, 0.086f, 0.75f, 0.014f),
            new("spine_3",     "Upper Chest / Back","neck_0",      Vector3.Up,    0.15f, 0.132f, 0.245f, 0.166f, 0.85f, 0.018f),
            new("spine_2",     "Mid Chest / Back",  "spine_3",     Vector3.Up,    0.14f, 0.120f, 0.230f, 0.155f, 0.84f, 0.012f),
            new("spine_1",     "Mid Spine",         "spine_2",     Vector3.Up,    0.13f, 0.110f, 0.220f, 0.145f, 0.82f, 0.010f),
            new("spine_0",     "Waist",             "spine_1",     Vector3.Up,    0.12f, 0.095f, 0.205f, 0.130f, 0.78f, 0.008f),
            new("pelvis",      "Pelvis & Hips",     null,          Vector3.Right, 0.18f, 0.095f, 0.220f, 0.135f, 0.78f, 0.010f, spanHipWidth: true),
            new("clavicle_L",  "Left Shoulder",     "arm_upper_L", new Vector3(1, 0, 0),  0.14f, 0.068f, 0.130f, 0.090f, 0.76f, 0.008f),
            new("clavicle_R",  "Right Shoulder",    "arm_upper_R", new Vector3(-1, 0, 0), 0.14f, 0.068f, 0.130f, 0.090f, 0.76f, 0.008f),
            new("arm_upper_L", "Left Upper Arm",    "arm_lower_L", new Vector3(1, 0, 0),  0.26f, 0.052f, 0.105f, 0.070f, 0.72f, 0.004f),
            new("arm_upper_R", "Right Upper Arm",   "arm_lower_R", new Vector3(-1, 0, 0), 0.26f, 0.052f, 0.105f, 0.070f, 0.72f, 0.004f),
            new("leg_upper_L", "Left Thigh",        "leg_lower_L", Vector3.Down,          0.38f, 0.075f, 0.210f, 0.105f, 0.80f, 0.012f),
            new("leg_upper_R", "Right Thigh",       "leg_lower_R", Vector3.Down,          0.38f, 0.075f, 0.210f, 0.105f, 0.80f, 0.012f),
            new("leg_lower_L", "Left Shin / Boot",  "ankle_L",     Vector3.Down,          0.36f, 0.055f, 0.155f, 0.078f, 0.78f, 0.010f),
            new("leg_lower_R", "Right Shin / Boot", "ankle_R",     Vector3.Down,          0.36f, 0.055f, 0.155f, 0.078f, 0.78f, 0.010f),
        };

        /// <summary>
        /// Fits body collision capsules to visible custom meshes weighted onto core hero bones.
        /// Any user-edited entries in <paramref name="existing"/> keep their custom scale/offset/enabled state
        /// unless <paramref name="forceResetEdits"/> is true.
        /// </summary>
        public static List<BodyCollisionCapsuleDef> FitFromRiggedMeshes(
            Skeleton3D skeleton,
            IReadOnlyList<MeshInstance3D> meshes,
            IEnumerable<BodyCollisionCapsuleDef> existing = null,
            bool forceResetEdits = false,
            IReadOnlyList<BoneChainDef> physicsChains = null)
        {
            var result = new List<BodyCollisionCapsuleDef>();
            if (skeleton == null || !GodotObject.IsInstanceValid(skeleton))
                return result;

            var existingByBone = new Dictionary<string, BodyCollisionCapsuleDef>(StringComparer.OrdinalIgnoreCase);
            var existingCustom = new List<BodyCollisionCapsuleDef>();
            if (existing != null)
            {
                foreach (var c in existing)
                {
                    if (c == null || string.IsNullOrWhiteSpace(c.BoneName)) continue;
                    if (c.IsCustomCollider || c.Shape == ColliderShapeKind.Box)
                    {
                        existingCustom.Add(c.Clone());
                    }
                    else
                    {
                        existingByBone[c.BoneName] = c;
                    }
                }
            }

            // Collect all skeleton bone indices belonging to active physics/cloth chains so flared
            // clothing/hair vertices (skirts, capes, coats, hair) never inflate core body colliders.
            var clothBoneIndices = new HashSet<int>();
            if (physicsChains != null)
            {
                foreach (var ch in physicsChains)
                {
                    if (ch == null || ch.Policy != ChainPolicy.PreservePhysicsChain)
                        continue;
                    var names = (ch.AppendedHeroBoneNames != null && ch.AppendedHeroBoneNames.Count > 0)
                        ? ch.AppendedHeroBoneNames
                        : ch.SourceBoneNames;
                    if (names == null) continue;
                    foreach (string bn in names)
                    {
                        if (string.IsNullOrWhiteSpace(bn)) continue;
                        int idx = FindBoneCaseInsensitive(skeleton, bn);
                        if (idx >= 0) clothBoneIndices.Add(idx);
                    }
                }
            }

            // Collect local-space vertices attributed to each relevant bone index
            var boneIdxByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var localVertsByBoneIdx = new Dictionary<int, List<Vector3>>();
            foreach (var spec in Specs)
            {
                int bIdx = FindBoneCaseInsensitive(skeleton, spec.BoneName);
                if (bIdx >= 0)
                {
                    boneIdxByName[spec.BoneName] = bIdx;
                    localVertsByBoneIdx[bIdx] = new List<Vector3>();
                }
            }

            if (boneIdxByName.Count == 0)
                return result;

            CollectBoneLocalVertices(skeleton, meshes, localVertsByBoneIdx, clothBoneIndices);

            Transform3D skelGlobalInv = skeleton.IsInsideTree()
                ? skeleton.GlobalTransform.AffineInverse()
                : Transform3D.Identity;

            foreach (var spec in Specs)
            {
                if (!boneIdxByName.TryGetValue(spec.BoneName, out int bIdx))
                    continue;

                if (!forceResetEdits && existingByBone.TryGetValue(spec.BoneName, out var prev) && prev.IsUserEdited)
                {
                    result.Add(prev.Clone());
                    continue;
                }

                Transform3D boneRestHero = skeleton.GetBoneGlobalRest(bIdx);
                Transform3D heroToBone = boneRestHero.AffineInverse();

                Vector3 p0Local;
                Vector3 p1Local;

                if (spec.SpanHipWidth)
                {
                    int hipL = FindBoneCaseInsensitive(skeleton, "leg_upper_L");
                    int hipR = FindBoneCaseInsensitive(skeleton, "leg_upper_R");
                    if (hipL >= 0 && hipR >= 0)
                    {
                        Vector3 posL = heroToBone * skeleton.GetBoneGlobalRest(hipL).Origin;
                        Vector3 posR = heroToBone * skeleton.GetBoneGlobalRest(hipR).Origin;
                        p0Local = posL * 0.72f;
                        p1Local = posR * 0.72f;
                    }
                    else
                    {
                        Vector3 worldOrigin = boneRestHero.Origin;
                        p0Local = heroToBone * (worldOrigin - spec.FallbackHeroDir * (spec.FallbackLength * 0.5f));
                        p1Local = heroToBone * (worldOrigin + spec.FallbackHeroDir * (spec.FallbackLength * 0.5f));
                    }
                }
                else
                {
                    p0Local = Vector3.Zero;
                    int childIdx = !string.IsNullOrEmpty(spec.EndChildBone) ? FindBoneCaseInsensitive(skeleton, spec.EndChildBone) : -1;
                    if (childIdx >= 0)
                    {
                        Vector3 childInLocal = heroToBone * skeleton.GetBoneGlobalRest(childIdx).Origin;
                        if (childInLocal.LengthSquared() > 0.0004f)
                            p1Local = childInLocal;
                        else
                            p1Local = heroToBone * (boneRestHero.Origin + spec.FallbackHeroDir.Normalized() * spec.FallbackLength);
                    }
                    else
                    {
                        p1Local = heroToBone * (boneRestHero.Origin + spec.FallbackHeroDir.Normalized() * spec.FallbackLength);
                    }

                    // Pull endpoints inward slightly on long chest/head segments so ends don't poke past joint tips
                    if (spec.BoneName.Equals("head", StringComparison.OrdinalIgnoreCase))
                    {
                        Vector3 axis = (p1Local - p0Local);
                        float len = axis.Length();
                        if (len > 0.04f)
                        {
                            Vector3 u = axis / len;
                            p0Local += u * 0.015f;
                            p1Local = p0Local + u * MathF.Min(len * 0.65f, 0.085f);
                        }
                    }
                }

                float r0 = spec.DefaultRadius;
                float r1 = spec.DefaultRadius;

                if (localVertsByBoneIdx.TryGetValue(bIdx, out var pts) && pts.Count >= 12)
                {
                    Vector3 seg = p1Local - p0Local;
                    float segLen = seg.Length();
                    Vector3 u = segLen > 1e-4f ? seg / segLen : new Vector3(1, 0, 0);

                    var rProx = new List<float>(pts.Count / 2 + 4);
                    var rDist = new List<float>(pts.Count / 2 + 4);
                    var rAll = new List<float>(pts.Count);

                    foreach (var v in pts)
                    {
                        Vector3 rel = v - p0Local;
                        float along = rel.Dot(u);
                        // Ignore extreme outliers far beyond this bone segment (e.g. long sleeves/hats)
                        if (segLen > 0.01f && (along < -segLen * 0.35f || along > segLen * 1.35f))
                            continue;
                        float radial = (rel - u * Mathf.Clamp(along, 0f, segLen)).Length();
                        if (radial > spec.MaxRadius * 1.65f) continue;

                        rAll.Add(radial);
                        float frac = segLen > 1e-4f ? along / segLen : 0.5f;
                        if (frac <= 0.5f) rProx.Add(radial);
                        else rDist.Add(radial);
                    }

                    if (rAll.Count >= 8)
                    {
                        rAll.Sort();
                        float rOverall = SamplePercentile(rAll, spec.Percentile) + spec.Margin;
                        r0 = rProx.Count >= 6
                            ? (SamplePercentileSorted(rProx, spec.Percentile) + spec.Margin)
                            : rOverall;
                        r1 = rDist.Count >= 6
                            ? (SamplePercentileSorted(rDist, spec.Percentile) + spec.Margin)
                            : rOverall;
                    }
                }

                r0 = Mathf.Clamp(r0, spec.MinRadius, spec.MaxRadius);
                r1 = Mathf.Clamp(r1, spec.MinRadius, spec.MaxRadius);

                var def = new BodyCollisionCapsuleDef
                {
                    BoneName = spec.BoneName,
                    DisplayName = spec.DisplayName,
                    Point0Local = p0Local,
                    Radius0Meters = r0,
                    Point1Local = p1Local,
                    Radius1Meters = r1,
                    RadiusScale = 1.0f,
                    LocalOffsetMeters = Vector3.Zero,
                    Enabled = true,
                    IsUserEdited = false
                };

                if (!forceResetEdits && existingByBone.TryGetValue(spec.BoneName, out var oldUnedited))
                {
                    def.Enabled = oldUnedited.Enabled;
                }

                result.Add(def);
            }

            // Smooth the spine_0 -> spine_1 -> spine_2 -> spine_3 envelope so spine_2 never creates
            // a concave pocket between spine_3 and spine_0 that traps hanging hair during look-up/dash.
            BodyCollisionCapsuleDef FindCap(string name) =>
                result.FirstOrDefault(c => c != null && c.BoneName.Equals(name, StringComparison.OrdinalIgnoreCase));

            var capS0 = FindCap("spine_0");
            var capS1 = FindCap("spine_1");
            var capS2 = FindCap("spine_2");
            var capS3 = FindCap("spine_3");
            if (capS0 != null && capS3 != null)
            {
                float rLow = MathF.Max(capS0.Radius0Meters, capS0.Radius1Meters);
                float rHigh = MathF.Max(capS3.Radius0Meters, capS3.Radius1Meters);
                if (capS1 != null && !capS1.IsUserEdited)
                {
                    float minS1 = Mathf.Lerp(rLow, rHigh, 0.33f) * 0.94f;
                    capS1.Radius0Meters = MathF.Max(capS1.Radius0Meters, minS1);
                    capS1.Radius1Meters = MathF.Max(capS1.Radius1Meters, minS1);
                }
                if (capS2 != null && !capS2.IsUserEdited)
                {
                    float minS2 = Mathf.Lerp(rLow, rHigh, 0.66f) * 0.94f;
                    capS2.Radius0Meters = MathF.Max(capS2.Radius0Meters, minS2);
                    capS2.Radius1Meters = MathF.Max(capS2.Radius1Meters, minS2);
                }
            }

            // Universal bind-pose clearance: shrink any non-user-edited capsule that encloses a
            // simulated cloth bone at rest pose so skirts/coats/capes/hair never spawn inside a
            // body collider and explode outward on frame 1 (works across all models & outfits).
            if (physicsChains != null && physicsChains.Count > 0)
            {
                ApplyClothBindPoseClearance(skeleton, result, physicsChains);
            }

            // Preserve user-added custom prop colliders (Boxes / extra Capsules) across Auto-Fit
            if (!forceResetEdits && existingCustom.Count > 0)
            {
                result.AddRange(existingCustom);
            }

            return result;
        }

        private static void ApplyClothBindPoseClearance(
            Skeleton3D skeleton,
            List<BodyCollisionCapsuleDef> capsules,
            IReadOnlyList<BoneChainDef> physicsChains)
        {
            const float InchesToMeters = 0.0254f;
            var dynamicNodes = new List<(Vector3 PosHero, float NodeColRMeters, string AnchorBone)>();

            foreach (var ch in physicsChains)
            {
                if (ch == null || ch.Policy != ChainPolicy.PreservePhysicsChain ||
                    ch.Simulation == SimulationKind.Jiggle)
                    continue;
                var names = (ch.AppendedHeroBoneNames != null && ch.AppendedHeroBoneNames.Count > 0)
                    ? ch.AppendedHeroBoneNames
                    : ch.SourceBoneNames;
                if (names == null || names.Count < 2) continue;

                float tipColM = MathF.Max(0.008f, ch.CollisionRadiusTipInches * InchesToMeters);
                int count = names.Count;
                for (int j = 1; j < count; j++) // j == 0 is pinned root
                {
                    string bn = names[j];
                    if (string.IsNullOrWhiteSpace(bn)) continue;
                    string srcBn = (ch.SourceBoneNames != null && j < ch.SourceBoneNames.Count) ? ch.SourceBoneNames[j] : null;
                    var jOv = ch.FindJointOverride(bn, srcBn);
                    if (jOv != null && jOv.Simulate.HasValue && !jOv.Simulate.Value) continue;

                    int bIdx = FindBoneCaseInsensitive(skeleton, bn);
                    if (bIdx < 0) continue;

                    float frac = count > 1 ? (float)j / (count - 1) : 1f;
                    float colM = (jOv != null && jOv.CollisionRadiusInches.HasValue)
                        ? MathF.Max(0.005f, jOv.CollisionRadiusInches.Value * InchesToMeters)
                        : Mathf.Lerp(tipColM * 0.45f, tipColM, frac);

                    Vector3 posHero = skeleton.GetBoneGlobalRest(bIdx).Origin;
                    dynamicNodes.Add((posHero, colM, ch.AnchorHeroBone ?? ""));
                }
            }

            if (dynamicNodes.Count == 0) return;

            var minRadiusByBone = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            foreach (var spec in Specs) minRadiusByBone[spec.BoneName] = spec.MinRadius;

            foreach (var cap in capsules)
            {
                if (cap == null || cap.IsUserEdited || !cap.Enabled) continue;
                int bIdx = FindBoneCaseInsensitive(skeleton, cap.BoneName);
                if (bIdx < 0) continue;

                // Never crush a mesh-fitted anatomical capsule into a pencil stick (e.g. 2.17 in thighs):
                // trim at most ~25% of fitted silhouette and always respect spec.MinRadius.
                float specMin = minRadiusByBone.TryGetValue(cap.BoneName, out float sm) ? sm : 0.075f;
                float floorR0 = MathF.Max(specMin, cap.Radius0Meters * 0.75f);
                float floorR1 = MathF.Max(specMin, cap.Radius1Meters * 0.75f);

                Transform3D boneRest = skeleton.GetBoneGlobalRest(bIdx);
                Vector3 p0Hero = boneRest * (cap.Point0Local + cap.LocalOffsetMeters);
                Vector3 p1Hero = boneRest * (cap.Point1Local + cap.LocalOffsetMeters);
                Vector3 seg = p1Hero - p0Hero;
                float segLenSq = seg.LengthSquared();

                foreach (var node in dynamicNodes)
                {
                    float t = segLenSq > 1e-6f
                        ? Mathf.Clamp((node.PosHero - p0Hero).Dot(seg) / segLenSq, 0f, 1f)
                        : 0.5f;
                    Vector3 closest = p0Hero + seg * t;
                    float dist = (node.PosHero - closest).Length();

                    // Ignore nodes sitting virtually on top of their own anchor bone's centerline
                    // (e.g. a scalp tuft right on the head bone origin) so we don't collapse that capsule.
                    if (dist < 0.055f && cap.BoneName.Equals(node.AnchorBone, StringComparison.OrdinalIgnoreCase))
                        continue;

                    float rAtT = Mathf.Lerp(cap.Radius0Meters, cap.Radius1Meters, t) * cap.RadiusScale;
                    float margin = MathF.Min(node.NodeColRMeters * 0.45f, 0.015f);
                    if (dist < rAtT + margin)
                    {
                        float rawSafeR = (dist - margin) / MathF.Max(0.01f, cap.RadiusScale);
                        if (t <= 0.75f) cap.Radius0Meters = MathF.Min(cap.Radius0Meters, MathF.Max(floorR0, rawSafeR));
                        if (t >= 0.25f) cap.Radius1Meters = MathF.Min(cap.Radius1Meters, MathF.Max(floorR1, rawSafeR));
                    }
                }
            }
        }

        private static float SamplePercentileSorted(List<float> vals, float p)
        {
            vals.Sort();
            return SamplePercentile(vals, p);
        }

        private static float SamplePercentile(List<float> sorted, float p)
        {
            if (sorted.Count == 0) return 0.1f;
            int idx = Mathf.Clamp((int)MathF.Floor((sorted.Count - 1) * Mathf.Clamp(p, 0f, 1f)), 0, sorted.Count - 1);
            return sorted[idx];
        }

        private static void CollectBoneLocalVertices(
            Skeleton3D skeleton,
            IReadOnlyList<MeshInstance3D> meshes,
            Dictionary<int, List<Vector3>> outByBoneIdx,
            HashSet<int> excludedClothBoneIndices = null)
        {
            if (meshes == null || meshes.Count == 0) return;

            Transform3D skelInv = skeleton.IsInsideTree() ? skeleton.GlobalTransform.AffineInverse() : Transform3D.Identity;
            var boneHeroInv = new Dictionary<int, Transform3D>();
            foreach (int bIdx in outByBoneIdx.Keys)
                boneHeroInv[bIdx] = skeleton.GetBoneGlobalRest(bIdx).AffineInverse();

            foreach (var mi in meshes)
            {
                if (mi == null || !GodotObject.IsInstanceValid(mi) || !mi.Visible || mi.Mesh == null || mi.Skin == null)
                    continue;

                var bindToSkel = new int[mi.Skin.GetBindCount()];
                for (int bi = 0; bi < bindToSkel.Length; bi++)
                {
                    bindToSkel[bi] = skeleton.FindBone(mi.Skin.GetBindName(bi));
                }

                Transform3D meshToHero = mi.IsInsideTree() && skeleton.IsInsideTree()
                    ? skelInv * mi.GlobalTransform
                    : Transform3D.Identity;

                int surfCount = mi.Mesh.GetSurfaceCount();
                for (int s = 0; s < surfCount; s++)
                {
                    var arr = mi.Mesh.SurfaceGetArrays(s);
                    if (arr == null || arr.Count <= (int)Mesh.ArrayType.Vertex) continue;
                    var verts = arr[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                    if (verts == null || verts.Length == 0) continue;
                    var bones = arr.Count > (int)Mesh.ArrayType.Bones ? arr[(int)Mesh.ArrayType.Bones].AsInt32Array() : null;
                    var weights = arr.Count > (int)Mesh.ArrayType.Weights ? arr[(int)Mesh.ArrayType.Weights].AsFloat32Array() : null;
                    if (bones == null || weights == null) continue;

                    int stride = bones.Length / Math.Max(1, verts.Length);
                    if (stride <= 0) continue;

                    // Subsample large meshes for fast O(1) ms interactive refits
                    int step = verts.Length > 12000 ? 2 : 1;
                    for (int v = 0; v < verts.Length; v += step)
                    {
                        int bestSkelBone = -1;
                        float bestW = 0f;
                        float clothW = 0f;
                        for (int k = 0; k < stride; k++)
                        {
                            float w = weights[v * stride + k];
                            if (w <= 0f) continue;
                            int bi = bones[v * stride + k];
                            int sb = (bi >= 0 && bi < bindToSkel.Length) ? bindToSkel[bi] : -1;
                            if (sb < 0) continue;
                            if (excludedClothBoneIndices != null && excludedClothBoneIndices.Contains(sb))
                                clothW += w;
                            if (w > bestW)
                            {
                                bestW = w;
                                bestSkelBone = sb;
                            }
                        }

                        // Skip vertices significantly weighted to cloth/hair/skirt physics chains so
                        // loose garments never inflate anatomical body colliders.
                        if (clothW >= 0.10f)
                            continue;

                        if (bestSkelBone >= 0 && bestW >= 0.30f && outByBoneIdx.TryGetValue(bestSkelBone, out var list))
                        {
                            Vector3 heroPt = meshToHero * verts[v];
                            list.Add(boneHeroInv[bestSkelBone] * heroPt);
                        }
                    }
                }
            }
        }

        public static bool TryMirrorSideCapsule(
            List<BodyCollisionCapsuleDef> capsules,
            BodyCollisionCapsuleDef source,
            Skeleton3D skeleton,
            out BodyCollisionCapsuleDef mirroredTwin)
        {
            mirroredTwin = null;
            if (capsules == null || source == null || skeleton == null || string.IsNullOrEmpty(source.BoneName))
                return false;

            string twinBone = null;
            if (source.BoneName.EndsWith("_L", StringComparison.OrdinalIgnoreCase))
                twinBone = source.BoneName[..^2] + "_R";
            else if (source.BoneName.EndsWith("_R", StringComparison.OrdinalIgnoreCase))
                twinBone = source.BoneName[..^2] + "_L";
            if (string.IsNullOrEmpty(twinBone)) return false;

            int srcIdx = FindBoneCaseInsensitive(skeleton, source.BoneName);
            int dstIdx = FindBoneCaseInsensitive(skeleton, twinBone);
            if (srcIdx < 0 || dstIdx < 0) return false;

            var existingTwin = capsules.FirstOrDefault(c => c.BoneName.Equals(twinBone, StringComparison.OrdinalIgnoreCase));
            if (existingTwin == null) return false;

            Transform3D srcHero = skeleton.GetBoneGlobalRest(srcIdx);
            Transform3D dstHeroInv = skeleton.GetBoneGlobalRest(dstIdx).AffineInverse();

            Vector3 MirrorX(Vector3 p) => new(-p.X, p.Y, p.Z);

            Vector3 p0Hero = MirrorX(srcHero * source.Point0Local);
            Vector3 p1Hero = MirrorX(srcHero * source.Point1Local);
            Vector3 offHero = MirrorX(srcHero * source.LocalOffsetMeters) - MirrorX(srcHero.Origin);

            existingTwin.Point0Local = dstHeroInv * p0Hero;
            existingTwin.Point1Local = dstHeroInv * p1Hero;
            existingTwin.LocalOffsetMeters = dstHeroInv.Basis * offHero;
            existingTwin.Radius0Meters = source.Radius0Meters;
            existingTwin.Radius1Meters = source.Radius1Meters;
            existingTwin.RadiusScale = source.RadiusScale;
            existingTwin.Enabled = source.Enabled;
            existingTwin.IsUserEdited = true;
            mirroredTwin = existingTwin;
            return true;
        }

        private static int FindBoneCaseInsensitive(Skeleton3D skel, string name)
        {
            int exact = skel.FindBone(name);
            if (exact >= 0) return exact;
            int n = skel.GetBoneCount();
            for (int i = 0; i < n; i++)
            {
                if (skel.GetBoneName(i).Equals(name, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return -1;
        }

        private sealed class CapsuleDto
        {
            [JsonPropertyName("id")] public string Id { get; set; } = "";
            [JsonPropertyName("shape")] public int Shape { get; set; } = 0;
            [JsonPropertyName("custom")] public bool Custom { get; set; } = false;
            [JsonPropertyName("bone")] public string Bone { get; set; } = "";
            [JsonPropertyName("label")] public string Label { get; set; } = "";
            [JsonPropertyName("p0")] public float[] P0 { get; set; }
            [JsonPropertyName("r0")] public float R0 { get; set; }
            [JsonPropertyName("p1")] public float[] P1 { get; set; }
            [JsonPropertyName("r1")] public float R1 { get; set; }
            [JsonPropertyName("scale")] public float Scale { get; set; } = 1.0f;
            [JsonPropertyName("off")] public float[] Off { get; set; }
            [JsonPropertyName("boxHalf")] public float[] BoxHalf { get; set; }
            [JsonPropertyName("rotDeg")] public float[] RotDeg { get; set; }
            [JsonPropertyName("planarizeBack")] public bool PlanarizeBack { get; set; } = false;
            [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
            [JsonPropertyName("edited")] public bool Edited { get; set; }
        }

        public static string SerializeToJson(IEnumerable<BodyCollisionCapsuleDef> capsules)
        {
            if (capsules == null) return "[]";
            var dtos = new List<CapsuleDto>();
            foreach (var c in capsules)
            {
                if (c == null || string.IsNullOrWhiteSpace(c.BoneName)) continue;
                dtos.Add(new CapsuleDto
                {
                    Id = c.ColliderId ?? "",
                    Shape = (int)c.Shape,
                    Custom = c.IsCustomCollider,
                    Bone = c.BoneName,
                    Label = c.DisplayName,
                    P0 = new[] { c.Point0Local.X, c.Point0Local.Y, c.Point0Local.Z },
                    R0 = c.Radius0Meters,
                    P1 = new[] { c.Point1Local.X, c.Point1Local.Y, c.Point1Local.Z },
                    R1 = c.Radius1Meters,
                    Scale = c.RadiusScale,
                    Off = new[] { c.LocalOffsetMeters.X, c.LocalOffsetMeters.Y, c.LocalOffsetMeters.Z },
                    BoxHalf = new[] { c.BoxHalfExtentsMeters.X, c.BoxHalfExtentsMeters.Y, c.BoxHalfExtentsMeters.Z },
                    RotDeg = new[] { c.LocalRotationDeg.X, c.LocalRotationDeg.Y, c.LocalRotationDeg.Z },
                    PlanarizeBack = c.PlanarizeBackOnly,
                    Enabled = c.Enabled,
                    Edited = c.IsUserEdited
                });
            }
            return JsonSerializer.Serialize(dtos, JsonOpts);
        }

        public static List<BodyCollisionCapsuleDef> DeserializeFromJson(string json)
        {
            var list = new List<BodyCollisionCapsuleDef>();
            if (string.IsNullOrWhiteSpace(json)) return list;
            try
            {
                var dtos = JsonSerializer.Deserialize<List<CapsuleDto>>(json, JsonOpts);
                if (dtos == null) return list;
                foreach (var d in dtos)
                {
                    if (d == null || string.IsNullOrWhiteSpace(d.Bone)) continue;
                    list.Add(new BodyCollisionCapsuleDef
                    {
                        ColliderId = d.Id ?? "",
                        Shape = d.Shape == (int)ColliderShapeKind.Box ? ColliderShapeKind.Box : ColliderShapeKind.Capsule,
                        IsCustomCollider = d.Custom,
                        BoneName = d.Bone,
                        DisplayName = d.Label ?? d.Bone,
                        Point0Local = d.P0 is { Length: >= 3 } ? new Vector3(d.P0[0], d.P0[1], d.P0[2]) : Vector3.Zero,
                        Radius0Meters = d.R0 > 0.005f ? d.R0 : 0.10f,
                        Point1Local = d.P1 is { Length: >= 3 } ? new Vector3(d.P1[0], d.P1[1], d.P1[2]) : Vector3.Zero,
                        Radius1Meters = d.R1 > 0.005f ? d.R1 : 0.10f,
                        RadiusScale = d.Scale > 0.01f ? d.Scale : 1.0f,
                        LocalOffsetMeters = d.Off is { Length: >= 3 } ? new Vector3(d.Off[0], d.Off[1], d.Off[2]) : Vector3.Zero,
                        BoxHalfExtentsMeters = d.BoxHalf is { Length: >= 3 }
                            ? new Vector3(MathF.Max(0.015f, d.BoxHalf[0]), MathF.Max(0.015f, d.BoxHalf[1]), MathF.Max(0.015f, d.BoxHalf[2]))
                            : new Vector3(0.13f, 0.15f, 0.08f),
                        LocalRotationDeg = d.RotDeg is { Length: >= 3 } ? new Vector3(d.RotDeg[0], d.RotDeg[1], d.RotDeg[2]) : Vector3.Zero,
                        PlanarizeBackOnly = d.PlanarizeBack,
                        Enabled = d.Enabled,
                        IsUserEdited = d.Edited
                    });
                }
            }
            catch (Exception ex)
            {
                GD.PushWarning($"[BodyColliderAutoFitter] Failed deserializing capsules JSON: {ex.Message}");
            }
            return list;
        }
    }
}

