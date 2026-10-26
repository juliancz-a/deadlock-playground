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

            /// <summary>True model-space bind global transforms extracted from skin inverse bind matrices.</summary>
            public Transform3D[] SourceBindGlobal { get; set; }

            /// <summary>True local rest transforms derived from bind global matrices in topological order.</summary>
            public Transform3D[] SourceBindLocal { get; set; }

            /// <summary>Indicates whether true skin bind matrices were present and applied.</summary>
            public bool HasSkinBind { get; set; }

            /// <summary>Human-readable description of the reference pose mode used (e.g. Skin Inverse Binds / Node Rest).</summary>
            public string ReferencePoseDescription { get; set; } = "Node Rest";

            public float NodeRestAsymmetryDeg { get; set; }
            public float BindPoseAsymmetryDeg { get; set; }

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

            result.RootScene = scene;
            result.SourceSkeleton = FindSkeleton(scene);
            if (result.SourceSkeleton != null)
            {
                CaptureSkinBindPose(scene, gltfState, result);
                result.SourceSkeleton.ResetBonePoses();
            }

            // Strip rendering geometry and materials to eliminate GPU/RenderingServer leaks
            // without deleting any nodes so Skeleton3D and AnimationPlayer are never orphaned.
            StripMeshResources(scene);

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

                result.RootScene = scene;
                result.SourceSkeleton = FindSkeleton(scene);
                if (result.SourceSkeleton != null)
                {
                    CaptureSkinBindPose(scene, null, result);
                    result.SourceSkeleton.ResetBonePoses();
                }

                // Strip rendering geometry without deleting nodes
                StripMeshResources(scene);

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
                    result.RootScene = scene;
                    result.SourceSkeleton = FindSkeleton(scene);
                    if (result.SourceSkeleton != null)
                    {
                        CaptureSkinBindPose(scene, fallbackState, result);
                        result.SourceSkeleton.ResetBonePoses();
                    }

                    StripMeshResources(scene);
                    ExtractAnimations(scene, result);
                    result.Success = true;
                    return;
                }
            }

            result.Success = false;
            result.ErrorMessage = "Could not parse FBX file. Please convert or export your animation as .glb or .gltf from Mixamo or Blender.";
        }

        private static void CaptureSkinBindPose(Node scene, GltfState gltfState, LoadResult result)
        {
            var sk = result.SourceSkeleton;
            if (sk == null) return;

            int boneCount = sk.GetBoneCount();
            var bindGlobals = new Transform3D[boneCount];
            var hasBind = new bool[boneCount];

            // Method 1: Inspect all MeshInstance3D nodes in the scene
            var meshInstances = new List<MeshInstance3D>();
            FindMeshInstances(scene, meshInstances);
            GD.Print($"[CaptureSkinBindPose] For '{result.FileName}': meshInstances={meshInstances.Count}, gltfSkins={gltfState?.GetSkins()?.Count}");

            foreach (var mi in meshInstances)
            {
                if (mi.Skin == null) continue;
                Skin skin = mi.Skin;
                int bindCount = skin.GetBindCount();
                for (int b = 0; b < bindCount; b++)
                {
                    int boneIdx = skin.GetBindBone(b);
                    if (boneIdx < 0 || boneIdx >= boneCount)
                    {
                        string bName = skin.GetBindName(b);
                        if (!string.IsNullOrEmpty(bName))
                        {
                            boneIdx = sk.FindBone(bName);
                        }
                    }

                    if (boneIdx >= 0 && boneIdx < boneCount && !hasBind[boneIdx])
                    {
                        Transform3D ibm = skin.GetBindPose(b);
                        bindGlobals[boneIdx] = ibm.AffineInverse();
                        hasBind[boneIdx] = true;
                    }
                }
            }

            // Method 2: If GltfState is available and some joints were missed, inspect gltfState.GetSkins()
            if (gltfState != null && gltfState.GetSkins() != null)
            {
                var skins = gltfState.GetSkins();
                var gltfNodes = gltfState.GetNodes();
                foreach (var gltfSkin in skins)
                {
                    if (gltfSkin == null || gltfSkin.Joints == null || gltfSkin.InverseBinds == null) continue;
                    int count = Math.Min(gltfSkin.Joints.Length, gltfSkin.InverseBinds.Count);
                    for (int j = 0; j < count; j++)
                    {
                        int nodeIdx = gltfSkin.Joints[j];
                        if (nodeIdx >= 0 && nodeIdx < gltfNodes.Count)
                        {
                            string nodeName = gltfNodes[nodeIdx].ResourceName;
                            int boneIdx = sk.FindBone(nodeName);
                            if (boneIdx >= 0 && boneIdx < boneCount && !hasBind[boneIdx])
                            {
                                Transform3D ibm = gltfSkin.InverseBinds[j];
                                bindGlobals[boneIdx] = ibm.AffineInverse();
                                hasBind[boneIdx] = true;
                            }
                        }
                    }
                }
            }

            int boundCount = hasBind.Count(h => h);
            if (boundCount == 0)
            {
                result.HasSkinBind = false;
                result.ReferencePoseDescription = "Node Rest (No Skin Binds)";
                GD.Print($"[ExternalAnimationLoader] '{result.FileName}': No skin bind matrices found. Using node rest pose.");
                return;
            }

            // Derive bindLocalRest for every bone in topological parent-first order
            var bindLocals = new Transform3D[boneCount];
            int[] order = AnimationRetargeter.GetTopologicalOrder(sk);

            foreach (int b in order)
            {
                int p = sk.GetBoneParent(b);
                Transform3D nodeRest = sk.GetBoneRest(b);

                if (hasBind[b])
                {
                    if (p == -1)
                    {
                        bindLocals[b] = bindGlobals[b];
                    }
                    else
                    {
                        if (hasBind[p])
                        {
                            bindLocals[b] = bindGlobals[p].AffineInverse() * bindGlobals[b];
                        }
                        else
                        {
                            Transform3D pGlobalRest = sk.GetBoneGlobalRest(p);
                            bindLocals[b] = pGlobalRest.AffineInverse() * bindGlobals[b];
                        }
                    }
                }
                else
                {
                    // Leaf or helper bone without direct skin bind: retain node rest relative to parent
                    bindLocals[b] = nodeRest;
                    if (p == -1)
                    {
                        bindGlobals[b] = nodeRest;
                    }
                    else
                    {
                        bindGlobals[b] = bindGlobals[p] * nodeRest;
                    }
                    hasBind[b] = true;
                }
            }

            result.SourceBindGlobal = bindGlobals;
            result.SourceBindLocal = bindLocals;
            result.HasSkinBind = true;
            result.ReferencePoseDescription = $"Skin Inverse Binds ({boundCount}/{boneCount} joints)";

            // Check symmetry before and after
            int FindClean(string target)
            {
                for (int i = 0; i < sk.GetBoneCount(); i++)
                {
                    string c = HumanoidBoneMapper.CleanBoneName(sk.GetBoneName(i));
                    if (c.Equals(target, StringComparison.OrdinalIgnoreCase)) return i;
                }
                return -1;
            }

            int thighL = FindClean("leftupleg"); if (thighL == -1) thighL = FindClean("thigh_l");
            int thighR = FindClean("rightupleg"); if (thighR == -1) thighR = FindClean("thigh_r");
            int calfL = FindClean("leftleg"); if (calfL == -1) calfL = FindClean("calf_l");
            int calfR = FindClean("rightleg"); if (calfR == -1) calfR = FindClean("calf_r");

            if (thighL != -1 && thighR != -1 && calfL != -1 && calfR != -1)
            {
                Vector3 nodeThighL = (sk.GetBoneGlobalRest(calfL).Origin - sk.GetBoneGlobalRest(thighL).Origin).Normalized();
                Vector3 nodeThighR = (sk.GetBoneGlobalRest(calfR).Origin - sk.GetBoneGlobalRest(thighR).Origin).Normalized();
                Vector3 reflNodeR = new Vector3(-nodeThighR.X, nodeThighR.Y, nodeThighR.Z);
                result.NodeRestAsymmetryDeg = Mathf.RadToDeg(Mathf.Acos(Math.Clamp(nodeThighL.Dot(reflNodeR), -1f, 1f)));

                Vector3 bindThighL = (bindGlobals[calfL].Origin - bindGlobals[thighL].Origin).Normalized();
                Vector3 bindThighR = (bindGlobals[calfR].Origin - bindGlobals[thighR].Origin).Normalized();
                Vector3 reflBindR = new Vector3(-bindThighR.X, bindThighR.Y, bindThighR.Z);
                result.BindPoseAsymmetryDeg = Mathf.RadToDeg(Mathf.Acos(Math.Clamp(bindThighL.Dot(reflBindR), -1f, 1f)));

                if (result.NodeRestAsymmetryDeg > 5f)
                {
                    GD.Print($"[ExternalAnimationLoader] '{result.FileName}': Detected dirty posed node-rest ({result.NodeRestAsymmetryDeg:F1}° asymmetry). Successfully restored true symmetric T-pose from skin binds ({result.BindPoseAsymmetryDeg:F1}° asymmetry).");
                }
            }

            // Update BoneRest on SourceSkeleton so all subsequent queries (and ResetBonePoses) evaluate the true T-pose
            for (int b = 0; b < boneCount; b++)
            {
                sk.SetBoneRest(b, bindLocals[b]);
            }
            sk.ResetBonePoses();

            GD.Print($"[ExternalAnimationLoader] '{result.FileName}': Successfully restored true T-pose bind from {boundCount} skin inverse bind matrices.");
        }

        private static void FindMeshInstances(Node current, List<MeshInstance3D> list)
        {
            if (current == null) return;
            if (current is MeshInstance3D mi) list.Add(mi);
            int count = current.GetChildCount();
            for (int i = 0; i < count; i++)
            {
                FindMeshInstances(current.GetChild(i), list);
            }
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
