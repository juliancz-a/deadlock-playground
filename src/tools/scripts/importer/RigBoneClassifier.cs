using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace DeadlockPlayground.Importer
{
    /// <summary>Detected skeleton naming convention (informational, also used to tune heuristics).</summary>
    public enum RigFamily
    {
        Unknown,
        AutoRigPro,
        Rigify,
        Mixamo,
        UnrealMannequin,
        UnityHumanoid,
        VrmVroid,
        Biped,
        ValveBiped,
        RobloxR15,
        RobloxR6,
        Generic
    }

    /// <summary>What a source bone is for.</summary>
    public enum BoneKind
    {
        /// <summary>Main humanoid joint (maps to a Deadlock hero bone).</summary>
        Core,
        Finger,
        Twist,
        Face,
        /// <summary>Clothing / hair / cape / skirt style secondary chain.</summary>
        Clothing,
        /// <summary>Tail / ears / wings / props style extra chain.</summary>
        Accessory,
        /// <summary>Animator control, IK, pole, constraint, reference bones. Never export-worthy unless they carry weights.</summary>
        Control,
        Helper,
        Root,
        Unknown
    }

    public sealed class BoneClassification
    {
        public int Index;
        public string Name = "";
        public int Parent = -1;
        public int Depth;
        public BoneKind Kind = BoneKind.Unknown;
        /// <summary>'L', 'R' or 'C'.</summary>
        public char Side = 'C';
        /// <summary>Hero canonical bone name suggestion (e.g. "arm_upper_L"). Null when not a mapped joint.</summary>
        public string Canonical;
        /// <summary>UI category label used to group bones in pickers.</summary>
        public string Category = "Other";
        /// <summary>Why the bone was classified this way (diagnostics / tooltips).</summary>
        public string Reason = "";
    }

    public sealed class ExtraBoneChain
    {
        public int RootIndex;
        public string RootName = "";
        public string Category = "Accessory";
        /// <summary>True when this 1-bone hub splits into 2+ child chains (e.g. HairRoot above tails).</summary>
        public bool IsMultiBranchHub;
        /// <summary>Source bone indices, parents always before children.</summary>
        public readonly List<int> BoneIndices = new();
        public string DisplayName => $"{RootName} ({BoneIndices.Count} bone{(BoneIndices.Count == 1 ? "" : "s")})";
    }

    public sealed class RigAnalysis
    {
        public RigFamily Family = RigFamily.Unknown;
        public RigFamily DetectedFamily = RigFamily.Unknown;
        public double Confidence;
        public double HierarchyShapeScore;
        public double SymmetryScore;
        public readonly Dictionary<RigFamily, double> FamilyScores = new();
        public BoneClassification[] Bones = Array.Empty<BoneClassification>();
        /// <summary>Hero canonical bone name -> source bone index (conflict-free, 1:1).</summary>
        public readonly Dictionary<string, int> CanonicalToSource = new(StringComparer.OrdinalIgnoreCase);

        public bool IsControlOrHelper(int idx)
            => idx >= 0 && idx < Bones.Length && (Bones[idx].Kind == BoneKind.Control || Bones[idx].Kind == BoneKind.Helper);
    }

    /// <summary>
    /// Generic, rig-agnostic bone classifier (Auto-Rig Pro, Rigify, Mixamo, Unreal, Unity, VRM, 3ds Max Biped, Source, ...).
    /// Works purely on bone names + hierarchy (no engine types) so it can be unit tested standalone.
    /// </summary>
    public static class RigBoneClassifier
    {
        private static readonly string[] FingerNames = { "thumb", "index", "middle", "ring", "pinky", "pinkie", "little" };
        private static readonly string[] FingerIdOrder = { "thumb", "index", "middle", "ring", "pinky" };

        private static readonly HashSet<string> PrefixTokens = new(StringComparer.Ordinal)
        {
            "mixamorig", "mixamo", "def", "org", "mch", "tgt", "skel", "rig", "valvebiped", "bip", "j", "b", "m", "avg", "armature"
        };

        private static readonly HashSet<string> ControlTokens = new(StringComparer.Ordinal)
        {
            "ik", "fk", "pole", "ctrl", "control", "controller", "cns", "proxy", "ref", "tweak", "target", "dupli",
            "master", "traj", "mch", "org", "offset", "visual", "pivot", "handle"
        };

        private static readonly HashSet<string> TwistTokens = new(StringComparer.Ordinal)
        {
            "twist", "roll", "notwist", "corrective", "correct", "helper", "proc", "procedural"
        };

        private static readonly HashSet<string> FaceTokens = new(StringComparer.Ordinal)
        {
            "jaw", "eye", "eyes", "eyeball", "eyelid", "eyelids", "lid", "lids", "brow", "eyebrow", "cheek", "lip", "lips",
            "mouth", "tongue", "tong", "teeth", "tooth", "nose", "nostril", "chin", "forehead", "face", "facial", "pupil",
            "iris", "eyelash", "lash", "temple", "mustache", "beard"
        };

        private static readonly string[] ClothingKeys =
        {
            "cloth", "cape", "cloak", "skirt", "dress", "coat", "scarf", "ribbon", "tie", "hair", "bang", "bangs", "ponytail",
            "braid", "sleeve", "belt", "strap", "flap", "tassel", "fringe", "jiggle", "phys", "hood", "robe", "tabard",
            "kilt", "sash", "lace", "pigtail", "twintail", "bun", "curtain", "banner", "flag"
        };

        private static readonly string[] AccessoryKeys =
        {
            "tail", "wing", "wings", "ear", "ears", "horn", "horns", "antenna", "tentacle", "weapon", "gun", "sword", "prop",
            "bag", "backpack", "quiver", "staff", "shield", "halo", "fin", "feather", "bow", "arrow", "knife", "holster",
            "cannon", "claw", "whisker", "beak", "mane"
        };

        // ---------------------------------------------------------------------------------------------------------
        // Public API
        // ---------------------------------------------------------------------------------------------------------

        public static RigAnalysis Analyze(IReadOnlyList<string> names, IReadOnlyList<int> parents)
            => Analyze(names, parents, familyOverride: null, weightedBones: null, boneWorldPositions: null);

        public static RigAnalysis Analyze(
            IReadOnlyList<string> names,
            IReadOnlyList<int> parents,
            RigFamily? familyOverride = null,
            ISet<int> weightedBones = null,
            IReadOnlyList<Godot.Vector3> boneWorldPositions = null)
        {
            int n = names.Count;
            var result = new RigAnalysis { Bones = new BoneClassification[n] };
            if (n == 0) return result;

            // Depth
            var depth = new int[n];
            for (int i = 0; i < n; i++)
            {
                int d = 0, p = parents[i], guard = 0;
                while (p >= 0 && p < n && guard++ < 512) { d++; p = parents[p]; }
                depth[i] = d;
            }

            // glTF export index suffix detection ("_0108", "_03"): only when it is pervasive and unique.
            bool stripGltfSuffix = DetectGltfIndexSuffix(names);

            // Parse every bone
            var parsed = new Parsed[n];
            for (int i = 0; i < n; i++) parsed[i] = Parse(names[i], stripGltfSuffix);

            result.SymmetryScore = ComputeSymmetryScore(parsed, boneWorldPositions);
            result.HierarchyShapeScore = ComputeHierarchyShapeScore(n, parents, depth);
            result.DetectedFamily = ScoreAndDetectFamily(names, parsed, result.SymmetryScore, result.HierarchyShapeScore, result.FamilyScores, out result.Confidence);
            result.Family = (familyOverride.HasValue && familyOverride.Value != RigFamily.Unknown)
                ? familyOverride.Value
                : result.DetectedFamily;

            // fingers 0-based? (any finger bone with number 0)
            bool fingerZeroBased = parsed.Any(p => p.FingerName != null && p.Nums.Count > 0 && p.Nums[0] == 0 && p.FingerDigits == null);

            bool hasRigifySpine001 = result.Family == RigFamily.Rigify &&
                                     parsed.Any(p => p.Tokens.Contains("spine") && p.Nums.Count > 0 && p.Nums[0] == 1);

            var cands = new List<Candidate>();

            // Precompute anatomical foot/hand subtree membership so articulated toe bones
            // (e.g. Auto-Rig Pro C_TOES_INDEX2.R) can never steal hand finger slots (C_INDEX2.R),
            // and hand fingers can never steal foot/toe slots.
            var isFootSubtree = new bool[n];
            var isHandSubtree = new bool[n];
            for (int i = 0; i < n; i++)
            {
                int cur = i, guard = 0;
                while (cur >= 0 && cur < n && guard++ < 256)
                {
                    var ct = parsed[cur].Tokens;
                    if (HasAny(ct, "toe", "toes", "foot", "ankle", "heel", "sole", "ball"))
                    {
                        isFootSubtree[i] = true;
                        break;
                    }
                    if (HasAny(ct, "hand", "wrist", "palm", "forearm"))
                    {
                        isHandSubtree[i] = true;
                        break;
                    }
                    cur = parents[cur];
                }
            }

            for (int i = 0; i < n; i++)
            {
                var p = parsed[i];
                var bc = new BoneClassification
                {
                    Index = i,
                    Name = names[i],
                    Parent = parents[i],
                    Depth = depth[i],
                    Side = p.Side
                };
                if (bc.Side == 'C' && boneWorldPositions != null && i < boneWorldPositions.Count)
                {
                    // Spatial side hint only when tokens did not resolve side and position is clearly lateral
                    float x = boneWorldPositions[i].X;
                    if (Math.Abs(x) > 0.12f && p.Tokens.Any(tok => tok is "arm" or "upperarm" or "forearm" or "hand" or "wrist" or "shoulder" or "clavicle" or "thigh" or "calf" or "shin" or "foot" or "ankle" or "toe"))
                    {
                        // Note: cannot determine facing universally without hero frame, so only record in reason if needed
                    }
                }
                result.Bones[i] = bc;
                bool isWeighted = weightedBones != null && weightedBones.Contains(i);
                Classify(bc, p, result.Family, fingerZeroBased, hasRigifySpine001, isWeighted, isFootSubtree[i], isHandSubtree[i], cands, depth);
            }

            // Second pass: any bone under a facial root (e.g. ORG-face or head facial rig) stays BoneKind.Face
            // so Rigify facial rigs don't flood extra cloth/physics chains.
            var isFaceBranch = new bool[n];
            for (int i = 0; i < n; i++)
            {
                int cur = i, guard = 0;
                while (cur >= 0 && cur < n && guard++ < 256)
                {
                    if (result.Bones[cur].Kind == BoneKind.Face ||
                        (parsed[cur].HasOrgMchPrefix && parsed[cur].Tokens.Contains("face")))
                    {
                        isFaceBranch[i] = true;
                        break;
                    }
                    if (result.Bones[cur].Kind == BoneKind.Core && result.Bones[cur].Canonical == "head")
                        break;
                    cur = parents[cur];
                }
            }
            for (int i = 0; i < n; i++)
            {
                if (isFaceBranch[i] && result.Bones[i].Kind != BoneKind.Core && result.Bones[i].Kind != BoneKind.Face)
                {
                    result.Bones[i].Kind = BoneKind.Face;
                    result.Bones[i].Reason = "facial rig subtree";
                    result.Bones[i].Canonical = null;
                    cands.RemoveAll(c => c.Index == i);
                }
            }

            DisambiguateArticulatedLegs(result, parsed, parents, cands);
            ResolveCandidates(result, parsed, cands);
            AssignCategories(result);
            return result;
        }

        /// <summary>
        /// Computes the set of source skeleton bone indices that directly carry non-zero skin weights
        /// across any mesh surface (supports both 4-bone and 8-bone Godot surface formats).
        /// </summary>
        public static HashSet<int> ComputeWeightedBoneIndices(Godot.Skeleton3D skeleton, IReadOnlyList<Godot.MeshInstance3D> meshes)
        {
            var weighted = new HashSet<int>();
            if (skeleton == null || meshes == null) return weighted;

            int boneCount = skeleton.GetBoneCount();
            foreach (var mi in meshes)
            {
                if (mi?.Mesh == null) continue;

                Dictionary<int, int> bindToSkel = null;
                if (mi.Skin != null)
                {
                    bindToSkel = new Dictionary<int, int>();
                    int binds = mi.Skin.GetBindCount();
                    for (int b = 0; b < binds; b++)
                    {
                        int sIdx = mi.Skin.GetBindBone(b);
                        if (sIdx < 0 || sIdx >= boneCount)
                        {
                            string bName = mi.Skin.GetBindName(b);
                            if (!string.IsNullOrEmpty(bName)) sIdx = skeleton.FindBone(bName);
                        }
                        if (sIdx >= 0 && sIdx < boneCount) bindToSkel[b] = sIdx;
                    }
                }

                int surfCount = mi.Mesh.GetSurfaceCount();
                for (int s = 0; s < surfCount; s++)
                {
                    var arrays = mi.Mesh.SurfaceGetArrays(s);
                    if (arrays == null || arrays.Count <= (int)Godot.Mesh.ArrayType.Weights) continue;
                    var vVar = arrays[(int)Godot.Mesh.ArrayType.Vertex];
                    var bVar = arrays[(int)Godot.Mesh.ArrayType.Bones];
                    var wVar = arrays[(int)Godot.Mesh.ArrayType.Weights];
                    if (vVar.VariantType == Godot.Variant.Type.Nil || bVar.VariantType == Godot.Variant.Type.Nil || wVar.VariantType == Godot.Variant.Type.Nil)
                        continue;

                    var verts = vVar.AsVector3Array();
                    int[] bones = bVar.AsInt32Array();
                    float[] weights = wVar.AsFloat32Array();
                    if (verts == null || bones == null || weights == null || verts.Length == 0) continue;

                    int stride = Math.Max(4, bones.Length / verts.Length);
                    if (stride * verts.Length > bones.Length || stride * verts.Length > weights.Length) stride = 4;

                    int total = Math.Min(bones.Length, weights.Length);
                    for (int k = 0; k < total; k++)
                    {
                        if (weights[k] <= 1e-5f) continue;
                        int rawIdx = bones[k];
                        int skelIdx = bindToSkel != null
                            ? (bindToSkel.TryGetValue(rawIdx, out int mapped) ? mapped : -1)
                            : rawIdx;
                        if (skelIdx >= 0 && skelIdx < boneCount)
                            weighted.Add(skelIdx);
                    }
                }
            }
            return weighted;
        }

        /// <summary>
        /// Implements "Only export bones that have weights, or whose descendants do" pruning.
        /// Control/helper bones (e.g. ARP *_ik, *_pole, *_cns, *_offset, *_proxy, *_ref, unweighted c_*)
        /// are dropped unless they directly carry skin weights.
        /// </summary>
        public static bool[] ComputeExportKeepMask(
            RigAnalysis analysis,
            ISet<int> weightedBones,
            bool keepUnweightedBones,
            ISet<int> disabledBones = null)
        {
            int n = analysis?.Bones?.Length ?? 0;
            var keep = new bool[n];
            if (n == 0) return keep;

            var children = new List<int>[n];
            for (int i = 0; i < n; i++) children[i] = new List<int>();
            for (int i = 0; i < n; i++)
            {
                int p = analysis.Bones[i].Parent;
                if (p >= 0 && p < n) children[p].Add(i);
            }

            // Compute post-order for weighted subtree propagation
            var order = new List<int>(n);
            var stack = new Stack<int>();
            for (int i = 0; i < n; i++)
            {
                if (analysis.Bones[i].Parent >= 0 && analysis.Bones[i].Parent < n) continue;
                stack.Push(i);
                while (stack.Count > 0)
                {
                    int cur = stack.Pop();
                    order.Add(cur);
                    foreach (int c in children[cur]) stack.Push(c);
                }
            }

            var hasWeightedDescendant = new bool[n];
            for (int k = order.Count - 1; k >= 0; k--)
            {
                int i = order[k];
                bool w = weightedBones != null && weightedBones.Contains(i) && (disabledBones == null || !disabledBones.Contains(i));
                if (!w)
                {
                    foreach (int c in children[i])
                    {
                        if (hasWeightedDescendant[c]) { w = true; break; }
                    }
                }
                hasWeightedDescendant[i] = w;
            }

            for (int i = 0; i < n; i++)
            {
                if (disabledBones != null && disabledBones.Contains(i))
                {
                    keep[i] = false;
                    continue;
                }

                var bc = analysis.Bones[i];
                bool directWeight = weightedBones != null && weightedBones.Contains(i);

                if (bc.Kind == BoneKind.Control || bc.Kind == BoneKind.Helper)
                {
                    // Drop control/IK/pole/cns/offset/proxy/ref/unweighted c_*; keep only if directly weighted
                    keep[i] = directWeight;
                    continue;
                }

                if (bc.Kind == BoneKind.Core || bc.Kind == BoneKind.Root)
                {
                    keep[i] = keepUnweightedBones || hasWeightedDescendant[i] || !string.IsNullOrEmpty(bc.Canonical);
                    continue;
                }

                keep[i] = keepUnweightedBones || hasWeightedDescendant[i];
            }

            return keep;
        }

        /// <summary>
        /// Resolves the best mapped hero skeleton bone name to serve as parent anchor for an extra chain.
        /// </summary>
        public static string ResolveAnchorHeroBoneName(
            RigAnalysis analysis,
            ExtraBoneChain chain,
            IReadOnlyDictionary<int, int> srcToTarget,
            Godot.Skeleton3D targetSkeleton)
        {
            if (analysis == null || chain == null || targetSkeleton == null) return "spine_3";
            int cur = chain.RootIndex >= 0 && chain.RootIndex < analysis.Bones.Length
                ? analysis.Bones[chain.RootIndex].Parent
                : -1;
            int guard = 0;
            while (cur >= 0 && cur < analysis.Bones.Length && guard++ < 256)
            {
                if (srcToTarget != null && srcToTarget.TryGetValue(cur, out int tgtIdx) && tgtIdx >= 0 && tgtIdx < targetSkeleton.GetBoneCount())
                {
                    return targetSkeleton.GetBoneName(tgtIdx);
                }
                string canonical = analysis.Bones[cur].Canonical;
                if (!string.IsNullOrEmpty(canonical) && targetSkeleton.FindBone(canonical) >= 0)
                {
                    return canonical;
                }
                cur = analysis.Bones[cur].Parent;
            }

            // Category fallback
            if (chain.Category == CatClothing)
            {
                string low = chain.RootName.ToLowerInvariant();
                if (low.Contains("skirt") || low.Contains("kilt") || low.Contains("sash") || low.Contains("belt") || low.Contains("coat"))
                    return targetSkeleton.FindBone("pelvis") >= 0 ? "pelvis" : "spine_0";
                if (low.Contains("hair") || low.Contains("bang") || low.Contains("ponytail") || low.Contains("braid") || low.Contains("hood"))
                    return targetSkeleton.FindBone("head") >= 0 ? "head" : "spine_3";
            }
            if (targetSkeleton.FindBone("spine_3") >= 0) return "spine_3";
            if (targetSkeleton.FindBone("pelvis") >= 0) return "pelvis";
            return targetSkeleton.GetBoneCount() > 0 ? targetSkeleton.GetBoneName(0) : "pelvis";
        }

        /// <summary>
        /// Finds connected groups of "extra" bones (clothing, hair, tail, accessories, weighted controls) that are not part
        /// of the humanoid mapping and carry weights (or have weighted descendants).
        /// </summary>
        public static List<ExtraBoneChain> FindExtraChains(RigAnalysis analysis, ISet<int> mappedSources, ISet<int> weightedBones)
        {
            var chains = new List<ExtraBoneChain>();
            int n = analysis.Bones.Length;
            if (n == 0) return chains;

            var children = new List<int>[n];
            for (int i = 0; i < n; i++) children[i] = new List<int>();
            for (int i = 0; i < n; i++)
            {
                int p = analysis.Bones[i].Parent;
                if (p >= 0 && p < n) children[p].Add(i);
            }

            // hasWeightedSubtree via iterative post-order
            var hasW = new bool[n];
            var order = new List<int>(n);
            var stack = new Stack<int>();
            for (int i = 0; i < n; i++)
            {
                if (analysis.Bones[i].Parent >= 0 && analysis.Bones[i].Parent < n) continue;
                stack.Push(i);
                while (stack.Count > 0)
                {
                    int cur = stack.Pop();
                    order.Add(cur);
                    foreach (int c in children[cur]) stack.Push(c);
                }
            }
            for (int k = order.Count - 1; k >= 0; k--)
            {
                int i = order[k];
                bool w = weightedBones != null && weightedBones.Contains(i);
                if (!w) foreach (int c in children[i]) { if (hasW[c]) { w = true; break; } }
                hasW[i] = w;
            }

            bool IsCandidate(int i)
            {
                if (mappedSources != null && mappedSources.Contains(i)) return false;
                if (!hasW[i]) return false;
                var k = analysis.Bones[i].Kind;
                return k == BoneKind.Clothing || k == BoneKind.Accessory || k == BoneKind.Unknown || k == BoneKind.Control;
            }

            var visited = new bool[n];
            bool IsWeightedDirect(int i) => weightedBones != null && weightedBones.Contains(i);

            void CollectSubChains(int startIdx)
            {
                if (startIdx < 0 || startIdx >= n || visited[startIdx] || !IsCandidate(startIdx)) return;

                // Walk linearly as long as there is a single candidate child.
                // When a candidate node forks into >= 2 candidate children (e.g. HairRoot, Coat TailsRoot, BreastRoot),
                // split each branch into its own linear ExtraBoneChain so jiggle chains stay unbranched and toggleable.
                var chain = new ExtraBoneChain { RootIndex = startIdx, RootName = analysis.Bones[startIdx].Name };
                int cur = startIdx;
                while (true)
                {
                    if (visited[cur]) break;
                    visited[cur] = true;
                    chain.BoneIndices.Add(cur);

                    var candKids = new List<int>();
                    foreach (int child in children[cur])
                    {
                        if (!visited[child] && IsCandidate(child))
                            candKids.Add(child);
                    }

                    if (candKids.Count == 1)
                    {
                        cur = candKids[0];
                        continue;
                    }

                    if (candKids.Count >= 2)
                    {
                        // Fork node: if this hub had only itself in the chain and has no direct vertex weights
                        // (e.g. unweighted BreastRoot / Coat TailsRoot), omit the empty container chain.
                        bool omitEmptyHub = chain.BoneIndices.Count == 1 && !IsWeightedDirect(cur);
                        if (!omitEmptyHub)
                        {
                            if (chain.BoneIndices.Count == 1)
                                chain.IsMultiBranchHub = true;
                            chain.Category = DetectChainCategory(chain, analysis);
                            chains.Add(chain);
                        }

                        foreach (int branchRoot in candKids)
                            CollectSubChains(branchRoot);
                        return;
                    }

                    break; // leaf
                }

                if (chain.BoneIndices.Count > 0)
                {
                    chain.Category = DetectChainCategory(chain, analysis);
                    chains.Add(chain);
                }
            }

            for (int k = 0; k < order.Count; k++)
            {
                int i = order[k];
                if (visited[i] || !IsCandidate(i)) continue;
                int p = analysis.Bones[i].Parent;
                if (p >= 0 && p < n && IsCandidate(p)) continue; // not a chain root

                CollectSubChains(i);
            }
            return chains;
        }

        // ---------------------------------------------------------------------------------------------------------
        // Parsing
        // ---------------------------------------------------------------------------------------------------------

        private sealed class Parsed
        {
            public string Raw = "";
            public string RawLower = "";
            public List<string> Tokens = new();
            public List<int> Nums = new();
            public List<string> NumStrings = new();
            public char Side = 'C';
            public bool LeadC;          // ARP style "c_" prefix
            public bool LeadCc;         // ARP custom "cc_" prefix
            public bool HasDefPrefix;   // Rigify DEF-
            public bool HasOrgMchPrefix;
            public bool IsLeaf;         // *_end, *_tip, *_nub, *_leaf, headtop
            public string FingerName;   // canonical finger name or null
            public string FingerDigits; // digits attached to a bare "finger" token (Biped)
            public bool ArpCenterSuffix;// ".x"
        }

        private static readonly Regex GltfSuffixRegex = new(@"_\d{2,4}$", RegexOptions.Compiled);

        private static bool DetectGltfIndexSuffix(IReadOnlyList<string> names)
        {
            if (names.Count < 8) return false;
            int match = 0;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            bool unique = true;
            foreach (var nm in names)
            {
                var m = GltfSuffixRegex.Match(nm);
                if (!m.Success) continue;
                match++;
                if (!seen.Add(m.Value)) unique = false;
            }
            return unique && match >= names.Count * 0.7;
        }

        private static string StripNamespace(string raw)
        {
            string name = raw.Trim();
            foreach (char sep in new[] { ':', '|', '/' })
            {
                int idx = name.LastIndexOf(sep);
                if (idx >= 0 && idx < name.Length - 1) name = name[(idx + 1)..];
            }
            return name;
        }

        private static List<string> SplitTokens(string name)
        {
            var sb = new StringBuilder(name.Length + 8);
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (c == '.' || c == '_' || c == '-' || c == ' ' || c == ':' || c == '|' || c == '/')
                {
                    sb.Append('_');
                    continue;
                }
                if (i > 0)
                {
                    char prev = name[i - 1];
                    bool prevSep = prev == '.' || prev == '_' || prev == '-' || prev == ' ' || prev == ':' || prev == '|' || prev == '/';
                    if (!prevSep)
                    {
                        if (char.IsUpper(c) && char.IsLower(prev)) sb.Append('_');
                        else if (char.IsDigit(c) != char.IsDigit(prev) && char.IsLetterOrDigit(prev) && char.IsLetterOrDigit(c)) sb.Append('_');
                    }
                }
                sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString().Split('_', StringSplitOptions.RemoveEmptyEntries).ToList();
        }

        private static Parsed Parse(string raw, bool stripGltfSuffix)
        {
            var p = new Parsed { Raw = raw, RawLower = raw.ToLowerInvariant() };
            string name = StripNamespace(raw);
            if (stripGltfSuffix) name = GltfSuffixRegex.Replace(name, "");

            string lower = name.ToLowerInvariant();
            p.LeadC = lower.StartsWith("c_") || lower.StartsWith("c.");
            p.LeadCc = lower.StartsWith("cc_") && !lower.StartsWith("cc_base");
            p.ArpCenterSuffix = lower.EndsWith(".x");
            p.HasOrgMchPrefix = lower.StartsWith("org-") || lower.StartsWith("org_") || lower.StartsWith("mch-") || lower.StartsWith("mch_");
            p.HasDefPrefix = lower.StartsWith("def-") || lower.StartsWith("def_");

            var tokens = SplitTokens(name);

            // Drop leading prefix tokens. "bip" is followed by its numeric id ("Bip01") which is dropped too.
            int start = 0;
            bool droppedBip = false;
            while (start < tokens.Count)
            {
                string t = tokens[start];
                if (t == "cc" && start + 1 < tokens.Count && tokens[start + 1] == "base") { start += 2; continue; }
                if (PrefixTokens.Contains(t) && start < tokens.Count - 1)
                {
                    if (t == "bip") droppedBip = true;
                    start++;
                    if (t == "bip" && start < tokens.Count && tokens[start].All(char.IsDigit) && start < tokens.Count - 1) start++;
                    continue;
                }
                break;
            }
            if (droppedBip && start < tokens.Count - 1 && tokens[start] == "c") start++; // VRM "J_Bip_C_Hips"
            if (start < tokens.Count - 1 && tokens[start] == "f" && FingerNames.Contains(tokens[start + 1])) start++; // Rigify f_index
            if (start > 0) tokens = tokens.Skip(start).ToList();

            bool hasL = false, hasR = false;
            var text = new List<string>();
            for (int i = 0; i < tokens.Count; i++)
            {
                string t = tokens[i];
                if (t.All(char.IsDigit))
                {
                    p.NumStrings.Add(t);
                    p.Nums.Add(int.TryParse(t, out int v) ? v : 0);
                    continue;
                }
                if (t == "l" || t == "left") { hasL = true; continue; }
                if (t == "r" || t == "right") { hasR = true; continue; }
                text.Add(t);
            }
            p.Side = hasL && !hasR ? 'L' : (hasR && !hasL ? 'R' : 'C');

            // ARP style trailing "x" (center) token
            if (text.Count > 1 && text[^1] == "x") text.RemoveAt(text.Count - 1);

            p.Tokens = text;

            // leaf markers
            if (text.Count > 0 && (text[^1] == "end" || text[^1] == "tip" || text[^1] == "nub" || text[^1] == "leaf")) p.IsLeaf = true;
            if (text.Contains("head") && text.Contains("top")) p.IsLeaf = true;

            // finger names
            foreach (var t in text)
            {
                if (FingerNames.Contains(t))
                {
                    p.FingerName = (t == "pinkie" || t == "little") ? "pinky" : t;
                    break;
                }
            }
            if (p.FingerName == null && text.Contains("finger") && p.NumStrings.Count > 0)
            {
                string digits = p.NumStrings[0];
                p.FingerDigits = digits;
                int id = digits[0] - '0';
                if (id >= 0 && id < FingerIdOrder.Length) p.FingerName = FingerIdOrder[id];
            }
            return p;
        }

        private static double ComputeSymmetryScore(Parsed[] parsed, IReadOnlyList<Godot.Vector3> positions)
        {
            int left = 0, right = 0;
            foreach (var p in parsed)
            {
                if (p.Side == 'L') left++;
                else if (p.Side == 'R') right++;
            }
            double tokenSym = (left + right) > 0
                ? 1.0 - Math.Abs(left - right) / (double)(left + right)
                : 0.0;
            if (positions == null || positions.Count != parsed.Length || positions.Count == 0)
                return tokenSym;

            int posPairs = 0;
            for (int i = 0; i < parsed.Length; i++)
            {
                if (parsed[i].Side != 'L') continue;
                var lp = positions[i];
                if (Math.Abs(lp.X) < 0.03f) continue;
                for (int j = 0; j < parsed.Length; j++)
                {
                    if (parsed[j].Side != 'R') continue;
                    var rp = positions[j];
                    if (Math.Abs(lp.X + rp.X) < 0.08f && Math.Abs(lp.Y - rp.Y) < 0.08f && Math.Abs(lp.Z - rp.Z) < 0.08f)
                    {
                        posPairs++;
                        break;
                    }
                }
            }
            double spatialSym = left > 0 ? Math.Min(1.0, posPairs / (double)left) : tokenSym;
            return 0.5 * tokenSym + 0.5 * spatialSym;
        }

        private static double ComputeHierarchyShapeScore(int n, IReadOnlyList<int> parents, int[] depth)
        {
            if (n < 6) return 0.1;
            var childCounts = new int[n];
            int maxDepth = 0;
            for (int i = 0; i < n; i++)
            {
                if (depth[i] > maxDepth) maxDepth = depth[i];
                int p = parents[i];
                if (p >= 0 && p < n) childCounts[p]++;
            }
            int branchHubs = childCounts.Count(c => c >= 3); // pelvis (spine+legs) and upper chest (neck+arms)
            double depthScore = Math.Min(1.0, maxDepth / 8.0);
            double hubScore = Math.Min(1.0, branchHubs / 2.0);
            return 0.5 * depthScore + 0.5 * hubScore;
        }

        private static RigFamily ScoreAndDetectFamily(
            IReadOnlyList<string> names,
            Parsed[] parsed,
            double symmetryScore,
            double hierarchyScore,
            Dictionary<RigFamily, double> scores,
            out double confidence)
        {
            scores.Clear();
            int n = Math.Max(1, names.Count);
            int arpC = 0, arpX = 0, arpBody = 0, defBody = 0, defFace = 0, mixamo = 0, ue = 0, unity = 0, vrm = 0, bip = 0, valve = 0;
            int rbxR15 = 0, rbxR6 = 0;
            bool hasHumanoidRootPart = false, hasLowerTorso = false, hasUpperTorso = false, hasPlainTorso = false;
            bool hasTraj = false;
            foreach (var p in parsed)
            {
                string l = p.RawLower;
                bool isFaceName = p.Tokens.Any(tok => FaceTokens.Contains(tok)) || l.Contains("face");
                if (l.StartsWith("c_")) arpC++;
                if (l.EndsWith(".x")) arpX++;
                if (l is "root.x" or "spine_01.x" or "spine_02.x" or "spine_03.x" or "neck.x" or "head.x" ||
                    ((l.EndsWith(".l") || l.EndsWith(".r")) &&
                     (l.Contains("thigh_stretch") || l.Contains("leg_stretch") || l.Contains("arm_stretch") || l.Contains("forearm_stretch") || l.Contains("shoulder.") || l.Contains("foot."))))
                {
                    arpBody++;
                }
                if (l == "c_traj" || l == "c_pos" || l.StartsWith("c_root_master")) hasTraj = true;
                if (p.HasDefPrefix)
                {
                    if (isFaceName) defFace++;
                    else defBody++;
                }
                if (l.Contains("mixamorig")) mixamo++;
                if (l.Contains("j_bip_")) vrm++;
                if (l.Contains("valvebiped")) valve++;
                if (l.StartsWith("bip01") || l.StartsWith("bip001") || l.StartsWith("bip ")) bip++;
                if (l == "upperarm_l" || l == "thigh_l" || l == "lowerarm_r" || l == "calf_r" || l == "ik_foot_root") ue++;
                if (l == "humanoidrootpart") hasHumanoidRootPart = true;
                if (l == "lowertorso" || l == "lower_torso") { hasLowerTorso = true; rbxR15 += 2; }
                if (l == "uppertorso" || l == "upper_torso") { hasUpperTorso = true; rbxR15 += 2; }
                if (l == "torso") hasPlainTorso = true;
                if (l is "leftupperarm" or "rightupperarm" or "leftlowerarm" or "rightlowerarm" or "leftupperleg" or "rightupperleg" or "leftlowerleg" or "rightlowerleg")
                {
                    rbxR15++;
                    unity++;
                }
                else if (l == "upperchest")
                {
                    unity++;
                }
                if (l is "left arm" or "right arm" or "left leg" or "right leg") rbxR6++;
            }

            double shapeBonus = 0.15 * symmetryScore + 0.10 * hierarchyScore;
            bool isRobloxR15Signature = (hasLowerTorso && hasUpperTorso) || (hasHumanoidRootPart && (hasLowerTorso || hasUpperTorso));
            bool isRobloxR6Signature = (hasHumanoidRootPart || hasPlainTorso) && !hasLowerTorso && !hasUpperTorso && rbxR6 >= 3;
            double rbxR15Score = isRobloxR15Signature
                ? Math.Min(1.0, (hasHumanoidRootPart ? 0.35 : 0.0) + 0.45 + Math.Min(0.20, rbxR15 * 0.03))
                : 0.0;
            double rbxR6Score = isRobloxR6Signature
                ? Math.Min(1.0, (hasHumanoidRootPart ? 0.35 : 0.0) + (hasPlainTorso ? 0.25 : 0.0) + Math.Min(0.35, rbxR6 * 0.10))
                : 0.0;
            // Suppress UnityHumanoid false positive when Roblox R15 markers (HumanoidRootPart / LowerTorso / UpperTorso) are present
            if (isRobloxR15Signature) unity = 0;

            double arpScore = (hasTraj ? 0.45 : 0.0)
                            + Math.Min(0.75, arpBody * 0.12)
                            + Math.Min(0.35, arpC * 0.05)
                            + Math.Min(0.25, arpX * 0.08)
                            + ((arpBody + arpC + arpX > 0) ? shapeBonus * 0.5 : 0.0);
            // Weight body/limb DEF-* bones strongly; only give facial DEF-* minor credit if no other rig owns the body.
            double effectiveDef = defBody + Math.Min(4.0, defFace * 0.15);
            scores[RigFamily.AutoRigPro] = Math.Min(1.0, arpScore);
            scores[RigFamily.Rigify] = Math.Min(1.0, Math.Min(0.85, effectiveDef / 10.0) + (effectiveDef > 0 ? shapeBonus * 0.6 : 0));
            scores[RigFamily.Mixamo] = Math.Min(1.0, Math.Min(0.85, mixamo / 12.0) + (mixamo > 0 ? shapeBonus * 0.6 : 0));
            scores[RigFamily.VrmVroid] = Math.Min(1.0, Math.Min(0.85, vrm / 10.0) + (vrm > 0 ? shapeBonus * 0.6 : 0));
            scores[RigFamily.ValveBiped] = Math.Min(1.0, Math.Min(0.85, valve / 10.0) + (valve > 0 ? shapeBonus * 0.6 : 0));
            scores[RigFamily.Biped] = Math.Min(1.0, Math.Min(0.80, bip / 10.0) + (bip > 0 ? shapeBonus * 0.6 : 0));
            scores[RigFamily.UnrealMannequin] = Math.Min(1.0, Math.Min(0.75, ue / 4.0) + (ue > 0 ? shapeBonus * 0.6 : 0));
            scores[RigFamily.UnityHumanoid] = Math.Min(1.0, Math.Min(0.75, unity / 4.0) + (unity > 0 ? shapeBonus * 0.6 : 0));
            scores[RigFamily.RobloxR15] = rbxR15Score;
            scores[RigFamily.RobloxR6] = rbxR6Score;
            scores[RigFamily.Generic] = Math.Min(0.65, 0.25 + shapeBonus + (n >= 12 ? 0.15 : 0.0));

            var best = scores.OrderByDescending(kv => kv.Value).First();
            if (best.Value >= 0.45)
            {
                confidence = Math.Round(best.Value, 3);
                return best.Key;
            }
            if (names.Count > 0)
            {
                confidence = Math.Round(scores[RigFamily.Generic], 3);
                return RigFamily.Generic;
            }
            confidence = 0.0;
            return RigFamily.Unknown;
        }

        // ---------------------------------------------------------------------------------------------------------
        // Classification
        // ---------------------------------------------------------------------------------------------------------

        private sealed class Candidate
        {
            public int Index;
            public string CanonicalKey;     // e.g. "arm_upper_L", "pelvis", "spine" (resolved later), "finger_index_0_L"
            public int Priority;            // lower is better
            public int NumPenalty;
            public int Depth;
        }

        private static bool HasAny(List<string> t, params string[] any)
        {
            foreach (var a in any) if (t.Contains(a)) return true;
            return false;
        }

        private static void Classify(
            BoneClassification bc, Parsed p, RigFamily family, bool fingerZeroBased, bool hasRigifySpine001,
            bool isWeighted, bool isFootSubtree, bool isHandSubtree, List<Candidate> cands, int[] depth)
        {
            var t = p.Tokens;
            char side = p.Side;
            string sfx = side == 'L' ? "_L" : side == 'R' ? "_R" : "";

            void Set(BoneKind kind, string reason) { bc.Kind = kind; bc.Reason = reason; }

            if (t.Count == 0) { Set(BoneKind.Unknown, "empty name"); return; }

            // ---- Controls / IK / unused carrier bones -------------------------------------------------------
            bool arp = family == RigFamily.AutoRigPro;
            if (t.Any(tok => tok is "unused" or "unusedbone" or "dummy" or "null"))
            {
                Set(BoneKind.Helper, "unused/carrier bone");
                return;
            }
            if (p.HasOrgMchPrefix) { Set(BoneKind.Control, "Rigify ORG/MCH bone"); return; }
            if (family == RigFamily.Rigify && !p.HasDefPrefix) { Set(BoneKind.Control, "Rigify non-DEF (control) bone"); return; }
            foreach (var tok in t)
            {
                if (ControlTokens.Contains(tok)) { Set(BoneKind.Control, $"control token '{tok}'"); return; }
                if (tok.Length > 2 && tok.StartsWith("ik") && tok != "ikon") { Set(BoneKind.Control, "IK helper"); return; }
            }
            if (arp && t.Count == 1 && t[0] == "root" && p.RawLower.StartsWith("root_ref")) { Set(BoneKind.Control, "ARP reference root"); return; }
            if (arp && (p.LeadC || p.LeadCc))
            {
                // In ARP, c_* bones are controls unless they carry skin weights (or are weighted finger deformers like c_thumb1.l)
                if (!isWeighted && p.FingerName == null)
                {
                    Set(BoneKind.Control, "Auto-Rig Pro control (c_/cc_)");
                    return;
                }
            }

            // ---- Twist / corrective ----------------------------------------------------------------------
            if (HasAny(t, TwistTokens.ToArray())) { Set(BoneKind.Twist, "twist/corrective bone"); return; }

            // ---- Root --------------------------------------------------------------------------------------
            if (p.RawLower == "humanoidrootpart")
            {
                if (family == RigFamily.RobloxR15)
                {
                    Set(BoneKind.Core, "Roblox R15 HumanoidRootPart = pelvis");
                    bc.Canonical = "pelvis";
                    cands.Add(new Candidate { Index = bc.Index, CanonicalKey = "pelvis", Priority = 0, Depth = depth[bc.Index] });
                    return;
                }
                Set(BoneKind.Root, "root");
                return;
            }
            if (t.Count == 1 && (t[0] == "root" || t[0] == "armature" || t[0] == "rootnode" || t[0] == "world" || t[0] == "scene"))
            {
                if (arp && t[0] == "root" && p.ArpCenterSuffix)
                {
                    Set(BoneKind.Core, "ARP root.x = pelvis");
                    cands.Add(new Candidate { Index = bc.Index, CanonicalKey = "pelvis", Priority = 5, Depth = depth[bc.Index] });
                    bc.Canonical = "pelvis";
                    return;
                }
                Set(BoneKind.Root, "root");
                return;
            }

            // ---- Roblox Accessory leaf nodes (e.g. "Accessory (LeftShoeAccessory)", "Accessory (black gym top)") ----
            if (p.RawLower.StartsWith("accessory ") || p.RawLower.StartsWith("accessory(") ||
                t.Contains("accessory") || p.RawLower.EndsWith("accessory"))
            {
                Set(BoneKind.Accessory, "Roblox accessory bone (collapses to parent)");
                return;
            }

            // ---- Face ---------------------------------------------------------------------------------------
            foreach (var tok in t)
            {
                if (FaceTokens.Contains(tok)) { Set(BoneKind.Face, $"face token '{tok}'"); return; }
            }

            // ---- Clothing / Hair / Accessories containing "middle"/"ring"/"little" (e.g. HairMiddle.L.001) -----
            // Guard against colliding with human hand finger names unless "finger"/"hand"/"wrist" is also present.
            if (!HasAny(t, "finger", "hand", "wrist"))
            {
                foreach (var tok in t)
                {
                    foreach (var k in ClothingKeys)
                        if (tok == k || (k.Length >= 4 && tok.StartsWith(k))) { Set(BoneKind.Clothing, $"clothing/hair keyword '{k}'"); return; }
                }
                foreach (var tok in t)
                {
                    foreach (var k in AccessoryKeys)
                        if (tok == k || (k.Length >= 4 && tok.StartsWith(k))) { Set(BoneKind.Accessory, $"accessory keyword '{k}'"); return; }
                }
            }

            // ---- Fingers ------------------------------------------------------------------------------------
            // Strictly exclude foot/toe bones (e.g. Auto-Rig Pro C_TOES_INDEX2.R) so articulated toes never
            // hijack hand finger slots (which have greater tree depth than foot bones and would otherwise lose tie-breaks!).
            bool hasToeOrFootToken = isFootSubtree || HasAny(t, "toe", "toes", "foot", "ankle", "heel", "sole", "ball");
            if (!hasToeOrFootToken && p.FingerName != null && (HasAny(t, FingerNames) || t.Contains("finger")) && side != 'C')
            {
                string joint;
                if (p.IsLeaf) joint = "end";
                else if (HasAny(t, "meta", "metacarpal", "carpal", "knuckle", "knuckles", "base")) joint = "meta";
                else if (t.Contains("proximal")) joint = "0";
                else if (t.Contains("intermediate")) joint = "1";
                else if (t.Contains("distal")) joint = "2";
                else if (p.FingerDigits != null)
                {
                    joint = p.FingerDigits.Length >= 2 ? (p.FingerDigits[1] - '0').ToString() : "0";
                    if (joint == "3") joint = "end";
                }
                else if (p.Nums.Count > 0)
                {
                    int num = p.Nums[0];
                    int j = fingerZeroBased ? num : num - 1;
                    if (j < 0) j = 0;
                    joint = j >= 3 ? "end" : j.ToString();
                }
                else joint = "0";

                string key = $"finger_{p.FingerName}_{joint}{sfx}";
                Set(BoneKind.Finger, $"finger {p.FingerName} joint {joint}");
                bc.Canonical = key;
                cands.Add(new Candidate { Index = bc.Index, CanonicalKey = key, Priority = 0, NumPenalty = 0, Depth = depth[bc.Index] });
                return;
            }
            if (!hasToeOrFootToken && p.FingerName != null && side == 'C' && HasAny(t, FingerNames))
            {
                Set(BoneKind.Finger, "finger without side");
                return;
            }
            if (!hasToeOrFootToken && t.Contains("palm")) { Set(BoneKind.Finger, "palm (collapses to hand)"); return; }

            // ---- Core humanoid roles -------------------------------------------------------------------------
            string role = null;
            int priority = 0;

            bool hasToe = !isHandSubtree && !HasAny(t, "hand", "wrist", "finger", "palm") && HasAny(t, "toe", "toes", "ball");
            bool hasFoot = !isHandSubtree && HasAny(t, "foot", "ankle");
            bool hasLeg = t.Contains("leg");
            bool hasArm = t.Contains("arm");

            if (hasToe && !hasFoot)
            {
                role = p.IsLeaf ? "ball_end" : "ball";
                priority = (HasAny(t, "big", "pinky", "index", "middle", "ring")) ? 3 : 0;
            }
            else if (hasFoot)
            {
                if (t.Contains("sole") || HasAny(t, "heel")) { Set(BoneKind.Helper, "sole/heel helper"); return; }
                role = "ankle";
            }
            else if (HasAny(t, "calf", "shin", "knee", "lowerleg") || (hasLeg && HasAny(t, "lower")))
            {
                role = "leg_lower";
            }
            else if (HasAny(t, "thigh", "upleg", "upperleg") || (hasLeg && HasAny(t, "upper", "up")))
            {
                role = "leg_upper";
            }
            else if (HasAny(t, "forearm", "lowerarm") || (hasArm && HasAny(t, "lower", "fore")) || t.Contains("elbow"))
            {
                role = "arm_lower";
            }
            else if (HasAny(t, "upperarm", "uparm") || (hasArm && HasAny(t, "upper", "up")))
            {
                role = "arm_upper";
            }
            else if (hasLeg && !HasAny(t, "upper", "lower"))
            {
                // Roblox R6 has a single leg bone ("Left Leg" / "Right Leg") with no knee split -> map to leg_upper.
                // Mixamo "LeftLeg" / ARP "leg_stretch" == shin -> leg_lower.
                role = family == RigFamily.RobloxR6 ? "leg_upper" : "leg_lower";
                priority = 1;
            }
            else if (hasArm && !HasAny(t, "hand", "wrist", "shoulder", "clavicle", "collar"))
            {
                role = "arm_upper";
                priority = 1;
            }
            else if (HasAny(t, "hand", "wrist"))
            {
                role = "hand";
            }
            else if (HasAny(t, "clavicle", "shoulder", "collar", "collarbone"))
            {
                role = "clavicle";
            }

            if (role != null)
            {
                if (p.IsLeaf && role != "ball_end") { Set(BoneKind.Helper, "limb tip/leaf"); return; }
                if (side == 'C')
                {
                    // Lateral roles without a side cannot be mapped reliably.
                    Set(BoneKind.Unknown, $"{role} without side");
                    return;
                }
                string key = role + sfx;
                Set(BoneKind.Core, $"{role} {side}");
                bc.Canonical = key;
                cands.Add(new Candidate
                {
                    Index = bc.Index,
                    CanonicalKey = key,
                    Priority = priority,
                    NumPenalty = p.Nums.Count > 0 && p.Nums[0] > 0 ? 1 + p.Nums[0] : 0,
                    Depth = depth[bc.Index]
                });
                return;
            }

            if (t.Contains("neck"))
            {
                if (p.IsLeaf) { Set(BoneKind.Helper, "neck tip"); return; }
                Set(BoneKind.Core, "neck");
                bc.Canonical = "neck_0";
                cands.Add(new Candidate { Index = bc.Index, CanonicalKey = "neck_0", NumPenalty = p.Nums.Count > 0 ? p.Nums[0] : 0, Depth = depth[bc.Index] });
                return;
            }
            if (t.Contains("head"))
            {
                if (p.IsLeaf) { Set(BoneKind.Helper, "head tip"); return; }
                Set(BoneKind.Core, "head");
                bc.Canonical = "head";
                cands.Add(new Candidate { Index = bc.Index, CanonicalKey = "head", NumPenalty = p.Nums.Count > 0 ? p.Nums[0] : 0, Depth = depth[bc.Index] });
                return;
            }
            if (HasAny(t, "pelvis", "hips", "hip") && side == 'C')
            {
                Set(BoneKind.Core, "pelvis");
                bc.Canonical = "pelvis";
                cands.Add(new Candidate { Index = bc.Index, CanonicalKey = "pelvis", Priority = 0, Depth = depth[bc.Index] });
                return;
            }
            if (HasAny(t, "pelvis", "hips", "hip"))
            {
                Set(BoneKind.Twist, "lateral pelvis helper (collapses)");
                return;
            }
            if (HasAny(t, "spine", "chest", "torso", "abdomen", "upperchest"))
            {
                if (p.IsLeaf) { Set(BoneKind.Helper, "spine tip"); return; }
                // Roblox R15: HumanoidRootPart = pelvis, LowerTorso = spine_0, UpperTorso = spine_3
                if (t.Contains("torso") && t.Contains("lower") && side == 'C')
                {
                    if (family == RigFamily.RobloxR15)
                    {
                        Set(BoneKind.Core, "Roblox R15 LowerTorso = spine_0");
                        bc.Canonical = "spine_0";
                        cands.Add(new Candidate { Index = bc.Index, CanonicalKey = "spine_0", Priority = 0, Depth = depth[bc.Index] });
                        return;
                    }
                    Set(BoneKind.Core, "Roblox LowerTorso = pelvis");
                    bc.Canonical = "pelvis";
                    cands.Add(new Candidate { Index = bc.Index, CanonicalKey = "pelvis", Priority = 1, Depth = depth[bc.Index] });
                    return;
                }
                if (t.Contains("torso") && t.Contains("upper") && side == 'C' && family == RigFamily.RobloxR15)
                {
                    Set(BoneKind.Core, "Roblox R15 UpperTorso = spine_3");
                    bc.Canonical = "spine_3";
                    cands.Add(new Candidate { Index = bc.Index, CanonicalKey = "spine_3", Priority = 0, Depth = depth[bc.Index] });
                    return;
                }
                // Roblox R6: single Torso bone acts as pelvis root
                if (family == RigFamily.RobloxR6 && t.Count == 1 && t[0] == "torso" && side == 'C')
                {
                    Set(BoneKind.Core, "Roblox R6 Torso = pelvis");
                    bc.Canonical = "pelvis";
                    cands.Add(new Candidate { Index = bc.Index, CanonicalKey = "pelvis", Priority = 1, Depth = depth[bc.Index] });
                    return;
                }
                int num = p.Nums.Count > 0 ? p.Nums[0] : -1;
                if (family == RigFamily.Rigify && t.Contains("spine"))
                {
                    if (num < 0 && hasRigifySpine001)
                    {
                        Set(BoneKind.Core, "Rigify DEF-spine = pelvis");
                        bc.Canonical = "pelvis";
                        cands.Add(new Candidate { Index = bc.Index, CanonicalKey = "pelvis", Priority = 2, Depth = depth[bc.Index] });
                        return;
                    }
                    if (num == 5)
                    {
                        Set(BoneKind.Core, "Rigify spine.005 = neck");
                        bc.Canonical = "neck_0";
                        cands.Add(new Candidate { Index = bc.Index, CanonicalKey = "neck_0", Priority = 2, Depth = depth[bc.Index] });
                        return;
                    }
                    if (num >= 6)
                    {
                        Set(BoneKind.Core, "Rigify spine.006 = head");
                        bc.Canonical = "head";
                        cands.Add(new Candidate { Index = bc.Index, CanonicalKey = "head", Priority = 2, Depth = depth[bc.Index] });
                        return;
                    }
                }
                Set(BoneKind.Core, "spine");
                bc.Canonical = "spine_?"; // resolved by rank later
                cands.Add(new Candidate { Index = bc.Index, CanonicalKey = "spine", Priority = 0, NumPenalty = Math.Max(0, num), Depth = depth[bc.Index] });
                return;
            }

            // ---- Everything else: extras ---------------------------------------------------------------------
            foreach (var tok in t)
            {
                foreach (var k in ClothingKeys)
                    if (tok == k || (k.Length >= 4 && tok.StartsWith(k))) { Set(BoneKind.Clothing, $"clothing/hair keyword '{k}'"); return; }
            }
            foreach (var tok in t)
            {
                foreach (var k in AccessoryKeys)
                    if (tok == k || (k.Length >= 4 && tok.StartsWith(k))) { Set(BoneKind.Accessory, $"accessory keyword '{k}'"); return; }
            }
            if (p.IsLeaf) { Set(BoneKind.Helper, "leaf/tip"); return; }
            Set(BoneKind.Unknown, "unrecognized");
        }

        private static void DisambiguateArticulatedLegs(RigAnalysis result, Parsed[] parsed, IReadOnlyList<int> parents, List<Candidate> cands)
        {
            int n = result.Bones.Length;
            var hasChild = new bool[n];
            for (int i = 0; i < n; i++)
            {
                int p = parents[i];
                if (p >= 0 && p < n) hasChild[p] = true;
            }

            foreach (string sfx in new[] { "_L", "_R" })
            {
                string upperKey = "leg_upper" + sfx;
                string lowerKey = "leg_lower" + sfx;

                // Find bare "leg" bones (classified as leg_lower with Priority == 1) that have a descendant candidate in leg_lower/ankle
                var legUpperPromotes = new List<Candidate>();
                foreach (var lc in cands)
                {
                    if (!string.Equals(lc.CanonicalKey, lowerKey, StringComparison.OrdinalIgnoreCase)) continue;
                    var p = parsed[lc.Index];
                    if (!p.Tokens.Contains("leg") || HasAny(p.Tokens, "lower", "upper", "up", "calf", "shin", "knee"))
                        continue;

                    // Check if any lower-leg/ankle/foot candidate descends from lc.Index
                    bool hasLegDescendant = false;
                    foreach (var other in cands)
                    {
                        if (other.Index == lc.Index) continue;
                        if (!string.Equals(other.CanonicalKey, lowerKey, StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(other.CanonicalKey, "ankle" + sfx, StringComparison.OrdinalIgnoreCase))
                            continue;

                        int cur = parents[other.Index], guard = 0;
                        while (cur >= 0 && cur < n && guard++ < 64)
                        {
                            if (cur == lc.Index) { hasLegDescendant = true; break; }
                            cur = parents[cur];
                        }
                        if (hasLegDescendant) break;
                    }

                    if (hasLegDescendant)
                        legUpperPromotes.Add(lc);
                }

                if (legUpperPromotes.Count > 0)
                {
                    // Demote childless hip/thigh helper siblings (e.g. Thigh.L with 0 children) below articulated leg_upper
                    foreach (var uc in cands)
                    {
                        if (string.Equals(uc.CanonicalKey, upperKey, StringComparison.OrdinalIgnoreCase) && !hasChild[uc.Index])
                            uc.Priority = Math.Max(uc.Priority, 5);
                    }
                    foreach (var prom in legUpperPromotes)
                    {
                        prom.CanonicalKey = upperKey;
                        prom.Priority = 0;
                        result.Bones[prom.Index].Canonical = upperKey;
                        result.Bones[prom.Index].Reason = "leg_upper (parent of knee/calf)";
                    }
                }
            }
        }

        private static void ResolveCandidates(RigAnalysis result, Parsed[] parsed, List<Candidate> cands)
        {
            var bones = result.Bones;

            // --- spine ranking ---------------------------------------------------------------------------------
            var spine = cands.Where(c => c.CanonicalKey == "spine").OrderBy(c => c.Depth).ThenBy(c => c.NumPenalty).ThenBy(c => c.Index).ToList();
            // limit to chain: if multiple spine candidates share the same depth keep the lowest number
            var chosenSpine = new List<Candidate>();
            int lastDepth = -1;
            foreach (var c in spine)
            {
                if (c.Depth == lastDepth) { Demote(bones[c.Index], "duplicate spine at same depth"); continue; }
                chosenSpine.Add(c);
                lastDepth = c.Depth;
            }
            string[] slots = chosenSpine.Count switch
            {
                0 => Array.Empty<string>(),
                1 => new[] { "spine_3" },
                2 => new[] { "spine_1", "spine_3" },
                3 => new[] { "spine_1", "spine_2", "spine_3" },
                _ => new[] { "spine_0", "spine_1", "spine_2", "spine_3" }
            };
            for (int i = 0; i < chosenSpine.Count; i++)
            {
                if (i < slots.Length)
                {
                    chosenSpine[i].CanonicalKey = slots[i];
                    bones[chosenSpine[i].Index].Canonical = slots[i];
                }
                else Demote(bones[chosenSpine[i].Index], "extra spine bone (collapses)");
            }

            // --- generic best candidate per canonical ------------------------------------------------------------
            var groups = cands.Where(c => c.CanonicalKey != "spine").GroupBy(c => c.CanonicalKey, StringComparer.OrdinalIgnoreCase);
            foreach (var g in groups)
            {
                var best = g.OrderBy(c => c.Priority).ThenBy(c => c.NumPenalty).ThenBy(c => c.Depth).ThenBy(c => c.Index).First();
                result.CanonicalToSource[g.Key] = best.Index;
                foreach (var c in g)
                {
                    if (c.Index == best.Index) continue;
                    Demote(bones[c.Index], $"duplicate of {g.Key} (collapses)");
                }
            }
            foreach (var c in chosenSpine)
            {
                if (bones[c.Index].Canonical == c.CanonicalKey && c.CanonicalKey.StartsWith("spine_", StringComparison.Ordinal))
                    result.CanonicalToSource[c.CanonicalKey] = c.Index;
            }

            // Validate: arm_lower should be a descendant of arm_upper (and leg_lower of leg_upper). Drop incoherent pairs.
            foreach (string sd in new[] { "_L", "_R" })
            {
                ValidateChain(result, "arm_upper" + sd, "arm_lower" + sd);
                ValidateChain(result, "leg_upper" + sd, "leg_lower" + sd);
            }
        }

        private static void ValidateChain(RigAnalysis result, string upperKey, string lowerKey)
        {
            if (!result.CanonicalToSource.TryGetValue(upperKey, out int up) || !result.CanonicalToSource.TryGetValue(lowerKey, out int low)) return;
            int guard = 0;
            int cur = result.Bones[low].Parent;
            while (cur >= 0 && guard++ < 256)
            {
                if (cur == up) return;
                cur = result.Bones[cur].Parent;
            }
            // Not a descendant: keep the upper (more reliable) but demote the lower to avoid scrambled limbs.
            result.CanonicalToSource.Remove(lowerKey);
            Demote(result.Bones[low], $"{lowerKey} is not below {upperKey} in the hierarchy");
        }

        private static void Demote(BoneClassification bc, string reason)
        {
            bc.Canonical = null;
            if (bc.Kind == BoneKind.Core || bc.Kind == BoneKind.Finger) bc.Kind = bc.Kind == BoneKind.Finger ? BoneKind.Finger : BoneKind.Twist;
            bc.Reason = reason;
        }

        // ---------------------------------------------------------------------------------------------------------
        // Categories
        // ---------------------------------------------------------------------------------------------------------

        public const string CatSpineHead = "Spine, Neck & Head";
        public const string CatLeftArm = "Left Arm";
        public const string CatRightArm = "Right Arm";
        public const string CatLeftHand = "Left Hand & Fingers";
        public const string CatRightHand = "Right Hand & Fingers";
        public const string CatLeftLeg = "Left Leg";
        public const string CatRightLeg = "Right Leg";
        public const string CatTwist = "Twist & Corrective";
        public const string CatFace = "Face";
        public const string CatClothing = "Clothing & Hair";
        public const string CatAccessory = "Tail, Ears & Accessories";
        public const string CatControls = "Controls & Helpers";
        public const string CatOther = "Other";

        private static void AssignCategories(RigAnalysis result)
        {
            foreach (var b in result.Bones)
            {
                b.Category = b.Kind switch
                {
                    BoneKind.Control => CatControls,
                    BoneKind.Helper => CatControls,
                    BoneKind.Root => CatControls,
                    BoneKind.Twist => CatTwist,
                    BoneKind.Face => CatFace,
                    BoneKind.Clothing => CatClothing,
                    BoneKind.Accessory => CatAccessory,
                    BoneKind.Finger => b.Side == 'R' ? CatRightHand : CatLeftHand,
                    BoneKind.Core => CategoryForCanonical(b.Canonical, b.Side),
                    _ => CatOther
                };
            }
        }

        private static string CategoryForCanonical(string canonical, char side)
        {
            if (string.IsNullOrEmpty(canonical)) return side == 'L' ? CatLeftArm : side == 'R' ? CatRightArm : CatSpineHead;
            if (canonical.StartsWith("hand_") || canonical.StartsWith("arm_") || canonical.StartsWith("clavicle_"))
                return canonical.EndsWith("_R") ? CatRightArm : CatLeftArm;
            if (canonical.StartsWith("leg_") || canonical.StartsWith("ankle_") || canonical.StartsWith("ball_"))
                return canonical.EndsWith("_R") ? CatRightLeg : CatLeftLeg;
            return CatSpineHead;
        }

        private static string DetectChainCategory(ExtraBoneChain chain, RigAnalysis a)
        {
            int clothing = 0, accessory = 0;
            foreach (int i in chain.BoneIndices)
            {
                if (a.Bones[i].Kind == BoneKind.Clothing) clothing++;
                else if (a.Bones[i].Kind == BoneKind.Accessory) accessory++;
            }
            if (clothing > 0 && clothing >= accessory) return CatClothing;
            if (accessory > 0) return CatAccessory;
            return CatAccessory;
        }

        /// <summary>
        /// Automatically assigns the best Source 2 / preview physics mode (<see cref="SimulationKind"/>)
        /// and balanced spring/collision defaults to each detected chain based on bone count, tokens,
        /// anchor bone, and sibling sheet grouping (Skirts/Coats/Capes/Back Hair -> ProxyCloth;
        /// Tails/Braids/Strands -> ClothChain; 1-bone Bounces -> Jiggle; Hubs/Front Bangs -> Rigid).
        /// </summary>
        public static void ApplyRecommendedPhysicsModes(IList<BoneChainDef> chains, bool enableCustomPhysics = true, bool resetTuningValues = true)
        {
            if (chains == null || chains.Count == 0) return;

            static bool HasAnyToken(string name, params string[] keys)
            {
                if (string.IsNullOrEmpty(name)) return false;
                string lower = name.ToLowerInvariant();
                foreach (var k in keys)
                {
                    if (lower.Contains(k, StringComparison.Ordinal)) return true;
                }
                return false;
            }

            static bool IsSheetClothName(string name) => HasAnyToken(
                name, "skirt", "dress", "coat", "cloak", "cape", "kimono", "apron", "tunic", "robe", "mantle", "gown", "coattail");

            static bool IsStandaloneStrandName(string name) => HasAnyToken(
                name, "ponytail", "pigtail", "twintail", "braid", "tail", "ribbon", "scarf", "tie", "belt", "strap", "cable", "chain");

            static bool IsGluteJiggleName(string name) => HasAnyToken(
                name, "butt", "glute", "booty", "rump", "pelvis_jiggle", "hip_jiggle", "thigh_jiggle", "ass_");

            static bool IsBustJiggleName(string name) => HasAnyToken(
                name, "breast", "boob", "pect", "bust", "oppai", "chest_jiggle");

            static bool IsSoftAnatomyJiggleName(string name) =>
                IsGluteJiggleName(name) || IsBustJiggleName(name) ||
                HasAnyToken(name, "ear", "horn", "ahoge", "pouch", "holster", "tassel", "cheek");

            static bool IsBackHairSheetCandidate(BoneChainDef ch)
            {
                if (ch == null || ch.SourceBoneNames.Count < 2) return false;
                string anchorLow = (ch.AnchorHeroBone ?? "").ToLowerInvariant();
                if (!anchorLow.Contains("head") && !anchorLow.Contains("neck")) return false;
                string nm = $"{ch.Name} {ch.RootSourceBone}";
                if (!HasAnyToken(nm, "hair", "strand", "lock")) return false;
                if (HasAnyToken(nm, "front", "bang", "fringe", "ponytail", "pigtail", "twintail", "braid", "ahoge", "poof")) return false;
                return true;
            }

            // Count sibling strands sharing torso/pelvis sheet groups and multi-strand back-hair sheets
            var sheetGroupCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var backHairGroupCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var ch in chains)
            {
                if (ch == null || ch.SourceBoneNames == null || ch.SourceBoneNames.Count < 2) continue;
                bool isHub = ch.Name.Contains("[Rigid]", StringComparison.OrdinalIgnoreCase) ||
                             ch.Category.Contains("Hub", StringComparison.OrdinalIgnoreCase);
                if (isHub) continue;

                string slot = ch.SlotId ?? "";
                string anchor = (ch.AnchorHeroBone ?? "").ToLowerInvariant();
                string nm = $"{ch.Name} {ch.RootSourceBone}";

                if (IsSoftAnatomyJiggleName(nm)) continue;

                if (IsBackHairSheetCandidate(ch))
                {
                    string hk = $"{slot}::head_hair";
                    backHairGroupCounts[hk] = backHairGroupCounts.GetValueOrDefault(hk) + 1;
                }
                else if (IsSheetClothName(nm) || anchor.Contains("pelvis") || anchor.Contains("spine_0") || anchor.Contains("spine_1"))
                {
                    if (!IsStandaloneStrandName(nm))
                    {
                        string sk = $"{slot}::{anchor}";
                        sheetGroupCounts[sk] = sheetGroupCounts.GetValueOrDefault(sk) + 1;
                    }
                }
            }

            foreach (var ch in chains)
            {
                if (ch == null) continue;
                string nm = $"{ch.Name} {ch.RootSourceBone}";
                string anchor = (ch.AnchorHeroBone ?? "").ToLowerInvariant();
                string slot = ch.SlotId ?? "";
                int boneCount = ch.SourceBoneNames?.Count ?? 0;

                bool isHub = ch.Name.Contains("[Rigid]", StringComparison.OrdinalIgnoreCase) ||
                             ch.Category.Contains("Hub", StringComparison.OrdinalIgnoreCase);
                bool singleHairLeaf = boneCount <= 1 && HasAnyToken(nm, "hair", "bang", "fringe", "poof");

                if (!enableCustomPhysics || isHub || singleHairLeaf)
                {
                    if (ch.Policy != ChainPolicy.Drop)
                        ch.Policy = ChainPolicy.CollapseToNearestCore;
                    ch.Simulation = SimulationKind.Jiggle;
                    continue;
                }

                ch.Policy = ChainPolicy.PreservePhysicsChain;

                int backHairCount = backHairGroupCounts.GetValueOrDefault($"{slot}::head_hair");
                int torsoSheetCount = sheetGroupCounts.GetValueOrDefault($"{slot}::{anchor}");

                if (IsGluteJiggleName(nm))
                {
                    if (resetTuningValues)
                    {
                        PhysicsPresetCatalog.ApplyPresetToChain(ch, PhysicsPresetCatalog.RigidJiggleId, resetMacros: true);
                        ch.AngleLimitDeg = 14.0f;
                        // On 2-bone buttroot -> butt chains, pin the root rigidly so seated/crouched
                        // poses don't double-lever two stacked Rigid FeJiggleBones.
                        if (boneCount >= 2 && ch.SourceBoneNames != null && ch.SourceBoneNames.Count >= 2)
                        {
                            var rootOv = ch.GetOrCreateJointOverride(ch.SourceBoneNames[0]);
                            rootOv.Simulate = false;
                        }
                    }
                    else
                    {
                        ch.Simulation = SimulationKind.Jiggle;
                        ch.PresetId = PhysicsPresetCatalog.RigidJiggleId;
                    }
                }
                else if (IsBustJiggleName(nm))
                {
                    if (resetTuningValues)
                        PhysicsPresetCatalog.ApplyPresetToChain(ch, PhysicsPresetCatalog.RigidJiggleId, resetMacros: true);
                    else
                    {
                        ch.Simulation = SimulationKind.Jiggle;
                        ch.PresetId = PhysicsPresetCatalog.RigidJiggleId;
                    }
                }
                else if (boneCount >= 2 && IsBackHairSheetCandidate(ch) && backHairCount >= 3)
                {
                    string hairPreset = boneCount <= 3 ? PhysicsPresetCatalog.MediumHairId : PhysicsPresetCatalog.LongFlowingHairId;
                    if (resetTuningValues)
                        PhysicsPresetCatalog.ApplyPresetToChain(ch, hairPreset, resetMacros: true);
                    else
                    {
                        ch.Simulation = SimulationKind.ProxyCloth;
                        ch.PresetId = hairPreset;
                    }
                }
                else if (boneCount >= 2 && IsSheetClothName(nm) && (torsoSheetCount >= 2 || HasAnyToken(nm, "skirt", "cape", "cloak", "dress", "coat")))
                {
                    string clothPreset;
                    if (HasAnyToken(nm, "cape", "cloak", "mantle"))
                        clothPreset = PhysicsPresetCatalog.CapeCloakId;
                    else if (HasAnyToken(nm, "coat", "coattail", "trench"))
                        clothPreset = PhysicsPresetCatalog.TrenchcoatLongCoatId;
                    else if (HasAnyToken(nm, "gown", "dress"))
                        clothPreset = PhysicsPresetCatalog.StructuredDressGownId;
                    else
                        clothPreset = PhysicsPresetCatalog.HeavySkirtDressId;

                    if (resetTuningValues)
                        PhysicsPresetCatalog.ApplyPresetToChain(ch, clothPreset, resetMacros: true);
                    else
                    {
                        ch.Simulation = SimulationKind.ProxyCloth;
                        ch.PresetId = clothPreset;
                    }
                }
                else if (boneCount >= 2 && !IsSoftAnatomyJiggleName(nm) && !IsStandaloneStrandName(nm) && (anchor.Contains("pelvis") || anchor.Contains("spine_0")) && torsoSheetCount >= 3)
                {
                    if (resetTuningValues)
                        PhysicsPresetCatalog.ApplyPresetToChain(ch, PhysicsPresetCatalog.HeavySkirtDressId, resetMacros: true);
                    else
                    {
                        ch.Simulation = SimulationKind.ProxyCloth;
                        ch.PresetId = PhysicsPresetCatalog.HeavySkirtDressId;
                    }
                }
                else if (boneCount == 1 || IsSoftAnatomyJiggleName(nm))
                {
                    if (resetTuningValues)
                        PhysicsPresetCatalog.ApplyPresetToChain(ch, PhysicsPresetCatalog.RigidJiggleId, resetMacros: true);
                    else
                    {
                        ch.Simulation = SimulationKind.Jiggle;
                        ch.PresetId = PhysicsPresetCatalog.RigidJiggleId;
                    }
                }
                else
                {
                    string chainPreset;
                    bool isHeadChain = anchor.Contains("head") || anchor.Contains("neck");
                    if (HasAnyToken(nm, "ponytail", "pigtail", "twintail", "braid") && boneCount >= 4)
                        chainPreset = PhysicsPresetCatalog.UltraLongPonytailId;
                    else if (isHeadChain && HasAnyToken(nm, "front", "bang", "fringe", "tuft"))
                        chainPreset = PhysicsPresetCatalog.ShortHairBangsId;
                    else if (isHeadChain && HasAnyToken(nm, "earring", "pendant", "key", "chain", "charm", "necklace"))
                        chainPreset = PhysicsPresetCatalog.ChainsKeysJewelryId;
                    else if (isHeadChain)
                        chainPreset = boneCount <= 3 ? PhysicsPresetCatalog.ShortHairBangsId : PhysicsPresetCatalog.LongFlowingHairId;
                    else if (HasAnyToken(nm, "tail"))
                        chainPreset = PhysicsPresetCatalog.TailFlexibleId;
                    else if (HasAnyToken(nm, "chain", "key", "pendant", "charm", "necklace", "earring"))
                        chainPreset = PhysicsPresetCatalog.ChainsKeysJewelryId;
                    else
                        chainPreset = PhysicsPresetCatalog.ScarfRibbonSashId;

                    if (resetTuningValues)
                        PhysicsPresetCatalog.ApplyPresetToChain(ch, chainPreset, resetMacros: true);
                    else
                    {
                        ch.Simulation = SimulationKind.Cloth;
                        ch.PresetId = chainPreset;
                    }
                }
            }
        }
    }
}

