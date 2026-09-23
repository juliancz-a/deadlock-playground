#pragma warning disable CS0618
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Godot;
using SteamDatabase.ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.IO;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.ResourceTypes.ModelAnimation;

namespace DeadlockPlayground.Tools;

/// <summary>
/// pose loader for Source 2 (Deadlock) models in Godot 4.
/// </summary>
public static class DeadlockAnimLoader
{
    // Source 2 (Z-up, inches) to glTF / Godot (Y-up, meters) root joint rotation conversion:
    // Q(-0.5, -0.5, -0.5, 0.5) corresponds to 90-degree rotations aligning Z-up to Y-up.
    private static readonly System.Numerics.Quaternion SourceToGltfRotation = new(-0.5f, -0.5f, -0.5f, 0.5f);
    private const float InchesToMeters = 0.0254f;

    // Cache of poses per hero node for direct, instantaneous pose application
    public class PoseData
    {
        public string Name { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public Dictionary<string, (Godot.Vector3 Position, Godot.Quaternion Rotation)> BoneTransforms { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public class AnimSequenceInfo
    {
        public string RawName { get; set; } = string.Empty;
        public string CleanName { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Category { get; set; } = "Misc / Other";
        public int FrameCount { get; set; } = 1;
        public float Fps { get; set; } = 30f;
        public float Duration { get; set; } = 0.1f;
        public bool IsLooping { get; set; }
        public ValveResourceFormat.ResourceTypes.ModelAnimation.Animation VrfAnimation { get; set; }
    }

    private static readonly Dictionary<int, Dictionary<string, PoseData>> _heroPosesCache = new();

    /// <summary>
    /// Loads, parses, and populates high-value Frame 0 poses into the hero scene's AnimationPlayer
    /// and internal cache without exploding memory with continuous interpolated vector tracks.
    /// </summary>
    public static int LoadHeroPoses(Node3D heroScene, Model vrfModel, Package package, string vmdlDirectory)
    {
        if (heroScene == null || vrfModel == null || package == null) return 0;

        var skeleton3D = SearchSkeleton(heroScene);
        if (skeleton3D == null)
        {
            GD.Print("[AnimLoader] Warning: No Skeleton3D found on heroScene. Cannot load animation poses.");
            return 0;
        }

        var animPlayer = SearchOrCreateAnimationPlayer(heroScene);

        var fileLoader = new GameFileLoader(package, vmdlDirectory);
        var vrfSkeleton = vrfModel.Skeleton;
        if (vrfSkeleton == null || vrfSkeleton.Bones.Length == 0)
        {
            GD.Print("[AnimLoader] Warning: VRF Model declares no skeleton bones.");
            return 0;
        }

        // 1. Gather all animations (embedded + referenced anim groups + external weapon/included clips)
        List<ValveResourceFormat.ResourceTypes.ModelAnimation.Animation> allAnimations;
        try
        {
            allAnimations = vrfModel.GetAllAnimations(fileLoader).ToList();
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[AnimLoader] Error retrieving model animations: {ex.Message}");
            allAnimations = vrfModel.GetEmbeddedAnimations().Cast<ValveResourceFormat.ResourceTypes.ModelAnimation.Animation>().ToList();
        }

        // Also check any external referenced anim groups not resolved yet
        try
        {
            foreach (string animGroupName in vrfModel.GetReferencedAnimationGroupNames())
            {
                if (string.IsNullOrWhiteSpace(animGroupName)) continue;
                string groupPath = animGroupName.EndsWith("_c") ? animGroupName : animGroupName + "_c";
                var groupEntry = package.FindEntry(groupPath);
                if (groupEntry != null)
                {
                    package.ReadEntry(groupEntry, out byte[] gData);
                    using var gRes = new ValveResourceFormat.Resource();
                    using var gMs = new MemoryStream(gData);
                    gRes.Read(gMs);
                    var loadedGroup = AnimationGroupLoader.LoadAnimationGroup(gRes, fileLoader, vrfSkeleton, vrfModel.FlexControllers);
                    if (loadedGroup != null)
                    {
                        foreach (var a in loadedGroup)
                        {
                            if (!allAnimations.Any(existing => existing.Name.Equals(a.Name, StringComparison.OrdinalIgnoreCase)))
                            {
                                allAnimations.Add(a);
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            GD.Print($"[AnimLoader] Note on secondary AnimGroup scan: {ex.Message}");
        }

        GD.Print($"[AnimLoader] Total raw animations found for model: {allAnimations.Count}");

        // 2. Filter out garbage/heavy debug animations and prioritize high-value posing clips
        var filteredAnims = FilterPoses(allAnimations);
        GD.Print($"[AnimLoader] Selected {filteredAnims.Count} high-value posing clips.");

        // 3. Prepare AnimationPlayer Library
        AnimationLibrary animLibrary;
        if (animPlayer.HasAnimationLibrary(""))
        {
            animLibrary = animPlayer.GetAnimationLibrary("");
        }
        else
        {
            animLibrary = new AnimationLibrary();
            animPlayer.AddAnimationLibrary("", animLibrary);
        }

        // Clear existing library animations to avoid stale entries
        foreach (var existingName in animLibrary.GetAnimationList())
        {
            animLibrary.RemoveAnimation(existingName);
        }

        var heroCache = new Dictionary<string, PoseData>(StringComparer.OrdinalIgnoreCase);
        int heroInstanceId = heroScene.GetInstanceId().GetHashCode();
        _heroPosesCache[heroInstanceId] = heroCache;

        // Path to Skeleton3D relative to AnimationPlayer
        string skeletonNodePath = animPlayer.GetPathTo(skeleton3D);

        // Frame buffer reused for all decodes
        var frame = new Frame(vrfSkeleton, vrfModel.FlexControllers);

        int loadedCount = 0;
        foreach (var anim in filteredAnims)
        {
            try
            {
                frame.Clear(vrfSkeleton);
                frame.FrameIndex = 0;
                anim.DecodeFrame(frame);

                bool isAdditive = anim.IsAdditive;
                if (isAdditive)
                {
                    // Compose additive delta over rest bind pose to avoid skeleton collapsing into the floor!
                    anim.ComposeAdditiveOverBindPose(frame.Bones, vrfSkeleton);
                }

                string animName = anim.Name;
                string cleanName = SanitizePoseName(animName);

                var godotAnim = new Godot.Animation
                {
                    Length = 0.1f,
                    Step = 0.1f
                };

                var poseData = new PoseData
                {
                    Name = cleanName,
                    DisplayName = FormatDisplayName(cleanName)
                };

                // Root bone detection: find bone without parent (usually root_motion or Scene Root)
                var rootBone = vrfSkeleton.Bones.FirstOrDefault(b => b.Parent == null);

                for (int b = 0; b < vrfSkeleton.Bones.Length; b++)
                {
                    var vrfBone = vrfSkeleton.Bones[b];
                    string boneName = vrfBone.Name;
                    var fb = frame.Bones[b];

                    bool isRoot = (vrfBone == rootBone);
                    var (convPos, convRot) = BakeConversion(fb.Position, fb.Angle, isRoot);

                    // FIX for primary_shoot and root motion desync:
                    // If this is a shoot, fire, recoil, or additive animation on the root bone,
                    // pin horizontal displacement (X and Z) to 0 so root motion does not launch the hero
                    // across the studio or reset the hierarchy offset, preserving true vertical stance and local aiming!
                    if (isRoot)
                    {
                        bool isShootOrRecoil = cleanName.Contains("shoot") || cleanName.Contains("fire") ||
                                               cleanName.Contains("attack") || cleanName.Contains("recoil");
                        if (isShootOrRecoil)
                        {
                            convPos = new System.Numerics.Vector3(0f, convPos.Y, 0f);
                        }
                    }

                    var gPos = new Godot.Vector3(convPos.X, convPos.Y, convPos.Z);
                    var gRot = new Godot.Quaternion(convRot.X, convRot.Y, convRot.Z, convRot.W);

                    // Insert Position3D track (Frame 0 only)
                    int pTrack = godotAnim.AddTrack(Godot.Animation.TrackType.Position3D);
                    godotAnim.TrackSetPath(pTrack, $"{skeletonNodePath}:{boneName}");
                    godotAnim.PositionTrackInsertKey(pTrack, 0.0, gPos);

                    // Insert Rotation3D track (Frame 0 only)
                    int rTrack = godotAnim.AddTrack(Godot.Animation.TrackType.Rotation3D);
                    godotAnim.TrackSetPath(rTrack, $"{skeletonNodePath}:{boneName}");
                    godotAnim.RotationTrackInsertKey(rTrack, 0.0, gRot);

                    poseData.BoneTransforms[boneName] = (gPos, gRot);
                }

                animLibrary.AddAnimation(cleanName, godotAnim);
                heroCache[cleanName] = poseData;
                loadedCount++;
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[AnimLoader] Error parsing frame 0 for '{anim.Name}': {ex.Message}");
            }
        }

        GD.Print($"[AnimLoader] Successfully imported {loadedCount} Zero-RAM poses into AnimationPlayer.");
        return loadedCount;
    }

    /// <summary>
    /// Applies a pose directly to a Skeleton3D from cached transform data without requiring AnimationPlayer playback.
    /// </summary>
    public static bool ApplyCachedPose(Node3D heroScene, Skeleton3D skeleton, string poseName)
    {
        if (heroScene == null || skeleton == null || string.IsNullOrWhiteSpace(poseName)) return false;

        int heroId = heroScene.GetInstanceId().GetHashCode();
        if (!_heroPosesCache.TryGetValue(heroId, out var heroCache)) return false;

        if (!heroCache.TryGetValue(poseName, out var poseData))
        {
            // Try fallback by matching clean name
            poseData = heroCache.Values.FirstOrDefault(p =>
                p.Name.Equals(poseName, StringComparison.OrdinalIgnoreCase) ||
                p.DisplayName.Equals(poseName, StringComparison.OrdinalIgnoreCase));
        }

        if (poseData == null) return false;

        for (int i = 0; i < skeleton.GetBoneCount(); i++)
        {
            skeleton.ResetBonePose(i);
        }

        foreach (var (boneName, transform) in poseData.BoneTransforms)
        {
            int boneIdx = skeleton.FindBone(boneName);
            if (boneIdx != -1)
            {
                skeleton.SetBonePosePosition(boneIdx, transform.Position);
                skeleton.SetBonePoseRotation(boneIdx, transform.Rotation);
            }
        }

        skeleton.ForceUpdateAllBoneTransforms();
        return true;
    }

    /// <summary>
    /// Cleans up cached pose records when a hero is unloaded.
    /// </summary>
    public static void ClearHeroPoses(Node3D heroScene)
    {
        if (heroScene == null) return;
        int heroId = heroScene.GetInstanceId().GetHashCode();
        _heroPosesCache.Remove(heroId);
    }

    #region Animation Filtering, Classification & Lazy Decoding

    /// <summary>
    /// Checks whether an animation sequence is an internal, additive, procedural, or developer/test sequence.
    /// </summary>
    public static bool IsBlacklisted(string rawName, bool isAdditive)
    {
        if (string.IsNullOrWhiteSpace(rawName)) return true;

        string lower = rawName.ToLowerInvariant();

        // 1. Additive / Blended sequences
        if (isAdditive || lower.Contains('@') || lower.Contains("_add") || lower.Contains("_delta") ||
            lower.Contains("delta_") || lower.Contains("aim_matrix") || lower.Contains("turn_matrix") ||
            lower.Contains("corrective_"))
        {
            return true;
        }

        // 2. Procedural / Helper sequences
        if (lower.Contains("ragdoll") || lower.Contains("blend_space") || lower.Contains("ik_calc") ||
            lower.Contains("lean_"))
        {
            return true;
        }

        // 3. Developer / Test sequences
        string fileName = Path.GetFileName(rawName.Replace('\\', '/')).ToLowerInvariant();
        if (fileName.StartsWith("test_") || fileName.StartsWith("dev_") || fileName.StartsWith("cam_") ||
            fileName.StartsWith("camera_") || lower.Contains("/test_") || lower.Contains("/dev_") ||
            lower.Contains("/cam_") || lower.Contains("/camera_"))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Classifies an animation into semantic categories based on clip keywords.
    /// </summary>
    public static string ClassifyCategory(string cleanName)
    {
        string lower = cleanName.ToLowerInvariant();

        // Idle: idle, id_, stand, rest
        if (lower.Contains("idle") || lower.Contains("id_") || lower.Contains("stand") || lower.Contains("rest"))
            return "Idle";

        // Locomotion: walk, run, sprint, jog, dash, jump, mantle, slide, crouch
        if (lower.Contains("walk") || lower.Contains("run") || lower.Contains("sprint") || lower.Contains("jog") ||
            lower.Contains("dash") || lower.Contains("jump") || lower.Contains("mantle") || lower.Contains("slide") ||
            lower.Contains("crouch"))
            return "Locomotion";

        // Combat: attack, shoot, fire, reload, aim, melee
        if (lower.Contains("attack") || lower.Contains("shoot") || lower.Contains("fire") || lower.Contains("reload") ||
            lower.Contains("aim") || lower.Contains("melee"))
            return "Combat";

        // Abilities: ability, cast, skill, ult
        if (lower.Contains("ability") || lower.Contains("cast") || lower.Contains("skill") || lower.Contains("ult"))
            return "Abilities";

        // Emotes & Expressions: taunt, cheer, flair, intro, win, portrait
        if (lower.Contains("taunt") || lower.Contains("cheer") || lower.Contains("flair") || lower.Contains("intro") ||
            lower.Contains("win") || lower.Contains("portrait"))
            return "Emotes & Expressions";

        // Reactions: hit, flinch, stun, death, die
        if (lower.Contains("hit") || lower.Contains("flinch") || lower.Contains("stun") || lower.Contains("death") ||
            lower.Contains("die"))
            return "Reactions";

        // Misc / Other: default fallback
        return "Misc / Other";
    }

    /// <summary>
    /// Determines initial looping heuristic for an animation sequence.
    /// </summary>
    public static bool DetermineLooping(string cleanName, string category)
    {
        string lower = cleanName.ToLowerInvariant();
        if (lower.Contains("loop") || lower.Contains("cycle")) return true;
        if (category == "Idle") return true;
        if (category == "Locomotion")
        {
            if (lower.Contains("jump") || lower.Contains("dash") || lower.Contains("mantle") || lower.Contains("slide"))
                return false;
            return true;
        }
        return false;
    }

    public static int GetCategoryPriority(string category) => category switch
    {
        "Idle" => 1,
        "Locomotion" => 2,
        "Combat" => 3,
        "Abilities" => 4,
        "Emotes & Expressions" => 5,
        "Reactions" => 6,
        _ => 7
    };

    /// <summary>
    /// Fast VRF metadata indexer: queries animations from the model without decoding bone transform curves.
    /// Emulates Source 2 Viewer (VRF) memory footprint and instant load times.
    /// </summary>
    public static List<AnimSequenceInfo> IndexAnimations(Model vrfModel, IFileLoader fileLoader)
    {
        var result = new List<AnimSequenceInfo>();
        if (vrfModel == null || fileLoader == null) return result;

        List<ValveResourceFormat.ResourceTypes.ModelAnimation.Animation> allAnimations;
        try
        {
            allAnimations = vrfModel.GetAllAnimations(fileLoader).ToList();
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[AnimLoader] Error retrieving model animations: {ex.Message}");
            allAnimations = vrfModel.GetEmbeddedAnimations().Cast<ValveResourceFormat.ResourceTypes.ModelAnimation.Animation>().ToList();
        }

        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var anim in allAnimations)
        {
            string rawName = anim.Name;
            if (IsBlacklisted(rawName, anim.IsAdditive)) continue;

            string cleanName = SanitizePoseName(rawName);
            if (string.IsNullOrWhiteSpace(cleanName) || !seenNames.Add(cleanName)) continue;

            string category = ClassifyCategory(cleanName);
            float fps = anim.Fps > 0 ? anim.Fps : 30f;
            int frameCount = anim.FrameCount > 0 ? anim.FrameCount : 1;
            float duration = anim.Duration > 0 ? anim.Duration : (frameCount > 1 ? (frameCount - 1) / fps : 0.1f);
            bool isLooping = DetermineLooping(cleanName, category);

            result.Add(new AnimSequenceInfo
            {
                RawName = rawName,
                CleanName = cleanName,
                DisplayName = FormatDisplayName(cleanName),
                Category = category,
                FrameCount = frameCount,
                Fps = fps,
                Duration = duration,
                IsLooping = isLooping,
                VrfAnimation = anim
            });
        }

        // Sort by category priority, then alphabetically by display name
        result.Sort((a, b) =>
        {
            int catOrderA = GetCategoryPriority(a.Category);
            int catOrderB = GetCategoryPriority(b.Category);
            if (catOrderA != catOrderB) return catOrderA.CompareTo(catOrderB);
            return string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
        });

        GD.Print($"[AnimLoader] Indexed {result.Count} discrete sequences across categories.");
        return result;
    }

    /// <summary>
    /// Decodes solely the selected sequence's keyframe data on-demand using ValveResourceFormat
    /// and registers it into Godot's AnimationLibrary.
    /// </summary>
    public static Godot.Animation DecodeAnimationToGodot(
        AnimSequenceInfo info,
        Skeleton3D skeleton,
        AnimationPlayer animPlayer,
        Model vrfModel)
    {
        if (info == null || skeleton == null || animPlayer == null || vrfModel == null || info.VrfAnimation == null)
            return null;

        AnimationLibrary animLibrary;
        if (animPlayer.HasAnimationLibrary(""))
        {
            animLibrary = animPlayer.GetAnimationLibrary("");
        }
        else
        {
            animLibrary = new AnimationLibrary();
            animPlayer.AddAnimationLibrary("", animLibrary);
        }

        if (animLibrary.HasAnimation(info.CleanName))
        {
            return animLibrary.GetAnimation(info.CleanName);
        }

        var vrfSkeleton = vrfModel.Skeleton;
        if (vrfSkeleton == null || vrfSkeleton.Bones.Length == 0) return null;

        float fps = info.Fps > 0 ? info.Fps : 30f;
        int totalFrames = Math.Max(1, info.FrameCount);
        float duration = info.Duration > 0 ? info.Duration : (totalFrames > 1 ? (totalFrames - 1) / fps : 0.1f);

        var godotAnim = new Godot.Animation
        {
            Length = Math.Max(0.01f, duration),
            Step = 1.0f / fps,
            LoopMode = info.IsLooping ? Godot.Animation.LoopModeEnum.Linear : Godot.Animation.LoopModeEnum.None
        };

        string skeletonNodePath = animPlayer.GetPathTo(skeleton);

        // Map bones that exist in both VRF skeleton and Godot Skeleton3D
        var boneTrackMap = new Dictionary<int, (int PosTrack, int RotTrack)>();
        for (int b = 0; b < vrfSkeleton.Bones.Length; b++)
        {
            string boneName = vrfSkeleton.Bones[b].Name;
            int godotBoneIdx = skeleton.FindBone(boneName);
            if (godotBoneIdx == -1) continue;

            int pTrack = godotAnim.AddTrack(Godot.Animation.TrackType.Position3D);
            godotAnim.TrackSetPath(pTrack, $"{skeletonNodePath}:{boneName}");

            int rTrack = godotAnim.AddTrack(Godot.Animation.TrackType.Rotation3D);
            godotAnim.TrackSetPath(rTrack, $"{skeletonNodePath}:{boneName}");

            boneTrackMap[b] = (pTrack, rTrack);
        }

        var frame = new Frame(vrfSkeleton, vrfModel.FlexControllers);
        var rootBone = vrfSkeleton.Bones.FirstOrDefault(b => b.Parent == null);

        for (int f = 0; f < totalFrames; f++)
        {
            float time = totalFrames > 1 ? (f / fps) : 0f;
            if (time > (float)godotAnim.Length) time = (float)godotAnim.Length;

            frame.Clear(vrfSkeleton);
            frame.FrameIndex = f;
            info.VrfAnimation.DecodeFrame(frame);

            if (info.VrfAnimation.IsAdditive)
            {
                info.VrfAnimation.ComposeAdditiveOverBindPose(frame.Bones, vrfSkeleton);
            }

            for (int b = 0; b < vrfSkeleton.Bones.Length; b++)
            {
                if (!boneTrackMap.TryGetValue(b, out var tracks)) continue;

                var vrfBone = vrfSkeleton.Bones[b];
                var fb = frame.Bones[b];
                bool isRoot = (vrfBone == rootBone);

                var (convPos, convRot) = BakeConversion(fb.Position, fb.Angle, isRoot);

                if (isRoot)
                {
                    bool isShootOrRecoil = info.CleanName.Contains("shoot") || info.CleanName.Contains("fire") ||
                                           info.CleanName.Contains("attack") || info.CleanName.Contains("recoil");
                    if (isShootOrRecoil)
                    {
                        convPos = new System.Numerics.Vector3(0f, convPos.Y, 0f);
                    }
                }

                var gPos = new Godot.Vector3(convPos.X, convPos.Y, convPos.Z);
                var gRot = new Godot.Quaternion(convRot.X, convRot.Y, convRot.Z, convRot.W);

                godotAnim.PositionTrackInsertKey(tracks.PosTrack, time, gPos);
                godotAnim.RotationTrackInsertKey(tracks.RotTrack, time, gRot);
            }
        }

        animLibrary.AddAnimation(info.CleanName, godotAnim);
        GD.Print($"[AnimLoader] Decoded and registered '{info.CleanName}' ({godotAnim.Length:F2}s, {totalFrames} frames) into AnimationLibrary.");
        return godotAnim;
    }

    /// <summary>
    /// Clears animations in AnimationLibrary to instantly reclaim RAM.
    /// </summary>
    public static void ClearAnimationLibrary(AnimationPlayer animPlayer)
    {
        if (animPlayer == null) return;
        if (animPlayer.HasAnimationLibrary(""))
        {
            var lib = animPlayer.GetAnimationLibrary("");
            foreach (var name in lib.GetAnimationList())
            {
                lib.RemoveAnimation(name);
            }
        }
    }

    /// <summary>
    /// Checks whether a bone is a procedural cloth or dress flap bone governed by ProceduralClothSolver.
    /// </summary>
    public static bool IsClothOrFlapBone(string boneName)
    {
        if (string.IsNullOrEmpty(boneName)) return false;
        return boneName.StartsWith("$cloth_", StringComparison.OrdinalIgnoreCase)
            || boneName.StartsWith("dress_cut_", StringComparison.OrdinalIgnoreCase)
            || boneName.StartsWith("dress_out_", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Strips any tracks for procedural cloth bones from a Godot Animation so AnimationPlayer
    /// does not overwrite conformed cloth poses with static bind pose keyframes.
    /// </summary>
    public static void StripClothTracks(Godot.Animation anim)
    {
        if (anim == null) return;
        for (int i = anim.GetTrackCount() - 1; i >= 0; i--)
        {
            string path = anim.TrackGetPath(i).ToString();
            int colonIdx = path.LastIndexOf(':');
            string target = colonIdx != -1 ? path.Substring(colonIdx + 1) : path;
            if (IsClothOrFlapBone(target))
            {
                anim.RemoveTrack(i);
            }
        }
    }

    private static List<ValveResourceFormat.ResourceTypes.ModelAnimation.Animation> FilterPoses(
        List<ValveResourceFormat.ResourceTypes.ModelAnimation.Animation> rawAnims)
    {
        var result = new List<ValveResourceFormat.ResourceTypes.ModelAnimation.Animation>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var anim in rawAnims)
        {
            string rawName = anim.Name;
            if (IsBlacklisted(rawName, anim.IsAdditive)) continue;

            string cleanName = SanitizePoseName(rawName);
            if (seenNames.Add(cleanName))
            {
                result.Add(anim);
            }
        }

        return result;
    }

    public static string SanitizePoseName(string rawName)
    {
        string name = rawName.Replace('\\', '/');

        // Strip file extension (.vnmclip, .vanim)
        if (name.EndsWith(".vnmclip", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".vanim", StringComparison.OrdinalIgnoreCase))
        {
            name = Path.GetFileNameWithoutExtension(name);
        }

        // Strip common VRF path prefixes
        int clipsIdx = name.IndexOf("/clips/", StringComparison.OrdinalIgnoreCase);
        if (clipsIdx != -1)
        {
            name = name.Substring(clipsIdx + 7);
        }
        else
        {
            int lastSlash = name.LastIndexOf('/');
            if (lastSlash != -1)
            {
                name = name.Substring(lastSlash + 1);
            }
        }

        return name.TrimStart('@');
    }

    public static string FormatDisplayName(string cleanName)
    {
        if (string.IsNullOrEmpty(cleanName)) return "";
        string formatted = cleanName.Replace('_', ' ');
        return System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(formatted);
    }

    #endregion

    #region Coordinate Frame Conversion

    /// <summary>
    /// Converts a Source 2 bone local translation and rotation to Godot / glTF coordinate space.
    /// Source 2 uses Z-up in inches. Godot / glTF uses Y-up in meters.
    /// </summary>
    private static (System.Numerics.Vector3 Translation, System.Numerics.Quaternion Rotation) BakeConversion(
        System.Numerics.Vector3 translation, System.Numerics.Quaternion rotation, bool isRoot)
    {
        translation *= InchesToMeters;
        if (isRoot)
        {
            translation = System.Numerics.Vector3.Transform(translation, SourceToGltfRotation);
            rotation = SourceToGltfRotation * rotation;
        }
        return (translation, rotation);
    }

    #endregion

    #region Scene Tree Search Helpers

    private static Skeleton3D SearchSkeleton(Node node)
    {
        if (node is Skeleton3D sk) return sk;
        foreach (Node child in node.GetChildren())
        {
            var res = SearchSkeleton(child);
            if (res != null) return res;
        }
        return null;
    }

    private static AnimationPlayer SearchOrCreateAnimationPlayer(Node node)
    {
        if (node is AnimationPlayer ap) return ap;
        foreach (Node child in node.GetChildren())
        {
            if (child is AnimationPlayer childAp) return childAp;
        }

        // Create a new AnimationPlayer if not present
        var newAp = new AnimationPlayer { Name = "AnimationPlayer" };
        node.AddChild(newAp);
        return newAp;
    }

    #endregion
}
