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
        public bool IsAdditive { get; set; }
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
                frame.ClearToIdentity();
                frame.FrameIndex = 0;
                anim.DecodeFrame(frame);

                string animName = anim.Name;
                string cleanName = SanitizePoseName(animName);

                bool isAdditive = anim.IsAdditive ||
                                  IsAdditiveOrRecoilSequence(animName) ||
                                  IsAdditiveOrRecoilSequence(cleanName) ||
                                  IsAdditiveDeltaFrame(frame, anim.IsAdditive, animName, vrfSkeleton);

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
                    int godotBoneIdx = skeleton3D.FindBone(boneName);
                    if (godotBoneIdx == -1) continue;

                    var fb = frame.Bones[b];
                    bool isRoot = (vrfBone == rootBone);
                    var (convPos, convRot) = BakeConversion(fb.Position, fb.Angle, isRoot, isAdditive);

                    var gPos = new Godot.Vector3(convPos.X, convPos.Y, convPos.Z);

                    if (isAdditive)
                    {
                        // 1. Decoded bone quaternions are in local bone space:
                        var correctedRot = new Godot.Quaternion(convRot.X, convRot.Y, convRot.Z, convRot.W).Normalized();

                        // 2. Preserve Pelvis Elevation Track:
                        if (boneName.Equals("pelvis", StringComparison.OrdinalIgnoreCase))
                        {
                            int pTrack = godotAnim.AddTrack(Godot.Animation.TrackType.Position3D);
                            godotAnim.TrackSetPath(pTrack, $"{skeletonNodePath}:pelvis");
                            godotAnim.PositionTrackInsertKey(pTrack, 0.0, skeleton3D.GetBoneRest(godotBoneIdx).Origin);
                        }

                        // 3. Prune identity tracks so lower body cleanly remains in bind pose
                        bool isIdentity = Math.Abs(correctedRot.X) < 1e-3f && Math.Abs(correctedRot.Y) < 1e-3f &&
                                          Math.Abs(correctedRot.Z) < 1e-3f && Math.Abs(Math.Abs(correctedRot.W) - 1.0f) < 1e-3f;

                        if (!isIdentity)
                        {
                            int rTrack = godotAnim.AddTrack(Godot.Animation.TrackType.Rotation3D);
                            godotAnim.TrackSetPath(rTrack, $"{skeletonNodePath}:{boneName}");
                            godotAnim.RotationTrackInsertKey(rTrack, 0.0, correctedRot);
                        }

                        poseData.BoneTransforms[boneName] = (
                            skeleton3D.GetBoneRest(godotBoneIdx).Origin,
                            isIdentity ? skeleton3D.GetBoneRest(godotBoneIdx).Basis.GetRotationQuaternion() : correctedRot
                        );
                    }
                    else
                    {
                        var keyRot = new Godot.Quaternion(convRot.X, convRot.Y, convRot.Z, convRot.W).Normalized();
                        // Standard full-body animation: insert both Position3D and Rotation3D tracks
                        int pTrack = godotAnim.AddTrack(Godot.Animation.TrackType.Position3D);
                        godotAnim.TrackSetPath(pTrack, $"{skeletonNodePath}:{boneName}");
                        godotAnim.PositionTrackInsertKey(pTrack, 0.0, gPos);

                        int rTrack = godotAnim.AddTrack(Godot.Animation.TrackType.Rotation3D);
                        godotAnim.TrackSetPath(rTrack, $"{skeletonNodePath}:{boneName}");
                        godotAnim.RotationTrackInsertKey(rTrack, 0.0, keyRot);

                        poseData.BoneTransforms[boneName] = (gPos, keyRot);
                    }
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
    public static bool IsBlacklisted(string rawName, bool isAdditive, string targetSkeletonName = null)
    {
        if (string.IsNullOrWhiteSpace(rawName)) return true;

        // In Deadlock, some .vnmskel clips are intermediate Maya test exports targeting external test skeletons.
        if (!string.IsNullOrEmpty(targetSkeletonName) &&
            targetSkeletonName.EndsWith(".vnmskel", StringComparison.OrdinalIgnoreCase))
        {
            string tLower = targetSkeletonName.ToLowerInvariant();
            if (tLower.Contains("maya") || tLower.Contains("test_") || tLower.Contains("dev_") || tLower.Contains("generic"))
            {
                return true;
            }
        }

        string lower = rawName.ToLowerInvariant();


        // 1. Procedural / Blend space matrices that are not discrete anim clips
        if (lower.Contains("corrective_") || lower.Contains("blend_space") || lower.Contains("ik_calc"))
        {
            return true;
        }

        // 2. Procedural / Helper sequences
        if (lower.Contains("ragdoll"))
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
    /// Explicit registry of retargeted/remapped animation names to guarantee "Remapped" category assignment.
    /// </summary>
    public static readonly HashSet<string> KnownRemappedAnimations = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Classifies an animation into semantic categories based on clip keywords.
    /// </summary>
    public static string ClassifyCategory(string cleanName, bool isVrfAdditive = false)
    {
        string lower = cleanName.ToLowerInvariant();

        // 0. Remapped / Imported External Animations
        if (KnownRemappedAnimations.Contains(cleanName) ||
            lower.StartsWith("retarget") || lower.Contains("retarget") ||
            lower.StartsWith("remap") || lower.Contains("remap") ||
            lower.StartsWith("imported") || lower.Contains("imported"))
            return "Remapped";

        // Exported / Custom modified animations
        if (lower.StartsWith("exported") || lower.Contains("export_") || lower.StartsWith("custom_"))
            return "Exported";

        // 1. Reactions: hit, flinch, stun, death, die, impact, react
        if (lower.Contains("hit") || lower.Contains("flinch") || lower.Contains("stun") || lower.Contains("death") ||
            lower.Contains("die") || lower.Contains("react") || lower.Contains("impact"))
            return "Reactions";

        // 2. Additive Layers: runtime firing/recoil deltas / item overlays
        if (IsRecoilOrAimDeltaSequence(cleanName) || cleanName.StartsWith("item_", StringComparison.OrdinalIgnoreCase))
            return "Additive Layers";

        // 3. Abilities: ability, cast, skill, ult (must precede Idle so ability holding poses are not misclassified as Idle)
        if (lower.Contains("ability") || lower.Contains("cast") || lower.Contains("skill") || lower.Contains("ult"))
            return "Abilities";

        // 4. Idle: idle, id_, stand, rest
        if (lower.Contains("idle") || lower.Contains("id_") || lower.Contains("stand") || lower.Contains("rest"))
            return "Idle";

        // 5. Locomotion: walk, run, sprint, jog, dash, jump, mantle, slide, crouch
        if (lower.Contains("walk") || lower.Contains("run") || lower.Contains("sprint") || lower.Contains("jog") ||
            lower.Contains("dash") || lower.Contains("jump") || lower.Contains("mantle") || lower.Contains("slide") ||
            lower.Contains("crouch"))
            return "Locomotion";

        // 6. Combat: attack, reload, melee, weapon
        if (lower.Contains("attack") || lower.Contains("reload") || lower.Contains("melee") || lower.Contains("weapon"))
            return "Combat";

        // 7. Emotes & Expressions: taunt, cheer, flair, intro, win, portrait
        if (lower.Contains("taunt") || lower.Contains("cheer") || lower.Contains("flair") || lower.Contains("intro") ||
            lower.Contains("win") || lower.Contains("portrait"))
            return "Emotes & Expressions";

        // Misc / Other: default fallback
        return "Misc / Other";
    }

    public static bool IsRecoilOrAimDeltaSequence(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        string lower = name.ToLowerInvariant();
        return lower.Contains("fire") ||
               lower.Contains("recoil") ||
               lower.Contains("shoot") ||
               lower.Contains("shot") ||
               lower.Contains("burst") ||
               lower.Contains("aimed") ||
               lower.Contains("aim_matrix") ||
               lower.Contains("turn_matrix") ||
               lower.Contains("lean_") ||
               lower.Contains("_add") ||
               lower.Contains("_delta") ||
               lower.Contains("delta_");
    }

    /// <summary>
    /// Determines initial looping heuristic for an animation sequence.
    /// </summary>
    public static bool DetermineLooping(string cleanName, string category)
    {
        string lower = cleanName.ToLowerInvariant();
        if (lower.Contains("loop") || lower.Contains("cycle")) return true;
        if (category == "Remapped" || category == "Exported") return true;
        if (category == "Idle") return true;
        if (category == "Additive Layers") return false;
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
        "Remapped" => 0,
        "Exported" => 0,
        "Idle" => 1,
        "Locomotion" => 2,
        "Combat" => 3,
        "Abilities" => 4,
        "Emotes & Expressions" => 5,
        "Reactions" => 6,
        "Additive Layers" => 7,
        _ => 8
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
            if (IsBlacklisted(rawName, anim.IsAdditive, anim.TargetSkeletonName)) continue;


            string cleanName = SanitizePoseName(rawName);
            if (string.IsNullOrWhiteSpace(cleanName) || !seenNames.Add(cleanName)) continue;

            string category = ClassifyCategory(cleanName, anim.IsAdditive);
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
        Model vrfModel,
        AnimSequenceInfo idleInfo = null,
        string targetAnimName = null)
    {
        if (info == null || skeleton == null || animPlayer == null || vrfModel == null || info.VrfAnimation == null)
            return null;

        string registerName = string.IsNullOrEmpty(targetAnimName) ? info.CleanName : targetAnimName;

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

        if (animLibrary.HasAnimation(registerName))
        {
            var existing = animLibrary.GetAnimation(registerName);
            if (existing != null && existing.HasMeta("decoded_by_anim_loader"))
            {
                return existing;
            }
            animLibrary.RemoveAnimation(registerName);
        }

        var vrfSkeleton = vrfModel.Skeleton;
        if (vrfSkeleton == null || vrfSkeleton.Bones.Length == 0) return null;

        if (info.VrfAnimation is ValveResourceFormat.ResourceTypes.ModelAnimation.ClipAnimation clipAnim)
        {
            if (clipAnim.Clip.TrackCompressionSettings.Length != vrfSkeleton.Bones.Length)
            {
                // In AnimGraph 2, partial clips (weapons, aiming, gestures) only author a subset of joints.
                // VRF's DecodeFrame safely decodes tracks up to Math.Min(tracks.Length, skeleton.Bones.Length).
                GD.Print($"[AnimLoader] Clip '{registerName}' has {clipAnim.Clip.TrackCompressionSettings.Length} tracks for {vrfSkeleton.Bones.Length} bones. Decoding partial tracks safely.");
            }
        }

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

        bool isAdditive = info.VrfAnimation.IsAdditive ||
                          IsAdditiveOrRecoilSequence(info.CleanName, info.VrfAnimation.IsAdditive) ||
                          IsAdditiveOrRecoilSequence(info.RawName, info.VrfAnimation.IsAdditive);

        if (info.Category == "Idle") isAdditive = false;

        info.IsAdditive = isAdditive;
        godotAnim.SetMeta("is_additive", isAdditive);
        godotAnim.SetMeta("decoded_by_anim_loader", true);

        var rootBone = vrfSkeleton.Bones.FirstOrDefault(b => b.Parent == null);

        var boneTrackMap = new Dictionary<int, (int PosTrack, int RotTrack)>();
        for (int b = 0; b < vrfSkeleton.Bones.Length; b++)
        {
            string boneName = vrfSkeleton.Bones[b].Name;
            int godotBoneIdx = skeleton.FindBone(boneName);
            if (godotBoneIdx == -1 || IsClothOrFlapBone(boneName)) continue;

            int pTrack = godotAnim.AddTrack(Godot.Animation.TrackType.Position3D);
            godotAnim.TrackSetPath(pTrack, $"{skeletonNodePath}:{boneName}");

            int rTrack = godotAnim.AddTrack(Godot.Animation.TrackType.Rotation3D);
            godotAnim.TrackSetPath(rTrack, $"{skeletonNodePath}:{boneName}");

            boneTrackMap[b] = (pTrack, rTrack);
        }

        // If additive and an idle underlay is requested, decode Frame 0 of idle in native Source 2 space
        Frame idleFrame = null;
        if (isAdditive && idleInfo != null && idleInfo.VrfAnimation != null)
        {
            try
            {
                idleFrame = new Frame(vrfSkeleton, vrfModel.FlexControllers);
                idleFrame.Clear(vrfSkeleton);
                idleFrame.FrameIndex = 0;
                idleInfo.VrfAnimation.DecodeFrame(idleFrame);
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[AnimLoader] Failed to decode idle base frame: {ex.Message}");
                idleFrame = null;
            }
        }

        var frame = new Frame(vrfSkeleton, vrfModel.FlexControllers);

        for (int f = 0; f < totalFrames; f++)
        {
            float time = totalFrames > 1 ? (f / fps) : 0f;
            if (time > (float)godotAnim.Length) time = (float)godotAnim.Length;

            if (isAdditive)
            {
                frame.ClearToIdentity();
            }
            else
            {
                frame.Clear(vrfSkeleton);
            }
            frame.FrameIndex = f;
            info.VrfAnimation.DecodeFrame(frame);

            for (int b = 0; b < vrfSkeleton.Bones.Length; b++)
            {
                if (!boneTrackMap.TryGetValue(b, out var tracks)) continue;

                var vrfBone = vrfSkeleton.Bones[b];
                bool isRoot = (vrfBone == rootBone);
                bool isPelvis = vrfBone.Name.Equals("pelvis", StringComparison.OrdinalIgnoreCase);

                System.Numerics.Vector3 compPos;
                System.Numerics.Quaternion compAngle;

                if (isAdditive)
                {
                    var basePos = idleFrame != null ? idleFrame.Bones[b].Position : vrfSkeleton.Bones[b].Position;
                    var baseAngle = idleFrame != null ? idleFrame.Bones[b].Angle : vrfSkeleton.Bones[b].Angle;
                    var delta = info.VrfAnimation.GetAdditiveDelta(b, frame.Bones[b]);

                    if (isRoot || isPelvis)
                    {
                        // Maintain standing root/pelvis height and orientation from base idle (or bind pose)
                        compPos = basePos;
                        compAngle = baseAngle;
                    }
                    else
                    {
                        // Non-root joints compose pure delta on top of base pose in native Source 2 quaternion space
                        compPos = basePos;
                        var deltaAngle = delta.Angle;
                        if (deltaAngle.W < 0f) deltaAngle = -deltaAngle;
                        compAngle = System.Numerics.Quaternion.Normalize(baseAngle * deltaAngle);
                    }
                }
                else
                {
                    compPos = frame.Bones[b].Position;
                    compAngle = frame.Bones[b].Angle;
                }

                var (convPos, convRot) = BakeConversion(compPos, compAngle, isRoot);

                var gPos = new Godot.Vector3(convPos.X, convPos.Y, convPos.Z);
                var gRot = new Godot.Quaternion(convRot.X, convRot.Y, convRot.Z, convRot.W).Normalized();

                godotAnim.PositionTrackInsertKey(tracks.PosTrack, time, gPos);
                godotAnim.RotationTrackInsertKey(tracks.RotTrack, time, gRot);
            }
        }

        StripClothTracks(godotAnim, skeleton);
        animLibrary.AddAnimation(registerName, godotAnim);
        GD.Print($"[AnimLoader] Decoded and registered '{registerName}' (isAdditive={isAdditive}, hasIdleUnderlay={idleFrame != null}, {godotAnim.Length:F2}s, {totalFrames} frames) into AnimationLibrary.");
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
    /// Delegates to the centralized BoneLayerManager.IsProceduralClothBone classifier.
    /// </summary>
    public static bool IsClothOrFlapBone(string boneName)
    {
        return BoneLayerManager.IsProceduralClothBone(boneName);
    }

    /// <summary>
    /// Accurately detects whether an animation sequence is an additive delta layer.
    /// Distinguishes between delta layers (child bone translation deltas < 10 inches from rest/identity)
    /// and full body animations (pelvis translation ~35-45 inches).
    /// </summary>
    public static bool IsAdditiveDeltaFrame(Frame frame, bool isVrfAdditive, string animName = null, Skeleton vrfSkeleton = null)
    {
        if (isVrfAdditive) return true;

        bool nameMatches = IsAdditiveOrRecoilSequence(animName);

        if (frame?.Bones != null && frame.Bones.Length > 0)
        {
            if (vrfSkeleton != null && vrfSkeleton.Bones.Length == frame.Bones.Length)
            {
                float maxDiff = 0f;
                int nonZeroDeltaBones = 0;
                for (int i = 0; i < frame.Bones.Length; i++)
                {
                    if (vrfSkeleton.Bones[i].Parent == null) continue; // skip root
                    float diff = (frame.Bones[i].Position - vrfSkeleton.Bones[i].Position).Length();
                    if (diff > 0.05f) nonZeroDeltaBones++;
                    if (diff > maxDiff) maxDiff = diff;
                }

                // If non-root child bones have zero delta or tiny offsets (< 5 inches) from rest:
                // It is an additive delta layer!
                if (maxDiff < 5.0f || nonZeroDeltaBones == 0)
                {
                    return true;
                }
            }
            else
            {
                float maxDist = 0f;
                int nonZeroBones = 0;
                for (int i = 0; i < frame.Bones.Length; i++)
                {
                    float d = frame.Bones[i].Position.Length();
                    if (d > 0.05f) nonZeroBones++;
                    if (d > maxDist) maxDist = d;
                }

                // In Source 2 inches:
                // Any full body pose (idle, run, jump) has pelvis at ~35-45 inches from root.
                // An additive delta layer has max bone translation < 5 inches across all bones.
                // If maxDist is < 10 inches, it is a delta layer.
                if (maxDist < 10f || nonZeroBones == 0)
                {
                    return true;
                }
            }

            return false;
        }

        return nameMatches;
    }

    /// <summary>
    /// Legacy compatibility helper.
    /// </summary>
    public static bool IsDeltaSequence(Frame frame, Skeleton vrfSkeleton, string animName, bool isVrfAdditive)
    {
        return IsAdditiveDeltaFrame(frame, isVrfAdditive, animName, vrfSkeleton);
    }

    /// <summary>
    /// Detects if an animation sequence is an additive firing, recoil, or aim delta sequence.
    /// Source 2 authors these as delta layers; when played standalone in Godot, absolute Position3D tracks
    /// on root hierarchy bones would overwrite bind translations and collapse the skeleton into origin.
    /// </summary>
    public static bool IsAdditiveOrRecoilSequence(string name, bool isVrfAdditive = false)
    {
        if (isVrfAdditive) return true;
        if (string.IsNullOrWhiteSpace(name)) return false;
        string lower = name.ToLowerInvariant();

        // Baseline idle stances are standalone poses, not additive/recoil layers
        if (lower == "shoot_idle" || lower == "stand_idle" || lower == "idle" || lower == "idle_shoot" ||
            lower.EndsWith("_stand_idle") || lower.EndsWith("primary_idle") || lower.EndsWith("primary_stand_idle"))
            return false;

        return lower.Contains("fire") ||
               lower.Contains("recoil") ||
               lower.Contains("shoot") ||
               lower.Contains("shot") ||
               lower.Contains("burst") ||
               lower.Contains("aimed") ||
               lower.Contains("flinch") ||
               lower.Contains("_add") ||
               lower.Contains("_delta") ||
               lower.Contains("delta_");
    }

    public static bool IsAdditiveDeltaLayer(string cleanName, bool isVrfAdditive = false)
    {
        return IsAdditiveOrRecoilSequence(cleanName, isVrfAdditive);
    }

    /// <summary>
    /// Accurately and generically determines if an animation is an additive delta layer.
    /// Checks explicit metadata, sequence keywords, category, and track structure (e.g. rotation tracks with <= 2 position tracks).
    /// </summary>
    public static bool IsAdditiveAnimation(Godot.Animation anim, AnimSequenceInfo info = null, Skeleton3D skeleton = null)
    {
        if (info != null)
        {
            if (info.Category == "Idle") return false;
            if (info.IsAdditive) return true;
            if (info.Category == "Additive Layers") return true;
            if (info.VrfAnimation != null && info.VrfAnimation.IsAdditive) return true;
            if (IsAdditiveOrRecoilSequence(info.RawName, info.VrfAnimation?.IsAdditive ?? false) ||
                IsAdditiveOrRecoilSequence(info.CleanName, info.VrfAnimation?.IsAdditive ?? false)) return true;
        }

        if (anim != null)
        {
            if (anim.HasMeta("is_additive") && anim.GetMeta("is_additive").AsBool()) return true;

            int posTracks = 0;
            int rotTracks = 0;
            int total = anim.GetTrackCount();
            for (int i = 0; i < total; i++)
            {
                var t = anim.TrackGetType(i);
                if (t == Godot.Animation.TrackType.Position3D) posTracks++;
                else if (t == Godot.Animation.TrackType.Rotation3D) rotTracks++;
            }

            // In Source 2 / Deadlock Playground, additive delta clips only generate rotation tracks and <= 2 position tracks (pelvis/root),
            // while standard full-body animations generate 50-200 position tracks.
            if (rotTracks >= 3 && posTracks <= 2) return true;

            if (HasCollapsedOrDeltaPositionTracks(anim, skeleton)) return true;
        }

        return false;
    }

    /// <summary>
    /// Checks if a track's rotation keys across all frames are pure identity or static near-identity.
    /// Used to prune non-animated lower-body tracks (legs, feet, pelvis) in additive shooting/recoil clips.
    /// </summary>
    private static bool IsIdentityOrStaticTrack(List<(float Time, Godot.Quaternion Rot)> keys)
    {
        if (keys == null || keys.Count == 0) return true;

        bool allIdentity = true;
        for (int i = 0; i < keys.Count; i++)
        {
            var q = keys[i].Rot;
            if (Math.Abs(q.X) > 1e-3f || Math.Abs(q.Y) > 1e-3f || Math.Abs(q.Z) > 1e-3f || Math.Abs(Math.Abs(q.W) - 1.0f) > 1e-3f)
            {
                allIdentity = false;
                break;
            }
        }
        if (allIdentity) return true;

        // If all keys are identical across all frames and nearly identity (< 1 degree deviation from rest)
        if (keys.Count > 1)
        {
            var first = keys[0].Rot;
            bool isNearlyIdentity = Math.Abs(first.X) < 0.015f && Math.Abs(first.Y) < 0.015f && Math.Abs(first.Z) < 0.015f;
            if (isNearlyIdentity)
            {
                bool hasChange = false;
                for (int i = 1; i < keys.Count; i++)
                {
                    float dot = Math.Abs(first.Dot(keys[i].Rot));
                    if (dot < 0.9999f)
                    {
                        hasChange = true;
                        break;
                    }
                }
                if (!hasChange) return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Generically detects if a Godot Animation has Position3D tracks that would collapse the skeleton
    /// (e.g. child limb bones positioned at (0,0,0) or tiny deltas rather than natural skeleton rest offsets).
    /// </summary>
    public static bool HasCollapsedOrDeltaPositionTracks(Godot.Animation anim, Skeleton3D skeleton = null)
    {
        if (anim == null) return false;

        int collapsedChildBones = 0;
        int checkedChildBones = 0;

        for (int i = 0; i < anim.GetTrackCount(); i++)
        {
            if (anim.TrackGetType(i) != Godot.Animation.TrackType.Position3D) continue;
            if (anim.TrackGetKeyCount(i) == 0) continue;

            string path = anim.TrackGetPath(i).ToString();
            int colonIdx = path.LastIndexOf(':');
            string boneName = colonIdx != -1 ? path.Substring(colonIdx + 1) : path;

            // Skip root bones (root, root_motion)
            if (boneName.Equals("root", StringComparison.OrdinalIgnoreCase) ||
                boneName.Equals("root_motion", StringComparison.OrdinalIgnoreCase) ||
                boneName.Equals("Scene Root", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var keyPos = anim.PositionTrackInterpolate(i, 0.0);

            if (skeleton != null)
            {
                int bIdx = skeleton.FindBone(boneName);
                if (bIdx != -1)
                {
                    // Check if this bone in rest pose has a physical distance from its parent joint
                    float restLen = skeleton.GetBoneRest(bIdx).Origin.Length();
                    if (restLen > 0.04f)
                    {
                        checkedChildBones++;
                        // If the animation positions this child bone at < 0.02m (2cm) from parent, it is collapsed
                        if (keyPos.Length() < 0.02f)
                        {
                            collapsedChildBones++;
                        }
                    }
                }
            }
            else
            {
                if (!boneName.Equals("pelvis", StringComparison.OrdinalIgnoreCase))
                {
                    checkedChildBones++;
                    if (keyPos.Length() < 0.01f)
                    {
                        collapsedChildBones++;
                    }
                }
            }
        }

        // If at least 2 child bones (or > 30% of child bone tracks) are collapsed near (0,0,0)
        return checkedChildBones > 0 && (collapsedChildBones >= 2 || (float)collapsedChildBones / checkedChildBones > 0.3f);
    }

    /// <summary>
    /// Generically determines whether an animation sequence is an additive delta or has collapsed
    /// position tracks that would crush the skeleton into origin.
    /// Checks VRF additive metadata, name heuristics, and inspects Godot Position3D track keys.
    /// </summary>
    public static bool ShouldStripPositionTracks(Godot.Animation anim, Skeleton3D skeleton = null, string animName = null, bool isVrfAdditive = false)
    {
        if (isVrfAdditive) return true;
        if (IsAdditiveOrRecoilSequence(animName, isVrfAdditive)) return true;
        if (anim != null && HasCollapsedOrDeltaPositionTracks(anim, skeleton)) return true;
        return false;
    }

    /// <summary>
    /// Strips absolute Position3D tracks from limb joints in additive / recoil / reaction sequences so only
    /// rotational tracks evaluate against the skeleton's natural bind/rest translations, while
    /// preserving or restoring the pelvis elevation track so hips do not collapse to the floor.
    /// </summary>
    public static void StripAdditivePositionTracks(Godot.Animation anim, Skeleton3D skeleton = null)
    {
        if (anim == null) return;
        string skeletonNodePath = null;

        for (int i = anim.GetTrackCount() - 1; i >= 0; i--)
        {
            string path = anim.TrackGetPath(i).ToString();
            int colonIdx = path.LastIndexOf(':');
            if (colonIdx != -1 && skeletonNodePath == null)
            {
                skeletonNodePath = path.Substring(0, colonIdx);
            }

            if (anim.TrackGetType(i) == Godot.Animation.TrackType.Position3D)
            {
                string target = colonIdx != -1 ? path.Substring(colonIdx + 1) : path;
                if (target.Equals("pelvis", StringComparison.OrdinalIgnoreCase))
                {
                    // Check if pelvis track itself is collapsed (< 0.25m, when rest pelvis height is ~0.9m)
                    if (anim.TrackGetKeyCount(i) > 0 && skeleton != null)
                    {
                        var key0 = anim.PositionTrackInterpolate(i, 0.0);
                        int pIdx = skeleton.FindBone("pelvis");
                        if (pIdx != -1)
                        {
                            float restLen = skeleton.GetBoneRest(pIdx).Origin.Length();
                            if (key0.Length() < 0.25f && restLen >= 0.5f)
                            {
                                // Remove collapsed (0,0,0) pelvis track so it will be replaced with rest elevation below
                                anim.RemoveTrack(i);
                                continue;
                            }
                        }
                    }
                    // Valid pelvis elevation track: preserve it
                    continue;
                }
                // Strip all non-pelvis bone Position3D tracks so limbs evaluate against rest pose
                anim.RemoveTrack(i);
            }
        }

        // Ensure pelvis position track exists and holds rest elevation
        if (skeleton != null)
        {
            int pelvisIdx = skeleton.FindBone("pelvis");
            if (pelvisIdx != -1)
            {
                bool hasPelvisPos = false;
                for (int i = 0; i < anim.GetTrackCount(); i++)
                {
                    if (anim.TrackGetType(i) == Godot.Animation.TrackType.Position3D &&
                        anim.TrackGetPath(i).ToString().EndsWith(":pelvis", StringComparison.OrdinalIgnoreCase))
                    {
                        hasPelvisPos = true;
                        break;
                    }
                }
                if (!hasPelvisPos)
                {
                    string targetPath = !string.IsNullOrEmpty(skeletonNodePath) ? $"{skeletonNodePath}:pelvis" : "%GeneralSkeleton:pelvis";
                    int pTrack = anim.AddTrack(Godot.Animation.TrackType.Position3D);
                    anim.TrackSetPath(pTrack, targetPath);
                    var restPos = skeleton.GetBoneRest(pelvisIdx).Origin;
                    anim.PositionTrackInsertKey(pTrack, 0.0, restPos);
                    if (anim.Length > 0.0)
                    {
                        anim.PositionTrackInsertKey(pTrack, anim.Length, restPos);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Composes an additive or reaction delta sequence on top of a base idle animation.
    /// Unkeyed limbs (legs, arms, weapon holding position) inherit the base idle pose,
    /// while delta rotation keys are composed over the base idle orientations.
    /// </summary>
   public static Godot.Animation ComposeOverIdle(
        Godot.Animation additiveAnim,
        Godot.Animation idleAnim,
        Skeleton3D skeleton)
    {
        if (additiveAnim == null) return null;
        if (idleAnim == null || skeleton == null) return additiveAnim;

        var composed = new Godot.Animation
        {
            Length = additiveAnim.Length,
            Step = additiveAnim.Step,
            LoopMode = additiveAnim.LoopMode
        };

        string samplePath = idleAnim.GetTrackCount() > 0 ? idleAnim.TrackGetPath(0).ToString() :
                           (additiveAnim.GetTrackCount() > 0 ? additiveAnim.TrackGetPath(0).ToString() : "");
        int colonIdx = samplePath.LastIndexOf(':');
        string skeletonNodePath = colonIdx != -1 ? samplePath.Substring(0, colonIdx) : "GeneralSkeleton";

        // 1. Build lookup of base idle tracks at time 0.0
        var idleBonePos = new Dictionary<string, Godot.Vector3>(StringComparer.OrdinalIgnoreCase);
        var idleBoneRot = new Dictionary<string, Godot.Quaternion>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < idleAnim.GetTrackCount(); i++)
        {
            if (idleAnim.TrackGetKeyCount(i) == 0) continue;
            string path = idleAnim.TrackGetPath(i).ToString();
            int cIdx = path.LastIndexOf(':');
            if (cIdx == -1) continue;
            string boneName = path.Substring(cIdx + 1);
            var type = idleAnim.TrackGetType(i);

            if (type == Godot.Animation.TrackType.Position3D)
            {
                idleBonePos[boneName] = idleAnim.PositionTrackInterpolate(i, 0.0);
            }
            else if (type == Godot.Animation.TrackType.Rotation3D)
            {
                idleBoneRot[boneName] = idleAnim.RotationTrackInterpolate(i, 0.0).Normalized();
            }
        }

        // 2. Map additive tracks by bone name (lowercase) and track type
        var additiveTrackMap = new Dictionary<(string Bone, Godot.Animation.TrackType Type), int>();
        for (int i = 0; i < additiveAnim.GetTrackCount(); i++)
        {
            string path = additiveAnim.TrackGetPath(i).ToString();
            int cIdx = path.LastIndexOf(':');
            if (cIdx == -1) continue;
            string boneName = path.Substring(cIdx + 1).ToLowerInvariant();
            additiveTrackMap[(boneName, additiveAnim.TrackGetType(i))] = i;
        }

        // 3. For each bone in the skeleton, build composed tracks
        int totalBones = skeleton.GetBoneCount();
        for (int b = 0; b < totalBones; b++)
        {
            string boneName = skeleton.GetBoneName(b);
            string boneNameLower = boneName.ToLowerInvariant();
            if (IsClothOrFlapBone(boneName)) continue;

            Godot.Transform3D boneRest = skeleton.GetBoneRest(b);
            Godot.Quaternion restRot = boneRest.Basis.GetRotationQuaternion().Normalized();

            // --- Position: strictly preserve Base Idle Position (or BoneRest fallback) for all bones ---
            Godot.Vector3 basePos = idleBonePos.TryGetValue(boneName, out var ip) ? ip : boneRest.Origin;
            int posTrack = composed.AddTrack(Godot.Animation.TrackType.Position3D);
            composed.TrackSetPath(posTrack, $"{skeletonNodePath}:{boneName}");
            composed.PositionTrackInsertKey(posTrack, 0.0, basePos);
            if (composed.Length > 0.0)
            {
                composed.PositionTrackInsertKey(posTrack, composed.Length, basePos);
            }

            // --- Rotation: compose pure delta on top of Base Idle Rotation (or BoneRest fallback) ---
            Godot.Quaternion baseIdleRot = idleBoneRot.TryGetValue(boneName, out var ir) ? ir.Normalized() : restRot;

            bool isRootOrPelvis = boneName.Equals("root", StringComparison.OrdinalIgnoreCase) ||
                                  boneName.Equals("root_motion", StringComparison.OrdinalIgnoreCase) ||
                                  boneName.Equals("Scene Root", StringComparison.OrdinalIgnoreCase) ||
                                  boneName.Equals("pelvis", StringComparison.OrdinalIgnoreCase);

            if (additiveTrackMap.TryGetValue((boneNameLower, Godot.Animation.TrackType.Rotation3D), out int addRotTrack))
            {
                int keyCount = additiveAnim.TrackGetKeyCount(addRotTrack);
                int newTrack = composed.AddTrack(Godot.Animation.TrackType.Rotation3D);
                composed.TrackSetPath(newTrack, $"{skeletonNodePath}:{boneName}");

                if (keyCount == 0)
                {
                    composed.RotationTrackInsertKey(newTrack, 0.0, baseIdleRot);
                    if (composed.Length > 0.0)
                    {
                        composed.RotationTrackInsertKey(newTrack, composed.Length, baseIdleRot);
                    }
                }
                else
                {
                    for (int k = 0; k < keyCount; k++)
                    {
                        double time = additiveAnim.TrackGetKeyTime(addRotTrack, k);
                        var keyRot = ((Godot.Quaternion)additiveAnim.TrackGetKeyValue(addRotTrack, k)).Normalized();
                        if (keyRot.W < 0f) keyRot = -keyRot;

                        // Candidate 1: keyRot was exported over rest pose (Q_key = Q_rest * Q_delta => Q_delta = Q_rest^-1 * Q_key)
                        var deltaFromRest = (restRot.Inverse() * keyRot).Normalized();
                        if (deltaFromRest.W < 0f) deltaFromRest = -deltaFromRest;

                        // Candidate 2: keyRot is already a delta relative to Identity (0, 0, 0, 1)
                        var deltaFromIdent = keyRot;

                        // Pick candidate closest to identity (highest W component)
                        var pureDelta = (deltaFromRest.W > deltaFromIdent.W) ? deltaFromRest : deltaFromIdent;

                        Godot.Quaternion finalRot;
                        if (isRootOrPelvis || Math.Abs(pureDelta.W) >= 0.9998f)
                        {
                            // Root/pelvis or un-animated / tiny sub-degree delta: preserve idle stance exactly
                            finalRot = baseIdleRot;
                        }
                        else
                        {
                            // True additive reaction delta composed over base idle orientation
                            finalRot = (baseIdleRot * pureDelta).Normalized();
                        }

                        composed.RotationTrackInsertKey(newTrack, time, finalRot);
                    }
                }
            }
            else
            {
                int newTrack = composed.AddTrack(Godot.Animation.TrackType.Rotation3D);
                composed.TrackSetPath(newTrack, $"{skeletonNodePath}:{boneName}");
                composed.RotationTrackInsertKey(newTrack, 0.0, baseIdleRot);
                if (composed.Length > 0.0)
                {
                    composed.RotationTrackInsertKey(newTrack, composed.Length, baseIdleRot);
                }
            }
        }

        return composed;
    }

    /// <summary>
    /// Strips any tracks for procedural cloth bones from a Godot Animation so AnimationPlayer
    /// does not overwrite conformed cloth poses with static bind pose keyframes.
    /// Kinematic clothing bones (coats, jackets, bags) with authored animation are preserved.
    /// </summary>
    public static void StripClothTracks(Godot.Animation anim, Skeleton3D skeleton = null)
    {
        if (anim == null) return;

        for (int i = anim.GetTrackCount() - 1; i >= 0; i--)
        {
            string path = anim.TrackGetPath(i).ToString();
            int colonIdx = path.LastIndexOf(':');
            string target = colonIdx != -1 ? path.Substring(colonIdx + 1) : path;
            if (BoneLayerManager.IsProceduralClothBone(target))
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
            if (IsBlacklisted(rawName, anim.IsAdditive, anim.TargetSkeletonName)) continue;


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
    /// Root orientation applies SourceToGltfRotation to align the skeleton upright.
    /// Child joints remain in local bone space where delta rotations compose cleanly.
    /// </summary>
    private static (System.Numerics.Vector3 Translation, System.Numerics.Quaternion Rotation) BakeConversion(
        System.Numerics.Vector3 translation, System.Numerics.Quaternion rotation, bool isRoot, bool isAdditive = false)
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
