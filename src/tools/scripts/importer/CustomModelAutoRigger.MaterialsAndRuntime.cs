using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using DeadlockPlayground.Animation;

namespace DeadlockPlayground.Importer
{
    public static partial class CustomModelAutoRigger
    {
        #region Runtime Twist & Limb Stretch Driver

        public static int EnsureForearmTwistBone(Skeleton3D skeleton, bool isLeft)
        {
            if (skeleton == null || !GodotObject.IsInstanceValid(skeleton)) return -1;
            string twistName = isLeft ? "arm_lower_L_TWIST" : "arm_lower_R_TWIST";
            int idx = skeleton.FindBone(twistName);
            if (idx == -1)
            {
                idx = skeleton.FindBone(isLeft ? "arm_lower_twist_L" : "arm_lower_twist_R");
            }

            string lowerName = isLeft ? "arm_lower_L" : "arm_lower_R";
            int lowerIdx = skeleton.FindBone(lowerName);
            if (lowerIdx == -1) return idx;

            string handName = isLeft ? "hand_L" : "hand_R";
            int handIdx = skeleton.FindBone(handName);

            Vector3 localHandPos = handIdx != -1
                ? skeleton.GetBoneRest(handIdx).Origin
                : (isLeft ? Vector3.Right * 0.25f : Vector3.Left * 0.25f);
            Transform3D twistRest = new Transform3D(Basis.Identity, localHandPos * 0.65f);

            if (idx == -1)
            {
                idx = skeleton.GetBoneCount();
                skeleton.AddBone(twistName);
                skeleton.SetBoneParent(idx, lowerIdx);
                skeleton.SetBoneRest(idx, twistRest);
            }
            else if (skeleton.GetBoneParent(idx) != lowerIdx)
            {
                skeleton.SetBoneParent(idx, lowerIdx);
            }

            return idx;
        }

        /// <summary>
        /// Ensures every mapped bone in heroSkeleton has a valid, non-zero InverseBindMatrix entry in skin.
        /// Searches existing binds by bone index or bone name, verifies/repairs the bind pose against
        /// skeleton.GetBoneGlobalRest(skeletonBoneIdx).AffineInverse() (within 1cm), or adds a new bind.
        /// </summary>
        public static int GetOrAddSkinBindIndex(Skin skin, Skeleton3D skeleton, int skeletonBoneIdx)
        {
            if (skin == null || skeleton == null || skeletonBoneIdx < 0 || skeletonBoneIdx >= skeleton.GetBoneCount())
                return 0;

            string boneName = skeleton.GetBoneName(skeletonBoneIdx);
            int bindCount = skin.GetBindCount();

            // 1. Search for existing bind matching either bone index or bone name
            for (int b = 0; b < bindCount; b++)
            {
                int bindBone = skin.GetBindBone(b);
                string bindName = skin.GetBindName(b);
                if (bindBone == skeletonBoneIdx || (!string.IsNullOrEmpty(bindName) && bindName == boneName))
                {
                    Transform3D restGlobal = skeleton.GetBoneGlobalRest(skeletonBoneIdx);
                    Transform3D expectedInv = restGlobal.AffineInverse();
                    Transform3D currentInv = skin.GetBindPose(b);
                    Transform3D currentPose = currentInv.AffineInverse();

                    bool originMismatch = currentPose.Origin.DistanceTo(restGlobal.Origin) > 0.01f;
                    Quaternion qCur = currentPose.Basis.Orthonormalized().GetRotationQuaternion();
                    Quaternion qRest = restGlobal.Basis.Orthonormalized().GetRotationQuaternion();
                    bool basisMismatch = qCur.AngleTo(qRest) > 0.01f;

                    if (originMismatch || basisMismatch ||
                        float.IsNaN(currentInv.Origin.X) || currentInv == Transform3D.Identity)
                    {
                        skin.SetBindPose(b, expectedInv);
                    }
                    if (skin.GetBindBone(b) != skeletonBoneIdx)
                    {
                        skin.SetBindBone(b, skeletonBoneIdx);
                    }
                    if (string.IsNullOrEmpty(bindName) || bindName != boneName)
                    {
                        skin.SetBindName(b, boneName);
                    }
                    return b;
                }
            }

            // 2. Not found in skin -> Add bind!
            Transform3D invBind = skeleton.GetBoneGlobalRest(skeletonBoneIdx).AffineInverse();
            skin.AddBind(skeletonBoneIdx, invBind);
            int newBindIdx = skin.GetBindCount() - 1;
            skin.SetBindName(newBindIdx, boneName);
            return newBindIdx;
        }

        public static void EnsureSkinHasBind(Skin skin, Skeleton3D skeleton, int boneIdx)
        {
            GetOrAddSkinBindIndex(skin, skeleton, boneIdx);
        }

        public static void DriveForearmTwist(Skeleton3D skeleton)
        {
            if (skeleton == null || !GodotObject.IsInstanceValid(skeleton)) return;
            DriveForearmTwistForArm(skeleton, isLeft: true);
            DriveForearmTwistForArm(skeleton, isLeft: false);
        }

        private static void DriveForearmTwistForArm(Skeleton3D skeleton, bool isLeft)
        {
            int lowerIdx = skeleton.FindBone(isLeft ? "arm_lower_L" : "arm_lower_R");
            int handIdx = skeleton.FindBone(isLeft ? "hand_L" : "hand_R");
            int twistIdx = skeleton.FindBone(isLeft ? "arm_lower_L_TWIST" : "arm_lower_R_TWIST");
            if (twistIdx == -1) twistIdx = skeleton.FindBone(isLeft ? "arm_lower_twist_L" : "arm_lower_twist_R");
            if (lowerIdx == -1 || handIdx == -1 || twistIdx == -1) return;

            Transform3D tHandRest = skeleton.GetBoneRest(handIdx);
            Vector3 handRestOffset = tHandRest.Origin;
            if (handRestOffset.LengthSquared() < 1e-6f) handRestOffset = isLeft ? Vector3.Right : Vector3.Left;
            Vector3 boneAxisInLower = handRestOffset.Normalized();

            // Transform forearm axis into hand local coordinate space
            Vector3 boneAxisInHand = (tHandRest.Basis.Inverse() * boneAxisInLower).Normalized();

            // Pose rotation of hand is already relative to rest
            Quaternion qHandPose = skeleton.GetBonePoseRotation(handIdx);

            // Swing-twist decomposition around forearm axis in hand space
            Vector3 qVec = new Vector3(qHandPose.X, qHandPose.Y, qHandPose.Z);
            float proj = qVec.Dot(boneAxisInHand);

            Quaternion qTwistInHand = new Quaternion(boneAxisInHand.X * proj, boneAxisInHand.Y * proj, boneAxisInHand.Z * proj, qHandPose.W);
            if (qTwistInHand.LengthSquared() > 1e-8f)
            {
                qTwistInHand = qTwistInHand.Normalized();
                if (qTwistInHand.W < 0)
                {
                    qTwistInHand = new Quaternion(-qTwistInHand.X, -qTwistInHand.Y, -qTwistInHand.Z, -qTwistInHand.W);
                }

                float twistAngle = 2.0f * Mathf.Atan2(new Vector3(qTwistInHand.X, qTwistInHand.Y, qTwistInHand.Z).Length(), qTwistInHand.W);
                if (proj < 0) twistAngle = -twistAngle;

                // Drive twist bone at 50% roll along the forearm axis in lower arm space
                Quaternion qHalfTwist = new Quaternion(boneAxisInLower, twistAngle * 0.5f).Normalized();
                skeleton.SetBonePoseRotation(twistIdx, qHalfTwist);
            }
            else
            {
                skeleton.SetBonePoseRotation(twistIdx, Quaternion.Identity);
            }
        }

        public static void ClampLimbBoneTranslations(Skeleton3D skeleton, float maxStretchMeters = 0.015f)
        {
            if (skeleton == null || !GodotObject.IsInstanceValid(skeleton)) return;
            int count = skeleton.GetBoneCount();
            float maxSq = maxStretchMeters * maxStretchMeters;

            for (int b = 0; b < count; b++)
            {
                string name = skeleton.GetBoneName(b).ToLowerInvariant();
                // Skip procedural/IK/twist/cloth/cervical/shoulder bones whose authored or constraint-driven translations exceed 1.5 cm legitimately
                if (IsExcludedClothOrAccessoryBone(name) ||
                    name.Contains("pelvis") || name.Contains("twist") ||
                    name.Contains("neck") || name.Contains("head") ||
                    name.Contains("clavicle") || name.Contains("finger"))
                {
                    continue;
                }

                bool isAnatomicalLimb = name.Contains("arm_upper") || name.Contains("arm_lower") ||
                                       name == "hand_l" || name == "hand_r" ||
                                       name.Contains("leg_upper") || name.Contains("leg_lower") ||
                                       name.Contains("ankle") || name.Contains("ball") ||
                                       name.Contains("thigh") || name.Contains("shin") ||
                                       name.Contains("calf") || name == "foot_l" || name == "foot_r";

                if (!isAnatomicalLimb) continue;

                Vector3 restOrigin = skeleton.GetBoneRest(b).Origin;
                Vector3 curOrigin = skeleton.GetBonePosePosition(b);
                Vector3 delta = curOrigin - restOrigin;
                if (delta.LengthSquared() > maxSq)
                {
                    skeleton.SetBonePosePosition(b, restOrigin + delta.Normalized() * maxStretchMeters);
                }
            }
        }

        public static void UpdateCustomModelRuntimePosing(Skeleton3D skeleton, bool clampStretch = true)
        {
            if (skeleton == null || !GodotObject.IsInstanceValid(skeleton)) return;
            if (clampStretch)
            {
                ClampLimbBoneTranslations(skeleton, 0.015f);
            }
            DriveForearmTwist(skeleton);
        }

        public static void RemoveAccessorySlot(Node3D heroNode, string slotId)
        {
            if (heroNode == null || !GodotObject.IsInstanceValid(heroNode) || string.IsNullOrWhiteSpace(slotId)) return;
            string containerName = GetContainerNameForSlot(slotId);
            var existing = heroNode.FindChild(containerName, true, false);
            if (existing != null && GodotObject.IsInstanceValid(existing))
            {
                existing.GetParent()?.RemoveChild(existing);
                existing.QueueFree();
            }
            var heroSkel = FindSkeleton(heroNode);
            if (heroSkel != null)
            {
                RemoveAppendedSlotBonesFromSkeleton(heroNode, heroSkel, slotId);
            }
            RecomputeHeroReplacementVisibility(heroNode);
        }

        public static void RemoveCustomModelAndRestoreHero(Node3D heroNode, bool includeAccessories = true)
        {
            if (heroNode == null || !GodotObject.IsInstanceValid(heroNode)) return;

            // 1. Remove Custom_Rigged_Character (and optionally all Custom_Accessory_* containers)
            var containers = heroNode.FindChildren("Custom_Rigged_Character", recursive: true, owned: false);
            foreach (var node in containers)
            {
                node.GetParent()?.RemoveChild(node);
                node.QueueFree();
            }

            if (includeAccessories)
            {
                foreach (var acc in CollectCustomContainers(heroNode))
                {
                    if (GodotObject.IsInstanceValid(acc))
                    {
                        acc.GetParent()?.RemoveChild(acc);
                        acc.QueueFree();
                    }
                }
            }

            // Also remove any standalone custom meshes matching reset scope
            var allMeshes = CollectMeshes(heroNode);
            foreach (var mi in allMeshes)
            {
                bool isSlotAcc = mi.HasMeta("AccessorySlotId") && !string.IsNullOrEmpty(mi.GetMeta("AccessorySlotId").AsString());
                if (!includeAccessories && isSlotAcc) continue;
                if (mi.HasMeta("IsCustomImportedMesh") || mi.Name.ToString().StartsWith("Custom_"))
                {
                    mi.GetParent()?.RemoveChild(mi);
                    mi.QueueFree();
                }
                else if (mi.HasMeta("ParentCompositeMesh"))
                {
                    // Remove any orphaned split submeshes before recomputing clean vanilla state
                    mi.MaterialOverlay = null;
                    mi.Visible = false;
                    mi.GetParent()?.RemoveChild(mi);
                    mi.QueueFree();
                }
            }

            var heroSkel = FindSkeleton(heroNode);
            if (heroSkel != null)
            {
                RemoveAppendedSlotBonesFromSkeleton(heroNode, heroSkel, includeAccessories ? null : "__main__");
            }

            // 2. Restore authentic Deadlock hero submeshes cleanly
            var heroMeshes = CollectOriginalHeroMeshes(heroNode);
            foreach (var mi in heroMeshes)
            {
                mi.MaterialOverlay = null;
                if (mi.HasMeta("IsHiddenComposite")) mi.RemoveMeta("IsHiddenComposite");
                if (mi.HasMeta("GeneratedSubmeshes")) mi.RemoveMeta("GeneratedSubmeshes");

                if (includeAccessories)
                {
                    mi.Visible = true;
                    if (mi.HasMeta("UserVisibility")) mi.SetMeta("UserVisibility", true);
                    if (mi.HasMeta("UserHiddenByCustomModel")) mi.RemoveMeta("UserHiddenByCustomModel");
                    int surfCount = mi.Mesh?.GetSurfaceCount() ?? 0;
                    for (int s = 0; s < surfCount; s++)
                    {
                        if (mi.HasMeta($"SurfaceKept_{s}")) mi.RemoveMeta($"SurfaceKept_{s}");
                        Material origMat = mi.HasMeta($"OriginalMaterial_{s}") ? mi.GetMeta($"OriginalMaterial_{s}").As<Material>() : null;
                        if (origMat != null) mi.SetSurfaceOverrideMaterial(s, origMat);
                        else if (mi.GetSurfaceOverrideMaterial(s) != null && mi.GetSurfaceOverrideMaterial(s).HasMeta("IsHiddenSurfaceMaterial"))
                            mi.SetSurfaceOverrideMaterial(s, null);
                    }
                }
                else
                {
                    // Clear stale per-surface custom flags before RecomputeHeroReplacementVisibility re-evaluates remaining accessories
                    int surfCount = mi.Mesh?.GetSurfaceCount() ?? 0;
                    for (int s = 0; s < surfCount; s++)
                    {
                        if (mi.HasMeta($"SurfaceKept_{s}")) mi.RemoveMeta($"SurfaceKept_{s}");
                    }
                }
            }

            if (!includeAccessories)
            {
                RecomputeHeroReplacementVisibility(heroNode);
                return;
            }

            // 3. Restore any original hero deformations cleanly
            RecomputeHeroDeformations(heroNode);
        }

        /// <summary>
        /// Loads a 3D model file (.glb, .gltf, .fbx), automatically repairing Blender KHR_materials_unlit / Emission
        /// materials (where baseColorFactor is [0,0,0,1] and textures/colors live in emissiveTexture/emissiveFactor),
        /// disambiguating generic material names, and tagging inverted-hull outline shells.
        /// </summary>
        public static Node3D LoadAndSanitize3DModelScene(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return null;

            Node importedScene = null;
            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            string baseDir = Path.GetDirectoryName(filePath) ?? "";

            try
            {
                if (ext == ".fbx")
                {
                    try
                    {
                        var fbxDoc = new FbxDocument();
                        var fbxState = new FbxState();
                        if (fbxDoc.AppendFromFile(filePath, fbxState) == Error.Ok)
                        {
                            importedScene = fbxDoc.GenerateScene(fbxState);
                        }
                    }
                    catch (Exception fbxEx)
                    {
                        GD.Print($"[CustomModelAutoRigger] Direct FBX load warning: {fbxEx.Message}. Trying GltfDocument fallback...");
                    }
                }

                if (importedScene == null && ext == ".glb")
                {
                    try
                    {
                        byte[] rawGlb = File.ReadAllBytes(filePath);
                        byte[] patchedGlb = PreprocessGlbBytesForUnlitAndEmissiveMaterials(rawGlb);
                        rawGlb = null; // Release raw buffer promptly so peak RAM stays minimal
                        var glbManifest = DeadlockPlayground.Materials.MaterialChannelDiscovery.ParseGlbManifest(patchedGlb);
                        var gltfDoc = new GltfDocument();
                        var gltfState = new GltfState();
                        if (gltfDoc.AppendFromBuffer(patchedGlb, baseDir, gltfState) == Error.Ok)
                        {
                            patchedGlb = null;
                            importedScene = gltfDoc.GenerateScene(gltfState);
                            if (importedScene != null)
                            {
                                DeadlockPlayground.Materials.MaterialChannelDiscovery.RegisterGlbManifest(importedScene, glbManifest);
                            }
                        }
                    }
                    catch (Exception glbEx)
                    {
                        GD.Print($"[CustomModelAutoRigger] GLB buffer preprocess warning: {glbEx.Message}. Falling back to AppendFromFile.");
                    }
                }

                if (importedScene == null)
                {
                    var gltfDoc = new GltfDocument();
                    var gltfState = new GltfState();
                    if (gltfDoc.AppendFromFile(filePath, gltfState) == Error.Ok)
                    {
                        importedScene = gltfDoc.GenerateScene(gltfState);
                    }
                }
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[CustomModelAutoRigger] Failed to load 3D file '{filePath}': {ex.Message}");
                return null;
            }

            if (importedScene == null) return null;

            var sceneRoot = importedScene as Node3D ?? new Node3D();
            if (sceneRoot != importedScene)
            {
                var childManifest = DeadlockPlayground.Materials.MaterialChannelDiscovery.TryGetGlbManifest(importedScene);
                if (childManifest != null)
                {
                    DeadlockPlayground.Materials.MaterialChannelDiscovery.RegisterGlbManifest(sceneRoot, childManifest);
                }
                sceneRoot.AddChild(importedScene);
            }

            if (!string.IsNullOrEmpty(filePath))
            {
                sceneRoot.SetMeta("ImportedSourceBaseDir", Path.GetDirectoryName(filePath) ?? "");
                sceneRoot.SetMeta("ImportedSourceFilePath", filePath);
            }

            SanitizeImportedModelMaterials(sceneRoot);
            return sceneRoot;
        }

        /// <summary>
        /// Repairs binary glTF 2.0 (.glb) JSON chunk in-memory before Godot's GltfDocument parses it:
        /// 1. When a material uses KHR_materials_unlit or Emission nodes with pbrMetallicRoughness.baseColorFactor = [0,0,0,1]:
        ///    - Copies emissiveTexture to pbrMetallicRoughness.baseColorTexture if baseColorTexture is missing (e.g. Sasha's 'Iris' pupils!).
        ///    - Sets baseColorFactor RGB to [1,1,1] (if textured) or emissiveFactor (if untextured, e.g. Sasha's white eye sclera 'material'!).
        /// 2. Disambiguates generic Blender material names ("material", "Material.001", etc.) with the mesh name so distinct materials never collide.
        /// </summary>
        public static byte[] PreprocessGlbBytesForUnlitAndEmissiveMaterials(byte[] glbBytes)
        {
            if (glbBytes == null || glbBytes.Length < 20) return glbBytes;

            uint magic = BitConverter.ToUInt32(glbBytes, 0);
            uint version = BitConverter.ToUInt32(glbBytes, 4);
            if (magic != 0x46546C67u || version != 2u) return glbBytes;

            uint jsonLen = BitConverter.ToUInt32(glbBytes, 12);
            uint jsonType = BitConverter.ToUInt32(glbBytes, 16);
            if (jsonType != 0x4E4F534Au || 20 + jsonLen > glbBytes.Length) return glbBytes;

            string jsonStr = System.Text.Encoding.UTF8.GetString(glbBytes, 20, (int)jsonLen).TrimEnd('\0', ' ', '\t', '\r', '\n');
            var rootNode = System.Text.Json.Nodes.JsonNode.Parse(jsonStr) as System.Text.Json.Nodes.JsonObject;
            if (rootNode == null) return glbBytes;

            var materialsArr = rootNode["materials"] as System.Text.Json.Nodes.JsonArray;
            if (materialsArr == null || materialsArr.Count == 0) return glbBytes;

            // Map material index -> owning mesh name for disambiguating generic "material" / "Material.001" names
            var matIdxToOwnerMeshName = new Dictionary<int, string>();
            if (rootNode["meshes"] is System.Text.Json.Nodes.JsonArray meshesArr)
            {
                for (int m = 0; m < meshesArr.Count; m++)
                {
                    if (meshesArr[m] is not System.Text.Json.Nodes.JsonObject meshObj) continue;
                    string mName = meshObj["name"]?.GetValue<string>() ?? $"mesh_{m}";
                    if (meshObj["primitives"] is System.Text.Json.Nodes.JsonArray prims)
                    {
                        foreach (var prim in prims)
                        {
                            if (prim is System.Text.Json.Nodes.JsonObject pObj && pObj["material"] != null)
                            {
                                int matIdx = pObj["material"].GetValue<int>();
                                if (!matIdxToOwnerMeshName.ContainsKey(matIdx))
                                {
                                    matIdxToOwnerMeshName[matIdx] = mName;
                                }
                            }
                        }
                    }
                }
            }

            var materialsUsingTexCoord1 = new HashSet<int>();
            bool modified = false;
            for (int i = 0; i < materialsArr.Count; i++)
            {
                if (materialsArr[i] is not System.Text.Json.Nodes.JsonObject matObj) continue;

                string matName = matObj["name"]?.GetValue<string>() ?? $"mat_{i}";
                string lowerMat = matName.ToLowerInvariant();

                // Disambiguate generic Blender default material names ("material", "material.001", etc.)
                if (lowerMat == "material" || lowerMat.StartsWith("material.") || lowerMat.StartsWith("material_"))
                {
                    if (matIdxToOwnerMeshName.TryGetValue(i, out string ownerMesh) && !string.IsNullOrWhiteSpace(ownerMesh))
                    {
                        string suffix = lowerMat == "material" ? "mat" : matName.Replace('.', '_');
                        string cleanOwner = ownerMesh.Replace('.', '_').Replace(' ', '_');
                        matObj["name"] = $"{cleanOwner}_{suffix}";
                        modified = true;
                    }
                }

                var pbr = matObj["pbrMetallicRoughness"] as System.Text.Json.Nodes.JsonObject;
                if (pbr == null)
                {
                    pbr = new System.Text.Json.Nodes.JsonObject();
                    matObj["pbrMetallicRoughness"] = pbr;
                    modified = true;
                }

                // 0. If baseColorTexture uses texCoord == 1 (UV2), record the material so its mesh primitives
                //    promote TEXCOORD_1 -> TEXCOORD_0 (since Godot StandardMaterial3D and Source 2 SMD only sample UV0).
                if (pbr["baseColorTexture"] is System.Text.Json.Nodes.JsonObject bcTexObj &&
                    bcTexObj["texCoord"] != null &&
                    bcTexObj["texCoord"].GetValue<int>() == 1)
                {
                    bcTexObj["texCoord"] = 0;
                    bcTexObj["wasTexCoord1"] = true;
                    materialsUsingTexCoord1.Add(i);
                    modified = true;
                }

                // 1. If baseColorTexture is missing but emissiveTexture is present, promote emissiveTexture to baseColorTexture!
                if (pbr["baseColorTexture"] == null && matObj["emissiveTexture"] is System.Text.Json.Nodes.JsonObject emTex)
                {
                    var clonedTex = System.Text.Json.Nodes.JsonNode.Parse(emTex.ToJsonString()) as System.Text.Json.Nodes.JsonObject;
                    if (clonedTex != null)
                    {
                        pbr["baseColorTexture"] = clonedTex;
                        modified = true;
                    }
                }

                // 2. Check if baseColorFactor is near-black [0, 0, 0, a] while texture or emissiveFactor is present
                float r = 1f, g = 1f, b = 1f, a = 1f;
                if (pbr["baseColorFactor"] is System.Text.Json.Nodes.JsonArray bcf && bcf.Count >= 3)
                {
                    r = bcf[0]?.GetValue<float>() ?? 1f;
                    g = bcf[1]?.GetValue<float>() ?? 1f;
                    b = bcf[2]?.GetValue<float>() ?? 1f;
                    if (bcf.Count >= 4) a = bcf[3]?.GetValue<float>() ?? 1f;
                }

                float er = 0f, eg = 0f, eb = 0f;
                if (matObj["emissiveFactor"] is System.Text.Json.Nodes.JsonArray ef && ef.Count >= 3)
                {
                    er = ef[0]?.GetValue<float>() ?? 0f;
                    eg = ef[1]?.GetValue<float>() ?? 0f;
                    eb = ef[2]?.GetValue<float>() ?? 0f;
                }

                bool hasBaseTex = pbr["baseColorTexture"] != null;
                bool isBlackFactor = r < 0.02f && g < 0.02f && b < 0.02f;

                if (isBlackFactor)
                {
                    if (hasBaseTex)
                    {
                        // Do not multiply textured unlit/emissive materials (like Bodysuit or Iris) by black [0,0,0]!
                        pbr["baseColorFactor"] = new System.Text.Json.Nodes.JsonArray(1.0f, 1.0f, 1.0f, a);
                        modified = true;
                    }
                    else if (er > 0.02f || eg > 0.02f || eb > 0.02f)
                    {
                        // Promote non-zero emissiveFactor (e.g. eye sclera [1,1,1] or colored outline shell) to baseColorFactor!
                        pbr["baseColorFactor"] = new System.Text.Json.Nodes.JsonArray(
                            Math.Clamp(er, 0f, 1f),
                            Math.Clamp(eg, 0f, 1f),
                            Math.Clamp(eb, 0f, 1f),
                            a);
                        modified = true;
                    }
                }
            }

            // Promote TEXCOORD_1 -> TEXCOORD_0 on any mesh primitive whose material specified baseColorTexture.texCoord == 1
            if (materialsUsingTexCoord1.Count > 0 && rootNode["meshes"] is System.Text.Json.Nodes.JsonArray meshesForUv)
            {
                foreach (var meshNode in meshesForUv)
                {
                    if (meshNode is not System.Text.Json.Nodes.JsonObject meshObj) continue;
                    if (meshObj["primitives"] is not System.Text.Json.Nodes.JsonArray prims) continue;
                    foreach (var prim in prims)
                    {
                        if (prim is not System.Text.Json.Nodes.JsonObject pObj || pObj["material"] == null) continue;
                        int matIdx = pObj["material"].GetValue<int>();
                        if (!materialsUsingTexCoord1.Contains(matIdx)) continue;
                        if (pObj["attributes"] is System.Text.Json.Nodes.JsonObject attrs &&
                            attrs["TEXCOORD_1"] != null &&
                            attrs["TEXCOORD_0"] != null)
                        {
                            int tc0 = attrs["TEXCOORD_0"].GetValue<int>();
                            int tc1 = attrs["TEXCOORD_1"].GetValue<int>();
                            attrs["TEXCOORD_0"] = tc1;
                            attrs["TEXCOORD_1"] = tc0;
                            pObj["swappedTexCoord1To0"] = true;
                            modified = true;
                        }
                    }
                }
            }

            if (!modified) return glbBytes;

            byte[] newJsonBytes = System.Text.Encoding.UTF8.GetBytes(rootNode.ToJsonString());
            int pad = (4 - (newJsonBytes.Length % 4)) % 4;
            int paddedJsonLen = newJsonBytes.Length + pad;

            int restOffset = 20 + (int)jsonLen;
            int restLen = glbBytes.Length - restOffset;
            uint totalLen = (uint)(20 + paddedJsonLen + restLen);

            byte[] outBytes = new byte[totalLen];
            using var ms = new MemoryStream(outBytes);
            using var bw = new BinaryWriter(ms);
            bw.Write(0x46546C67u);
            bw.Write(2u);
            bw.Write(totalLen);
            bw.Write((uint)paddedJsonLen);
            bw.Write(0x4E4F534Au);
            bw.Write(newJsonBytes);
            for (int p = 0; p < pad; p++) bw.Write((byte)0x20);
            if (restLen > 0)
            {
                bw.Write(glbBytes, restOffset, restLen);
            }
            return outBytes;
        }

        /// <summary>
        /// Inspects all MeshInstance3D nodes and their surfaces in an imported 3D scene:
        /// - Repairs any StandardMaterial3D where AlbedoTexture is null but EmissionTexture is present,
        ///   or where AlbedoColor is black (0,0,0) while a texture or Emission color is present.
        /// - Disables VertexColorUseAsAlbedo if the mesh has an all-black vertex color array.
        /// - Detects Inverted-Hull Outline Shells ("outline_*") and configures single-sided culling + unshaded mode.
        /// </summary>
        public static void SanitizeImportedModelMaterials(Node3D sceneRoot, Dictionary<string, bool> outlineOverrides = null)
        {
            if (sceneRoot == null || !GodotObject.IsInstanceValid(sceneRoot)) return;

            var assignedMatNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var matToUniqueName = new Dictionary<Material, string>();
            var glbManifest = DeadlockPlayground.Materials.MaterialChannelDiscovery.TryGetGlbManifest(sceneRoot);

            // Only scan sibling external textures lazily when the source is .fbx/.gltf or a material lacks AlbedoTexture,
            // avoiding decoding up to 48 unrelated images from Downloads/staging folders for self-contained .glb files!
            string srcPathMeta = sceneRoot.HasMeta("ImportedSourceFilePath") ? sceneRoot.GetMeta("ImportedSourceFilePath").AsString() : "";
            bool isFbxOrGltf = srcPathMeta.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase) ||
                               srcPathMeta.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase);
            List<Texture2D> externalTexturePool = null;
            List<Texture2D> GetExternalTexturePool()
            {
                if (externalTexturePool == null)
                    externalTexturePool = CollectSiblingExternalTextures(sceneRoot);
                return externalTexturePool;
            }

            var meshes = CollectMeshes(sceneRoot);
            foreach (var mi in meshes)
            {
                if (mi?.Mesh == null) continue;
                string rawMeshName = mi.Name.ToString();
                int surfCount = mi.Mesh.GetSurfaceCount();

                for (int s = 0; s < surfCount; s++)
                {
                    Material mat = mi.Mesh.SurfaceGetMaterial(s) ?? mi.GetSurfaceOverrideMaterial(s) ?? mi.MaterialOverride;
                    if (mat == null) continue;

                    // Disambiguate material name across distinct material instances so generic names
                    // (like "material", "Material.001", "lambert1", "default") never collide between meshes/surfaces
                    if (!matToUniqueName.TryGetValue(mat, out string uniqueMatName))
                    {
                        string rawMatName = mat.ResourceName?.Trim();
                        string lowerRaw = (rawMatName ?? "").ToLowerInvariant();
                        bool isGenericMat = string.IsNullOrWhiteSpace(rawMatName) ||
                            lowerRaw == "material" || lowerRaw.StartsWith("material.") || lowerRaw.StartsWith("material_") ||
                            lowerRaw == "default" || lowerRaw == "defaultmaterial" || lowerRaw == "custom_mat" ||
                            lowerRaw.StartsWith("lambert") || lowerRaw.StartsWith("phong") || lowerRaw.StartsWith("blinn") ||
                            lowerRaw == "standard" || lowerRaw.StartsWith("standard.");

                        if (isGenericMat)
                        {
                            string suffixTag = string.IsNullOrWhiteSpace(rawMatName) ? "mat" : rawMatName.Replace('.', '_').Replace(' ', '_');
                            rawMatName = surfCount > 1 ? $"{rawMeshName}_s{s}_{suffixTag}" : $"{rawMeshName}_{suffixTag}";
                        }

                        uniqueMatName = rawMatName;
                        int suffix = 1;
                        while (assignedMatNames.Contains(uniqueMatName))
                        {
                            uniqueMatName = $"{rawMatName}_{suffix++}";
                        }

                        assignedMatNames.Add(uniqueMatName);
                        matToUniqueName[mat] = uniqueMatName;
                        mat.ResourceName = uniqueMatName;
                    }

                    mi.SetMeta($"SourceMaterialName_{s}", uniqueMatName);
                    if (s == 0) mi.SetMeta("SourceMaterialName", uniqueMatName);
                    mat.SetMeta("MaterialName", uniqueMatName);

                    // Check if vertex color array is all black/zero
                    bool hasAllBlackVertexColors = false;
                    var arrays = mi.Mesh.SurfaceGetArrays(s);
                    if (arrays != null && arrays.Count > (int)Mesh.ArrayType.Color && arrays[(int)Mesh.ArrayType.Color].VariantType != Variant.Type.Nil)
                    {
                        var colors = arrays[(int)Mesh.ArrayType.Color].AsColorArray();
                        if (colors != null && colors.Length > 0)
                        {
                            bool anyNonBlack = false;
                            int step = Math.Max(1, colors.Length / 128);
                            for (int c = 0; c < colors.Length; c += step)
                            {
                                if (colors[c].R > 0.02f || colors[c].G > 0.02f || colors[c].B > 0.02f)
                                {
                                    anyNonBlack = true;
                                    break;
                                }
                            }
                            hasAllBlackVertexColors = !anyNonBlack;
                        }
                    }

                    bool isOutline = IsLikelyOutlineShell(mi, mat);
                    if (outlineOverrides != null && outlineOverrides.TryGetValue(rawMeshName, out bool userOutline))
                    {
                        isOutline = userOutline;
                    }

                    mi.SetMeta("IsOutlineShell", isOutline);

                    if (mat is StandardMaterial3D stdMat)
                    {
                        if (hasAllBlackVertexColors && stdMat.VertexColorUseAsAlbedo)
                        {
                            stdMat.VertexColorUseAsAlbedo = false;
                        }

                        // Repair unlit/emissive texture & color if not already patched at GLB level
                        if (stdMat.AlbedoTexture == null && stdMat.EmissionTexture != null)
                        {
                            stdMat.AlbedoTexture = stdMat.EmissionTexture;
                        }

                        // For FBX/GLTF materials where Godot left AlbedoTexture null (e.g. external .fbm / Textures/ files
                        // referenced by absolute artist paths), try matching a sibling color texture before baking solid white!
                        if (stdMat.AlbedoTexture == null && isFbxOrGltf)
                        {
                            var matchedAlbedo = TryMatchSiblingAlbedoTexture(GetExternalTexturePool(), uniqueMatName, rawMeshName);
                            if (matchedAlbedo != null)
                            {
                                stdMat.AlbedoTexture = matchedAlbedo;
                                GD.Print($"[CustomModelAutoRigger] Bound sibling albedo texture '{matchedAlbedo.ResourceName}' to FBX/GLTF material '{uniqueMatName}'.");
                            }
                        }

                        // If material has no AlbedoTexture, bake its solid AlbedoColor into a 16x16 solid-color ImageTexture!
                        // This prevents untextured solid-color materials (e.g. mouth, eye whites, outline_black, outline_for_cube)
                        // from turning black in Painter or borrowing the pistol's texture!
                        if (stdMat.AlbedoTexture == null)
                        {
                            Color solidCol = stdMat.AlbedoColor;
                            if (stdMat.EmissionEnabled && (stdMat.Emission.R > 0.02f || stdMat.Emission.G > 0.02f || stdMat.Emission.B > 0.02f) &&
                                solidCol.R < 0.02f && solidCol.G < 0.02f && solidCol.B < 0.02f)
                            {
                                solidCol = stdMat.Emission;
                                // Pure emissive / Roblox Neon materials exported from Blender often omit glTF metallicFactor,
                                // which defaults to 1.0 and turns the surface solid black in PBR/Source 2 shaders.
                                if (stdMat.MetallicTexture == null && stdMat.Metallic >= 0.99f)
                                {
                                    stdMat.Metallic = 0.0f;
                                }
                            }

                            var img = Image.CreateEmpty(16, 16, false, Image.Format.Rgba8);
                            img.Fill(solidCol);
                            var solidTex = ImageTexture.CreateFromImage(img);
                            solidTex.ResourceName = $"{uniqueMatName}_solid_tex";
                            stdMat.AlbedoTexture = solidTex;
                            stdMat.AlbedoColor = Colors.White;
                        }
                        else
                        {
                            bool isBlackAlbedo = stdMat.AlbedoColor.R < 0.02f && stdMat.AlbedoColor.G < 0.02f && stdMat.AlbedoColor.B < 0.02f;
                            if (isBlackAlbedo)
                            {
                                stdMat.AlbedoColor = new Color(1f, 1f, 1f, stdMat.AlbedoColor.A > 0f ? stdMat.AlbedoColor.A : 1f);
                            }
                        }

                        // Clear redundant unlit Emission so Toon Shader doesn't add (1,1,1) white emission on top of Albedo
                        if (stdMat.ShadingMode == BaseMaterial3D.ShadingModeEnum.Unshaded && stdMat.EmissionEnabled)
                        {
                            stdMat.EmissionEnabled = false;
                            stdMat.Emission = Colors.Black;
                        }
                        // Prevent glTF omitted metallicFactor (=1.0) on unlit/emissive/outline materials without a metalness
                        // texture (e.g. Roblox Neon hair Cube.054Accessory) from turning solid black on VPK export/reload.
                        if (stdMat.MetallicTexture == null && stdMat.Metallic >= 0.99f &&
                            (isOutline || stdMat.ShadingMode == BaseMaterial3D.ShadingModeEnum.Unshaded || stdMat.EmissionEnabled || stdMat.Roughness >= 0.85f))
                        {
                            stdMat.Metallic = 0.0f;
                        }

                        stdMat.SetMeta("IsOutlineShell", isOutline);
                        if (!stdMat.HasMeta("SourceTransparencyMode"))
                        {
                            stdMat.SetMeta("SourceTransparencyMode", (int)stdMat.Transparency);
                            stdMat.SetMeta("SourceAlphaScissorThreshold", stdMat.AlphaScissorThreshold);
                        }
                        if (!stdMat.HasMeta("SourceCullMode"))
                        {
                            stdMat.SetMeta("SourceCullMode", (int)stdMat.CullMode);
                        }

                        if (isOutline)
                        {
                            // Inverted-hull outline shells must NEVER be two-sided (CullModeEnum.Disabled),
                            // otherwise the front of the outer shell covers the character body underneath!
                            if (stdMat.CullMode == BaseMaterial3D.CullModeEnum.Disabled)
                            {
                                stdMat.CullMode = BaseMaterial3D.CullModeEnum.Back;
                            }
                            stdMat.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
                        }
                    }

                    if (!isOutline && DeadlockPlayground.Materials.MaterialChannelDiscovery.TryGetChannelSet(mat) == null)
                    {
                        DeadlockPlayground.Materials.GlbMaterialSlotInfo matchedSlot = null;
                        if (glbManifest != null)
                        {
                            string rawMatName = mat.ResourceName ?? "";
                            matchedSlot = glbManifest.Materials.FirstOrDefault(gm =>
                                !string.IsNullOrEmpty(gm.MaterialName) &&
                                (string.Equals(gm.MaterialName, rawMatName, StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(gm.MaterialName, uniqueMatName, StringComparison.OrdinalIgnoreCase)));
                        }
                        var discovered = DeadlockPlayground.Materials.MaterialChannelDiscovery.DiscoverCustomModelChannels(
                            mat,
                            uniqueMatName,
                            matchedSlot,
                            isFbxOrGltf ? GetExternalTexturePool() : null);
                        DeadlockPlayground.Materials.MaterialChannelDiscovery.RegisterChannelSet(mat, discovered);

                        if (mat is StandardMaterial3D stdMeta)
                        {
                            if (discovered.IsSourceTranslucent)
                            {
                                stdMeta.SetMeta("SourceTransparencyMode", (int)BaseMaterial3D.TransparencyEnum.Alpha);
                            }
                            else if (discovered.IsSourceAlphaTest)
                            {
                                stdMeta.SetMeta("SourceTransparencyMode", (int)BaseMaterial3D.TransparencyEnum.AlphaScissor);
                                stdMeta.SetMeta("SourceAlphaScissorThreshold", discovered.SourceAlphaCutoff);
                            }
                            if (matchedSlot != null && matchedSlot.BaseColorTexCoord == 1)
                            {
                                stdMeta.SetMeta("UseTexCoord1", true);
                            }
                        }
                    }
                }
            }
        }

        private static Vector2[] ResolvePrimaryUvsForSurface(Mesh srcMesh, MeshInstance3D srcMi, int surfaceIdx, Godot.Collections.Array arrays)
        {
            if (arrays == null) return null;
            Material mat = srcMesh?.SurfaceGetMaterial(surfaceIdx) ?? srcMi?.GetSurfaceOverrideMaterial(surfaceIdx) ?? srcMi?.MaterialOverride;
            bool preferUv2 = mat != null && mat.HasMeta("UseTexCoord1") && mat.GetMeta("UseTexCoord1").AsBool();
            if (preferUv2 && arrays.Count > (int)Mesh.ArrayType.TexUV2)
            {
                var uv2 = GetVector2Array(arrays[(int)Mesh.ArrayType.TexUV2]);
                if (uv2 != null && uv2.Length > 0) return uv2;
            }
            return arrays.Count > (int)Mesh.ArrayType.TexUV ? GetVector2Array(arrays[(int)Mesh.ArrayType.TexUV]) : null;
        }

        private static List<Texture2D> CollectSiblingExternalTextures(Node3D sceneRoot)
        {
            var pool = new List<Texture2D>();
            if (sceneRoot == null || !sceneRoot.HasMeta("ImportedSourceBaseDir")) return pool;
            string baseDir = sceneRoot.GetMeta("ImportedSourceBaseDir").AsString();
            if (string.IsNullOrEmpty(baseDir) || !Directory.Exists(baseDir)) return pool;

            var searchDirs = new List<string> { baseDir };
            string texSub = Path.Combine(baseDir, "textures");
            if (Directory.Exists(texSub)) searchDirs.Add(texSub);
            string texCapSub = Path.Combine(baseDir, "Textures");
            if (Directory.Exists(texCapSub) && !string.Equals(texSub, texCapSub, StringComparison.OrdinalIgnoreCase))
                searchDirs.Add(texCapSub);

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string dir in searchDirs)
            {
                try
                {
                    foreach (string file in Directory.EnumerateFiles(dir))
                    {
                        if (!seen.Add(file)) continue;
                        string ext = Path.GetExtension(file).ToLowerInvariant();
                        if (ext != ".png" && ext != ".tga" && ext != ".jpg" && ext != ".jpeg" && ext != ".webp") continue;
                        if (pool.Count >= 48) break;
                        var img = new Image();
                        if (img.Load(file) == Error.Ok)
                        {
                            var tex = ImageTexture.CreateFromImage(img);
                            tex.ResourceName = Path.GetFileName(file);
                            pool.Add(tex);
                        }
                    }
                }
                catch { }
            }
            return pool;
        }

        /// <summary>
        /// Auto-detects whether a MeshInstance3D or its Material is an Inverted-Hull Outline Shell
        /// (e.g. "Outline_Black", "Outline for Cube_005", "Outline for pistol", "Sasha.Body.Outline").
        /// </summary>
        private static int FindNearestCoreHeroBone(Skeleton3D heroSkeleton, Vector3 posHeroSpace, float surfaceMaxY = float.NaN)
        {
            if (heroSkeleton == null) return 0;

            int headIdx = FindBoneAny(heroSkeleton, "head", "head_01", "head0");
            Vector3 headOrigin = Vector3.Zero;
            if (headIdx >= 0)
            {
                headOrigin = heroSkeleton.GetBoneGlobalRest(headIdx).Origin;
                // Surface centered near/above the skull/jaw plane (head, hair, hats, face accessories) belongs to head.
                if (posHeroSpace.Y >= headOrigin.Y - 0.14f)
                {
                    return headIdx;
                }
                // Long hair / hoods / tall hats hang down behind the neck/shoulders so their centroid sits below
                // headOrigin.Y - 0.14f even though their crown reaches the skull. Bind them to head (never neck_0
                // or arm_upper_L, which rips hair apart when synthetic head-anchored cloth chains are added!).
                if (!float.IsNaN(surfaceMaxY) && surfaceMaxY >= headOrigin.Y - 0.05f)
                {
                    float dx = posHeroSpace.X - headOrigin.X;
                    float dz = posHeroSpace.Z - headOrigin.Z;
                    if (dx * dx + dz * dz <= 0.38f * 0.38f)
                    {
                        return headIdx;
                    }
                }
            }

            string[] coreNames =
            {
                "head", "neck_0", "spine_3", "spine_2", "spine_1", "spine_0", "pelvis",
                "arm_upper_L", "arm_lower_L", "hand_L",
                "arm_upper_R", "arm_lower_R", "hand_R",
                "leg_upper_L", "leg_lower_L", "ankle_L",
                "leg_upper_R", "leg_lower_R", "ankle_R"
            };

            int spine2Idx = heroSkeleton.FindBone("spine_2");
            float spine2Y = spine2Idx >= 0 ? heroSkeleton.GetBoneGlobalRest(spine2Idx).Origin.Y : float.MaxValue;
            bool isMidlineUpper = headIdx >= 0 && MathF.Abs(posHeroSpace.X - headOrigin.X) < 0.16f && posHeroSpace.Y >= spine2Y;

            int bestIdx = -1;
            float bestDistSq = float.MaxValue;
            foreach (string name in coreNames)
            {
                if (isMidlineUpper && (name.StartsWith("arm_", StringComparison.Ordinal) || name.StartsWith("hand_", StringComparison.Ordinal)))
                    continue;
                int idx = heroSkeleton.FindBone(name);
                if (idx < 0) continue;
                float dSq = heroSkeleton.GetBoneGlobalRest(idx).Origin.DistanceSquaredTo(posHeroSpace);
                if (dSq < bestDistSq)
                {
                    bestDistSq = dSq;
                    bestIdx = idx;
                }
            }

            return bestIdx >= 0 ? bestIdx : 0;
        }

        public static bool IsLikelyOutlineShell(MeshInstance3D mi, Material mat = null)
        {
            if (mi == null) return false;
            mat ??= mi.Mesh != null && mi.Mesh.GetSurfaceCount() > 0
                ? (mi.Mesh.SurfaceGetMaterial(0) ?? mi.GetSurfaceOverrideMaterial(0) ?? mi.MaterialOverride)
                : null;

            string meshName = mi.Name.ToString();
            string matName = mat?.ResourceName ?? "";
            if (string.IsNullOrEmpty(matName) && mi.HasMeta("SourceMaterialName"))
            {
                matName = mi.GetMeta("SourceMaterialName").AsString();
            }

            if (matName.Contains("outline", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // On multi-surface meshes (e.g. Roblox merged head/hair/accessory meshes whose parent node
            // happens to be named after an outline accessory), trust the per-surface material name
            // rather than falsely flagging every surface (head, hair, eyes) as an outline shell.
            bool isMultiSurfaceWithNamedMat = mi.Mesh != null && mi.Mesh.GetSurfaceCount() > 1 && !string.IsNullOrWhiteSpace(matName);
            if (!isMultiSurfaceWithNamedMat && meshName.Contains("outline", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }

        private static void ConfigureRiggedSubmeshMetadata(MeshInstance3D newMi, MeshInstance3D srcMi, Material surfMat, RiggingOptions options, Node3D heroNode = null)
        {
            if (newMi == null || srcMi == null) return;

            string srcName = srcMi.Name.ToString();
            bool isOutline = srcMi.HasMeta("IsOutlineShell") && srcMi.GetMeta("IsOutlineShell").AsBool();
            if (!isOutline)
            {
                isOutline = IsLikelyOutlineShell(srcMi, surfMat);
            }
            if (options?.SubmeshIsOutlineShell != null && options.SubmeshIsOutlineShell.TryGetValue(srcName, out bool overrideOutline))
            {
                isOutline = overrideOutline;
            }

            newMi.SetMeta("IsOutlineShell", isOutline);
            newMi.SetMeta("SourceSubmeshName", srcName);
            if (!string.IsNullOrWhiteSpace(options?.SlotId))
            {
                newMi.SetMeta("AccessorySlotId", options.SlotId);
                if (!string.IsNullOrWhiteSpace(options.AccessoryName))
                {
                    newMi.SetMeta("AccessorySlotName", options.AccessoryName);
                }
            }

            if (surfMat is StandardMaterial3D stdMat)
            {
                stdMat.SetMeta("IsOutlineShell", isOutline);
                if (!stdMat.HasMeta("SourceTransparencyMode"))
                {
                    stdMat.SetMeta("SourceTransparencyMode", (int)stdMat.Transparency);
                    stdMat.SetMeta("SourceAlphaScissorThreshold", stdMat.AlphaScissorThreshold);
                }
                if (!stdMat.HasMeta("SourceCullMode"))
                {
                    stdMat.SetMeta("SourceCullMode", (int)stdMat.CullMode);
                }
                if (stdMat.HasMeta("SourceTransparencyMode"))
                {
                    newMi.SetMeta("SourceTransparencyMode", stdMat.GetMeta("SourceTransparencyMode").AsInt32());
                }
                if (stdMat.HasMeta("SourceAlphaScissorThreshold"))
                {
                    newMi.SetMeta("SourceAlphaScissorThreshold", stdMat.GetMeta("SourceAlphaScissorThreshold").AsSingle());
                }
                if (stdMat.HasMeta("SourceCullMode"))
                {
                    newMi.SetMeta("SourceCullMode", stdMat.GetMeta("SourceCullMode").AsInt32());
                }
                if (isOutline)
                {
                    if (stdMat.CullMode == BaseMaterial3D.CullModeEnum.Disabled)
                    {
                        stdMat.CullMode = BaseMaterial3D.CullModeEnum.Back;
                    }
                    stdMat.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
                }
            }

            if (options?.SubmeshReplacementMap != null &&
                options.SubmeshReplacementMap.TryGetValue(srcName, out string repTarget) &&
                !string.IsNullOrWhiteSpace(repTarget))
            {
                newMi.SetMeta("ReplacesHeroSubmesh", repTarget);
            }

            string rawMatName = surfMat?.ResourceName;
            if (string.IsNullOrWhiteSpace(rawMatName) && srcMi.HasMeta("SourceMaterialName"))
            {
                rawMatName = srcMi.GetMeta("SourceMaterialName").AsString();
            }
            if (string.IsNullOrWhiteSpace(rawMatName))
            {
                rawMatName = $"{srcName}_mat";
            }

            ResolveAndReuseSharedCustomMaterial(heroNode, newMi, surfMat, rawMatName, isOutline, options);
        }

        private static bool IsNodeOrAncestorQueuedForDeletion(Node node)
        {
            Node cur = node;
            while (cur != null)
            {
                if (!GodotObject.IsInstanceValid(cur) || cur.IsQueuedForDeletion())
                    return true;
                cur = cur.GetParent();
            }
            return false;
        }

        private static void ResolveAndReuseSharedCustomMaterial(
            Node3D heroNode,
            MeshInstance3D newMi,
            Material surfMat,
            string rawMatName,
            bool isOutline,
            RiggingOptions options)
        {
            if (newMi == null || surfMat == null) return;

            string contentSig = ComputeMaterialContentSignature(surfMat, isOutline);
            surfMat.SetMeta("MaterialContentSignature", contentSig);

            // Reuse any existing live custom material on the hero whose textures + material flags match cleanly
            // (covers both 1-click Mirror L <-> R cloning and manually adding a second model sharing the same textures).
            var usedNamesWithOtherSig = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (heroNode != null && GodotObject.IsInstanceValid(heroNode))
            {
                foreach (var container in CollectCustomContainers(heroNode))
                {
                    if (!GodotObject.IsInstanceValid(container) || container.IsQueuedForDeletion()) continue;
                    string myContainerName = GetContainerNameForSlot(options?.SlotId);
                    bool isPendingReplacementSlot = !string.IsNullOrWhiteSpace(options?.SlotId) &&
                                                    string.Equals(container.Name.ToString(), myContainerName, StringComparison.OrdinalIgnoreCase) &&
                                                    container.IsQueuedForDeletion();
                    if (isPendingReplacementSlot) continue;

                    foreach (var existingMi in CollectMeshes(container))
                    {
                        if (existingMi == null || existingMi == newMi || IsNodeOrAncestorQueuedForDeletion(existingMi))
                            continue;
                        if (existingMi.Mesh == null || existingMi.Mesh.GetSurfaceCount() == 0)
                            continue;

                        Material existingMat = existingMi.Mesh.SurfaceGetMaterial(0)
                            ?? (existingMi.HasMeta("OriginalMaterial_0") ? existingMi.GetMeta("OriginalMaterial_0").AsGodotObject() as Material : null);
                        if (existingMat == null) continue;

                        bool existingOutline = existingMi.HasMeta("IsOutlineShell") && existingMi.GetMeta("IsOutlineShell").AsBool();
                        string existingSig = existingMat.HasMeta("MaterialContentSignature")
                            ? existingMat.GetMeta("MaterialContentSignature").AsString()
                            : ComputeMaterialContentSignature(existingMat, existingOutline);

                        string existingName = existingMi.HasMeta("SourceMaterialName")
                            ? existingMi.GetMeta("SourceMaterialName").AsString()
                            : existingMat.ResourceName;

                        if (string.Equals(existingSig, contentSig, StringComparison.Ordinal))
                        {
                            if (newMi.Mesh is ArrayMesh am && am.GetSurfaceCount() > 0)
                            {
                                am.SurfaceSetMaterial(0, existingMat);
                            }
                            newMi.SetMeta("OriginalMaterial_0", existingMat);

                            string sharedName = !string.IsNullOrWhiteSpace(existingName) ? existingName : rawMatName;
                            newMi.SetMeta("SourceMaterialName", sharedName);
                            if (string.IsNullOrWhiteSpace(existingMat.ResourceName))
                            {
                                existingMat.ResourceName = sharedName;
                            }
                            GD.Print($"[CustomModelAutoRigger] Reused shared material '{sharedName}' ({contentSig}) on '{newMi.Name}'.");
                            return;
                        }

                        if (!string.IsNullOrWhiteSpace(existingName))
                        {
                            usedNamesWithOtherSig.Add(existingName);
                        }
                    }
                }
            }

            string finalName = rawMatName.Trim();
            if (usedNamesWithOtherSig.Contains(finalName))
            {
                string shortHash = contentSig.Length >= 10 ? contentSig.Substring(4, 6) : contentSig;
                finalName = $"{finalName}_{shortHash}";
            }

            newMi.SetMeta("SourceMaterialName", finalName);
            surfMat.ResourceName = finalName;
            surfMat.SetMeta("MaterialName", finalName);
        }

        private static ulong FnvMixBytes(ulong h, byte[] data)
        {
            if (data == null) return h ^ 0x9e3779b97f4a7c15UL;
            unchecked
            {
                for (int i = 0; i < data.Length; i++)
                {
                    h ^= data[i];
                    h *= 1099511628211UL;
                }
            }
            return h;
        }

        private static ulong FnvMixInt(ulong h, int v)
        {
            unchecked
            {
                h ^= (byte)(v & 0xFF); h *= 1099511628211UL;
                h ^= (byte)((v >> 8) & 0xFF); h *= 1099511628211UL;
                h ^= (byte)((v >> 16) & 0xFF); h *= 1099511628211UL;
                h ^= (byte)((v >> 24) & 0xFF); h *= 1099511628211UL;
            }
            return h;
        }

        private static ulong FnvMixString(ulong h, string s)
        {
            if (string.IsNullOrEmpty(s)) return FnvMixInt(h, 0);
            unchecked
            {
                h = FnvMixInt(h, s.Length);
                for (int i = 0; i < s.Length; i++)
                {
                    char ch = s[i];
                    h ^= (byte)(ch & 0xFF); h *= 1099511628211UL;
                    h ^= (byte)(ch >> 8); h *= 1099511628211UL;
                }
            }
            return h;
        }

        private static ulong HashMaterialTextureContent(Texture2D tex, ulong h)
        {
            if (tex == null) return FnvMixInt(h, 0);
            h = FnvMixInt(h, 1);

            if (tex.HasMeta("_CachedTexHash64"))
            {
                ulong cached = unchecked((ulong)tex.GetMeta("_CachedTexHash64").AsInt64());
                return FnvMixInt(FnvMixInt(h, (int)(cached & 0xFFFFFFFF)), (int)(cached >> 32));
            }

            ulong th = 14695981039346656037UL;
            string rp = tex.ResourcePath;
            if (!string.IsNullOrWhiteSpace(rp) && !rp.Contains("::", StringComparison.Ordinal))
            {
                th = FnvMixString(th, rp.Replace('\\', '/').ToLowerInvariant());
            }

            Image img = tex.GetImage();
            if (img != null && !img.IsEmpty())
            {
                th = FnvMixInt(th, img.GetWidth());
                th = FnvMixInt(th, img.GetHeight());
                th = FnvMixInt(th, (int)img.GetFormat());
                th = FnvMixBytes(th, img.GetData());
            }
            else
            {
                th = FnvMixInt(th, tex.GetWidth());
                th = FnvMixInt(th, tex.GetHeight());
            }

            tex.SetMeta("_CachedTexHash64", unchecked((long)th));
            return FnvMixInt(FnvMixInt(h, (int)(th & 0xFFFFFFFF)), (int)(th >> 32));
        }

        /// <summary>
        /// Computes a deterministic 64-bit content signature over a material's texture pixel payloads
        /// and rendering flags so identical materials reuse one shared Material/Atlas/VMAT across slots.
        /// </summary>
        public static string ComputeMaterialContentSignature(Material mat, bool isOutline = false)
        {
            ulong h = 14695981039346656037UL;
            h = FnvMixInt(h, isOutline ? 1 : 0);

            if (mat is StandardMaterial3D stdMat)
            {
                float effAlpha = stdMat.HasMeta("OrigAlbedoAlpha")
                    ? stdMat.GetMeta("OrigAlbedoAlpha").AsSingle()
                    : stdMat.AlbedoColor.A;
                var effTransp = stdMat.HasMeta("OrigTransparency")
                    ? (BaseMaterial3D.TransparencyEnum)stdMat.GetMeta("OrigTransparency").AsInt32()
                    : (stdMat.HasMeta("SourceTransparencyMode") && stdMat.Transparency == BaseMaterial3D.TransparencyEnum.Disabled
                        ? (BaseMaterial3D.TransparencyEnum)stdMat.GetMeta("SourceTransparencyMode").AsInt32()
                        : stdMat.Transparency);
                float effScissor = stdMat.HasMeta("OrigAlphaScissorThreshold")
                    ? stdMat.GetMeta("OrigAlphaScissorThreshold").AsSingle()
                    : stdMat.AlphaScissorThreshold;

                h = FnvMixInt(h, 101);
                h = FnvMixInt(h, Mathf.RoundToInt(stdMat.AlbedoColor.R * 255f));
                h = FnvMixInt(h, Mathf.RoundToInt(stdMat.AlbedoColor.G * 255f));
                h = FnvMixInt(h, Mathf.RoundToInt(stdMat.AlbedoColor.B * 255f));
                h = FnvMixInt(h, Mathf.RoundToInt(effAlpha * 255f));
                h = FnvMixInt(h, Mathf.RoundToInt(stdMat.Metallic * 1000f));
                h = FnvMixInt(h, Mathf.RoundToInt(stdMat.Roughness * 1000f));
                h = FnvMixInt(h, (int)effTransp);
                h = FnvMixInt(h, effTransp == BaseMaterial3D.TransparencyEnum.AlphaScissor ? Mathf.RoundToInt(effScissor * 1000f) : 0);
                h = FnvMixInt(h, (int)stdMat.CullMode);
                h = FnvMixInt(h, (int)stdMat.ShadingMode);
                h = FnvMixInt(h, stdMat.EmissionEnabled ? 1 : 0);
                if (stdMat.EmissionEnabled)
                {
                    h = FnvMixInt(h, Mathf.RoundToInt(stdMat.Emission.R * 255f));
                    h = FnvMixInt(h, Mathf.RoundToInt(stdMat.Emission.G * 255f));
                    h = FnvMixInt(h, Mathf.RoundToInt(stdMat.Emission.B * 255f));
                    h = FnvMixInt(h, Mathf.RoundToInt(stdMat.EmissionEnergyMultiplier * 1000f));
                }
                h = FnvMixInt(h, Mathf.RoundToInt(stdMat.Uv1Scale.X * 1000f));
                h = FnvMixInt(h, Mathf.RoundToInt(stdMat.Uv1Scale.Y * 1000f));
                h = FnvMixInt(h, Mathf.RoundToInt(stdMat.Uv1Offset.X * 1000f));
                h = FnvMixInt(h, Mathf.RoundToInt(stdMat.Uv1Offset.Y * 1000f));

                h = HashMaterialTextureContent(stdMat.AlbedoTexture, h);
                h = HashMaterialTextureContent(stdMat.EmissionTexture, h);
                h = HashMaterialTextureContent(stdMat.NormalTexture, h);
                h = HashMaterialTextureContent(stdMat.RoughnessTexture, h);
                h = HashMaterialTextureContent(stdMat.MetallicTexture, h);
                h = HashMaterialTextureContent(stdMat.AOTexture, h);
            }
            else if (mat is ShaderMaterial shMat)
            {
                h = FnvMixInt(h, 202);
                h = FnvMixString(h, shMat.Shader?.ResourcePath ?? "");
                h = HashMaterialTextureContent(shMat.GetShaderParameter("albedo_texture").As<Texture2D>(), h);
                h = HashMaterialTextureContent(shMat.GetShaderParameter("normal_texture").As<Texture2D>(), h);
                h = HashMaterialTextureContent(shMat.GetShaderParameter("roughness_texture").As<Texture2D>(), h);
                h = HashMaterialTextureContent(shMat.GetShaderParameter("ao_texture").As<Texture2D>(), h);
            }
            else if (mat != null)
            {
                h = FnvMixString(h, mat.ResourcePath);
                h = FnvMixString(h, mat.ResourceName);
            }

            return $"sig_{h:x16}";
        }

        private readonly struct SocketBakeMeta
        {
            public readonly Transform3D BoneRest;
            public readonly Vector3 Pivot;
            public readonly Transform3D BakedInv;

            public SocketBakeMeta(Transform3D boneRest, Vector3 pivot, Transform3D bakedInv)
            {
                BoneRest = boneRest;
                Pivot = pivot;
                BakedInv = bakedInv;
            }
        }

        private readonly struct SocketBakeOutput
        {
            public readonly Skin PropSkin;
            public readonly SocketBakeMeta Meta;

            public SocketBakeOutput(Skin propSkin, SocketBakeMeta meta)
            {
                PropSkin = propSkin;
                Meta = meta;
            }
        }

        public static Transform3D ComputeSocketHeroMatrix(Transform3D boneRest, Vector3 pivot, Vector3 userPosOffset, Vector3 userRotOffset, float userScale)
        {
            float safeScale = userScale > 0.001f ? userScale : 1.0f;
            Basis rotBasis = Basis.FromEuler(new Vector3(
                Mathf.DegToRad(userRotOffset.X),
                Mathf.DegToRad(userRotOffset.Y),
                Mathf.DegToRad(userRotOffset.Z)
            ), EulerOrder.Yxz);

            Basis scaledBasis = (boneRest.Basis * rotBasis) * Basis.FromScale(new Vector3(safeScale, safeScale, safeScale));
            Vector3 origin = pivot + (boneRest.Basis * userPosOffset);
            return new Transform3D(scaledBasis, origin);
        }

        private static SocketBakeOutput BakeSocketPropSurface(
            Skeleton3D targetSkeleton,
            string socketBoneName,
            string meshKey,
            RiggingOptions options,
            Dictionary<string, SocketFrame> socketFrames,
            Vector3[] inputVerts,
            Vector3[] inputNorms,
            Vector3[] alignedVerts,
            Vector3[] alignedNorms,
            int[] boneIndices,
            float[] boneWeights,
            Transform3D? localToHeroSpace,
            bool? hasInputNormals = null)
        {
            int tgtBone = targetSkeleton.FindBone(socketBoneName);
            if (tgtBone == -1) tgtBone = FindBoneAny(targetSkeleton, "weapon_hand_R", "weapon_hand_L", "hand_R", "hand_L", "head");
            if (tgtBone == -1) tgtBone = 0;

            Transform3D boneRest = tgtBone >= 0 ? targetSkeleton.GetBoneGlobalRest(tgtBone) : Transform3D.Identity;
            var propSkin = new Skin();
            propSkin.AddBind(tgtBone, boneRest.AffineInverse());

            bool snapToBone = options.SubmeshSnapToSocket != null &&
                              options.SubmeshSnapToSocket.TryGetValue(meshKey, out bool snap) && snap;
            Vector3 userPosOffset = options.SubmeshSocketOffsetPos != null &&
                                    options.SubmeshSocketOffsetPos.TryGetValue(meshKey, out var uPos) ? uPos : Vector3.Zero;
            Vector3 userRotOffset = options.SubmeshSocketOffsetRot != null &&
                                    options.SubmeshSocketOffsetRot.TryGetValue(meshKey, out var uRot) ? uRot : Vector3.Zero;
            float userScale = options.SubmeshSocketScale != null &&
                              options.SubmeshSocketScale.TryGetValue(meshKey, out float uScale) && uScale > 0.001f ? uScale : 1.0f;

            SocketFrame frame = default;
            bool hasFrame = socketFrames != null && socketFrames.TryGetValue(socketBoneName, out frame);

            Vector3 socketPos = hasFrame ? frame.SocketCenter : boneRest.Origin;
            Vector3 centerPoint = hasFrame ? frame.Center : Vector3.Zero;
            Basis pcaBasis = hasFrame ? frame.PcaBasis : Basis.Identity;
            Basis targetOrient = hasFrame ? frame.TargetOrientation : Basis.Identity;

            Vector3 pivot = snapToBone ? socketPos : Vector3.Zero;
            Transform3D bakedHeroMatrix = ComputeSocketHeroMatrix(boneRest, pivot, userPosOffset, userRotOffset, userScale);

            int vCount = alignedVerts.Length;
            bool useNorms = hasInputNormals ?? (inputNorms != null && inputNorms.Length == vCount);

            for (int v = 0; v < vCount; v++)
            {
                Vector3 heroVert = localToHeroSpace.HasValue ? (localToHeroSpace.Value * inputVerts[v]) : inputVerts[v];
                Vector3 baseVert = snapToBone
                    ? targetOrient * (pcaBasis * (heroVert - centerPoint))
                    : heroVert;
                alignedVerts[v] = bakedHeroMatrix * baseVert;

                if (useNorms && inputNorms != null && v < inputNorms.Length)
                {
                    Vector3 heroNorm = localToHeroSpace.HasValue ? (localToHeroSpace.Value.Basis * inputNorms[v]).Normalized() : inputNorms[v];
                    Vector3 baseNorm = snapToBone ? (targetOrient * (pcaBasis * heroNorm)) : heroNorm;
                    alignedNorms[v] = (bakedHeroMatrix.Basis * baseNorm).Normalized();
                }
                else
                {
                    alignedNorms[v] = Vector3.Up;
                }

                boneIndices[v * 4 + 0] = 0; // Bind 0 in propSkin
                boneWeights[v * 4 + 0] = 1.0f;
                boneIndices[v * 4 + 1] = 0;
                boneWeights[v * 4 + 1] = 0f;
                boneIndices[v * 4 + 2] = 0;
                boneWeights[v * 4 + 2] = 0f;
                boneIndices[v * 4 + 3] = 0;
                boneWeights[v * 4 + 3] = 0f;
            }

            var meta = new SocketBakeMeta(boneRest, pivot, bakedHeroMatrix.AffineInverse());
            return new SocketBakeOutput(propSkin, meta);
        }

        private static void ApplySocketMetadata(MeshInstance3D newMi, string meshKey, SocketBakeMeta meta)
        {
            if (newMi == null) return;
            newMi.SetMeta("IsSocketProp", true);
            newMi.SetMeta("SocketMeshKey", meshKey);
            newMi.SetMeta("SocketBoneRest", meta.BoneRest);
            newMi.SetMeta("SocketPivot", meta.Pivot);
            newMi.SetMeta("SocketBakedInv", meta.BakedInv);
        }

        /// <summary>
        /// Updates a rigged socket prop's Skin bind pose in O(1) when socket Pos/Rot/Scale offsets change,
        /// avoiding vertex buffer rebuilds and full KNN/bind passes while matching animated playback identically.
        /// </summary>
        public static bool TryUpdateSocketSkinBindPose(MeshInstance3D mi, Vector3 userPosOffset, Vector3 userRotOffset, float userScale)
        {
            if (mi == null || !GodotObject.IsInstanceValid(mi) || mi.Skin == null || mi.Skin.GetBindCount() <= 0)
                return false;
            if (!mi.HasMeta("IsSocketProp") || !mi.GetMeta("IsSocketProp").AsBool())
                return false;
            if (!mi.HasMeta("SocketBoneRest") || !mi.HasMeta("SocketPivot") || !mi.HasMeta("SocketBakedInv"))
                return false;

            Transform3D boneRest = mi.GetMeta("SocketBoneRest").AsTransform3D();
            Vector3 pivot = mi.GetMeta("SocketPivot").AsVector3();
            Transform3D bakedInv = mi.GetMeta("SocketBakedInv").AsTransform3D();

            Transform3D liveHeroMatrix = ComputeSocketHeroMatrix(boneRest, pivot, userPosOffset, userRotOffset, userScale);
            mi.Skin.SetBindPose(0, boneRest.AffineInverse() * liveHeroMatrix * bakedInv);
            return true;
        }

        #endregion
    }
}
