using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Godot;

namespace DeadlockPlayground.Animation
{
    /// <summary>
    /// Loads external 3D animation files (.glb, .gltf, .fbx) in memory at runtime,
    /// extracting the source Skeleton3D and AnimationPlayer animation clips for retargeting.
    /// </summary>
    public static class ExternalAnimationLoader
    {
        public class LoadResult : IDisposable
        {
            public bool Success { get; set; }
            public string ErrorMessage { get; set; } = "";
            public string FilePath { get; set; } = "";
            public string FileName => Path.GetFileName(FilePath);
            public Node RootScene { get; set; }
            public Skeleton3D SourceSkeleton { get; set; }
            public Dictionary<string, Godot.Animation> Animations { get; } = new(StringComparer.OrdinalIgnoreCase);

            public void Dispose()
            {
                if (RootScene != null && GodotObject.IsInstanceValid(RootScene))
                {
                    if (RootScene.IsInsideTree())
                    {
                        RootScene.QueueFree();
                    }
                    else
                    {
                        RootScene.Free();
                    }
                    RootScene = null;
                }
            }
        }

        /// <summary>
        /// Loads an external .glb, .gltf, or .fbx animation file and extracts its skeleton and clips.
        /// </summary>
        public static LoadResult Load(string filePath)
        {
            var result = new LoadResult { FilePath = filePath };

            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                result.Success = false;
                result.ErrorMessage = $"File does not exist: {filePath}";
                return result;
            }

            string ext = Path.GetExtension(filePath).ToLowerInvariant();

            try
            {
                if (ext == ".glb" || ext == ".gltf")
                {
                    LoadGltf(filePath, result);
                }
                else if (ext == ".fbx")
                {
                    LoadFbx(filePath, result);
                }
                else
                {
                    result.Success = false;
                    result.ErrorMessage = $"Unsupported animation format: '{ext}'. Please select .glb, .gltf, or .fbx";
                    return result;
                }

                if (!result.Success) return result;

                // Validate extracted skeleton and animations
                if (result.SourceSkeleton == null)
                {
                    result.Success = false;
                    result.ErrorMessage = "No Skeleton3D found inside the imported file.";
                    result.Dispose();
                    return result;
                }

                if (result.Animations.Count == 0)
                {
                    result.Success = false;
                    result.ErrorMessage = "No animation tracks found inside the imported file.";
                    result.Dispose();
                    return result;
                }

                GD.Print($"[ExternalAnimationLoader] Loaded '{result.FileName}': Skeleton '{result.SourceSkeleton.Name}' with {result.SourceSkeleton.GetBoneCount()} bones, {result.Animations.Count} animations ({string.Join(", ", result.Animations.Keys)}).");
                result.Success = true;
                return result;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = $"Unexpected error loading '{Path.GetFileName(filePath)}': {ex.Message}";
                GD.PrintErr($"[ExternalAnimationLoader] {result.ErrorMessage}\n{ex.StackTrace}");
                result.Dispose();
                return result;
            }
        }

        private static void LoadGltf(string filePath, LoadResult result)
        {
            var gltfDoc = new GltfDocument();
            var gltfState = new GltfState();

            Error err = gltfDoc.AppendFromFile(filePath, gltfState);
            if (err != Error.Ok)
            {
                result.Success = false;
                result.ErrorMessage = $"Godot GltfDocument failed to parse file ({err}).";
                return;
            }

            Node scene = gltfDoc.GenerateScene(gltfState);
            if (scene == null)
            {
                result.Success = false;
                result.ErrorMessage = "GltfDocument failed to generate 3D scene.";
                return;
            }

            // Strip rendering geometry and materials to eliminate GPU/RenderingServer leaks
            // without deleting any nodes so Skeleton3D and AnimationPlayer are never orphaned.
            StripMeshResources(scene);

            result.RootScene = scene;
            result.SourceSkeleton = FindSkeleton(scene);
            ExtractAnimations(scene, result);

            result.Success = true;
        }

        private static void LoadFbx(string filePath, LoadResult result)
        {
            try
            {
                var fbxDoc = new FbxDocument();
                var fbxState = new FbxState();

                Error err = fbxDoc.AppendFromFile(filePath, fbxState);
                if (err != Error.Ok)
                {
                    result.Success = false;
                    result.ErrorMessage = $"FbxDocument failed to parse FBX file ({err}).";
                    return;
                }

                Node scene = fbxDoc.GenerateScene(fbxState);
                if (scene == null)
                {
                    result.Success = false;
                    result.ErrorMessage = "FbxDocument failed to generate 3D scene from FBX.";
                    return;
                }

                // Strip rendering geometry without deleting nodes
                StripMeshResources(scene);

                result.RootScene = scene;
                result.SourceSkeleton = FindSkeleton(scene);
                ExtractAnimations(scene, result);
                result.Success = true;
                return;
            }
            catch (Exception fbxEx)
            {
                GD.PrintErr($"[ExternalAnimationLoader] Direct FbxDocument error: {fbxEx.Message}. Trying GltfDocument fallback...");
            }

            // Fallback: try GltfDocument in case the file is an embedded glb/fbx hybrid
            var fallbackDoc = new GltfDocument();
            var fallbackState = new GltfState();
            Error fbErr = fallbackDoc.AppendFromFile(filePath, fallbackState);
            if (fbErr == Error.Ok)
            {
                Node scene = fallbackDoc.GenerateScene(fallbackState);
                if (scene != null)
                {
                    StripMeshResources(scene);
                    result.RootScene = scene;
                    result.SourceSkeleton = FindSkeleton(scene);
                    ExtractAnimations(scene, result);
                    result.Success = true;
                    return;
                }
            }

            result.Success = false;
            result.ErrorMessage = "Could not parse FBX file. Please convert or export your animation as .glb or .gltf from Mixamo or Blender.";
        }

        private static void StripMeshResources(Node current)
        {
            if (current == null) return;

            if (current is MeshInstance3D mi)
            {
                mi.Mesh = null;
                mi.Skin = null;
            }
            else if (current is GeometryInstance3D gi)
            {
                gi.SetProcess(false);
                gi.Visible = false;
            }

            int count = current.GetChildCount();
            for (int i = 0; i < count; i++)
            {
                StripMeshResources(current.GetChild(i));
            }
        }

        private static Skeleton3D FindSkeleton(Node root)
        {
            var skeletons = new List<Skeleton3D>();
            FindAllSkeletons(root, skeletons);
            if (skeletons.Count == 0) return null;
            return skeletons.OrderByDescending(s => s.GetBoneCount()).First();
        }

        private static void FindAllSkeletons(Node current, List<Skeleton3D> list)
        {
            if (current == null) return;
            if (current is Skeleton3D sk) list.Add(sk);

            int count = current.GetChildCount();
            for (int i = 0; i < count; i++)
            {
                FindAllSkeletons(current.GetChild(i), list);
            }
        }

        private static void ExtractAnimations(Node scene, LoadResult result)
        {
            var players = new List<AnimationPlayer>();
            FindAnimationPlayers(scene, players);

            foreach (var player in players)
            {
                // Check all animation libraries
                var libraryNames = player.GetAnimationLibraryList();
                foreach (string libName in libraryNames)
                {
                    var lib = player.GetAnimationLibrary(libName);
                    if (lib == null) continue;

                    var animNames = lib.GetAnimationList();
                    foreach (string animName in animNames)
                    {
                        var anim = lib.GetAnimation(animName);
                        if (anim != null && anim.GetTrackCount() > 0)
                        {
                            string key = CleanClipName(animName);
                            if (!result.Animations.ContainsKey(key))
                            {
                                result.Animations[key] = anim;
                            }
                        }
                    }
                }

                // Check default animations
                var names = player.GetAnimationList();
                foreach (string animName in names)
                {
                    var anim = player.GetAnimation(animName);
                    if (anim != null && anim.GetTrackCount() > 0)
                    {
                        string key = CleanClipName(animName);
                        if (!result.Animations.ContainsKey(key))
                        {
                            result.Animations[key] = anim;
                        }
                    }
                }
            }
        }

        private static void FindAnimationPlayers(Node root, List<AnimationPlayer> list)
        {
            if (root == null) return;
            if (root is AnimationPlayer ap) list.Add(ap);

            int count = root.GetChildCount();
            for (int i = 0; i < count; i++)
            {
                FindAnimationPlayers(root.GetChild(i), list);
            }
        }

        private static string CleanClipName(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "animation";
            string name = raw.Trim();
            if (name.Contains('/')) name = name[(name.LastIndexOf('/') + 1)..];
            if (name.Contains('|')) name = name[(name.LastIndexOf('|') + 1)..];
            return name;
        }
    }
}
