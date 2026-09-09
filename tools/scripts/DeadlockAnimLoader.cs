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

    #region Animation Filtering & Pose Categorization

    private static List<ValveResourceFormat.ResourceTypes.ModelAnimation.Animation> FilterPoses(
        List<ValveResourceFormat.ResourceTypes.ModelAnimation.Animation> rawAnims)
    {
        var result = new List<ValveResourceFormat.ResourceTypes.ModelAnimation.Animation>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // High priority keywords for studio hero posing
        string[] highPriorityKeywords = new[]
        {
            "stand_idle", "primary_idle", "idle", "ui_hero_select", "ui_pose",
            "hero_pose", "ui_hero", "idle_loadout", "out_of_combat_stand_idle",
            "stand", "crouch_idle", "primary_crouch_idle", "crouch",
            "walk", "primary_walk", "walk_n", "walk_center",
            "run", "primary_run", "run_n", "run_c", "run_center", "sprint",
            "primary_shoot", "shoot_idle", "hip_fire", "primary_hip_fire", "run_shoot", "shoot",
            "melee", "cast", "attack", "reload", "jump", "dash", "aim"
        };

        // Garbage/debug keywords to unconditionally discard
        string[] garbageKeywords = new[]
        {
            "ragdoll", "test_", "debug_", "_delta", "_face", "gesture_", "turn_",
            "flinch_", "hit_", "pain_", "spawn_", "death_", "knockdown_inair",
            "knockdown_large", "knockdown_medium", "mantle_", "stun_", "drown_"
        };

        foreach (var anim in rawAnims)
        {
            string rawName = anim.Name;
            if (string.IsNullOrWhiteSpace(rawName)) continue;

            string lower = rawName.ToLowerInvariant();

            // Discard garbage animations
            if (garbageKeywords.Any(g => lower.Contains(g))) continue;

            // Discard raw uncomposable additive layers unless they are high-value shoot/aim poses
            if (anim.IsAdditive)
            {
                bool isShootOrAim = lower.Contains("shoot") || lower.Contains("fire") || lower.Contains("aim");
                if (!isShootOrAim) continue;
            }

            // Check if matches high-value criteria
            bool isHighValue = highPriorityKeywords.Any(k => lower.Contains(k));
            if (!isHighValue) continue;

            string cleanName = SanitizePoseName(rawName);
            if (seenNames.Add(cleanName))
            {
                result.Add(anim);
            }
        }

        // Ensure we always have at least one idle pose if available
        if (result.Count == 0 && rawAnims.Count > 0)
        {
            result.AddRange(rawAnims.Take(10));
        }

        // Sort poses alphabetically by clean display name with idle/stand first
        result.Sort((a, b) =>
        {
            string nameA = SanitizePoseName(a.Name).ToLowerInvariant();
            string nameB = SanitizePoseName(b.Name).ToLowerInvariant();

            int scoreA = GetPoseSortPriority(nameA);
            int scoreB = GetPoseSortPriority(nameB);

            if (scoreA != scoreB) return scoreA.CompareTo(scoreB);
            return string.Compare(nameA, nameB, StringComparison.OrdinalIgnoreCase);
        });

        return result;
    }

    private static int GetPoseSortPriority(string lowerName)
    {
        if (lowerName.Contains("stand_idle") || lowerName.Contains("primary_idle") || lowerName.Contains("hero_select")) return 1;
        if (lowerName.Contains("idle")) return 2;
        if (lowerName.Contains("crouch_idle")) return 3;
        if (lowerName.Contains("stand")) return 4;
        if (lowerName.Contains("crouch")) return 5;
        if (lowerName.Contains("walk")) return 6;
        if (lowerName.Contains("run")) return 7;
        if (lowerName.Contains("shoot") || lowerName.Contains("fire")) return 8;
        return 20;
    }

    private static string SanitizePoseName(string rawName)
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

    private static string FormatDisplayName(string cleanName)
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
