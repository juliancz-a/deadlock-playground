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
        public bool IsCompatible { get; set; } = true;
        public string IncompatibilityReason { get; set; } = null;
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

        // Path to Skeleton3D relative to AnimationPlayer RootNode
        string skeletonNodePath = GetSkeletonTrackPathPrefix(animPlayer, skeleton3D);

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

                for (int b = 0; b < vrfSkeleton.Bones.Length; b++)
                {
                    var vrfBone = vrfSkeleton.Bones[b];
                    string boneName = vrfBone.Name;
                    int godotBoneIdx = skeleton3D.FindBone(boneName);
                    if (godotBoneIdx == -1) continue;

                    var fb = frame.Bones[b];
                    bool isRoot = (vrfBone.Parent == null);
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
        ClearAnimationCaches();
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

        // If explicitly registered by the user as an imported/remapped animation, never blacklist
        if (KnownRemappedAnimations.Contains(rawName))
        {
            return false;
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

        // 4. Valve Developer Retarget Setup Poses (e.g. pose_retarget, pose_retarget_astro, pose_retarget_genman2handed)
        if (lower.Contains("retarget") || lower.Contains("reference_pose") || lower.Contains("pose_bind") ||
            fileName.Contains("retarget") || fileName.Contains("pose_bind") ||
            lower.Equals("bind_pose") || lower.Equals("t_pose"))
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
    /// Checks if a bone name corresponds to a root or root-motion bone.
    /// </summary>
    public static bool IsRootMotionBoneName(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        return name.Equals("root_motion", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("root", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Scene Root", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("SceneRoot", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("world", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Classifies an animation into semantic categories based on clip keywords using tokenized matching.
    /// </summary>
    public static string ClassifyCategory(string cleanName, bool isVrfAdditive = false)
    {
        string lower = cleanName.ToLowerInvariant();

        // 0. Remapped / Imported External Animations (strictly user-imported or explicitly registered remapped clips)
        if (KnownRemappedAnimations.Contains(cleanName) ||
            lower.StartsWith("imported_") || lower.StartsWith("custom_imported_") ||
            lower.StartsWith("remap_user_"))
            return "Remapped";

        // Exported / Custom modified animations
        if (lower.StartsWith("exported") || lower.Contains("export_") || lower.StartsWith("custom_"))
            return "Exported";

        // Additive Layers: VRF additive flag or explicit additive delta keywords
        if (isVrfAdditive || lower.Contains("_add") || lower.Contains("_delta") || lower.Contains("delta_") ||
            lower.Contains("aim_matrix") || lower.Contains("turn_matrix") ||
            lower.EndsWith("_aim") || lower.StartsWith("aim_") || lower.Contains("_aim_") ||
            lower.Contains("lean") || lower.Contains("swing_additive") || lower.Contains("slide_aim") ||
            lower.Contains("custom01_slide") || lower.EndsWith("_fire_l") || lower.EndsWith("_fire_r") ||
            lower.Equals("primary_fire_l") || lower.Equals("primary_fire_r"))
            return "Additive Layers";

        // Tokenize into words for precise matching
        var tokens = lower.Split(new[] { '_', ' ', '-', '.', '/' }, StringSplitOptions.RemoveEmptyEntries);
        var tokenSet = new HashSet<string>(tokens, StringComparer.OrdinalIgnoreCase);

        bool HasAnyToken(params string[] query) => query.Any(q => tokenSet.Contains(q));
        bool HasPrefix(string prefix) => tokens.Any(t => t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

        // 1. Reactions & Status: hit, flinch, stun, death, die, impact, react, knockdown, sleep, hurt, burn
        if (HasAnyToken("hit", "flinch", "stun", "death", "die", "died", "impact", "knockdown", "sleep", "hurt", "burn", "react", "reaction", "ragdoll"))
            return "Reactions & Status";

        // 2. Abilities & Magic: ability, cast, skill, ult, ultimate, spell, power, channel, a1, a2, a3, a4
        if (HasAnyToken("ability", "cast", "skill", "ult", "ultimate", "spell", "power", "channel", "magic", "heal", "buff") ||
            HasPrefix("a1") || HasPrefix("a2") || HasPrefix("a3") || HasPrefix("a4"))
            return "Abilities & Magic";

        // 3. Traversal & Zipline: jump, fall, land, zipline, climb, mantle, air, vault, leap
        if (HasAnyToken("jump", "fall", "falling", "land", "landing", "zipline", "zip", "climb", "mantle", "vault", "leap", "air") &&
            !HasAnyToken("flair", "hair", "chair"))
            return "Traversal & Zipline";

        // 4. Locomotion: walk, run, sprint, jog, dash, slide, strafe, trot, crouch
        if (HasAnyToken("walk", "run", "running", "sprint", "sprinting", "jog", "dash", "slide", "sliding", "strafe", "trot", "crouching", "move", "movement") ||
            (HasAnyToken("crouch") && !HasAnyToken("idle", "rest", "pose")))
            return "Locomotion";

        // 5. Combat & Attacks: attack, reload, melee, weapon, fire, shoot, shot, burst, draw, holster, aim, throw, toss
        if (HasAnyToken("attack", "reload", "reloading", "melee", "weapon", "fire", "firing", "shoot", "shooting", "shot", "burst", "draw", "holster", "aim", "aiming", "gun", "throw", "toss"))
            return "Combat & Attacks";

        // 6. Idles & Stances: idle, stand, rest, menu, select, crouch_idle
        if (HasAnyToken("idle", "stand", "standing", "rest", "resting", "menu", "select", "pose") ||
            lower.Contains("stand_idle") || lower.Contains("crouch_idle"))
            return "Idles & Stances";

        // 7. Emotes & Taunts: taunt, cheer, flair, intro, win, winner, portrait, emote, dance, laugh, victory
        if (HasAnyToken("taunt", "cheer", "flair", "intro", "win", "winner", "victory", "portrait", "emote", "dance", "laugh", "celebrate"))
            return "Emotes & Taunts";

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
               lower.Contains("lean") ||
               lower.Contains("swing") ||
               lower.Contains("impact") ||
               lower.Contains("custom01_slide") ||
               lower.Contains("_add") ||
               lower.Contains("_delta") ||
               lower.Contains("delta_");
    }

    /// <summary>
    /// Accurately detects whether an animation sequence is an additive delta layer.
    /// Checks Source 2 metadata, semantic category, and authored delta naming patterns.
    /// </summary>
    public static bool IsDeltaSequence(AnimSequenceInfo info, Skeleton vrfSkeleton = null)
    {
        if (info == null) return false;
        if (info.Category == "Idles & Stances" || info.Category == "Idle") return false;
        if (info.Category == "Locomotion") return false;
        if (info.VrfAnimation != null && info.VrfAnimation.IsAdditive) return true;
        if (info.IsAdditive) return true;

        string name = info.CleanName.ToLowerInvariant();
        if (name.StartsWith("item_")) return false; // Item carriage animations are full locomotion/idles
        if (info.Category == "Additive Layers") return true;

        if (name.Contains("_add") || name.Contains("_delta") || name.Contains("delta_") ||
            name.Contains("aim") || name.Contains("flinch") || name.Contains("recoil") ||
            name.Contains("lean") || name.Contains("swing") || name.Contains("impact") ||
            name.StartsWith("custom01_slide") ||
            name.Contains("fire_l") || name.Contains("fire_r"))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Finds a default standing combat idle sequence directly from embedded model animations.
    /// Used as an authoritative base pose for composing additive and reaction delta sequences.
    /// </summary>
    public static AnimSequenceInfo FindDefaultIdleFromModel(Model vrfModel, IFileLoader fileLoader = null)
    {
        if (vrfModel == null) return null;
        try
        {
            var anims = vrfModel.GetEmbeddedAnimations();
            foreach (var anim in anims)
            {
                if (anim.IsAdditive) continue;
                string clean = SanitizePoseName(anim.Name).ToLowerInvariant();
                if ((clean.Contains("stand_idle") || clean.Contains("idle")) &&
                    !clean.Contains("crouch") && !clean.Contains("ability") && !clean.Contains("cast") &&
                    !clean.Contains("item") && !clean.Contains("lean"))
                {
                    return new AnimSequenceInfo
                    {
                        RawName = anim.Name,
                        CleanName = clean,
                        DisplayName = FormatDisplayName(clean),
                        Category = "Idles & Stances",
                        VrfAnimation = anim
                    };
                }
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Determines initial looping heuristic for an animation sequence.
    /// </summary>
    public static bool DetermineLooping(string cleanName, string category)
    {
        string lower = cleanName.ToLowerInvariant();
        if (lower.Contains("loop") || lower.Contains("cycle")) return true;
        if (category == "Remapped" || category == "Exported") return true;
        if (category == "Idles & Stances" || category == "Idle") return true;
        if (category == "Additive Layers") return false;
        if (category == "Traversal & Zipline")
        {
            if (lower.Contains("zipline")) return true;
            return false;
        }
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
        "Idles & Stances" or "Idle" => 1,
        "Locomotion" => 2,
        "Traversal & Zipline" => 3,
        "Combat & Attacks" or "Combat" => 4,
        "Abilities & Magic" or "Abilities" => 5,
        "Reactions & Status" or "Reactions" => 6,
        "Emotes & Taunts" or "Emotes & Expressions" => 7,
        "Additive Layers" => 8,
        _ => 9
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

        // Secondary scan: resolve referenced animation groups (.vagrp_c) not loaded yet
        var vrfSkeleton = vrfModel.Skeleton;
        if (vrfSkeleton != null)
        {
            try
            {
                foreach (string animGroupName in vrfModel.GetReferencedAnimationGroupNames())
                {
                    if (string.IsNullOrWhiteSpace(animGroupName)) continue;
                    string search = animGroupName.Replace('\\', '/');
                    if (!search.EndsWith("_c", StringComparison.OrdinalIgnoreCase))
                    {
                        search += "_c";
                    }

                    var gRes = fileLoader.LoadFile(search) ?? fileLoader.LoadFileCompiled(search);
                    if (gRes != null)
                    {
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
                GD.Print($"[AnimLoader] Secondary AnimGroup scan note: {ex.Message}");
            }
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
            bool isAdditive = anim.IsAdditive;

            bool isCompatible = true;
            string incompReason = null;

            if (!string.IsNullOrEmpty(anim.TargetSkeletonName))
            {
                string tSkel = anim.TargetSkeletonName.ToLowerInvariant();
                if (tSkel.Contains("weapon") || tSkel.Contains("props/") || tSkel.Contains("/props") ||
                    tSkel.Contains("viewmodel") || tSkel.Contains("/v_") || tSkel.StartsWith("v_") ||
                    tSkel.Contains("generic_human"))
                {
                    isCompatible = false;
                    incompReason = $"Authored for prop/weapon skeleton: {Path.GetFileName(tSkel)}";
                }
            }

            if (isCompatible && anim is ValveResourceFormat.ResourceTypes.ModelAnimation.ClipAnimation ca && vrfSkeleton != null)
            {
                int trackCount = ca.Clip.TrackCompressionSettings.Length;
                if (trackCount < 8 && vrfSkeleton.Bones.Length > 50)
                {
                    isCompatible = false;
                    incompReason = $"Sub-prop clip ({trackCount} tracks vs {vrfSkeleton.Bones.Length} hero bones)";
                }
            }

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
                IsAdditive = isAdditive,
                IsCompatible = isCompatible,
                IncompatibilityReason = incompReason,
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
    /// Resolves the authoritative track path prefix for a Skeleton3D relative to AnimationPlayer.RootNode.
    /// In Godot 4, AnimationMixer tracks are resolved strictly relative to RootNode, not the AnimationPlayer node itself.
    /// </summary>
    public static string GetSkeletonTrackPathPrefix(AnimationPlayer animPlayer, Skeleton3D skeleton)
    {
        if (skeleton == null) return "GeneralSkeleton";

        if (animPlayer != null)
        {
            // 1. Inspect any existing valid animation in animPlayer to extract its authoritative track prefix
            foreach (var existingName in animPlayer.GetAnimationList())
            {
                var existingAnim = animPlayer.GetAnimation(existingName);
                if (existingAnim != null && existingAnim.GetTrackCount() > 0)
                {
                    for (int i = 0; i < existingAnim.GetTrackCount(); i++)
                    {
                        string firstTrackPath = existingAnim.TrackGetPath(i).ToString();
                        int cIdx = firstTrackPath.LastIndexOf(':');
                        if (cIdx != -1)
                        {
                            string candidate = firstTrackPath.Substring(0, cIdx);
                            // Avoid candidate starting with "../" because that was the faulty prefix
                            if (candidate.StartsWith("../")) continue;

                            Node animRoot = animPlayer.GetNodeOrNull(animPlayer.RootNode) ?? animPlayer.GetParent();
                            if (animRoot != null && animRoot.GetNodeOrNull(candidate) is Skeleton3D)
                            {
                                return candidate;
                            }
                        }
                    }
                }
            }

            // 2. Resolve relative to animPlayer.RootNode (AnimationMixer evaluates relative to RootNode)
            Node root = animPlayer.GetNodeOrNull(animPlayer.RootNode) ?? animPlayer.GetParent();
            if (root != null)
            {
                string relPath = root.GetPathTo(skeleton).ToString();
                if (!string.IsNullOrEmpty(relPath))
                {
                    if (relPath == ".") return ".";
                    return relPath;
                }
            }

            // 3. Fallback relative to animPlayer itself, stripping leading "../" if targeting parent's child
            string apRel = animPlayer.GetPathTo(skeleton).ToString();
            if (!string.IsNullOrEmpty(apRel))
            {
                if (apRel.StartsWith("../"))
                {
                    string stripped = apRel.Substring(3);
                    if (animPlayer.GetParent()?.GetNodeOrNull(stripped) is Skeleton3D)
                    {
                        return stripped;
                    }
                }
                return apRel;
            }
        }

        return skeleton.Name.ToString();
    }

    #region Secondary Skeleton Retargeting Cache

    private static readonly Dictionary<string, Skeleton> _secondarySkeletonCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<(string ModelSkel, string SecondarySkel), SkeletonRetargeter> _retargeterCache = new();

    public static Skeleton GetOrLoadSecondarySkeleton(IFileLoader fileLoader, string skeletonName)
    {
        if (fileLoader == null || string.IsNullOrWhiteSpace(skeletonName)) return null;

        lock (_secondarySkeletonCache)
        {
            if (_secondarySkeletonCache.TryGetValue(skeletonName, out var cached))
            {
                return cached;
            }

            try
            {
                var skel = Skeleton.FromSkeletonResource(fileLoader, skeletonName);
                if (skel != null)
                {
                    _secondarySkeletonCache[skeletonName] = skel;
                    return skel;
                }
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[AnimLoader] Failed to load secondary skeleton '{skeletonName}': {ex.Message}");
            }

            return null;
        }
    }

    public static SkeletonRetargeter GetOrCreateRetargeter(Skeleton modelSkel, Skeleton secondarySkel, string secondarySkelName)
    {
        if (modelSkel == null || secondarySkel == null) return null;

        string modelSkelName = modelSkel.Name ?? "model";
        var key = (modelSkelName, secondarySkelName);

        lock (_retargeterCache)
        {
            if (_retargeterCache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            try
            {
                var retargeter = new SkeletonRetargeter(modelSkel, secondarySkel);
                _retargeterCache[key] = retargeter;
                return retargeter;
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[AnimLoader] Failed to create retargeter for '{secondarySkelName}': {ex.Message}");
                return null;
            }
        }
    }

    public static void ClearAnimationCaches()
    {
        lock (_secondarySkeletonCache) _secondarySkeletonCache.Clear();
        lock (_retargeterCache) _retargeterCache.Clear();
    }

    #endregion

    /// <summary>
    /// Decodes solely the selected sequence's keyframe data on-demand using ValveResourceFormat
    /// and registers it into Godot's AnimationLibrary.
    /// Supports secondary AnimGraph .vnmskel skeletons via Valve's SkeletonRetargeter.
    /// </summary>
    public static Godot.Animation DecodeAnimationToGodot(
        AnimSequenceInfo info,
        Skeleton3D skeleton,
        AnimationPlayer animPlayer,
        Model vrfModel,
        IFileLoader fileLoader = null,
        AnimSequenceInfo idleInfo = null,
        string targetAnimName = null,
        bool inPlace = true)
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
                bool isStaleVersion = !existing.HasMeta("anim_loader_version") || existing.GetMeta("anim_loader_version").AsInt32() < 5;
                bool isMismatchedInPlace = existing.HasMeta("in_place") && existing.GetMeta("in_place").AsBool() != inPlace;
                bool hasBrokenPrefix = false;
                for (int t = 0; t < existing.GetTrackCount(); t++)
                {
                    if (existing.TrackGetPath(t).ToString().StartsWith("../"))
                    {
                        hasBrokenPrefix = true;
                        break;
                    }
                }

                if (!isStaleVersion && !hasBrokenPrefix && !isMismatchedInPlace)
                {
                    return existing;
                }
            }
            animLibrary.RemoveAnimation(registerName);
        }

        var vrfSkeleton = vrfModel.Skeleton;
        if (vrfSkeleton == null || vrfSkeleton.Bones.Length == 0) return null;

        int clipTrackCount = -1;
        if (info.VrfAnimation is ValveResourceFormat.ResourceTypes.ModelAnimation.ClipAnimation clipAnim)
        {
            clipTrackCount = clipAnim.Clip.TrackCompressionSettings.Length;
            if (clipTrackCount != vrfSkeleton.Bones.Length)
            {
                GD.Print($"[AnimLoader] Clip '{registerName}' has {clipTrackCount} tracks for {vrfSkeleton.Bones.Length} bones. Resolving via retargeter or safe decoding.");
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

        string skeletonNodePath = GetSkeletonTrackPathPrefix(animPlayer, skeleton);

        bool isAdditive = info.VrfAnimation.IsAdditive || IsDeltaSequence(info, vrfSkeleton);
        info.IsAdditive = isAdditive;
        godotAnim.SetMeta("is_additive", isAdditive);
        godotAnim.SetMeta("decoded_by_anim_loader", true);
        godotAnim.SetMeta("anim_loader_version", 5);
        godotAnim.SetMeta("in_place", inPlace);

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

        // If additive and an idle underlay is requested, or if partial/delta clip, decode Frame 0 of idle
        Frame idleFrame = null;
        if (idleInfo != null && idleInfo.VrfAnimation != null)
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
        else if (isAdditive && vrfModel != null)
        {
            var defaultIdle = FindDefaultIdleFromModel(vrfModel, fileLoader);
            if (defaultIdle != null && defaultIdle.VrfAnimation != null)
            {
                try
                {
                    idleFrame = new Frame(vrfSkeleton, vrfModel.FlexControllers);
                    idleFrame.Clear(vrfSkeleton);
                    idleFrame.FrameIndex = 0;
                    defaultIdle.VrfAnimation.DecodeFrame(idleFrame);
                }
                catch (Exception ex)
                {
                    GD.PrintErr($"[AnimLoader] Failed to decode auto-resolved idle base frame: {ex.Message}");
                    idleFrame = null;
                }
            }
        }

        if (isAdditive && idleFrame != null)
        {
            godotAnim.SetMeta("composed", true);
        }

        // Check if clip targets a secondary skeleton (.vnmskel) that requires Source 2 retargeting
        bool requiresRetargeting = false;
        Skeleton secondarySkel = null;
        SkeletonRetargeter retargeter = null;

        if (!isAdditive && info.VrfAnimation is ValveResourceFormat.ResourceTypes.ModelAnimation.ClipAnimation ca &&
            !string.IsNullOrEmpty(ca.Clip.SkeletonName) && fileLoader != null)
        {
            string secSkelName = ca.Clip.SkeletonName;
            bool isDifferentSkeleton = !secSkelName.Equals(vrfSkeleton.Name, StringComparison.OrdinalIgnoreCase) ||
                                       ca.Clip.TrackCompressionSettings.Length != vrfSkeleton.Bones.Length;

            if (isDifferentSkeleton)
            {
                secondarySkel = GetOrLoadSecondarySkeleton(fileLoader, secSkelName);
                if (secondarySkel != null)
                {
                    retargeter = GetOrCreateRetargeter(vrfSkeleton, secondarySkel, secSkelName);
                    if (retargeter != null && retargeter.HasMappedBones)
                    {
                        requiresRetargeting = true;
                    }
                }
            }
        }

        // Diagnostic output
        GD.Print($"[AnimDiag] Decode '{registerName}' | VrfAdditive: {info.VrfAnimation.IsAdditive} | Tracks: {clipTrackCount}/{vrfSkeleton.Bones.Length} | Retargeted: {requiresRetargeting} | inPlace: {inPlace} | TargetSkel: '{info.VrfAnimation.TargetSkeletonName}'");

        Frame frame = null;
        Frame secFrame = null;
        System.Numerics.Matrix4x4[] modelWorld = null;
        System.Numerics.Matrix4x4[] invWorld = null;
        int[] parentIndices = null;

        if (requiresRetargeting)
        {
            secFrame = new Frame(secondarySkel, vrfModel.FlexControllers);
            modelWorld = new System.Numerics.Matrix4x4[vrfSkeleton.Bones.Length];
            invWorld = new System.Numerics.Matrix4x4[vrfSkeleton.Bones.Length];
            parentIndices = new int[vrfSkeleton.Bones.Length];
            for (int i = 0; i < vrfSkeleton.Bones.Length; i++)
            {
                var p = vrfSkeleton.Bones[i].Parent;
                parentIndices[i] = p != null ? Array.IndexOf(vrfSkeleton.Bones, p) : -1;
            }
        }
        else
        {
            frame = new Frame(vrfSkeleton, vrfModel.FlexControllers);
        }

        for (int f = 0; f < totalFrames; f++)
        {
            float time = totalFrames > 1 ? (f / fps) : 0f;
            if (time > (float)godotAnim.Length) time = (float)godotAnim.Length;

            if (requiresRetargeting)
            {
                secFrame.Clear(secondarySkel);
                secFrame.FrameIndex = f;
                info.VrfAnimation.DecodeFrame(secFrame);
                retargeter.Retarget(secFrame, modelWorld);

                for (int i = 0; i < vrfSkeleton.Bones.Length; i++)
                {
                    if (!System.Numerics.Matrix4x4.Invert(modelWorld[i], out invWorld[i]))
                    {
                        invWorld[i] = System.Numerics.Matrix4x4.Identity;
                    }
                }

                for (int b = 0; b < vrfSkeleton.Bones.Length; b++)
                {
                    if (!boneTrackMap.TryGetValue(b, out var tracks)) continue;

                    var vrfBone = vrfSkeleton.Bones[b];
                    bool isParentless = (vrfBone.Parent == null);
                    bool isRootMotionBone = IsRootMotionBoneName(vrfBone.Name);

                    System.Numerics.Matrix4x4 localMat;
                    if (isParentless)
                    {
                        localMat = modelWorld[b];
                    }
                    else
                    {
                        int p = parentIndices[b];
                        localMat = p >= 0 ? modelWorld[b] * invWorld[p] : modelWorld[b];
                    }

                    System.Numerics.Matrix4x4.Decompose(localMat, out _, out var compAngle, out var compPos);
                    var (convPos, convRot) = BakeConversion(compPos, compAngle, isParentless);

                    if (inPlace && isRootMotionBone)
                    {
                        convPos = new System.Numerics.Vector3(0f, convPos.Y, 0f);
                    }

                    var gPos = new Godot.Vector3(convPos.X, convPos.Y, convPos.Z);
                    var gRot = new Godot.Quaternion(convRot.X, convRot.Y, convRot.Z, convRot.W).Normalized();

                    godotAnim.PositionTrackInsertKey(tracks.PosTrack, time, gPos);
                    godotAnim.RotationTrackInsertKey(tracks.RotTrack, time, gRot);
                }
            }
            else
            {
                if (isAdditive)
                {
                    frame.ClearToIdentity();
                }
                else if (idleFrame != null && clipTrackCount > 0 && clipTrackCount < vrfSkeleton.Bones.Length)
                {
                    // For partial clips, initialize unkeyed bones from idle stance rather than rigid T-pose
                    for (int b = 0; b < vrfSkeleton.Bones.Length; b++)
                    {
                        frame.Bones[b].Position = idleFrame.Bones[b].Position;
                        frame.Bones[b].Angle = idleFrame.Bones[b].Angle;
                    }
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
                    bool isParentless = (vrfBone.Parent == null);
                    bool isRootMotionBone = IsRootMotionBoneName(vrfBone.Name);
                    bool isPelvis = vrfBone.Name.Equals("pelvis", StringComparison.OrdinalIgnoreCase);

                    System.Numerics.Vector3 compPos;
                    System.Numerics.Quaternion compAngle;

                    if (isAdditive)
                    {
                        var basePos = idleFrame != null ? idleFrame.Bones[b].Position : vrfSkeleton.Bones[b].Position;
                        var baseAngle = idleFrame != null ? idleFrame.Bones[b].Angle : vrfSkeleton.Bones[b].Angle;
                        var delta = info.VrfAnimation.GetAdditiveDelta(b, frame.Bones[b]);

                        if (isParentless || isPelvis || isRootMotionBone)
                        {
                            // Maintain standing root/pelvis height and orientation from base idle (or bind pose)
                            compPos = basePos;
                            compAngle = baseAngle;
                        }
                        else
                        {
                            compPos = basePos;
                            var deltaAngle = delta.Angle;
                            if (Math.Abs(deltaAngle.W) > 0.999f)
                            {
                                compAngle = baseAngle;
                            }
                            else
                            {
                                if (deltaAngle.W < 0f) deltaAngle = -deltaAngle;
                                compAngle = System.Numerics.Quaternion.Normalize(baseAngle * deltaAngle);
                            }
                        }
                    }
                    else
                    {
                        compPos = frame.Bones[b].Position;
                        compAngle = frame.Bones[b].Angle;
                    }

                    var (convPos, convRot) = BakeConversion(compPos, compAngle, isParentless);

                    // In-place root motion locking (locks horizontal travel only on root motion bones so hero stays centered)
                    if (inPlace && isRootMotionBone)
                    {
                        convPos = new System.Numerics.Vector3(0f, convPos.Y, 0f);
                    }

                    var gPos = new Godot.Vector3(convPos.X, convPos.Y, convPos.Z);
                    var gRot = new Godot.Quaternion(convRot.X, convRot.Y, convRot.Z, convRot.W).Normalized();

                    godotAnim.PositionTrackInsertKey(tracks.PosTrack, time, gPos);
                    godotAnim.RotationTrackInsertKey(tracks.RotTrack, time, gRot);
                }
            }
        }

        StripClothTracks(godotAnim, skeleton);
        animLibrary.AddAnimation(registerName, godotAnim);
        GD.Print($"[AnimLoader] Decoded and registered '{registerName}' (isAdditive={isAdditive}, hasIdleUnderlay={idleFrame != null}, inPlace={inPlace}, retargeted={requiresRetargeting}, {godotAnim.Length:F2}s, {totalFrames} frames) into AnimationLibrary.");
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
    /// Source 2 authors these as delta layers. Strictly honors VRF metadata.
    /// </summary>
    public static bool IsAdditiveOrRecoilSequence(string name, bool isVrfAdditive = false)
    {
        return isVrfAdditive;
    }

    public static bool IsAdditiveDeltaLayer(string cleanName, bool isVrfAdditive = false)
    {
        return isVrfAdditive;
    }

    /// <summary>
    /// Accurately and generically determines if an animation is an additive delta layer.
    /// Strictly trusts VRF metadata, avoiding heuristic name-based false positives.
    /// </summary>
    public static bool IsAdditiveAnimation(Godot.Animation anim, AnimSequenceInfo info = null, Skeleton3D skeleton = null)
    {
        if (info != null)
        {
            if (info.Category == "Idles & Stances" || info.Category == "Idle") return false;
            if (info.VrfAnimation != null) return info.VrfAnimation.IsAdditive;
            return info.IsAdditive;
        }

        if (anim != null)
        {
            // If already composed over idle or decoded, honor explicit metadata and never re-guess
            if (anim.HasMeta("composed") && anim.GetMeta("composed").AsBool()) return false;
            if (anim.HasMeta("is_additive")) return anim.GetMeta("is_additive").AsBool();
            if (anim.HasMeta("decoded_by_anim_loader")) return false;

            // Only use structural heuristic on legacy pre-baked animations (not decoded by anim loader)
            int posTracks = 0;
            int rotTracks = 0;
            int total = anim.GetTrackCount();
            for (int i = 0; i < total; i++)
            {
                var t = anim.TrackGetType(i);
                if (t == Godot.Animation.TrackType.Position3D) posTracks++;
                else if (t == Godot.Animation.TrackType.Rotation3D) rotTracks++;
            }

            if (rotTracks >= 3 && posTracks <= 2) return true;
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
    /// Evaluated only on legacy/glTF clips, never on dynamically decoded lazy animations.
    /// </summary>
    public static bool ShouldStripPositionTracks(Godot.Animation anim, Skeleton3D skeleton = null, string animName = null, bool isVrfAdditive = false)
    {
        if (anim != null && anim.HasMeta("decoded_by_anim_loader")) return false;
        if (isVrfAdditive) return true;
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

        string skeletonNodePath = GetSkeletonTrackPathPrefix(null, skeleton);
        string samplePath = idleAnim.GetTrackCount() > 0 ? idleAnim.TrackGetPath(0).ToString() :
                           (additiveAnim.GetTrackCount() > 0 ? additiveAnim.TrackGetPath(0).ToString() : "");
        int colonIdx = samplePath.LastIndexOf(':');
        if (colonIdx != -1)
        {
            string cand = samplePath.Substring(0, colonIdx);
            if (!cand.StartsWith("../"))
            {
                skeletonNodePath = cand;
            }
        }
        if (skeletonNodePath.StartsWith("../"))
        {
            skeletonNodePath = skeletonNodePath.Substring(3);
        }

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

        composed.SetMeta("composed", true);
        composed.SetMeta("is_additive", false);
        composed.SetMeta("decoded_by_anim_loader", true);
        composed.SetMeta("anim_loader_version", 5);

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
    public static (System.Numerics.Vector3 Translation, System.Numerics.Quaternion Rotation) BakeConversion(
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
