using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using DeadlockPlayground.Catalog;
using DeadlockPlayground.Materials;
using DeadlockPlayground.Painter;
using ValvePak;
using ValveKeyValue;
using ValveResourceFormat;
using ValveResourceFormat.IO;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;
using Mesh = Godot.Mesh;

namespace DeadlockPlayground.Importer
{
    /// <summary>
    /// Phase P3 Source 2 Model, Material, and VPK Exporter for Custom 3D Models in Deadlock Playground.
    /// Implements the CSDK13 + Section 14 Decompile-and-Patch architecture:
    /// 1. Extracts custom model geometry, skin-resolved bone weights (max 4, normalized, cloth-free),
    ///    and material textures on the main thread into an immutable CustomModelExportBundle.
    /// 2. Decompiles the vanilla hero's .vmdl_c via ValveResourceFormat.IO.ModelExtract to preserve
    ///    100% of AttachmentList (camera rails), NmSkeletonList, AnimGraph2List, AnimConstraintList,
    ///    GameDataList, HitboxSetList, Skeleton, and Physics ragdoll blocks.
    /// 3. Emits Source 2 bind-pose SMD meshes (inverse of VRF SourceToGltfRotation (-0.5,-0.5,-0.5,0.5)
    ///    and InchesToMeters = 0.0254) with exact bone origins/angles from the decompiled .vmdl Skeleton.
    /// 4. Compiles custom .vmat -> .vmat_c + .vtex_c (shader "pbr.vfx") and .vmdl -> .vmdl_c inside an
    ///    isolated addon directory (content/citadel_addons/_temp_export -> game/citadel_addons/_temp_export)
    ///    with a strict VPK packaging allowlist (< 35 files total).
    /// </summary>
    public static class CustomModelExporter
    {
        public const float InchesToMeters = 0.0254f;
        public const float MetersToInches = 1.0f / InchesToMeters; // 39.37007874f

        public class VmdlSkeletonBone
        {
            public int Index { get; set; }
            public string Name { get; set; } = "";
            public int ParentIndex { get; set; } = -1;
            public Vector3 Origin { get; set; } = Vector3.Zero;
            public Vector3 AnglesDeg { get; set; } = Vector3.Zero; // [pitch, yaw, roll] in degrees
            public System.Numerics.Quaternion ParentRot { get; set; } = System.Numerics.Quaternion.Identity;
            public bool HasParentRot { get; set; } = false;
        }

        /// <summary>
        /// Immutable, thread-safe bundle of custom model geometry, textures, and skeleton metadata
        /// extracted on the main thread prior to background compilation.
        /// </summary>
        public class CustomModelExportBundle
        {
            public class SubmeshData
            {
                public string SubmeshName { get; set; } = "";
                public string OriginalHeroReplacementName { get; set; } = "";
                public string MaterialName { get; set; } = "";
                public Vector3[] Vertices { get; set; } = Array.Empty<Vector3>();
                public Vector3[] Normals { get; set; } = Array.Empty<Vector3>();
                public Vector2[] UVs { get; set; } = Array.Empty<Vector2>();
                public int[] Indices { get; set; } = Array.Empty<int>();
                public int[] BoneIndices { get; set; } = Array.Empty<int>(); // 4 skeleton bone indices per vertex
                public float[] BoneWeights { get; set; } = Array.Empty<float>(); // 4 floats per vertex (sum = 1.0)
                public byte[] AlbedoPngBytes { get; set; }
                public byte[] NormalPngBytes { get; set; }
                public byte[] RoughnessPngBytes { get; set; }
                public byte[] AmbientOcclusionPngBytes { get; set; }
                public byte[] ShadowPngBytes { get; set; }
                public byte[] SelfIllumPngBytes { get; set; }
                public byte[] TintMaskPngBytes { get; set; }
                public Color AlbedoTint { get; set; } = Colors.White;
                public float RoughnessScalar { get; set; } = 0.6f;
                public float MetallicScalar { get; set; } = 0.0f;
                public bool IsOutlineShell { get; set; }
                public bool IsTranslucent { get; set; }
                public bool IsAlphaTest { get; set; }
                public bool IsDoubleSided { get; set; }
                public bool IsUnlit { get; set; }
                /// <summary>
                /// Non-empty for preserved vanilla split surfaces kept alongside a custom model:
                /// remaps this surface's material directly to the vanilla .vmat without compiling new .vmat/.vtex assets.
                /// </summary>
                public string PreserveVanillaVmatPath { get; set; } = "";
            }

            public class ClothCollisionCapsuleDef
            {
                public string ParentBone { get; set; } = "";
                public Vector3 Point0 { get; set; }
                public float Radius0 { get; set; }
                public Vector3 Point1 { get; set; }
                public float Radius1 { get; set; }
            }

            public string HeroCodename { get; set; } = "hero";
            public string HeroModelVpkPath { get; set; } = "";
            public string DeadlockGamePath { get; set; } = "";
            public string StagingDir { get; set; } = "";
            public string OutputVpkPath { get; set; } = "";
            public string ResourceCompilerExe { get; set; } = "";
            public List<SubmeshData> Submeshes { get; } = new();
            /// <summary>
            /// Preserved visible vanilla split/composite surfaces (e.g. priest_shotgun, priest_gun_crossbow, priest_stake)
            /// exported into a separate kept-props SMD using vanilla .vmat remaps.
            /// </summary>
            public List<SubmeshData> PreservedVanillaSubmeshes { get; } = new();
            /// <summary>
            /// Explicit set of standalone vanilla RenderMesh / .dmx names (trimmed of leading '_') chosen to remain visible.
            /// When HasExplicitKeptVanillaFilter is true, only these standalone .dmx files are preserved.
            /// </summary>
            public HashSet<string> AllowedKeptVanillaDmxNames { get; } = new(StringComparer.OrdinalIgnoreCase);
            public bool HasExplicitKeptVanillaFilter { get; set; } = false;
            public List<string> BoneNames { get; } = new();
            public Dictionary<string, string> BoneHierarchy { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, Transform3D> BoneLocalRestTransforms { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, Transform3D> BoneRestTransforms { get; } = new(StringComparer.OrdinalIgnoreCase);
            public byte[] OriginalHeroVmdlCBytes { get; set; }
            public bool StabilizeNeckFloatingHead { get; set; } = false;
            public HashSet<string> PreservedCustomBoneNames { get; } = new(StringComparer.OrdinalIgnoreCase);
            public List<BoneChainDef> PreservedChains { get; } = new();
            public List<ClothCollisionCapsuleDef> VanillaCollisionCapsules { get; } = new();
            public List<ClothCollisionCapsuleDef> CustomFittedWorldCapsules { get; } = new();
            /// <summary>
            /// Exact <c>$cloth_m0p{vi}</c> dynamic vertex names generated by <see cref="WriteProceduralClothProxyDmx"/>
            /// for chains with <see cref="BoneChainDef.PlanarizeBackBarrier"/> enabled, used to emit a real
            /// CSDK13 <c>ClothVertexMap</c> + planarized torso barrier (<c>FeModel.CollisionPlanes</c>).
            /// </summary>
            public List<string> PlanarizeBackProxyNodes { get; } = new();
            /// <summary>
            /// Exact <c>$cloth_m0p{vi}</c> dynamic vertex names for closed wrap-around sheets (<c>!OpenSheet</c>, e.g. skirts/dresses),
            /// used to emit Yamato-style planarized <c>leg_upper_L/R</c> <c>CollisionPlanes</c> so full-size leg capsules
            /// push skirts smoothly without shrinking the thighs into sticks.
            /// </summary>
            public List<string> PlanarizeWrapProxyNodes { get; } = new();
        }

        public class ModelExportOptions
        {
            public string HeroCodename { get; set; } = "hero";
            public string ModelVpkRelativePath { get; set; } = "models/heroes_staging/{hero}/{hero}.vmdl_c";
            public string DeadlockGamePath { get; set; }
            public string StagingDir { get; set; }
            public string OutputVpkPath { get; set; }
            public string ResourceCompilerExe { get; set; }
            public List<MeshInstance3D> CustomMeshes { get; set; } = new();
            public Node3D HeroRoot { get; set; }
            public CustomModelExportBundle PreExtractedBundle { get; set; }
        }

        public class ModelExportResult
        {
            public bool Success { get; set; }
            public string ErrorMessage { get; set; }
            public string CompiledVmdlCPath { get; set; }
            public string CompiledVmeshCPath { get; set; }
            public string GameStagingAddonRoot { get; set; }
            public List<string> CompiledVmatCFiles { get; } = new();
            public List<string> CompiledVtexCFiles { get; } = new();
            public Dictionary<string, string> AllowlistedVpkFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Extracts all mesh arrays, skin-resolved weights (capped to 4, cloth-free, normalized),
        /// power-of-2 textures, and bone metadata ON THE MAIN THREAD before worker thread dispatch.
        /// </summary>
        public static CustomModelExportBundle BuildExportBundle(
            List<MeshInstance3D> meshes,
            Skeleton3D skeleton,
            string heroCodename,
            string heroModelVpkPath = null,
            byte[] originalHeroVmdlCBytes = null,
            string stagingDir = null,
            string compilerExe = null,
            string deadlockGamePath = null,
            IEnumerable<BoneChainDef> preservedChains = null)
        {
            string cleanHero = string.IsNullOrEmpty(heroCodename) ? "hero" : heroCodename.ToLowerInvariant();
            if (string.IsNullOrEmpty(heroModelVpkPath))
            {
                heroModelVpkPath = DeadlockHeroCatalog.ResolveHero(cleanHero)?.VmdlRelativePath
                                   ?? $"models/heroes_staging/{cleanHero}/{cleanHero}.vmdl_c";
            }

            if (string.IsNullOrEmpty(deadlockGamePath))
            {
                var pm = new GamePathManager();
                deadlockGamePath = !string.IsNullOrEmpty(pm.CurrentGamePath) ? pm.CurrentGamePath : pm.AutoDetectDeadlockPath();
            }

            var bundle = new CustomModelExportBundle
            {
                HeroCodename = cleanHero,
                HeroModelVpkPath = heroModelVpkPath,
                DeadlockGamePath = deadlockGamePath ?? "",
                OriginalHeroVmdlCBytes = originalHeroVmdlCBytes,
                StagingDir = stagingDir ?? "",
                ResourceCompilerExe = compilerExe ?? "",
                StabilizeNeckFloatingHead = cleanHero == "chrono"
            };

            if (preservedChains != null)
            {
                foreach (var ch in preservedChains)
                {
                    if (ch == null || ch.Policy != ChainPolicy.PreservePhysicsChain) continue;
                    bundle.PreservedChains.Add(ch.Clone());
                    foreach (var bn in ch.AppendedHeroBoneNames)
                        if (!string.IsNullOrWhiteSpace(bn)) bundle.PreservedCustomBoneNames.Add(bn);
                }
            }

            if (skeleton != null && GodotObject.IsInstanceValid(skeleton) && skeleton.HasMeta("CustomAppendedBoneEntries"))
            {
                var entries = skeleton.GetMeta("CustomAppendedBoneEntries").AsStringArray();
                var chainBonesByKey = new Dictionary<string, (string Slot, string ChainId, List<string> Bones)>(StringComparer.OrdinalIgnoreCase);
                if (entries != null)
                {
                    foreach (var rec in entries)
                    {
                        var parts = rec.Split('|', 3);
                        if (parts.Length == 3 && !string.IsNullOrWhiteSpace(parts[2]))
                        {
                            bundle.PreservedCustomBoneNames.Add(parts[2]);
                            if (!parts[1].Equals("finger_chains", StringComparison.OrdinalIgnoreCase))
                            {
                                string gk = $"{parts[0]}|{parts[1]}";
                                if (!chainBonesByKey.TryGetValue(gk, out var grp))
                                {
                                    grp = (parts[0], parts[1], new List<string>());
                                    chainBonesByKey[gk] = grp;
                                }
                                grp.Bones.Add(parts[2]);
                            }
                        }
                    }
                }

                // Fallback: if caller omitted preservedChains (e.g. direct export call without CustomJiggleBoneDriver),
                // reconstruct active chains from tagged appended metadata unless physics were explicitly disabled.
                bool physicsAllowed = !skeleton.HasMeta("CustomPhysicsEnabled") || skeleton.GetMeta("CustomPhysicsEnabled").AsBool();
                if (preservedChains == null && physicsAllowed && bundle.PreservedChains.Count == 0 && chainBonesByKey.Count > 0)
                {
                    foreach (var kv in chainBonesByKey.Values)
                    {
                        if (kv.Bones.Count == 0) continue;
                        string anchor = "head";
                        int b0 = skeleton.FindBone(kv.Bones[0]);
                        if (b0 >= 0)
                        {
                            int p0 = skeleton.GetBoneParent(b0);
                            if (p0 >= 0) anchor = skeleton.GetBoneName(p0);
                        }
                        bundle.PreservedChains.Add(new BoneChainDef
                        {
                            ChainId = kv.ChainId,
                            Name = kv.ChainId,
                            Category = "Clothing & Hair",
                            SlotId = kv.Slot,
                            AnchorHeroBone = anchor,
                            RootSourceBone = kv.Bones[0],
                            Policy = ChainPolicy.PreservePhysicsChain,
                            Simulation = SimulationKind.Jiggle,
                            SourceBoneNames = new List<string>(kv.Bones),
                            AppendedHeroBoneNames = new List<string>(kv.Bones)
                        });
                    }
                }
            }

            var clothBoneIndices = new HashSet<int>();

            if (skeleton != null && GodotObject.IsInstanceValid(skeleton))
            {
                int boneCount = skeleton.GetBoneCount();
                for (int b = 0; b < boneCount; b++)
                {
                    string bName = skeleton.GetBoneName(b);
                    bundle.BoneNames.Add(bName);
                    int p = skeleton.GetBoneParent(b);
                    string pName = p >= 0 ? skeleton.GetBoneName(p) : null;
                    bundle.BoneHierarchy[bName] = pName;
                    bundle.BoneLocalRestTransforms[bName] = skeleton.GetBoneRest(b);
                    bundle.BoneRestTransforms[bName] = skeleton.GetBoneGlobalRest(b);

                    if (IsClothBoneName(bName) && !bundle.PreservedCustomBoneNames.Contains(bName))
                    {
                        clothBoneIndices.Add(b);
                    }
                }
            }
            else
            {
                bundle.BoneNames.Add("root_motion");
                bundle.BoneHierarchy["root_motion"] = null;
                bundle.BoneLocalRestTransforms["root_motion"] = Transform3D.Identity;
                bundle.BoneRestTransforms["root_motion"] = Transform3D.Identity;
            }

            NormalizePreservedCustomBoneNamesForSource2(bundle);

            // Capture mesh-fitted body collision capsules in Godot Hero World space (meters) so Export
            // converts them cleanly into Source 2 bone-local ClothShapeCapsule inches regardless of root orientation.
            if (skeleton != null && GodotObject.IsInstanceValid(skeleton))
            {
                List<BodyCollisionCapsuleDef> fittedCaps = null;
                if (skeleton.HasMeta(BodyColliderAutoFitter.SkeletonMetaKey))
                {
                    fittedCaps = BodyColliderAutoFitter.DeserializeFromJson(
                        skeleton.GetMeta(BodyColliderAutoFitter.SkeletonMetaKey).AsString());
                }
                else if (meshes != null && meshes.Count > 0 && bundle.PreservedChains.Count > 0)
                {
                    fittedCaps = BodyColliderAutoFitter.FitFromRiggedMeshes(skeleton, meshes, physicsChains: bundle.PreservedChains);
                }

                if (fittedCaps != null)
                {
                    foreach (var cap in fittedCaps)
                    {
                        if (cap == null || !cap.Enabled || string.IsNullOrWhiteSpace(cap.BoneName)) continue;
                        int bIdx = skeleton.FindBone(cap.BoneName);
                        if (bIdx < 0) continue;
                        Transform3D bRest = skeleton.GetBoneGlobalRest(bIdx);
                        bundle.CustomFittedWorldCapsules.Add(new CustomModelExportBundle.ClothCollisionCapsuleDef
                        {
                            ParentBone = cap.BoneName,
                            Point0 = bRest * cap.EffectivePoint0,
                            Radius0 = cap.EffectiveRadius0,
                            Point1 = bRest * cap.EffectivePoint1,
                            Radius1 = cap.EffectiveRadius1
                        });
                    }
                }
            }

            // Track unique material objects -> sanitized material names so shared materials reuse 1 .vmat_c/.vtex_c
            var materialInstanceToName = new Dictionary<ulong, string>();
            var textureHashToMatName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var usedMatNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (meshes != null)
            {
                int subCounter = 0;
                for (int i = 0; i < meshes.Count; i++)
                {
                    var meshInst = meshes[i];
                    if (meshInst == null || !GodotObject.IsInstanceValid(meshInst) || meshInst.Mesh == null || !meshInst.Visible)
                        continue;

                    Mesh mesh = meshInst.Mesh;
                    int surfCount = mesh.GetSurfaceCount();

                    // Build Skin bindIdx -> Skeleton3D boneIdx mapping for this MeshInstance3D
                    Dictionary<int, int> bindToSkelMap = BuildSkinBindToSkeletonMap(skeleton, meshInst.Skin);

                    for (int s = 0; s < surfCount; s++)
                    {
                        var arrays = mesh.SurfaceGetArrays(s);
                        if (arrays == null || arrays.Count <= (int)Mesh.ArrayType.Vertex)
                            continue;

                        var vVar = arrays[(int)Mesh.ArrayType.Vertex];
                        if (vVar.VariantType == Variant.Type.Nil) continue;
                        Vector3[] rawVerts = vVar.AsVector3Array();
                        if (rawVerts == null || rawVerts.Length == 0) continue;

                        int vCount = rawVerts.Length;
                        Vector3[] rawNorms = arrays.Count > (int)Mesh.ArrayType.Normal && arrays[(int)Mesh.ArrayType.Normal].VariantType != Variant.Type.Nil
                            ? arrays[(int)Mesh.ArrayType.Normal].AsVector3Array()
                            : null;
                        Vector2[] rawUvs = arrays.Count > (int)Mesh.ArrayType.TexUV && arrays[(int)Mesh.ArrayType.TexUV].VariantType != Variant.Type.Nil
                            ? arrays[(int)Mesh.ArrayType.TexUV].AsVector2Array()
                            : null;
                        int[] rawIndices = arrays.Count > (int)Mesh.ArrayType.Index && arrays[(int)Mesh.ArrayType.Index].VariantType != Variant.Type.Nil
                            ? arrays[(int)Mesh.ArrayType.Index].AsInt32Array()
                            : null;

                        if (rawNorms == null || rawNorms.Length != vCount)
                        {
                            rawNorms = new Vector3[vCount];
                            Array.Fill(rawNorms, Vector3.Up);
                        }
                        if (rawUvs == null || rawUvs.Length != vCount)
                        {
                            rawUvs = new Vector2[vCount];
                        }
                        if (rawIndices == null || rawIndices.Length == 0)
                        {
                            rawIndices = new int[vCount];
                            for (int k = 0; k < vCount; k++) rawIndices[k] = k;
                        }

                        int[] rawBones = arrays.Count > (int)Mesh.ArrayType.Bones && arrays[(int)Mesh.ArrayType.Bones].VariantType != Variant.Type.Nil
                            ? arrays[(int)Mesh.ArrayType.Bones].AsInt32Array()
                            : null;
                        float[] rawWeights = arrays.Count > (int)Mesh.ArrayType.Weights && arrays[(int)Mesh.ArrayType.Weights].VariantType != Variant.Type.Nil
                            ? arrays[(int)Mesh.ArrayType.Weights].AsFloat32Array()
                            : null;

                        int[] cappedBones = new int[vCount * 4];
                        float[] cappedWeights = new float[vCount * 4];

                        NormalizeAndCapWeights(
                            vCount,
                            rawBones,
                            rawWeights,
                            bindToSkelMap,
                            clothBoneIndices,
                            skeleton,
                            bundle.BoneNames.Count,
                            cappedBones,
                            cappedWeights,
                            bundle.StabilizeNeckFloatingHead);

                        // Resolve authentic material (checking OriginalMaterial_s metadata, SurfaceGetMaterial, and OverrideMaterial)
                        Godot.Material origMetaMat = meshInst.HasMeta($"OriginalMaterial_{s}")
                            ? meshInst.GetMeta($"OriginalMaterial_{s}").As<Godot.Material>()
                            : (meshInst.HasMeta("OriginalMaterial_0") ? meshInst.GetMeta("OriginalMaterial_0").As<Godot.Material>() : null);

                        Godot.Material surfMat = mesh.SurfaceGetMaterial(s);
                        Godot.Material overrideMat = meshInst.GetSurfaceOverrideMaterial(s) ?? meshInst.MaterialOverride;

                        StandardMaterial3D stdMat = (overrideMat as StandardMaterial3D)
                                                    ?? (surfMat as StandardMaterial3D)
                                                    ?? (origMetaMat as StandardMaterial3D);

                        Godot.Material primaryMat = stdMat ?? overrideMat ?? surfMat ?? origMetaMat;

                        byte[] albedoPng = null;
                        byte[] normalPng = null;
                        byte[] roughPng = null;
                        byte[] aoPng = null;
                        byte[] shadowPng = null;
                        byte[] selfIllumPng = null;
                        byte[] tintMaskPng = null;
                        Color albedoTint = Colors.White;
                        float roughScalar = 0.6f;
                        float metalScalar = 0.0f;

                        bool isOutlineShell = (meshInst.HasMeta("IsOutlineShell") && meshInst.GetMeta("IsOutlineShell").AsBool())
                                              || CustomModelAutoRigger.IsLikelyOutlineShell(meshInst, primaryMat);
                        bool isTranslucent = false;
                        bool isAlphaTest = false;
                        bool isDoubleSided = false;
                        bool isUnlit = isOutlineShell;

                        if (stdMat != null)
                        {
                            Texture2D albedoTex = stdMat.AlbedoTexture ?? stdMat.EmissionTexture;
                            albedoTint = stdMat.AlbedoColor;
                            bool isBlackAlbedo = albedoTint.R < 0.02f && albedoTint.G < 0.02f && albedoTint.B < 0.02f;
                            if (isBlackAlbedo)
                            {
                                if (albedoTex != null)
                                {
                                    albedoTint = new Color(1f, 1f, 1f, albedoTint.A > 0f ? albedoTint.A : 1f);
                                }
                                else if (stdMat.EmissionEnabled && (stdMat.Emission.R > 0.02f || stdMat.Emission.G > 0.02f || stdMat.Emission.B > 0.02f))
                                {
                                    albedoTint = new Color(stdMat.Emission.R, stdMat.Emission.G, stdMat.Emission.B, albedoTint.A > 0f ? albedoTint.A : 1f);
                                }
                            }

                            roughScalar = Mathf.Clamp(stdMat.Roughness, 0.05f, 1.0f);
                            metalScalar = Mathf.Clamp(stdMat.Metallic, 0.0f, 1.0f);
                            isTranslucent = stdMat.Transparency == BaseMaterial3D.TransparencyEnum.Alpha;
                            isAlphaTest = stdMat.Transparency == BaseMaterial3D.TransparencyEnum.AlphaScissor ||
                                          stdMat.Transparency == BaseMaterial3D.TransparencyEnum.AlphaHash;
                            isDoubleSided = !isOutlineShell && stdMat.CullMode == BaseMaterial3D.CullModeEnum.Disabled;
                            // Do not force F_SELF_ILLUM on regular character/prop meshes just because the source glTF
                            // used KHR_materials_unlit (ShadingMode=Unshaded); Deadlock pbr.vfx should still shade and
                            // shadow-receive them unless they are inverted-hull ink outline shells or carry a SelfIllum mask.
                            isUnlit = isOutlineShell;

                            albedoPng = ExtractTexturePngPowerOfTwo(albedoTex, albedoTint);
                            normalPng = ExtractTexturePngPowerOfTwo(stdMat.NormalTexture, Colors.White);
                            roughPng = ExtractTexturePngPowerOfTwo(stdMat.RoughnessTexture, Colors.White);
                            aoPng = ExtractTexturePngPowerOfTwo(stdMat.AOTexture, Colors.White);
                        }
                        else if (primaryMat is ShaderMaterial sMat)
                        {
                            albedoPng = ExtractShaderTexturePngPowerOfTwo(sMat, "albedo_texture", "texture_albedo", "g_tColor");
                            normalPng = ExtractShaderTexturePngPowerOfTwo(sMat, "normal_texture", "texture_normal", "g_tNormal");
                            roughPng = ExtractShaderTexturePngPowerOfTwo(sMat, "roughness_texture", "texture_roughness");
                            aoPng = ExtractShaderTexturePngPowerOfTwo(sMat, "ao_texture", "texture_ambient_occlusion", "g_tAmbientOcclusion");
                        }

                        // Check multi-channel registry & live GPU Texture Painter edits (handles ORM-unpacked AO,
                        // external tier-2 maps like _shadow/_ao/_rough, and live per-channel brush/imported edits)
                        var chSet = primaryMat != null ? MaterialChannelDiscovery.TryGetChannelSet(primaryMat) : null;
                        var lm = SkinLayerManager.Instance;
                        SubmeshNodeInfo matchedSubInfo = lm?.MeshHierarchy?.Submeshes?.FirstOrDefault(smInfo =>
                            smInfo != null && smInfo.Mesh == meshInst && smInfo.SurfaceIndex == s);

                        byte[] ResolveMultiChannelPng(MaterialTextureChannel ch, byte[] fallbackBytes)
                        {
                            if (lm != null && matchedSubInfo != null && lm.HasChannelEdits(matchedSubInfo, ch))
                            {
                                using var baked = lm.BakeChannelImageForSubmesh(matchedSubInfo, ch);
                                var bakedBytes = ImageToPngBytesPowerOfTwo(baked);
                                if (bakedBytes != null && bakedBytes.Length > 0) return bakedBytes;
                            }
                            var entry = chSet?.GetChannel(ch);
                            if (entry != null && !entry.IsDefaultFallback && entry.IsPresent && entry.IsEnabled)
                            {
                                if (entry.SourceImage != null && !entry.SourceImage.IsEmpty())
                                {
                                    var bytes = ImageToPngBytesPowerOfTwo(entry.SourceImage);
                                    if (bytes != null && bytes.Length > 0) return bytes;
                                }
                                if (entry.Texture != null)
                                {
                                    var bytes = ExtractTexturePngPowerOfTwo(entry.Texture, Colors.White);
                                    if (bytes != null && bytes.Length > 0) return bytes;
                                }
                            }
                            return fallbackBytes;
                        }

                        albedoPng = ResolveMultiChannelPng(MaterialTextureChannel.Color, albedoPng);
                        if (lm != null && matchedSubInfo != null && !isTranslucent && !isAlphaTest)
                        {
                            string subMatKey = !string.IsNullOrEmpty(matchedSubInfo.OriginalVmatPath)
                                ? matchedSubInfo.OriginalVmatPath.ToLowerInvariant().Trim()
                                : lm.GetMaterialKey(matchedSubInfo.Mesh, matchedSubInfo.SurfaceIndex);
                            if (lm.HasEditedAlphaForMaterial(subMatKey) ||
                                (string.Equals(lm.ActiveBaseMaterialKey, subMatKey, StringComparison.OrdinalIgnoreCase) && lm.EnableAlphaTransparency))
                            {
                                isAlphaTest = true;
                            }
                        }
                        normalPng = ResolveMultiChannelPng(MaterialTextureChannel.Normal, normalPng);
                        roughPng = ResolveMultiChannelPng(MaterialTextureChannel.Roughness, roughPng);
                        aoPng = ResolveMultiChannelPng(MaterialTextureChannel.AmbientOcclusion, aoPng);
                        shadowPng = ResolveMultiChannelPng(MaterialTextureChannel.Shadow, null);
                        bool hasPaintedSelfIllum = lm != null && matchedSubInfo != null &&
                                                   lm.HasChannelEdits(matchedSubInfo, MaterialTextureChannel.SelfIllum);
                        bool allowExtractedSelfIllum = hasPaintedSelfIllum || stdMat == null || stdMat.EmissionEnabled;
                        selfIllumPng = allowExtractedSelfIllum
                            ? ResolveMultiChannelPng(MaterialTextureChannel.SelfIllum, null)
                            : null;
                        tintMaskPng = ResolveMultiChannelPng(MaterialTextureChannel.TintMask, null);

                        // If material has no texture image, bake its solid AlbedoColor into a 64x64 PNG
                        if (albedoPng == null || albedoPng.Length == 0)
                        {
                            albedoPng = CreateSolidColorPngBytes(64, 64, albedoTint);
                        }

                        // Resolve candidate material name before deduplication so distinct materials (e.g. eye white vs pistol) never collide
                        string candidateRaw = primaryMat?.ResourceName;
                        if (string.IsNullOrWhiteSpace(candidateRaw) && meshInst.HasMeta("SourceMaterialName"))
                        {
                            candidateRaw = meshInst.GetMeta("SourceMaterialName").AsString();
                        }
                        if (string.IsNullOrWhiteSpace(candidateRaw) && primaryMat != null && primaryMat.HasMeta("MaterialName"))
                        {
                            candidateRaw = primaryMat.GetMeta("MaterialName").AsString();
                        }
                        if (string.IsNullOrWhiteSpace(candidateRaw))
                        {
                            string baseMeshName = meshInst.Name.ToString();
                            int surfIdx = baseMeshName.IndexOf("_surf", StringComparison.OrdinalIgnoreCase);
                            if (surfIdx > 0) baseMeshName = baseMeshName.Substring(0, surfIdx);
                            candidateRaw = $"{cleanHero}_{baseMeshName}";
                        }

                        string baseSanitized = SanitizeMaterialName(candidateRaw);
                        if (baseSanitized == "custom_mat")
                        {
                            baseSanitized = SanitizeMaterialName($"{cleanHero}_mat_{usedMatNames.Count}");
                        }

                        // Deduplicate material name across submeshes sharing the same Material instance or identical (name + texture + flags)
                        string matName = null;
                        ulong matId = primaryMat != null ? primaryMat.GetInstanceId() : 0;
                        string texHash = ComputeFastHash(albedoPng, normalPng, roughPng, aoPng);
                        string dedupKey = !string.IsNullOrEmpty(texHash)
                            ? $"{baseSanitized}_{texHash}_{isOutlineShell}_{isTranslucent}_{isAlphaTest}"
                            : "";

                        if (matId != 0 && materialInstanceToName.TryGetValue(matId, out string existingById))
                        {
                            matName = existingById;
                        }
                        else if (!string.IsNullOrEmpty(dedupKey) && textureHashToMatName.TryGetValue(dedupKey, out string existingByHash))
                        {
                            matName = existingByHash;
                        }
                        else
                        {
                            matName = baseSanitized;
                            int suffix = 1;
                            while (!usedMatNames.Add(matName))
                            {
                                matName = $"{baseSanitized}_{suffix++}";
                            }

                            if (matId != 0) materialInstanceToName[matId] = matName;
                            if (!string.IsNullOrEmpty(dedupKey)) textureHashToMatName[dedupKey] = matName;
                        }

                        string repName = meshInst.HasMeta("ReplacesHeroSubmesh")
                            ? meshInst.GetMeta("ReplacesHeroSubmesh").AsString()
                            : "";

                        bundle.Submeshes.Add(new CustomModelExportBundle.SubmeshData
                        {
                            SubmeshName = surfCount > 1 ? $"{meshInst.Name}_s{s}" : meshInst.Name.ToString(),
                            OriginalHeroReplacementName = repName,
                            MaterialName = matName,
                            Vertices = rawVerts,
                            Normals = rawNorms,
                            UVs = rawUvs,
                            Indices = rawIndices,
                            BoneIndices = cappedBones,
                            BoneWeights = cappedWeights,
                            AlbedoPngBytes = albedoPng,
                            NormalPngBytes = normalPng,
                            RoughnessPngBytes = roughPng,
                            AmbientOcclusionPngBytes = aoPng,
                            ShadowPngBytes = shadowPng,
                            SelfIllumPngBytes = selfIllumPng,
                            TintMaskPngBytes = tintMaskPng,
                            AlbedoTint = albedoTint,
                            RoughnessScalar = roughScalar,
                            MetallicScalar = metalScalar,
                            IsOutlineShell = isOutlineShell,
                            IsTranslucent = isTranslucent,
                            IsAlphaTest = isAlphaTest,
                            IsDoubleSided = isDoubleSided,
                            IsUnlit = isUnlit
                        });

                        subCounter++;
                    }
                }
            }

            return bundle;
        }

        /// <summary>
        /// Source 2 resourcecompiler.exe's .smd importer lowercases bone names and replaces non-[a-z0-9_]
        /// characters (such as Blender's '.' in "HairCenter.001" -> "haircenter_001"), while .vmdl Skeleton
        /// blocks and DMX cloth proxies preserve raw casing and punctuation. If preserved custom physics bones
        /// keep mixed-case or dotted names, resourcecompiler spawns a second unweighted set of hair/cloth bones
        /// alongside empty JiggleBone nodes. Normalizing preserved custom bone names up front keeps BoneNames,
        /// BoneHierarchy, PreservedChains, SMD, Skeleton, and _cloth_proxy.dmx 100% identical.
        /// </summary>
        internal static void NormalizePreservedCustomBoneNamesForSource2(CustomModelExportBundle bundle)
        {
            if (bundle == null || bundle.PreservedCustomBoneNames.Count == 0)
                return;

            var nonCustomNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var bn in bundle.BoneNames)
            {
                if (!string.IsNullOrEmpty(bn) && !bundle.PreservedCustomBoneNames.Contains(bn))
                    nonCustomNames.Add(bn);
            }

            var usedNormalized = new HashSet<string>(nonCustomNames, StringComparer.OrdinalIgnoreCase);
            var renameMap = new Dictionary<string, string>(StringComparer.Ordinal);

            string SanitizeCustomBoneName(string raw)
            {
                if (string.IsNullOrWhiteSpace(raw)) return "custom_bone";
                string clean = Regex.Replace(raw.Trim(), @"[^a-zA-Z0-9_]", "_").ToLowerInvariant();
                if (string.IsNullOrEmpty(clean)) clean = "custom_bone";
                // Guard against accidentally triggering IsClothBoneName only if raw wasn't already custom,
                // though PreservedCustomBoneNames already exempts preserved bones from stripping.
                return clean;
            }

            for (int i = 0; i < bundle.BoneNames.Count; i++)
            {
                string orig = bundle.BoneNames[i];
                if (string.IsNullOrEmpty(orig) || !bundle.PreservedCustomBoneNames.Contains(orig))
                    continue;
                if (renameMap.ContainsKey(orig))
                    continue;

                string baseClean = SanitizeCustomBoneName(orig);
                string candidate = baseClean;
                int suffix = 2;
                while (usedNormalized.Contains(candidate))
                {
                    candidate = $"{baseClean}_{suffix}";
                    suffix++;
                }
                usedNormalized.Add(candidate);
                if (!string.Equals(orig, candidate, StringComparison.Ordinal))
                    renameMap[orig] = candidate;
            }

            // Also cover any PreservedCustomBoneNames not present in BoneNames
            foreach (var orig in bundle.PreservedCustomBoneNames.ToList())
            {
                if (string.IsNullOrEmpty(orig) || renameMap.ContainsKey(orig))
                    continue;
                string baseClean = SanitizeCustomBoneName(orig);
                string candidate = baseClean;
                int suffix = 2;
                while (usedNormalized.Contains(candidate))
                {
                    candidate = $"{baseClean}_{suffix}";
                    suffix++;
                }
                usedNormalized.Add(candidate);
                if (!string.Equals(orig, candidate, StringComparison.Ordinal))
                    renameMap[orig] = candidate;
            }

            if (renameMap.Count == 0)
                return;

            string Remap(string s) => (s != null && renameMap.TryGetValue(s, out string mapped)) ? mapped : s;

            for (int i = 0; i < bundle.BoneNames.Count; i++)
                bundle.BoneNames[i] = Remap(bundle.BoneNames[i]);

            var updatedPreserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var bn in bundle.PreservedCustomBoneNames)
                updatedPreserved.Add(Remap(bn));
            bundle.PreservedCustomBoneNames.Clear();
            foreach (var bn in updatedPreserved)
                bundle.PreservedCustomBoneNames.Add(bn);

            var newHierarchy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in bundle.BoneHierarchy)
                newHierarchy[Remap(kv.Key)] = Remap(kv.Value);
            bundle.BoneHierarchy.Clear();
            foreach (var kv in newHierarchy)
                bundle.BoneHierarchy[kv.Key] = kv.Value;

            var newLocalRest = new Dictionary<string, Transform3D>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in bundle.BoneLocalRestTransforms)
                newLocalRest[Remap(kv.Key)] = kv.Value;
            bundle.BoneLocalRestTransforms.Clear();
            foreach (var kv in newLocalRest)
                bundle.BoneLocalRestTransforms[kv.Key] = kv.Value;

            var newRest = new Dictionary<string, Transform3D>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in bundle.BoneRestTransforms)
                newRest[Remap(kv.Key)] = kv.Value;
            bundle.BoneRestTransforms.Clear();
            foreach (var kv in newRest)
                bundle.BoneRestTransforms[kv.Key] = kv.Value;

            foreach (var ch in bundle.PreservedChains)
            {
                if (ch == null) continue;
                ch.AnchorHeroBone = Remap(ch.AnchorHeroBone);
                ch.RootSourceBone = Remap(ch.RootSourceBone);
                for (int i = 0; i < ch.SourceBoneNames.Count; i++)
                    ch.SourceBoneNames[i] = Remap(ch.SourceBoneNames[i]);
                for (int i = 0; i < ch.AppendedHeroBoneNames.Count; i++)
                    ch.AppendedHeroBoneNames[i] = Remap(ch.AppendedHeroBoneNames[i]);
            }
        }

        /// <summary>
        /// Registers kept visible vanilla props onto an existing export bundle on the main thread:
        /// - Standalone vanilla submeshes go into AllowedKeptVanillaDmxNames (preserved natively as .dmx RenderMeshFiles).
        /// - Split composite vanilla surfaces (e.g. Venator's shotgun/crossbow/stake split out of priest.dmx) are
        ///   extracted into PreservedVanillaSubmeshes so they compile into {hero}_kept_props.smd pointing directly to
        ///   their authentic vanilla .vmat without generating duplicate .vmat/.vtex files.
        /// </summary>
        public static void AddPreservedVanillaMeshes(
            CustomModelExportBundle bundle,
            IEnumerable<MeshInstance3D> keptCompositeMeshes,
            IEnumerable<string> keptStandaloneNames,
            Skeleton3D skeleton)
        {
            if (bundle == null) return;
            bundle.HasExplicitKeptVanillaFilter = true;

            if (keptStandaloneNames != null)
            {
                foreach (var rawName in keptStandaloneNames)
                {
                    if (string.IsNullOrWhiteSpace(rawName)) continue;
                    string norm = rawName.Trim();
                    string noLead = norm.TrimStart('_');
                    bundle.AllowedKeptVanillaDmxNames.Add(norm);
                    if (!string.IsNullOrEmpty(noLead))
                        bundle.AllowedKeptVanillaDmxNames.Add(noLead);
                }
            }

            if (keptCompositeMeshes == null) return;

            var emptyClothSet = new HashSet<int>();
            foreach (var meshInst in keptCompositeMeshes)
            {
                if (meshInst == null || !GodotObject.IsInstanceValid(meshInst) || meshInst.Mesh == null || !meshInst.Visible)
                    continue;

                Mesh mesh = meshInst.Mesh;
                int surfCount = mesh.GetSurfaceCount();
                Dictionary<int, int> bindToSkelMap = BuildSkinBindToSkeletonMap(skeleton, meshInst.Skin);

                for (int s = 0; s < surfCount; s++)
                {
                    var arrays = mesh.SurfaceGetArrays(s);
                    if (arrays == null || arrays.Count <= (int)Mesh.ArrayType.Vertex) continue;
                    var vVar = arrays[(int)Mesh.ArrayType.Vertex];
                    if (vVar.VariantType == Variant.Type.Nil) continue;
                    Vector3[] rawVerts = vVar.AsVector3Array();
                    if (rawVerts == null || rawVerts.Length == 0) continue;

                    int vCount = rawVerts.Length;
                    Vector3[] rawNorms = arrays.Count > (int)Mesh.ArrayType.Normal && arrays[(int)Mesh.ArrayType.Normal].VariantType != Variant.Type.Nil
                        ? arrays[(int)Mesh.ArrayType.Normal].AsVector3Array()
                        : null;
                    Vector2[] rawUvs = arrays.Count > (int)Mesh.ArrayType.TexUV && arrays[(int)Mesh.ArrayType.TexUV].VariantType != Variant.Type.Nil
                        ? arrays[(int)Mesh.ArrayType.TexUV].AsVector2Array()
                        : null;
                    int[] rawIndices = arrays.Count > (int)Mesh.ArrayType.Index && arrays[(int)Mesh.ArrayType.Index].VariantType != Variant.Type.Nil
                        ? arrays[(int)Mesh.ArrayType.Index].AsInt32Array()
                        : null;

                    if (rawNorms == null || rawNorms.Length != vCount)
                    {
                        rawNorms = new Vector3[vCount];
                        Array.Fill(rawNorms, Vector3.Up);
                    }
                    if (rawUvs == null || rawUvs.Length != vCount)
                        rawUvs = new Vector2[vCount];
                    if (rawIndices == null || rawIndices.Length == 0)
                    {
                        rawIndices = new int[vCount];
                        for (int k = 0; k < vCount; k++) rawIndices[k] = k;
                    }

                    int[] rawBones = arrays.Count > (int)Mesh.ArrayType.Bones && arrays[(int)Mesh.ArrayType.Bones].VariantType != Variant.Type.Nil
                        ? arrays[(int)Mesh.ArrayType.Bones].AsInt32Array()
                        : null;
                    float[] rawWeights = arrays.Count > (int)Mesh.ArrayType.Weights && arrays[(int)Mesh.ArrayType.Weights].VariantType != Variant.Type.Nil
                        ? arrays[(int)Mesh.ArrayType.Weights].AsFloat32Array()
                        : null;

                    int[] cappedBones = new int[vCount * 4];
                    float[] cappedWeights = new float[vCount * 4];
                    NormalizeAndCapWeights(
                        vCount,
                        rawBones,
                        rawWeights,
                        bindToSkelMap,
                        emptyClothSet,
                        skeleton,
                        bundle.BoneNames.Count,
                        cappedBones,
                        cappedWeights,
                        stabilizeNeckFloatingHead: false);

                    string fullVmatPath = HeroMeshHierarchy.ResolveAuthenticVmatFullPath(meshInst, s);
                    string origVmatPath = !string.IsNullOrEmpty(fullVmatPath)
                        ? fullVmatPath
                        : (meshInst.HasMeta("OriginalVmatPath") ? meshInst.GetMeta("OriginalVmatPath").AsString() : "");
                    origVmatPath = (origVmatPath ?? "").Replace('\\', '/').TrimStart('/');
                    if (origVmatPath.EndsWith(".vmat_c", StringComparison.OrdinalIgnoreCase))
                        origVmatPath = origVmatPath[..^2];

                    string matName = !string.IsNullOrEmpty(origVmatPath)
                        ? SanitizeMaterialName(Path.GetFileNameWithoutExtension(origVmatPath))
                        : SanitizeMaterialName(meshInst.Name.ToString());

                    bundle.PreservedVanillaSubmeshes.Add(new CustomModelExportBundle.SubmeshData
                    {
                        SubmeshName = surfCount > 1 ? $"{meshInst.Name}_s{s}" : meshInst.Name.ToString(),
                        MaterialName = matName,
                        PreserveVanillaVmatPath = origVmatPath,
                        Vertices = rawVerts,
                        Normals = rawNorms,
                        UVs = rawUvs,
                        Indices = rawIndices,
                        BoneIndices = cappedBones,
                        BoneWeights = cappedWeights
                    });
                }
            }
        }

        private static Dictionary<int, int> BuildSkinBindToSkeletonMap(Skeleton3D skeleton, Skin skin)
        {
            var bindToSkel = new Dictionary<int, int>();
            if (skeleton == null || !GodotObject.IsInstanceValid(skeleton)) return bindToSkel;

            int boneCount = skeleton.GetBoneCount();
            if (skin != null && GodotObject.IsInstanceValid(skin) && skin.GetBindCount() > 0)
            {
                int bindCount = skin.GetBindCount();
                for (int i = 0; i < bindCount; i++)
                {
                    int skelBone = skin.GetBindBone(i);
                    if (skelBone < 0 && !string.IsNullOrEmpty(skin.GetBindName(i)))
                    {
                        skelBone = skeleton.FindBone(skin.GetBindName(i));
                    }
                    if (skelBone >= 0 && skelBone < boneCount)
                    {
                        bindToSkel[i] = skelBone;
                    }
                }
            }
            return bindToSkel;
        }

        public static bool IsUniformColor(Image img, out Color uniformColor)
        {
            uniformColor = Colors.White;
            if (img == null || img.IsEmpty()) return false;
            int w = img.GetWidth();
            int h = img.GetHeight();
            if (w * h == 0) return false;
            uniformColor = img.GetPixel(0, 0);
            int stepX = Math.Max(1, w / 16);
            int stepY = Math.Max(1, h / 16);
            for (int y = 0; y < h; y += stepY)
            {
                for (int x = 0; x < w; x += stepX)
                {
                    Color px = img.GetPixel(x, y);
                    if (Math.Abs(px.R - uniformColor.R) > 0.015f ||
                        Math.Abs(px.G - uniformColor.G) > 0.015f ||
                        Math.Abs(px.B - uniformColor.B) > 0.015f ||
                        Math.Abs(px.A - uniformColor.A) > 0.015f)
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        private static byte[] ImageToPngBytesPowerOfTwo(Image srcImg)
        {
            if (srcImg == null || srcImg.IsEmpty()) return null;
            using var img = (Image)srcImg.Duplicate();
            if (img.IsCompressed()) img.Decompress();
            if (img.HasMipmaps()) img.ClearMipmaps();
            if (IsUniformColor(img, out Color uniColor))
            {
                return CreateSolidColorPngBytes(64, 64, uniColor);
            }
            EnsurePowerOfTwoResolution(img);
            return img.SavePngToBuffer();
        }

        private static byte[] ExtractTexturePngPowerOfTwo(Texture2D tex, Color tint)
        {
            if (tex == null) return null;
            var img = tex.GetImage();
            if (img == null || img.IsEmpty()) return null;

            img = (Image)img.Duplicate();
            if (img.IsCompressed()) img.Decompress();

            bool applyTint = MathF.Abs(tint.R - 1.0f) > 0.004f ||
                             MathF.Abs(tint.G - 1.0f) > 0.004f ||
                             MathF.Abs(tint.B - 1.0f) > 0.004f ||
                             MathF.Abs(tint.A - 1.0f) > 0.004f;

            // If the image is a solid uniform color (or tiny placeholder like 16x16),
            // export it at 64x64 instead of a giant 2048x2048 texture, saving ~5.4 MB per material!
            if (IsUniformColor(img, out Color uniColor))
            {
                if (applyTint)
                {
                    uniColor = new Color(uniColor.R * tint.R, uniColor.G * tint.G, uniColor.B * tint.B, uniColor.A * tint.A);
                }
                return CreateSolidColorPngBytes(64, 64, uniColor);
            }

            if (applyTint)
            {
                if (img.GetFormat() != Image.Format.Rgba8)
                {
                    img.Convert(Image.Format.Rgba8);
                }
                int w = img.GetWidth();
                int h = img.GetHeight();
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        Color p = img.GetPixel(x, y);
                        img.SetPixel(x, y, new Color(p.R * tint.R, p.G * tint.G, p.B * tint.B, p.A * tint.A));
                    }
                }
            }

            EnsurePowerOfTwoResolution(img);
            return img.SavePngToBuffer();
        }

        private static byte[] ExtractShaderTexturePngPowerOfTwo(ShaderMaterial sMat, params string[] paramNames)
        {
            if (sMat == null) return null;
            foreach (var p in paramNames)
            {
                var v = sMat.GetShaderParameter(p);
                if (v.VariantType == Variant.Type.Object && v.AsGodotObject() is Texture2D tex)
                {
                    var bytes = ExtractTexturePngPowerOfTwo(tex, Colors.White);
                    if (bytes != null && bytes.Length > 0) return bytes;
                }
            }
            return null;
        }

        private static void EnsurePowerOfTwoResolution(Image img)
        {
            if (img == null || img.IsEmpty()) return;
            int w = img.GetWidth();
            int h = img.GetHeight();
            int potW = NearestPowerOfTwo(w, 64, 4096);
            int potH = NearestPowerOfTwo(h, 64, 4096);
            if (w != potW || h != potH)
            {
                img.Resize(potW, potH, Image.Interpolation.Bilinear);
            }
        }

        private static int NearestPowerOfTwo(int value, int minVal, int maxVal)
        {
            if (value <= minVal) return minVal;
            if (value >= maxVal) return maxVal;
            int lower = 1;
            while (lower * 2 <= value) lower *= 2;
            int upper = lower * 2;
            int chosen = (value - lower < upper - value) ? lower : upper;
            return Math.Clamp(chosen, minVal, maxVal);
        }

        private static string ComputeFastHash(params byte[][] buffers)
        {
            if (buffers == null || buffers.Length == 0 || buffers[0] == null || buffers[0].Length == 0) return "";
            using var sha = SHA256.Create();
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < buffers.Length; i++)
            {
                var buf = buffers[i];
                if (buf == null || buf.Length == 0) continue;
                byte[] h = sha.ComputeHash(buf);
                if (sb.Length > 0) sb.Append('_');
                sb.Append(Convert.ToHexString(h, 0, i == 0 ? 8 : 4));
            }
            return sb.ToString();
        }

        public static bool IsClothBoneName(string boneName)
        {
            if (string.IsNullOrEmpty(boneName)) return false;
            return boneName.StartsWith("$cloth", StringComparison.OrdinalIgnoreCase)
                || boneName.StartsWith("_cloth", StringComparison.OrdinalIgnoreCase)
                || boneName.Contains("$cloth", StringComparison.OrdinalIgnoreCase)
                || boneName.Contains("cloth_sim", StringComparison.OrdinalIgnoreCase);
        }

        private static int FindNonClothFallbackBone(Skeleton3D skeleton, int boneIdx, HashSet<int> clothBones)
        {
            if (skeleton == null) return 0;
            int curr = skeleton.GetBoneParent(boneIdx);
            while (curr >= 0)
            {
                if (!clothBones.Contains(curr)) return curr;
                curr = skeleton.GetBoneParent(curr);
            }
            int pelvis = skeleton.FindBone("pelvis");
            if (pelvis >= 0 && !clothBones.Contains(pelvis)) return pelvis;
            return 0;
        }

        /// <summary>
        /// Maps Skin bind indices to Skeleton3D bone indices, strips $cloth bones, merges duplicate
        /// bone influences, caps to 4 influences per vertex, and normalizes so sum(weights) == 1.0000.
        /// </summary>
        public static void NormalizeAndCapWeights(
            int vCount,
            int[] rawBones,
            float[] rawWeights,
            Dictionary<int, int> bindToSkelMap,
            HashSet<int> clothBoneIndices,
            Skeleton3D skeleton,
            int maxBoneCount,
            int[] outBones,
            float[] outWeights,
            bool stabilizeNeckFloatingHead = false)
        {
            int fallbackRoot = 0;
            int headBoneIdx = skeleton != null ? skeleton.FindBone("head") : -1;
            int neckBoneIdx = skeleton != null ? skeleton.FindBone("neck_0") : -1;
            if (neckBoneIdx < 0 && skeleton != null) neckBoneIdx = skeleton.FindBone("neck");
            int spine3BoneIdx = skeleton != null ? skeleton.FindBone("spine_3") : -1;
            int targetAnchorIdx = neckBoneIdx >= 0 ? neckBoneIdx : spine3BoneIdx;

            if (skeleton != null)
            {
                int pelvis = skeleton.FindBone("pelvis");
                if (pelvis >= 0) fallbackRoot = pelvis;
            }

            if (rawBones == null || rawWeights == null || rawBones.Length == 0 || rawWeights.Length == 0)
            {
                for (int v = 0; v < vCount; v++)
                {
                    outBones[v * 4 + 0] = fallbackRoot;
                    outWeights[v * 4 + 0] = 1.0f;
                }
                return;
            }

            int stride = rawBones.Length / vCount;
            if (stride <= 0) stride = 4;

            var merged = new Dictionary<int, float>(8);

            for (int v = 0; v < vCount; v++)
            {
                merged.Clear();
                int baseOff = v * stride;
                for (int k = 0; k < stride; k++)
                {
                    if (baseOff + k >= rawBones.Length || baseOff + k >= rawWeights.Length) break;
                    int rawIdx = rawBones[baseOff + k];
                    float w = rawWeights[baseOff + k];
                    if (w <= 0.00001f || float.IsNaN(w)) continue;

                    // Resolve Skin bind index -> Skeleton3D bone index
                    int b = (bindToSkelMap != null && bindToSkelMap.Count > 0)
                        ? (bindToSkelMap.TryGetValue(rawIdx, out int mappedSkel) ? mappedSkel : rawIdx)
                        : rawIdx;

                    if (b < 0 || b >= maxBoneCount) b = fallbackRoot;

                    if (clothBoneIndices != null && clothBoneIndices.Contains(b))
                    {
                        b = FindNonClothFallbackBone(skeleton, b, clothBoneIndices);
                    }

                    if (stabilizeNeckFloatingHead && headBoneIdx >= 0 && targetAnchorIdx >= 0 && b == headBoneIdx)
                    {
                        b = targetAnchorIdx;
                    }

                    merged[b] = merged.GetValueOrDefault(b, 0f) + w;
                }

                if (merged.Count == 0)
                {
                    outBones[v * 4 + 0] = fallbackRoot;
                    outWeights[v * 4 + 0] = 1.0f;
                    continue;
                }

                var sorted = merged.OrderByDescending(kv => kv.Value).Take(4).ToList();
                float sum = 0f;
                for (int i = 0; i < sorted.Count; i++) sum += sorted[i].Value;

                if (sum <= 0.00001f)
                {
                    outBones[v * 4 + 0] = fallbackRoot;
                    outWeights[v * 4 + 0] = 1.0f;
                }
                else
                {
                    float assignedSum = 0f;
                    for (int i = 0; i < 4; i++)
                    {
                        if (i < sorted.Count)
                        {
                            outBones[v * 4 + i] = sorted[i].Key;
                            float nw = sorted[i].Value / sum;
                            outWeights[v * 4 + i] = nw;
                            assignedSum += nw;
                        }
                        else
                        {
                            outBones[v * 4 + i] = 0;
                            outWeights[v * 4 + i] = 0f;
                        }
                    }
                    if (sorted.Count > 0 && MathF.Abs(1.0f - assignedSum) > 1e-6f)
                    {
                        outWeights[v * 4 + 0] += (1.0f - assignedSum);
                    }
                }
            }
        }

        /// <summary>
        /// High-level entry point supporting both pre-extracted bundles and main-thread options.
        /// </summary>
        public static async Task<ModelExportResult> ExportCustomModelAsync(
            ModelExportOptions options,
            IProgress<ExportProgressReport> progress = null,
            CancellationToken ct = default)
        {
            if (options == null)
                return new ModelExportResult { Success = false, ErrorMessage = "ModelExportOptions is null." };

            CustomModelExportBundle bundle = options.PreExtractedBundle;
            if (bundle == null)
            {
                Skeleton3D skel = options.HeroRoot != null ? CustomModelAutoRigger.FindSkeleton(options.HeroRoot) : null;
                bundle = BuildExportBundle(
                    options.CustomMeshes,
                    skel,
                    options.HeroCodename,
                    options.ModelVpkRelativePath,
                    null,
                    options.StagingDir,
                    options.ResourceCompilerExe,
                    options.DeadlockGamePath);
                bundle.OutputVpkPath = options.OutputVpkPath;
            }

            return await ExportAndCompileCustomModelAsync(bundle, progress, ct);
        }

        /// <summary>
        /// Worker-thread safe compilation and optional standalone VPK packaging using CustomModelExportBundle.
        /// Never calls Godot APIs on background threads.
        /// </summary>
        public static async Task<ModelExportResult> ExportAndCompileCustomModelAsync(
            CustomModelExportBundle bundle,
            IProgress<ExportProgressReport> progress = null,
            CancellationToken ct = default)
        {
            var result = new ModelExportResult();
            if (bundle == null || bundle.Submeshes.Count == 0)
            {
                result.ErrorMessage = "No custom submeshes provided in CustomModelExportBundle.";
                return result;
            }

            return await Task.Run(() =>
            {
                try
                {
                    ct.ThrowIfCancellationRequested();

                    string hero = string.IsNullOrEmpty(bundle.HeroCodename) ? "hero" : bundle.HeroCodename.ToLowerInvariant();
                    string vmdlRel = string.IsNullOrEmpty(bundle.HeroModelVpkPath)
                        ? $"models/heroes_staging/{hero}/{hero}.vmdl_c"
                        : bundle.HeroModelVpkPath.Replace('\\', '/').TrimStart('/');

                    if (vmdlRel.EndsWith(".vmdl_c", StringComparison.OrdinalIgnoreCase))
                        vmdlRel = vmdlRel[..^2];
                    else if (!vmdlRel.EndsWith(".vmdl", StringComparison.OrdinalIgnoreCase))
                        vmdlRel += ".vmdl";

                    string modelFolderRel = Path.GetDirectoryName(vmdlRel)?.Replace('\\', '/') ?? $"models/heroes_staging/{hero}";
                    string modelBaseName = Path.GetFileNameWithoutExtension(vmdlRel);
                    string materialsFolderRel = $"{modelFolderRel}/materials";

                    // Resolve CSDK13 / compiler and isolated addon staging directories
                    string compilerExe = bundle.ResourceCompilerExe;
                    if (string.IsNullOrEmpty(compilerExe) || !File.Exists(compilerExe))
                    {
                        var pm = new GamePathManager();
                        compilerExe = pm.ResolveResourceCompilerExe();
                    }

                    string csdkRoot = ResolveCsdkRootFromStagingOrCompiler(bundle.StagingDir, compilerExe);
                    string contentAddonRoot = Path.Combine(csdkRoot, "content", "citadel_addons", "_temp_export");
                    string gameAddonRoot = Path.Combine(csdkRoot, "game", "citadel_addons", "_temp_export");
                    result.GameStagingAddonRoot = gameAddonRoot;

                    // Clean isolated staging directories before starting so zero stale files exist
                    try
                    {
                        if (Directory.Exists(contentAddonRoot)) Directory.Delete(contentAddonRoot, true);
                        if (Directory.Exists(gameAddonRoot)) Directory.Delete(gameAddonRoot, true);
                    }
                    catch { /* Best effort clean */ }

                    string contentModelDir = Path.Combine(contentAddonRoot, modelFolderRel.Replace('/', Path.DirectorySeparatorChar));
                    string contentMatDir = Path.Combine(contentAddonRoot, materialsFolderRel.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(contentModelDir);
                    Directory.CreateDirectory(contentMatDir);

                    // 1. Decompile vanilla hero .vmdl_c via ValveResourceFormat.IO.ModelExtract
                    progress?.Report(new ExportProgressReport(0.30f, $"[INFO] Extracting authentic ModelDoc template from vanilla '{vmdlRel}_c' via ValveResourceFormat..."));
                    var (decompiledVmdlText, extractedSubfiles, vrfBones) = DecompileVanillaHeroVmdl(bundle, vmdlRel + "_c", modelFolderRel, reconstructSoftbody: false);

                    // Write physics hull/mesh .dmx subfiles, cloth proxies (when enabled), and non-replaced weapon/prop .dmx subfiles into contentModelDir
                    var writtenSubfileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    bool customReplacesWeapon = bundle.Submeshes.Any(sm => IsWeaponReplacementSubmesh(sm));
                    var allowedStandaloneDmxLeafs = ResolveAllowedStandaloneDmxLeafs(decompiledVmdlText, bundle, customReplacesWeapon);

                    foreach (var (subFileName, subBytes) in extractedSubfiles)
                    {
                        if (subBytes == null || subBytes.Length == 0) continue;
                        string leafName = Path.GetFileName(subFileName);
                        string lowerLeaf = leafName.ToLowerInvariant();
                        string stem = Path.GetFileNameWithoutExtension(leafName).TrimStart('_');

                        bool isPhysicsShape = lowerLeaf.Contains("hull") || lowerLeaf.Contains("phys");
                        bool isClothProxy = lowerLeaf.Contains("cloth_proxy") && allowedStandaloneDmxLeafs.Count > 0;
                        bool isWeaponDmx = !lowerLeaf.Contains("_lod") && (
                            allowedStandaloneDmxLeafs.Contains(leafName) ||
                            (bundle.HasExplicitKeptVanillaFilter
                                ? (bundle.AllowedKeptVanillaDmxNames.Contains(stem) || bundle.AllowedKeptVanillaDmxNames.Contains(Path.GetFileNameWithoutExtension(leafName)))
                                : (!customReplacesWeapon && (lowerLeaf.Contains("gun") || lowerLeaf.Contains("weapon")))));

                        if (isPhysicsShape || isClothProxy || isWeaponDmx)
                        {
                            string outSubPath = Path.Combine(contentModelDir, leafName);
                            File.WriteAllBytes(outSubPath, subBytes);
                            writtenSubfileNames.Add(leafName);
                        }
                    }

                    // Authoritative bone list: directly from vanilla vmdl_c m_modelSkeleton (vrfBones)
                    // This guarantees bit-for-bit identical rest pose and local bone axes to vanilla,
                    // preventing Ragdoll physics from exploding/twisting on death!
                    List<VmdlSkeletonBone> targetSkeletonBones = null;
                    if (vrfBones != null && vrfBones.Count > 0)
                    {
                        targetSkeletonBones = vrfBones;
                    }
                    else
                    {
                        targetSkeletonBones = ParseSkeletonBonesFromVmdlText(decompiledVmdlText);
                    }
                    MergePreservedCustomBonesIntoSkeleton(bundle, targetSkeletonBones);
                    ExtendShortLeafClothBonesInBundle(bundle, targetSkeletonBones);
                    GD.Print($"[CustomModelExporter] Final exported bone list ({targetSkeletonBones.Count} bones, {bundle.PreservedCustomBoneNames.Count} custom/preserved): " +
                             string.Join(", ", targetSkeletonBones.Select(b => b.Name)));

                    // 2. Group custom submeshes into body vs accessory groups and write Source 2 bind-pose SMD(s)
                    string primaryBodyMeshName = DetectPrimaryBodyGroupMeshName(decompiledVmdlText, modelBaseName);
                    bool primaryBodyKeptInVanillaDmx = writtenSubfileNames.Any(leaf =>
                    {
                        string stem = Path.GetFileNameWithoutExtension(leaf).TrimStart('_');
                        return stem.Equals(primaryBodyMeshName.TrimStart('_'), StringComparison.OrdinalIgnoreCase) &&
                               (bundle.AllowedKeptVanillaDmxNames.Contains(primaryBodyMeshName) ||
                                bundle.AllowedKeptVanillaDmxNames.Contains(stem));
                    });
                    string customSmdMeshName = primaryBodyKeptInVanillaDmx
                        ? $"{modelBaseName}_custom_accessories"
                        : primaryBodyMeshName;

                    string smdFileName = $"{modelBaseName}_body_custom.smd";
                    string smdFullPath = Path.Combine(contentModelDir, smdFileName);
                    string smdRelPath = $"{modelFolderRel}/{smdFileName}";

                    progress?.Report(new ExportProgressReport(0.38f, $"[INFO] Writing Source 2 bind-pose SMD '{smdFileName}' ({bundle.Submeshes.Count} surface(s))..."));
                    WriteSourceSpaceSmd(smdFullPath, bundle, bundle.Submeshes, targetSkeletonBones);

                    string animBindFileName = $"{modelBaseName}_anim_bind.smd";
                    string animBindFullPath = Path.Combine(contentModelDir, animBindFileName);
                    WriteRestBindAnimationSmd(animBindFullPath, bundle, targetSkeletonBones);

                    var customRenderMeshes = new List<(string MeshName, string SmdRelativePath)>
                    {
                        (customSmdMeshName, smdRelPath)
                    };

                    if (bundle.PreservedVanillaSubmeshes.Count > 0)
                    {
                        string keptSmdFileName = $"{modelBaseName}_kept_props.smd";
                        string keptSmdFullPath = Path.Combine(contentModelDir, keptSmdFileName);
                        string keptSmdRelPath = $"{modelFolderRel}/{keptSmdFileName}";
                        progress?.Report(new ExportProgressReport(0.41f, $"[INFO] Writing preserved vanilla props SMD '{keptSmdFileName}' ({bundle.PreservedVanillaSubmeshes.Count} surface(s))..."));
                        WriteSourceSpaceSmd(keptSmdFullPath, bundle, bundle.PreservedVanillaSubmeshes, targetSkeletonBones);
                        customRenderMeshes.Add(($"{modelBaseName}_kept_props", keptSmdRelPath));
                    }

                    // Hybrid physics export: ProxyCloth chains stitch into a single-sided ClothProxyMeshFile DMX sheet,
                    // while ClothChain & compact Jiggle chains compile natively as Softbody ClothChain nodes alongside
                    // mesh-fitted ClothShapeCapsule colliders (no overlapping cloth nodes on the same bones).
                    var activePhysicsChains = bundle.PreservedChains?
                        .Where(c => c != null && c.Policy == ChainPolicy.PreservePhysicsChain)
                        .ToList() ?? new List<BoneChainDef>();
                    var proxyClothChains = activePhysicsChains
                        .Where(c => c.Simulation == SimulationKind.ProxyCloth)
                        .ToList();
                    int clothChainCount = activePhysicsChains.Count - proxyClothChains.Count;

                    if (proxyClothChains.Count > 0)
                    {
                        string proxyLeaf = $"{modelBaseName}_cloth_proxy.dmx";
                        string proxyFullPath = Path.Combine(contentModelDir, proxyLeaf);
                        string proxyRelPath = $"{modelFolderRel}/{proxyLeaf}";
                        if (WriteProceduralClothProxyDmx(proxyFullPath, proxyRelPath, bundle, targetSkeletonBones))
                        {
                            writtenSubfileNames.Add(proxyLeaf);
                            progress?.Report(new ExportProgressReport(0.42f, $"[INFO] Generated stitched ProxyCloth DMX '{proxyLeaf}' for {proxyClothChains.Count} sheet strand(s)."));
                        }
                    }

                    if (clothChainCount > 0)
                    {
                        progress?.Report(new ExportProgressReport(0.43f, $"[INFO] Configuring native Source 2 Softbody ClothChain physics ({clothChainCount} chain(s)) + body collision capsules..."));
                    }

                    // 3. Write custom materials (.vmat) and power-of-2 textures (.png + .vtex) into contentMatDir
                    progress?.Report(new ExportProgressReport(0.45f, $"[INFO] Generating custom materials and textures in '{materialsFolderRel}'..."));
                    var materialFiles = WriteAllCustomMaterialsAndTextures(
                        bundle,
                        contentMatDir,
                        materialsFolderRel,
                        out List<string> writtenVtexPaths,
                        out List<string> writtenVmatPaths);

                    // 4. Generate patched .vmdl preserving AttachmentList, NmSkeletonList, AnimGraph2List, HitboxSetList, GameDataList, Skeleton
                    string vmdlFullPath = Path.Combine(contentAddonRoot, vmdlRel.Replace('/', Path.DirectorySeparatorChar));
                    string vmdlContent = GeneratePatchedVmdl(
                        decompiledVmdlText,
                        bundle,
                        modelFolderRel,
                        materialsFolderRel,
                        customRenderMeshes,
                        materialFiles.Keys.ToList(),
                        writtenSubfileNames,
                        includeAnimGraph2Nodes: true);
                    File.WriteAllText(vmdlFullPath, vmdlContent, new UTF8Encoding(false));

                    // 5. Compile with headless resourcecompiler.exe (CSDK13 native / CSDK12 fallback)
                    if (!string.IsNullOrEmpty(compilerExe) && File.Exists(compilerExe))
                    {
                        // 5a. Compile each .vmat (which automatically compiles its referenced _color.png / _normal.png -> .vtex_c in CSDK13!)
                        int matIdx = 0;
                        foreach (var vmatPath in writtenVmatPaths)
                        {
                            ct.ThrowIfCancellationRequested();
                            matIdx++;
                            progress?.Report(new ExportProgressReport(
                                0.50f + 0.20f * ((float)matIdx / Math.Max(1, writtenVmatPaths.Count)),
                                $"[INFO] Compiling custom material ({matIdx}/{writtenVmatPaths.Count}): {Path.GetFileName(vmatPath)}..."));
                            RunResourceCompiler(compilerExe, vmatPath, out string matLog);
                        }

                        // 5b. Compile the patched .vmdl -> .vmdl_c
                        progress?.Report(new ExportProgressReport(0.78f, $"[INFO] Compiling custom hero model '{Path.GetFileName(vmdlFullPath)}'..."));
                        bool vmdlOk = RunResourceCompiler(compilerExe, vmdlFullPath, out string vmdlLog);

                        string expectedVmdlC = Path.Combine(gameAddonRoot, (vmdlRel + "_c").Replace('/', Path.DirectorySeparatorChar));

                        // CSDK12 Fallback: If older compiler fails on NmSkeletonList/AnimGraph2List class allocation,
                        // strip those two blocks, compile with CSDK12, and binary-patching AnimGraph2 references into .vmdl_c!
                        if ((!vmdlOk || !File.Exists(expectedVmdlC)) &&
                            vmdlLog != null &&
                            (vmdlLog.Contains("NmSkeletonList", StringComparison.OrdinalIgnoreCase) ||
                             vmdlLog.Contains("AnimGraph2List", StringComparison.OrdinalIgnoreCase) ||
                             vmdlLog.Contains("Failed to allocate an instance of class", StringComparison.OrdinalIgnoreCase)))
                        {
                            progress?.Report(new ExportProgressReport(0.82f, "[INFO] Legacy CSDK12 detected; compiling geometry and binary-patching AnimGraph 2 metadata..."));
                            string fallbackVmdl = GeneratePatchedVmdl(
                                decompiledVmdlText,
                                bundle,
                                modelFolderRel,
                                materialsFolderRel,
                                customRenderMeshes,
                                materialFiles.Keys.ToList(),
                                writtenSubfileNames,
                                includeAnimGraph2Nodes: false);
                            File.WriteAllText(vmdlFullPath, fallbackVmdl, new UTF8Encoding(false));
                            vmdlOk = RunResourceCompiler(compilerExe, vmdlFullPath, out vmdlLog);

                            if (File.Exists(expectedVmdlC))
                            {
                                InjectAnimGraph2ReferencesIntoCompiledVmdlC(expectedVmdlC, bundle, vmdlRel + "_c");
                            }
                        }

                        if (!File.Exists(expectedVmdlC))
                        {
                            if (!string.IsNullOrWhiteSpace(vmdlLog))
                            {
                                progress?.Report(new ExportProgressReport(0.85f, $"[ERROR] resourcecompiler.exe output:\n{vmdlLog.Trim()}", ExportLogLevel.Error));
                            }
                            result.Success = false;
                            result.ErrorMessage = $"resourcecompiler.exe failed to compile '{Path.GetFileName(vmdlFullPath)}' (see log console for details).";
                            return result;
                        }

                        result.CompiledVmdlCPath = expectedVmdlC;
                        string vmdlRelKey = (vmdlRel + "_c").Replace('\\', '/').TrimStart('/');
                        result.AllowlistedVpkFiles[vmdlRelKey] = expectedVmdlC;

                        // Check for any emitted child .vmesh_c inside gameAddonRoot/models/...
                        string gameModelDir = Path.Combine(gameAddonRoot, modelFolderRel.Replace('/', Path.DirectorySeparatorChar));
                        if (Directory.Exists(gameModelDir))
                        {
                            foreach (var vmeshFile in Directory.GetFiles(gameModelDir, "*.vmesh_c", SearchOption.AllDirectories))
                            {
                                string rel = Path.GetRelativePath(gameAddonRoot, vmeshFile).Replace('\\', '/').TrimStart('/');
                                result.CompiledVmeshCPath ??= vmeshFile;
                                result.AllowlistedVpkFiles[rel] = vmeshFile;
                            }

                            // Collect ONLY the compiled .vmat_c and .vtex_c emitted inside gameAddonRoot/models/...
                            foreach (var vmatC in Directory.GetFiles(gameModelDir, "*.vmat_c", SearchOption.AllDirectories))
                            {
                                string rel = Path.GetRelativePath(gameAddonRoot, vmatC).Replace('\\', '/').TrimStart('/');
                                result.CompiledVmatCFiles.Add(vmatC);
                                result.AllowlistedVpkFiles[rel] = vmatC;
                            }

                            foreach (var vtexC in Directory.GetFiles(gameModelDir, "*.vtex_c", SearchOption.AllDirectories))
                            {
                                string rel = Path.GetRelativePath(gameAddonRoot, vtexC).Replace('\\', '/').TrimStart('/');
                                result.CompiledVtexCFiles.Add(vtexC);
                                result.AllowlistedVpkFiles[rel] = vtexC;
                            }

                            // Verify every .vtex_c referenced by every compiled .vmat_c is staged in AllowlistedVpkFiles
                            foreach (var vmatC in result.CompiledVmatCFiles)
                            {
                                try
                                {
                                    using var vmatRes = new ValveResourceFormat.Resource();
                                    vmatRes.Read(vmatC);
                                    if (vmatRes.DataBlock is ValveResourceFormat.ResourceTypes.Material matData)
                                    {
                                        foreach (var texParam in matData.TextureParams)
                                        {
                                            if (string.IsNullOrWhiteSpace(texParam.Value)) continue;
                                            string vtexRelC = texParam.Value.Replace('\\', '/').TrimStart('/');
                                            if (!vtexRelC.EndsWith("_c", StringComparison.OrdinalIgnoreCase))
                                                vtexRelC += "_c";

                                            if (!result.AllowlistedVpkFiles.ContainsKey(vtexRelC))
                                            {
                                                string candidateInAddon = Path.Combine(gameAddonRoot, vtexRelC.Replace('/', Path.DirectorySeparatorChar));
                                                string candidateInCitadel = Path.Combine(csdkRoot, "game", "citadel", vtexRelC.Replace('/', Path.DirectorySeparatorChar));
                                                if (File.Exists(candidateInAddon))
                                                {
                                                    result.AllowlistedVpkFiles[vtexRelC] = candidateInAddon;
                                                }
                                                else if (File.Exists(candidateInCitadel) && vtexRelC.StartsWith("models/", StringComparison.OrdinalIgnoreCase))
                                                {
                                                    result.AllowlistedVpkFiles[vtexRelC] = candidateInCitadel;
                                                }
                                            }
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    GD.PrintErr($"[CustomModelExporter] Warning inspecting '{Path.GetFileName(vmatC)}': {ex.Message}");
                                }
                            }
                        }
                    }

                    // 6. Optional standalone VPK packaging (used by tests or direct model export)
                    if (!string.IsNullOrEmpty(bundle.OutputVpkPath))
                    {
                        var vpkEntries = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

                        foreach (var (relPath, diskPath) in result.AllowlistedVpkFiles)
                        {
                            if (File.Exists(diskPath))
                            {
                                vpkEntries[relPath] = File.ReadAllBytes(diskPath);
                            }
                        }

                        if (!ValidateAllowlist(vpkEntries.Keys, out string rejectedReason))
                        {
                            result.Success = false;
                            result.ErrorMessage = $"VPK allowlist validation failed: {rejectedReason}";
                            return result;
                        }

                        if (vpkEntries.Count > 65)
                        {
                            result.Success = false;
                            result.ErrorMessage = $"VPK file count ({vpkEntries.Count}) exceeded strict upper bound of 65 files.";
                            return result;
                        }

                        if (vpkEntries.Count > 0)
                        {
                            WriteStandaloneVpk(bundle.OutputVpkPath, vpkEntries);
                        }
                    }

                    result.Success = true;
                    return result;
                }
                catch (OperationCanceledException)
                {
                    result.Success = false;
                    result.ErrorMessage = "Model export cancelled.";
                    return result;
                }
                catch (Exception ex)
                {
                    result.Success = false;
                    result.ErrorMessage = ex.Message;
                    return result;
                }
            }, ct);
        }

        private static string ResolveCsdkRootFromStagingOrCompiler(string stagingDir, string compilerExe)
        {
            if (!string.IsNullOrEmpty(compilerExe))
            {
                string norm = compilerExe.Replace('\\', '/');
                int gameBinIdx = norm.IndexOf("/game/bin", StringComparison.OrdinalIgnoreCase);
                if (gameBinIdx > 0)
                {
                    return norm.Substring(0, gameBinIdx).Replace('/', Path.DirectorySeparatorChar);
                }
            }

            if (!string.IsNullOrEmpty(stagingDir))
            {
                string norm = stagingDir.Replace('\\', '/').TrimEnd('/');
                int addonIdx = norm.IndexOf("/content/citadel_addons", StringComparison.OrdinalIgnoreCase);
                if (addonIdx > 0) return norm.Substring(0, addonIdx).Replace('/', Path.DirectorySeparatorChar);

                int contentIdx = norm.IndexOf("/content/citadel", StringComparison.OrdinalIgnoreCase);
                if (contentIdx > 0) return norm.Substring(0, contentIdx).Replace('/', Path.DirectorySeparatorChar);

                return stagingDir;
            }

            return Path.Combine(Path.GetTempPath(), $"deadlock_custom_export_{Guid.NewGuid():N}");
        }

        /// <summary>
        /// Opens the vanilla hero's .vmdl_c from OriginalHeroVmdlCBytes or Deadlock's pak01_dir.vpk
        /// and decompiles it via ValveResourceFormat.IO.ModelExtract.
        /// </summary>
        private sealed class EmptyFileLoader : IFileLoader
        {
            public ValveResourceFormat.Resource LoadFile(string file) => null;
            public ValveResourceFormat.Resource LoadFileCompiled(string file) => null;
            public ValveResourceFormat.CompiledShader.ShaderCollection LoadShader(string shaderName) => null;
            public Stream GetFileStream(string file) => null;
        }

        public static (string DecompiledVmdl, Dictionary<string, byte[]> Subfiles, List<VmdlSkeletonBone> VrfBones) DecompileVanillaHeroVmdl(
            CustomModelExportBundle bundle,
            string vmdlCPath,
            string modelFolderRel,
            bool reconstructSoftbody = false)
        {
            var subfiles = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            var vrfBones = new List<VmdlSkeletonBone>();
            string decompiledVmdl = null;

            Package basePackage = null;
            try
            {
                string vpkPath = null;
                if (!string.IsNullOrEmpty(bundle.DeadlockGamePath))
                {
                    string candidate = Path.Combine(bundle.DeadlockGamePath, "game", "citadel", "pak01_dir.vpk");
                    if (File.Exists(candidate)) vpkPath = candidate;
                }
                if (string.IsNullOrEmpty(vpkPath))
                {
                    var pm = new GamePathManager();
                    string gp = !string.IsNullOrEmpty(pm.CurrentGamePath) ? pm.CurrentGamePath : pm.AutoDetectDeadlockPath();
                    if (!string.IsNullOrEmpty(gp))
                    {
                        string candidate = Path.Combine(gp, "game", "citadel", "pak01_dir.vpk");
                        if (File.Exists(candidate)) vpkPath = candidate;
                    }
                }

                byte[] vmdlBytes = bundle.OriginalHeroVmdlCBytes;
                GameFileLoader fileLoader = null;

                if (!string.IsNullOrEmpty(vpkPath) && File.Exists(vpkPath))
                {
                    basePackage = new Package();
                    basePackage.Read(vpkPath);
                    fileLoader = new GameFileLoader(basePackage, modelFolderRel);

                    if (vmdlBytes == null || vmdlBytes.Length == 0)
                    {
                        var entry = basePackage.FindEntry(vmdlCPath.Replace('\\', '/'));
                        if (entry != null)
                        {
                            basePackage.ReadEntry(entry, out vmdlBytes);
                            bundle.OriginalHeroVmdlCBytes = vmdlBytes;
                        }
                    }
                }

                if (vmdlBytes != null && vmdlBytes.Length > 0)
                {
                    using var vrfResource = new ValveResourceFormat.Resource
                    {
                        FileName = vmdlCPath.EndsWith("_c", StringComparison.OrdinalIgnoreCase) ? vmdlCPath[..^2] : vmdlCPath
                    };
                    using var ms = new MemoryStream(vmdlBytes);
                    vrfResource.Read(ms);

                    if (vrfResource.DataBlock is Model vrfModel && vrfModel.Skeleton?.Bones != null)
                    {
                        foreach (var b in vrfModel.Skeleton.Bones)
                        {
                            Vector3 eulerDeg = QuaternionToSourceQAngleDegrees(b.Angle);
                            vrfBones.Add(new VmdlSkeletonBone
                            {
                                Index = b.Index,
                                Name = b.Name,
                                ParentIndex = b.Parent?.Index ?? -1,
                                Origin = new Vector3(b.Position.X, b.Position.Y, b.Position.Z),
                                AnglesDeg = eulerDeg,
                                ParentRot = b.Angle,
                                HasParentRot = true
                            });
                        }
                    }

                    // Extract vanilla body collision capsules & spheres from FeModel so custom hair/cloth collides cleanly with torso/neck/arms
                    var physFe = (vrfResource.DataBlock as Model)?.GetEmbeddedPhys()?.FeModel;
                    if (physFe != null && bundle != null && bundle.VanillaCollisionCapsules.Count == 0)
                    {
                        var ctrlNames = physFe.CtrlName;
                        if (physFe.TaperedCapsuleRigids != null)
                        {
                            foreach (var cap in physFe.TaperedCapsuleRigids)
                            {
                                if (cap.Spheres == null || cap.Spheres.Length < 2) continue;
                                string bName = (ctrlNames != null && cap.Node >= 0 && cap.Node < ctrlNames.Length) ? ctrlNames[cap.Node] : null;
                                if (string.IsNullOrWhiteSpace(bName)) continue;
                                var s0 = cap.Spheres[0];
                                var s1 = cap.Spheres[1];
                                bundle.VanillaCollisionCapsules.Add(new CustomModelExportBundle.ClothCollisionCapsuleDef
                                {
                                    ParentBone = bName,
                                    Point0 = new Vector3(s0.X, s0.Y, s0.Z),
                                    Radius0 = MathF.Max(0.25f, s0.W),
                                    Point1 = new Vector3(s1.X, s1.Y, s1.Z),
                                    Radius1 = MathF.Max(0.25f, s1.W)
                                });
                            }
                        }
                        if (physFe.SphereRigids != null)
                        {
                            foreach (var sph in physFe.SphereRigids)
                            {
                                string bName = (ctrlNames != null && sph.Node >= 0 && sph.Node < ctrlNames.Length) ? ctrlNames[sph.Node] : null;
                                if (string.IsNullOrWhiteSpace(bName) || !sph.Sphere.HasValue) continue;
                                var s = sph.Sphere.Value;
                                bundle.VanillaCollisionCapsules.Add(new CustomModelExportBundle.ClothCollisionCapsuleDef
                                {
                                    ParentBone = bName,
                                    Point0 = new Vector3(s.X, s.Y, s.Z),
                                    Radius0 = MathF.Max(0.25f, s.W),
                                    Point1 = new Vector3(s.X, s.Y, s.Z),
                                    Radius1 = MathF.Max(0.25f, s.W)
                                });
                            }
                        }
                    }

                    IFileLoader effectiveLoader = (IFileLoader)fileLoader ?? new EmptyFileLoader();
                    var extractor = new ModelExtract(vrfResource, effectiveLoader)
                    {
                        Type = ModelExtract.ModelExtractType.Default,
                        ReconstructSoftbody = reconstructSoftbody
                    };
                    extractor.AnimationsToExtract.Clear();
                    using var contentFile = extractor.ToContentFile();
                    if (contentFile.Data != null && contentFile.Data.Length > 0)
                    {
                        decompiledVmdl = Encoding.UTF8.GetString(contentFile.Data);
                    }
                    else
                    {
                        decompiledVmdl = extractor.ToValveModel();
                    }

                    if (contentFile.SubFiles != null)
                    {
                        foreach (var sf in contentFile.SubFiles)
                        {
                            if (sf?.Extract == null || string.IsNullOrEmpty(sf.FileName)) continue;
                            string fn = sf.FileName;
                            if (fn.Contains("_lod", StringComparison.OrdinalIgnoreCase))
                                continue;
                            if (!reconstructSoftbody && fn.Contains("cloth_proxy", StringComparison.OrdinalIgnoreCase))
                                continue;
                            try
                            {
                                subfiles[fn] = sf.Extract();
                            }
                            catch { /* Ignore optional subfile extract error */ }
                        }
                    }

                    // Resolve any PreservedVanillaSubmeshes whose PreserveVanillaVmatPath lacks a VPK directory
                    if (basePackage != null && bundle?.PreservedVanillaSubmeshes != null)
                    {
                        foreach (var keptSub in bundle.PreservedVanillaSubmeshes)
                        {
                            if (keptSub == null) continue;
                            string cur = (keptSub.PreserveVanillaVmatPath ?? "").Replace('\\', '/').TrimStart('/');
                            if (cur.Contains('/')) continue;

                            string stem = !string.IsNullOrEmpty(cur)
                                ? Path.GetFileNameWithoutExtension(cur)
                                : SanitizeMaterialName(keptSub.MaterialName);
                            if (string.IsNullOrEmpty(stem)) continue;

                            string[] candidates =
                            {
                                $"{modelFolderRel.TrimEnd('/')}/materials/{stem}.vmat_c",
                                $"materials/{modelFolderRel.TrimEnd('/')}/{stem}.vmat_c",
                                $"materials/models/heroes_wip/{bundle.HeroCodename}/{stem}.vmat_c",
                                $"models/heroes_wip/{bundle.HeroCodename}/materials/{stem}.vmat_c",
                                $"models/heroes_staging/{bundle.HeroCodename}/materials/{stem}.vmat_c"
                            };

                            foreach (string cand in candidates)
                            {
                                string normCand = cand.Replace('\\', '/').TrimStart('/');
                                if (basePackage.FindEntry(normCand) != null)
                                {
                                    keptSub.PreserveVanillaVmatPath = normCand[..^2]; // strip "_c" -> ".vmat"
                                    break;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[CustomModelExporter] Warning during ModelExtract decompilation: {ex.Message}");
            }
            finally
            {
                basePackage?.Dispose();
            }

            return (decompiledVmdl, subfiles, vrfBones);
        }

        /// <summary>
        /// Parses the exact bone hierarchy, local origins (in Source 2 inches), and local QAngles (degrees)
        /// from the decompiled .vmdl's _class = "Skeleton" block.
        /// </summary>
        public static List<VmdlSkeletonBone> ParseSkeletonBonesFromVmdlText(string vmdlText)
        {
            var bones = new List<VmdlSkeletonBone>();
            if (string.IsNullOrEmpty(vmdlText)) return bones;

            string skelBlock = null;
            if (vmdlText.IndexOf("rootNode", StringComparison.Ordinal) >= 0)
            {
                var topBlocks = ParseRootNodeChildrenBlocks(vmdlText);
                skelBlock = topBlocks.FirstOrDefault(b => b.ClassName == "Skeleton").BlockText;
            }
            else if (string.Equals(ExtractKvStringProperty(vmdlText, "_class"), "Skeleton", StringComparison.Ordinal))
            {
                skelBlock = vmdlText;
            }
            if (string.IsNullOrEmpty(skelBlock)) return bones;

            ParseBoneBlocksRecursive(skelBlock, -1, bones);
            return bones;
        }

        private static void ParseBoneBlocksRecursive(string containerText, int parentBoneIdx, List<VmdlSkeletonBone> bones)
        {
            int childrenIdx = IndexOfChildrenArrayOutsideQuotes(containerText);
            if (childrenIdx < 0) return;

            int openBracket = containerText.IndexOf('[', childrenIdx);
            if (openBracket < 0) return;
            int closeBracket = FindMatchingBracket(containerText, openBracket, '[', ']');
            if (closeBracket < 0) return;

            string innerArray = containerText.Substring(openBracket + 1, closeBracket - openBracket - 1);
            var childObjects = SplitTopLevelKvObjects(innerArray);

            foreach (var objText in childObjects)
            {
                string cls = ExtractKvStringProperty(objText, "_class");
                if (cls != "Bone") continue;

                // Extract header before nested children = [
                int nestedChildrenPos = IndexOfChildrenArrayOutsideQuotes(objText);
                string headerSlice = nestedChildrenPos > 0 ? objText.Substring(0, nestedChildrenPos) : objText;

                string name = ExtractKvStringProperty(headerSlice, "name");
                Vector3 origin = ExtractKvVec3Property(headerSlice, "origin");
                Vector3 angles = ExtractKvVec3Property(headerSlice, "angles");

                int myIdx = bones.Count;
                bones.Add(new VmdlSkeletonBone
                {
                    Index = myIdx,
                    Name = name ?? $"bone_{myIdx}",
                    ParentIndex = parentBoneIdx,
                    Origin = origin,
                    AnglesDeg = angles
                });

                if (nestedChildrenPos > 0)
                {
                    ParseBoneBlocksRecursive(objText.Substring(nestedChildrenPos - 8), myIdx, bones);
                }
            }
        }

        private static int IndexOfChildrenArrayOutsideQuotes(string text)
        {
            var match = Regex.Match(text, @"\bchildren\s*=\s*\[");
            return match.Success ? match.Index : -1;
        }

        private static string ExtractKvStringProperty(string text, string key)
        {
            var m = Regex.Match(text, $@"\b{Regex.Escape(key)}\s*=\s*""([^""]*)""");
            return m.Success ? m.Groups[1].Value : null;
        }

        private static Vector3 ExtractKvVec3Property(string text, string key)
        {
            var m = Regex.Match(text, $@"\b{Regex.Escape(key)}\s*=\s*\[\s*([-\d\.eE\+]+)\s*,\s*([-\d\.eE\+]+)\s*,\s*([-\d\.eE\+]+)\s*\]");
            if (!m.Success) return Vector3.Zero;
            float.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float x);
            float.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float y);
            float.TryParse(m.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float z);
            return new Vector3(x, y, z);
        }

        private static List<(string ClassName, string BlockText)> ParseRootNodeChildrenBlocks(string vmdlText)
        {
            var result = new List<(string ClassName, string BlockText)>();
            if (string.IsNullOrEmpty(vmdlText)) return result;

            int rootIdx = vmdlText.IndexOf("rootNode", StringComparison.Ordinal);
            if (rootIdx < 0) return result;
            int childrenIdx = vmdlText.IndexOf("children", rootIdx, StringComparison.Ordinal);
            if (childrenIdx < 0) return result;
            int openBracket = vmdlText.IndexOf('[', childrenIdx);
            if (openBracket < 0) return result;
            int closeBracket = FindMatchingBracket(vmdlText, openBracket, '[', ']');
            if (closeBracket < 0) return result;

            string inner = vmdlText.Substring(openBracket + 1, closeBracket - openBracket - 1);
            var objects = SplitTopLevelKvObjects(inner);
            foreach (var obj in objects)
            {
                string cls = ExtractKvStringProperty(obj, "_class") ?? "";
                result.Add((cls, obj));
            }
            return result;
        }

        private static List<string> SplitTopLevelKvObjects(string arrayInnerText)
        {
            var list = new List<string>();
            int depth = 0;
            bool inQuotes = false;
            int objStart = -1;

            for (int i = 0; i < arrayInnerText.Length; i++)
            {
                char c = arrayInnerText[i];
                if (c == '"' && (i == 0 || arrayInnerText[i - 1] != '\\'))
                {
                    inQuotes = !inQuotes;
                    continue;
                }
                if (inQuotes) continue;

                if (c == '{')
                {
                    if (depth == 0) objStart = i;
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0 && objStart >= 0)
                    {
                        list.Add(arrayInnerText.Substring(objStart, i - objStart + 1));
                        objStart = -1;
                    }
                }
            }
            return list;
        }

        private static int FindMatchingBracket(string text, int openPos, char openChar, char closeChar)
        {
            int depth = 0;
            bool inQuotes = false;
            for (int i = openPos; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '"' && (i == 0 || text[i - 1] != '\\'))
                {
                    inQuotes = !inQuotes;
                    continue;
                }
                if (inQuotes) continue;

                if (c == openChar) depth++;
                else if (c == closeChar)
                {
                    depth--;
                    if (depth == 0) return i;
                }
            }
            return -1;
        }

        private static string DetectPrimaryBodyGroupMeshName(string decompiledVmdlText, string fallbackHeroName)
        {
            if (!string.IsNullOrEmpty(decompiledVmdlText))
            {
                var blocks = ParseRootNodeChildrenBlocks(decompiledVmdlText);
                string bgBlock = blocks.FirstOrDefault(b => b.ClassName == "BodyGroupList").BlockText;
                if (!string.IsNullOrEmpty(bgBlock))
                {
                    var m = Regex.Match(bgBlock, @"meshes\s*=\s*\[\s*""([^""]+)""");
                    if (m.Success && !string.IsNullOrWhiteSpace(m.Groups[1].Value))
                    {
                        return m.Groups[1].Value;
                    }
                }
            }
            return "body";
        }

        private static bool IsWeaponReplacementSubmesh(CustomModelExportBundle.SubmeshData sm)
        {
            if (sm == null) return false;
            string rep = sm.OriginalHeroReplacementName?.ToLowerInvariant() ?? "";
            string name = sm.SubmeshName?.ToLowerInvariant() ?? "";
            return rep.Contains("gun") || rep.Contains("weapon") ||
                   name.Contains("gun") || name.Contains("weapon") ||
                   name.Contains("pistol") || name.Contains("rifle") ||
                   name.Contains("sword") || name.Contains("blade") ||
                   name.Contains("bow") || name.Contains("katana");
        }

        private static HashSet<string> ResolveAllowedStandaloneDmxLeafs(
            string decompiledVmdlText,
            CustomModelExportBundle bundle,
            bool customReplacesWeapon)
        {
            var leafSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(decompiledVmdlText)) return leafSet;

            var blocks = ParseRootNodeChildrenBlocks(decompiledVmdlText);
            string origRenderMeshBlock = blocks.FirstOrDefault(b => b.ClassName == "RenderMeshList").BlockText;
            if (string.IsNullOrEmpty(origRenderMeshBlock)) return leafSet;

            int chIdx = IndexOfChildrenArrayOutsideQuotes(origRenderMeshBlock);
            int ob = chIdx >= 0 ? origRenderMeshBlock.IndexOf('[', chIdx) : -1;
            int cb = ob >= 0 ? FindMatchingBracket(origRenderMeshBlock, ob, '[', ']') : -1;
            if (cb <= ob) return leafSet;

            foreach (var rmObj in SplitTopLevelKvObjects(origRenderMeshBlock.Substring(ob + 1, cb - ob - 1)))
            {
                string rmName = ExtractKvStringProperty(rmObj, "name");
                string rmFile = ExtractKvStringProperty(rmObj, "filename");
                if (string.IsNullOrEmpty(rmName) || string.IsNullOrEmpty(rmFile)) continue;
                string leaf = Path.GetFileName(rmFile);
                string stem = Path.GetFileNameWithoutExtension(leaf).TrimStart('_');
                string cleanRm = rmName.TrimStart('_');
                if (rmName.Contains("_lod", StringComparison.OrdinalIgnoreCase) ||
                    leaf.Contains("_lod", StringComparison.OrdinalIgnoreCase))
                    continue;

                bool keep = bundle.HasExplicitKeptVanillaFilter
                    ? (bundle.AllowedKeptVanillaDmxNames.Contains(rmName) ||
                       bundle.AllowedKeptVanillaDmxNames.Contains(cleanRm) ||
                       bundle.AllowedKeptVanillaDmxNames.Contains(stem) ||
                       bundle.AllowedKeptVanillaDmxNames.Contains(Path.GetFileNameWithoutExtension(leaf)))
                    : (!customReplacesWeapon &&
                       (rmName.Contains("gun", StringComparison.OrdinalIgnoreCase) ||
                        rmName.Contains("weapon", StringComparison.OrdinalIgnoreCase)));

                if (keep)
                    leafSet.Add(leaf);
            }
            return leafSet;
        }

        /// <summary>
        /// Generates the patched ModelDoc .vmdl file by preserving 100% of the vanilla hero's
        /// AttachmentList, NmSkeletonList, AnimGraph2List, AnimConstraintList, WeightListList,
        /// GameDataList, HitboxSetList, Skeleton, and Physics blocks while replacing RenderMeshList,
        /// BodyGroupList, LODGroupList, and MaterialGroupList for our custom model.
        /// </summary>
        public static string GeneratePatchedVmdl(
            string decompiledVmdlText,
            CustomModelExportBundle bundle,
            string modelFolderRel,
            string materialsFolderRel,
            List<(string MeshName, string SmdRelativePath)> customRenderMeshes,
            List<string> customMaterialNames,
            HashSet<string> availableSubfileNames,
            bool includeAnimGraph2Nodes = true)
        {
            string hero = string.IsNullOrEmpty(bundle.HeroCodename) ? "hero" : bundle.HeroCodename.ToLowerInvariant();
            var blocks = ParseRootNodeChildrenBlocks(decompiledVmdlText);
            bool customReplacesWeapon = bundle.Submeshes.Any(sm => IsWeaponReplacementSubmesh(sm));

            // Detect preserved weapon/prop RenderMeshFile entries from the vanilla RenderMeshList whose .dmx we extracted
            var preservedWeaponEntries = new List<(string Name, string Filename)>();
            string origRenderMeshBlock = blocks.FirstOrDefault(b => b.ClassName == "RenderMeshList").BlockText;
            if (!string.IsNullOrEmpty(origRenderMeshBlock) && availableSubfileNames != null)
            {
                int chIdx = IndexOfChildrenArrayOutsideQuotes(origRenderMeshBlock);
                if (chIdx >= 0)
                {
                    int ob = origRenderMeshBlock.IndexOf('[', chIdx);
                    int cb = ob >= 0 ? FindMatchingBracket(origRenderMeshBlock, ob, '[', ']') : -1;
                    if (cb > ob)
                    {
                        foreach (var rmObj in SplitTopLevelKvObjects(origRenderMeshBlock.Substring(ob + 1, cb - ob - 1)))
                        {
                            string rmName = ExtractKvStringProperty(rmObj, "name");
                            string rmFile = ExtractKvStringProperty(rmObj, "filename");
                            if (!string.IsNullOrEmpty(rmName) && !string.IsNullOrEmpty(rmFile))
                            {
                                string leaf = Path.GetFileName(rmFile);
                                string stem = Path.GetFileNameWithoutExtension(leaf).TrimStart('_');
                                string cleanRm = rmName.TrimStart('_');
                                if (!availableSubfileNames.Contains(leaf) ||
                                    rmName.Contains("_lod", StringComparison.OrdinalIgnoreCase) ||
                                    leaf.Contains("_lod", StringComparison.OrdinalIgnoreCase))
                                    continue;

                                bool keep = bundle.HasExplicitKeptVanillaFilter
                                    ? (bundle.AllowedKeptVanillaDmxNames.Contains(rmName) ||
                                       bundle.AllowedKeptVanillaDmxNames.Contains(cleanRm) ||
                                       bundle.AllowedKeptVanillaDmxNames.Contains(stem) ||
                                       bundle.AllowedKeptVanillaDmxNames.Contains(Path.GetFileNameWithoutExtension(leaf)))
                                    : (!customReplacesWeapon &&
                                       (rmName.Contains("gun", StringComparison.OrdinalIgnoreCase) ||
                                        rmName.Contains("weapon", StringComparison.OrdinalIgnoreCase)));

                                if (keep)
                                {
                                    preservedWeaponEntries.Add((rmName, rmFile));
                                }
                            }
                        }
                    }
                }
            }

            var validMeshNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in customRenderMeshes) validMeshNames.Add(m.MeshName);
            foreach (var w in preservedWeaponEntries) validMeshNames.Add(w.Name);

            // Build RenderMeshList block
            var rmSb = new StringBuilder();
            rmSb.AppendLine("\t\t\t{");
            rmSb.AppendLine("\t\t\t\t_class = \"RenderMeshList\"");
            rmSb.AppendLine("\t\t\t\tchildren = ");
            rmSb.AppendLine("\t\t\t\t[");
            foreach (var (meshName, smdRelPath) in customRenderMeshes)
            {
                rmSb.AppendLine("\t\t\t\t\t{");
                rmSb.AppendLine("\t\t\t\t\t\t_class = \"RenderMeshFile\"");
                rmSb.AppendLine($"\t\t\t\t\t\tname = \"{meshName}\"");
                rmSb.AppendLine($"\t\t\t\t\t\tfilename = \"{smdRelPath}\"");
                rmSb.AppendLine("\t\t\t\t\t\timport_scale = 1.0");
                rmSb.AppendLine("\t\t\t\t\t\timport_filter = ");
                rmSb.AppendLine("\t\t\t\t\t\t{");
                rmSb.AppendLine("\t\t\t\t\t\t\texclude_by_default = false");
                rmSb.AppendLine("\t\t\t\t\t\t\texception_list = [  ]");
                rmSb.AppendLine("\t\t\t\t\t\t}");
                rmSb.AppendLine("\t\t\t\t\t},");
            }
            foreach (var (wName, wFile) in preservedWeaponEntries)
            {
                rmSb.AppendLine("\t\t\t\t\t{");
                rmSb.AppendLine("\t\t\t\t\t\t_class = \"RenderMeshFile\"");
                rmSb.AppendLine($"\t\t\t\t\t\tname = \"{wName}\"");
                rmSb.AppendLine($"\t\t\t\t\t\tfilename = \"{wFile}\"");
                rmSb.AppendLine("\t\t\t\t\t\timport_scale = 1.0");
                rmSb.AppendLine("\t\t\t\t\t\timport_filter = ");
                rmSb.AppendLine("\t\t\t\t\t\t{");
                rmSb.AppendLine("\t\t\t\t\t\t\texclude_by_default = false");
                rmSb.AppendLine("\t\t\t\t\t\t\texception_list = [  ]");
                rmSb.AppendLine("\t\t\t\t\t\t}");
                rmSb.AppendLine("\t\t\t\t\t},");
            }
            rmSb.Append("\t\t\t\t]\r\n\t\t\t}");

            // Build MaterialGroupList block mapping custom materials + kept vanilla prop material paths
            var mgSb = new StringBuilder();
            mgSb.AppendLine("\t\t\t{");
            mgSb.AppendLine("\t\t\t\t_class = \"MaterialGroupList\"");
            mgSb.AppendLine("\t\t\t\tchildren = ");
            mgSb.AppendLine("\t\t\t\t[");
            mgSb.AppendLine("\t\t\t\t\t{");
            mgSb.AppendLine("\t\t\t\t\t\t_class = \"DefaultMaterialGroup\"");
            mgSb.AppendLine("\t\t\t\t\t\tremaps = ");
            mgSb.AppendLine("\t\t\t\t\t\t[");
            var emittedFromMats = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var matName in customMaterialNames.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                emittedFromMats.Add(matName);
                mgSb.AppendLine("\t\t\t\t\t\t\t{");
                mgSb.AppendLine($"\t\t\t\t\t\t\t\tfrom = \"{matName}.vmat\"");
                mgSb.AppendLine($"\t\t\t\t\t\t\t\tto = \"{materialsFolderRel}/{matName}.vmat\"");
                mgSb.AppendLine("\t\t\t\t\t\t\t},");
            }
            if (bundle.PreservedVanillaSubmeshes != null)
            {
                foreach (var keptSub in bundle.PreservedVanillaSubmeshes)
                {
                    if (string.IsNullOrEmpty(keptSub?.PreserveVanillaVmatPath)) continue;
                    string fromMat = SanitizeMaterialName(keptSub.MaterialName);
                    if (!emittedFromMats.Add(fromMat)) continue;
                    string toVmat = keptSub.PreserveVanillaVmatPath.Replace('\\', '/').TrimStart('/');
                    if (toVmat.EndsWith(".vmat_c", StringComparison.OrdinalIgnoreCase))
                        toVmat = toVmat[..^2];
                    if (!toVmat.EndsWith(".vmat", StringComparison.OrdinalIgnoreCase))
                        toVmat += ".vmat";
                    mgSb.AppendLine("\t\t\t\t\t\t\t{");
                    mgSb.AppendLine($"\t\t\t\t\t\t\t\tfrom = \"{fromMat}.vmat\"");
                    mgSb.AppendLine($"\t\t\t\t\t\t\t\tto = \"{toVmat}\"");
                    mgSb.AppendLine("\t\t\t\t\t\t\t},");
                }
            }
            mgSb.AppendLine("\t\t\t\t\t\t]");
            mgSb.AppendLine("\t\t\t\t\t\tuse_global_default = false");
            mgSb.AppendLine("\t\t\t\t\t\tglobal_default_material = \"\"");
            mgSb.AppendLine("\t\t\t\t\t},");
            mgSb.Append("\t\t\t\t]\r\n\t\t\t}");

            // Build LODGroupList block referencing our active non-LOD mesh names
            var lodSb = new StringBuilder();
            lodSb.AppendLine("\t\t\t{");
            lodSb.AppendLine("\t\t\t\t_class = \"LODGroupList\"");
            lodSb.AppendLine("\t\t\t\tchildren = ");
            lodSb.AppendLine("\t\t\t\t[");
            lodSb.AppendLine("\t\t\t\t\t{");
            lodSb.AppendLine("\t\t\t\t\t\t_class = \"LODGroup\"");
            lodSb.AppendLine("\t\t\t\t\t\tswitch_threshold = 0.0");
            lodSb.AppendLine("\t\t\t\t\t\tmesh_references = ");
            lodSb.AppendLine("\t\t\t\t\t\t[");
            foreach (var validMesh in validMeshNames)
            {
                lodSb.AppendLine("\t\t\t\t\t\t\t{");
                lodSb.AppendLine($"\t\t\t\t\t\t\t\tmesh_name = \"{validMesh}\"");
                lodSb.AppendLine("\t\t\t\t\t\t\t},");
            }
            lodSb.AppendLine("\t\t\t\t\t\t]");
            lodSb.AppendLine("\t\t\t\t\t},");
            lodSb.Append("\t\t\t\t]\r\n\t\t\t}");

            // If we have decompiled blocks from ModelExtract, patch them in place
            if (blocks.Count > 0)
            {
                var outSb = new StringBuilder();
                outSb.AppendLine("<!-- kv3 encoding:text:version{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d} format:modeldoc39:version{1ceecb0d-4093-4729-ab18-b76886c64cde} -->");
                outSb.AppendLine("{");
                outSb.AppendLine("\trootNode = ");
                outSb.AppendLine("\t{");
                outSb.AppendLine("\t\t_class = \"RootNode\"");
                outSb.AppendLine("\t\tchildren = ");
                outSb.AppendLine("\t\t[");

                bool wroteMaterialGroup = false;
                bool wroteAnimationList = false;
                bool hasNmSkeletonList = blocks.Any(b => b.ClassName == "NmSkeletonList");
                bool hasAnimGraph2List = blocks.Any(b => b.ClassName == "AnimGraph2List");

                // Single lightweight 1-frame rest AnimFile keeps ModelBoneFlags.Animation intact in compiled .vmdl_c
                // without shipping external sequence files in the VPK.
                string vmdlNorm = string.IsNullOrEmpty(bundle.HeroModelVpkPath)
                    ? $"{hero}.vmdl"
                    : bundle.HeroModelVpkPath.Replace('\\', '/');
                if (vmdlNorm.EndsWith(".vmdl_c", StringComparison.OrdinalIgnoreCase)) vmdlNorm = vmdlNorm[..^2];
                string animModelBase = Path.GetFileNameWithoutExtension(vmdlNorm);
                if (string.IsNullOrEmpty(animModelBase)) animModelBase = hero;
                string animListBlock =
                    "\t\t\t{\r\n" +
                    "\t\t\t\t_class = \"AnimationList\"\r\n" +
                    "\t\t\t\tchildren = \r\n" +
                    "\t\t\t\t[\r\n" +
                    "\t\t\t\t\t{\r\n" +
                    "\t\t\t\t\t\t_class = \"AnimFile\"\r\n" +
                    "\t\t\t\t\t\tname = \"bind_idle_ref\"\r\n" +
                    $"\t\t\t\t\t\tsource_filename = \"{modelFolderRel}/{animModelBase}_anim_bind.smd\"\r\n" +
                    "\t\t\t\t\t\tactivity_name = \"\"\r\n" +
                    "\t\t\t\t\t\tactivity_weight = 1\r\n" +
                    "\t\t\t\t\t\tweight_list_name = \"\"\r\n" +
                    "\t\t\t\t\t\tfade_in_time = 0.0\r\n" +
                    "\t\t\t\t\t\tfade_out_time = 0.0\r\n" +
                    "\t\t\t\t\t\tlooping = false\r\n" +
                    "\t\t\t\t\t\tdelta = false\r\n" +
                    "\t\t\t\t\t\tworldSpace = false\r\n" +
                    "\t\t\t\t\t\thidden = false\r\n" +
                    "\t\t\t\t\t\tanim_markup_ordered = false\r\n" +
                    "\t\t\t\t\t\tdisable_compression = false\r\n" +
                    "\t\t\t\t\t\tanimgraph_additive = false\r\n" +
                    "\t\t\t\t\t\tstart_frame = -1\r\n" +
                    "\t\t\t\t\t\tend_frame = -1\r\n" +
                    "\t\t\t\t\t\tframerate = -1.0\r\n" +
                    "\t\t\t\t\t\treverse = false\r\n" +
                    "\t\t\t\t\t},\r\n" +
                    "\t\t\t\t]\r\n" +
                    "\t\t\t}";

                bool hasActivePhysicsChains = bundle.PreservedChains != null &&
                    bundle.PreservedChains.Any(c => c != null && c.Policy == ChainPolicy.PreservePhysicsChain);
                bool hasProxyDesignatedChains = hasActivePhysicsChains &&
                    bundle.PreservedChains.Any(c => c != null && c.Policy == ChainPolicy.PreservePhysicsChain && c.Simulation == SimulationKind.ProxyCloth);
                bool hasClothChainDesignated = hasActivePhysicsChains &&
                    bundle.PreservedChains.Any(c => c != null && c.Policy == ChainPolicy.PreservePhysicsChain && c.Simulation == SimulationKind.Cloth);
                bool hasJiggleDesignatedChains = hasActivePhysicsChains &&
                    bundle.PreservedChains.Any(c => c != null && c.Policy == ChainPolicy.PreservePhysicsChain && c.Simulation == SimulationKind.Jiggle);
                string expectedClothProxyLeaf = $"{animModelBase}_cloth_proxy.dmx";
                string customClothProxyRelPath = $"{modelFolderRel}/{expectedClothProxyLeaf}";
                bool hasCustomClothProxyFile = hasActivePhysicsChains && availableSubfileNames != null &&
                    availableSubfileNames.Any(f => Path.GetFileName(f).Equals(expectedClothProxyLeaf, StringComparison.OrdinalIgnoreCase));
                // In hybrid mode (ProxyCloth chains in _cloth_proxy.dmx + Cloth chains in ClothChain), emit both without duplicating bones;
                // Jiggle chains run strictly via JiggleBoneList (CollisionMask=0) and are never duplicated into ClothChain.
                bool emitClothChainsInSoftbody = hasClothChainDesignated && (!hasCustomClothProxyFile || hasProxyDesignatedChains);
                bool wroteClothProxyMeshList = false;
                bool wroteSoftbody = false;
                bool wroteJiggleList = false;

                var boneParentByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var srcWorldByName = new Dictionary<string, (Vector3 Pos, System.Numerics.Quaternion Rot)>(StringComparer.OrdinalIgnoreCase);
                if (bundle.BoneHierarchy != null)
                {
                    foreach (var kv in bundle.BoneHierarchy)
                    {
                        if (!string.IsNullOrEmpty(kv.Key))
                            boneParentByName[kv.Key] = kv.Value;
                    }
                }

                // Pre-extract patched Skeleton hierarchy + world transforms so Softbody ClothChain +
                // mesh-fitted ClothShapeCapsule blocks resolve exact bone frames regardless of block order.
                foreach (var (cls, blockText) in blocks)
                {
                    if (cls != "Skeleton") continue;
                    string skelSource = bundle.PreservedCustomBoneNames.Count > 0
                        ? PatchSkeletonBlockWithCustomBones(blockText, bundle)
                        : blockText;
                    var parsedBones = ParseSkeletonBonesFromVmdlText(skelSource);
                    var srcPosArr = new Vector3[parsedBones.Count];
                    var srcRotArr = new System.Numerics.Quaternion[parsedBones.Count];
                    for (int bi = 0; bi < parsedBones.Count; bi++)
                    {
                        var pb = parsedBones[bi];
                        string pName = (pb.ParentIndex >= 0 && pb.ParentIndex < parsedBones.Count)
                            ? parsedBones[pb.ParentIndex].Name
                            : null;
                        boneParentByName[pb.Name] = pName;

                        var qLocal = SourceQAngleDegreesToQuaternion(pb.AnglesDeg);
                        if (pb.ParentIndex >= 0 && pb.ParentIndex < bi)
                        {
                            var pPos = srcPosArr[pb.ParentIndex];
                            var pRot = srcRotArr[pb.ParentIndex];
                            srcRotArr[bi] = System.Numerics.Quaternion.Normalize(pRot * qLocal);
                            srcPosArr[bi] = pPos + RotateVecBySysQuat(pRot, pb.Origin);
                        }
                        else
                        {
                            srcRotArr[bi] = System.Numerics.Quaternion.Normalize(qLocal);
                            srcPosArr[bi] = pb.Origin;
                        }
                        srcWorldByName[pb.Name] = (srcPosArr[bi], srcRotArr[bi]);
                    }
                    break;
                }

                foreach (var (cls, blockText) in blocks)
                {
                    if (cls == "AnimationList")
                    {
                        outSb.AppendLine(animListBlock + ",");
                        wroteAnimationList = true;
                        continue;
                    }

                    if (!includeAnimGraph2Nodes && (cls == "NmSkeletonList" || cls == "AnimGraph2List"))
                        continue;

                    if (cls == "RenderMeshList")
                    {
                        outSb.AppendLine(rmSb.ToString() + ",");
                        outSb.AppendLine(mgSb.ToString() + ",");
                        wroteMaterialGroup = true;
                        continue;
                    }

                    if (cls == "MaterialGroupList")
                    {
                        if (!wroteMaterialGroup)
                        {
                            outSb.AppendLine(mgSb.ToString() + ",");
                            wroteMaterialGroup = true;
                        }
                        continue;
                    }

                    if (cls == "BodyGroupList")
                    {
                        string patchedBg = PatchBodyGroupListBlock(blockText, validMeshNames);
                        outSb.AppendLine("\t\t\t" + patchedBg.TrimStart() + ",");
                        continue;
                    }

                    if (cls == "LODGroupList")
                    {
                        outSb.AppendLine(lodSb.ToString() + ",");
                        continue;
                    }

                    if (cls == "Skeleton" && bundle.PreservedCustomBoneNames.Count > 0)
                    {
                        string patchedSkel = PatchSkeletonBlockWithCustomBones(blockText, bundle);
                        outSb.AppendLine("\t\t\t" + patchedSkel.TrimStart() + ",");
                        continue;
                    }

                    if (cls == "ClothProxyMeshList" && hasCustomClothProxyFile)
                    {
                        string patchedProxyList = PatchClothProxyMeshListBlock(blockText, customClothProxyRelPath, keepVanillaEntries: customRenderMeshes.Count > 1 || preservedWeaponEntries.Count > 0);
                        outSb.AppendLine("\t\t\t" + patchedProxyList.TrimStart() + ",");
                        wroteClothProxyMeshList = true;
                        continue;
                    }

                    if (cls == "JiggleBoneList" && hasActivePhysicsChains)
                    {
                        if (hasJiggleDesignatedChains && !wroteJiggleList)
                        {
                            string jiggleBlock = BuildJigglePhysicsVmdlBlock(bundle.PreservedChains, srcWorldByName, boneParentByName);
                            if (!string.IsNullOrEmpty(jiggleBlock))
                                outSb.AppendLine(jiggleBlock + ",");
                            wroteJiggleList = true;
                        }
                        continue;
                    }

                    if (cls == "Softbody")
                    {
                        if (hasCustomClothProxyFile || hasActivePhysicsChains)
                        {
                            outSb.AppendLine(BuildSoftbodyVmdlBlock(bundle, boneParentByName, includeClothChains: emitClothChainsInSoftbody, srcWorldByName: srcWorldByName, skipProxyDesignated: hasCustomClothProxyFile) + ",");
                            wroteSoftbody = true;
                            continue;
                        }
                        wroteSoftbody = true;
                    }

                    outSb.AppendLine("\t\t\t" + blockText.TrimStart() + ",");
                }

                if (hasCustomClothProxyFile)
                {
                    if (!wroteClothProxyMeshList)
                    {
                        outSb.AppendLine(BuildClothProxyMeshListVmdlBlock(customClothProxyRelPath) + ",");
                    }
                    if (!wroteSoftbody)
                    {
                        outSb.AppendLine(BuildSoftbodyVmdlBlock(bundle, boneParentByName, includeClothChains: emitClothChainsInSoftbody, srcWorldByName: srcWorldByName, skipProxyDesignated: true) + ",");
                        wroteSoftbody = true;
                    }
                }
                else if (hasActivePhysicsChains && !wroteSoftbody)
                {
                    outSb.AppendLine(BuildSoftbodyVmdlBlock(bundle, boneParentByName, includeClothChains: emitClothChainsInSoftbody, srcWorldByName: srcWorldByName, skipProxyDesignated: false) + ",");
                    wroteSoftbody = true;
                }

                // Emit JiggleBoneList exclusively for SimulationKind.Jiggle chains (never overlapping ClothChain / ProxyCloth).
                if (hasJiggleDesignatedChains && !wroteJiggleList)
                {
                    string jiggleBlock = BuildJigglePhysicsVmdlBlock(bundle.PreservedChains, srcWorldByName, boneParentByName);
                    if (!string.IsNullOrEmpty(jiggleBlock))
                    {
                        outSb.AppendLine(jiggleBlock + ",");
                    }
                }

                if (!wroteMaterialGroup)
                {
                    outSb.AppendLine(mgSb.ToString() + ",");
                }

                if (!wroteAnimationList)
                {
                    outSb.AppendLine(animListBlock + ",");
                }

                if (includeAnimGraph2Nodes && !hasNmSkeletonList)
                {
                    outSb.AppendLine($"\t\t\t{{\r\n\t\t\t\t_class = \"NmSkeletonList\"\r\n\t\t\t\tchildren = \r\n\t\t\t\t[\r\n\t\t\t\t\t{{\r\n\t\t\t\t\t\t_class = \"NmSkeletonReference\"\r\n\t\t\t\t\t\tfilename = \"{modelFolderRel}/{hero}.vnmskel\"\r\n\t\t\t\t\t}},\r\n\t\t\t\t]\r\n\t\t\t}},");
                }

                if (includeAnimGraph2Nodes && !hasAnimGraph2List)
                {
                    outSb.AppendLine($"\t\t\t{{\r\n\t\t\t\t_class = \"AnimGraph2List\"\r\n\t\t\t\tchildren = \r\n\t\t\t\t[\r\n\t\t\t\t\t{{\r\n\t\t\t\t\t\t_class = \"DefaultAnimGraph2\"\r\n\t\t\t\t\t\tfilename = \"animgraphs/animgraph2/hero/hero.vnmgraph+{hero}.vnmgraph\"\r\n\t\t\t\t\t}},\r\n\t\t\t\t\t{{\r\n\t\t\t\t\t\t_class = \"AnimGraph2\"\r\n\t\t\t\t\t\tname = \"ui\"\r\n\t\t\t\t\t\tfilename = \"animgraphs/animgraph2/hero/hero_ui.vnmgraph+{hero}.vnmgraph\"\r\n\t\t\t\t\t}},\r\n\t\t\t\t]\r\n\t\t\t}},");
                }

                outSb.AppendLine("\t\t]");
                outSb.AppendLine("\t\tmodel_archetype = \"\"");
                outSb.AppendLine("\t\tprimary_associated_entity = \"\"");
                outSb.AppendLine("\t\tanim_graph_name = \"\"");
                outSb.AppendLine("\t}");
                outSb.AppendLine("}");
                return outSb.ToString();
            }

            // Fallback if vanilla .vmdl_c was unavailable in isolated unit test
            return GenerateFallbackVmdl(hero, modelFolderRel, materialsFolderRel, rmSb.ToString(), mgSb.ToString(), lodSb.ToString(), includeAnimGraph2Nodes, bundle.PreservedChains);
        }

        /// <summary>
        /// Filters BodyGroupChoice.meshes arrays inside BodyGroupList so only meshes present in
        /// RenderMeshList remain, while keeping all BodyGroup and BodyGroupChoice names intact!
        /// </summary>
        private static string PatchBodyGroupListBlock(string bodyGroupBlock, HashSet<string> validMeshNames)
        {
            return Regex.Replace(
                bodyGroupBlock,
                @"meshes\s*=\s*\[([^\]]*)\]",
                match =>
                {
                    string inner = match.Groups[1].Value;
                    var kept = new List<string>();
                    foreach (Match qm in Regex.Matches(inner, @"""([^""]+)"""))
                    {
                        string mName = qm.Groups[1].Value;
                        if (validMeshNames.Contains(mName))
                        {
                            kept.Add($"\"{mName}\"");
                        }
                    }
                    if (kept.Count == 0)
                    {
                        return "meshes = [  ]";
                    }
                    return $"meshes = [ {string.Join(", ", kept)} ]";
                });
        }

        private static string GenerateFallbackVmdl(
            string hero,
            string modelFolderRel,
            string materialsFolderRel,
            string renderMeshBlock,
            string materialGroupBlock,
            string lodGroupBlock,
            bool includeAnimGraph2Nodes,
            List<BoneChainDef> preservedChains = null)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<!-- kv3 encoding:text:version{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d} format:modeldoc39:version{1ceecb0d-4093-4729-ab18-b76886c64cde} -->");
            sb.AppendLine("{");
            sb.AppendLine("\trootNode = ");
            sb.AppendLine("\t{");
            sb.AppendLine("\t\t_class = \"RootNode\"");
            sb.AppendLine("\t\tchildren = ");
            sb.AppendLine("\t\t[");
            sb.AppendLine(renderMeshBlock + ",");
            sb.AppendLine(materialGroupBlock + ",");
            sb.AppendLine(lodGroupBlock + ",");
            if (preservedChains != null && preservedChains.Any(c => c != null && c.Policy == ChainPolicy.PreservePhysicsChain))
            {
                string jiggleBlock = BuildJigglePhysicsVmdlBlock(preservedChains);
                if (!string.IsNullOrEmpty(jiggleBlock))
                {
                    sb.AppendLine(jiggleBlock + ",");
                }
                if (preservedChains.Any(c => c != null && c.Policy == ChainPolicy.PreservePhysicsChain && c.Simulation != SimulationKind.Jiggle))
                {
                    sb.AppendLine(BuildSoftbodyClothParamsVmdlBlock() + ",");
                }
            }
            if (includeAnimGraph2Nodes)
            {
                sb.AppendLine($"\t\t\t{{\r\n\t\t\t\t_class = \"NmSkeletonList\"\r\n\t\t\t\tchildren = \r\n\t\t\t\t[\r\n\t\t\t\t\t{{\r\n\t\t\t\t\t\t_class = \"NmSkeletonReference\"\r\n\t\t\t\t\t\tfilename = \"{modelFolderRel}/{hero}.vnmskel\"\r\n\t\t\t\t\t}},\r\n\t\t\t\t]\r\n\t\t\t}},");
                sb.AppendLine($"\t\t\t{{\r\n\t\t\t\t_class = \"AnimGraph2List\"\r\n\t\t\t\tchildren = \r\n\t\t\t\t[\r\n\t\t\t\t\t{{\r\n\t\t\t\t\t\t_class = \"DefaultAnimGraph2\"\r\n\t\t\t\t\t\tfilename = \"animgraphs/animgraph2/hero/hero.vnmgraph+{hero}.vnmgraph\"\r\n\t\t\t\t\t}},\r\n\t\t\t\t\t{{\r\n\t\t\t\t\t\t_class = \"AnimGraph2\"\r\n\t\t\t\t\t\tname = \"ui\"\r\n\t\t\t\t\t\tfilename = \"animgraphs/animgraph2/hero/hero_ui.vnmgraph+{hero}.vnmgraph\"\r\n\t\t\t\t\t}},\r\n\t\t\t\t]\r\n\t\t\t}},");
            }
            sb.AppendLine("\t\t]");
            sb.AppendLine("\t}");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private static readonly System.Numerics.Quaternion SourceToGltfRootQuat = new(-0.5f, -0.5f, -0.5f, 0.5f);
        private static readonly System.Numerics.Quaternion GltfToSourceRootQuat = new(0.5f, 0.5f, 0.5f, 0.5f);

        private static (Vector3 OriginIn, System.Numerics.Quaternion ParentRot, Vector3 AnglesDeg) GodotLocalBoneToSource(
            Transform3D localT, bool isRootBone)
        {
            var qGd = localT.Basis.GetRotationQuaternion();
            var qLocalSys = new System.Numerics.Quaternion(qGd.X, qGd.Y, qGd.Z, qGd.W);
            Vector3 originIn;
            System.Numerics.Quaternion qSys;
            if (isRootBone)
            {
                originIn = GodotToSourcePosition(localT.Origin);
                qSys = System.Numerics.Quaternion.Normalize(GltfToSourceRootQuat * qLocalSys);
            }
            else
            {
                // Non-root children in Godot already live inside the parent bone's Source 2 coordinate frame.
                originIn = localT.Origin * MetersToInches;
                qSys = System.Numerics.Quaternion.Normalize(qLocalSys);
            }
            Vector3 anglesDeg = QuaternionToSourceQAngleDegrees(qSys);
            return (originIn, qSys, anglesDeg);
        }

        private static void MergePreservedCustomBonesIntoSkeleton(
            CustomModelExportBundle bundle,
            List<VmdlSkeletonBone> targetSkeletonBones)
        {
            if (bundle == null || targetSkeletonBones == null || bundle.PreservedCustomBoneNames.Count == 0)
                return;

            var nameToIdx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < targetSkeletonBones.Count; i++)
                nameToIdx[targetSkeletonBones[i].Name] = i;

            var pending = new List<string>();
            foreach (string bName in bundle.BoneNames)
            {
                if (bundle.PreservedCustomBoneNames.Contains(bName) && !nameToIdx.ContainsKey(bName))
                    pending.Add(bName);
            }

            // Insert topologically so any parent custom bone precedes its custom children.
            while (pending.Count > 0)
            {
                bool addedAny = false;
                for (int pi = 0; pi < pending.Count; pi++)
                {
                    string bName = pending[pi];
                    string parentName = bundle.BoneHierarchy.TryGetValue(bName, out string pn) ? (pn ?? "") : "";
                    if (!string.IsNullOrEmpty(parentName) && !nameToIdx.ContainsKey(parentName) &&
                        pending.Any(x => x.Equals(parentName, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    int parentIdx = -1;
                    if (!string.IsNullOrEmpty(parentName) && !nameToIdx.TryGetValue(parentName, out parentIdx))
                        parentIdx = -1;

                    Transform3D localT = bundle.BoneLocalRestTransforms.TryGetValue(bName, out var lt) ? lt : Transform3D.Identity;
                    var (originIn, qSys, anglesDeg) = GodotLocalBoneToSource(localT, isRootBone: parentIdx < 0);

                    int newIdx = targetSkeletonBones.Count;
                    nameToIdx[bName] = newIdx;
                    targetSkeletonBones.Add(new VmdlSkeletonBone
                    {
                        Index = newIdx,
                        Name = bName,
                        ParentIndex = parentIdx,
                        Origin = originIn,
                        AnglesDeg = anglesDeg,
                        ParentRot = qSys,
                        HasParentRot = true
                    });
                    pending.RemoveAt(pi);
                    pi--;
                    addedAny = true;
                }
                if (!addedAny)
                {
                    // Cycle/missing parent safety break: emit remaining attached to pelvis/root
                    int fallbackRoot = nameToIdx.TryGetValue("pelvis", out int pv) ? pv : (targetSkeletonBones.Count > 0 ? 0 : -1);
                    foreach (string bName in pending)
                    {
                        Transform3D localT = bundle.BoneLocalRestTransforms.TryGetValue(bName, out var lt) ? lt : Transform3D.Identity;
                        var (originIn, qSys, anglesDeg) = GodotLocalBoneToSource(localT, isRootBone: fallbackRoot < 0);
                        int newIdx = targetSkeletonBones.Count;
                        nameToIdx[bName] = newIdx;
                        targetSkeletonBones.Add(new VmdlSkeletonBone
                        {
                            Index = newIdx,
                            Name = bName,
                            ParentIndex = fallbackRoot,
                            Origin = originIn,
                            AnglesDeg = anglesDeg,
                            ParentRot = qSys,
                            HasParentRot = true
                        });
                    }
                    break;
                }
            }
        }

        /// <summary>
        /// When a cloth/hair chain's leaf bone stops well above the bottom of its weighted mesh
        /// (e.g. haircenter_003 at Z=60 in while the hair mesh hangs down to Z=16 in), Source 2's
        /// FeModel.NodeBases only back-solves existing skeleton joints, making the hanging tail rotate
        /// as a rigid blade. This synthesizes 1-3 real child extension bones into the exported skeleton
        /// and smoothly re-weights those lower vertices along the extension chain.
        /// </summary>
        private static void ExtendShortLeafClothBonesInBundle(
            CustomModelExportBundle bundle,
            List<VmdlSkeletonBone> targetSkeletonBones)
        {
            if (bundle?.PreservedChains == null || bundle.Submeshes == null || bundle.Submeshes.Count == 0 ||
                bundle.BoneNames == null || targetSkeletonBones == null || targetSkeletonBones.Count == 0)
                return;

            var nameToSmdIdx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < targetSkeletonBones.Count; i++)
                nameToSmdIdx[targetSkeletonBones[i].Name] = i;

            // Compute Source 2 world position & rotation for each skeleton bone (inches)
            var worldPos = new List<System.Numerics.Vector3>(targetSkeletonBones.Count + 24);
            var worldRot = new List<System.Numerics.Quaternion>(targetSkeletonBones.Count + 24);
            var computed = new List<bool>(targetSkeletonBones.Count + 24);
            for (int i = 0; i < targetSkeletonBones.Count; i++)
            {
                worldPos.Add(System.Numerics.Vector3.Zero);
                worldRot.Add(System.Numerics.Quaternion.Identity);
                computed.Add(false);
            }

            void EnsureWorld(int bi)
            {
                if (bi < 0 || bi >= targetSkeletonBones.Count || computed[bi]) return;
                computed[bi] = true;
                var b = targetSkeletonBones[bi];
                var qLocal = b.HasParentRot ? System.Numerics.Quaternion.Normalize(b.ParentRot) : SourceQAngleDegreesToQuaternion(b.AnglesDeg);
                var pLocal = new System.Numerics.Vector3(b.Origin.X, b.Origin.Y, b.Origin.Z);
                if (b.ParentIndex >= 0 && b.ParentIndex < targetSkeletonBones.Count)
                {
                    EnsureWorld(b.ParentIndex);
                    worldRot[bi] = System.Numerics.Quaternion.Normalize(worldRot[b.ParentIndex] * qLocal);
                    worldPos[bi] = worldPos[b.ParentIndex] + System.Numerics.Vector3.Transform(pLocal, worldRot[b.ParentIndex]);
                }
                else
                {
                    worldRot[bi] = qLocal;
                    worldPos[bi] = pLocal;
                }
            }
            for (int i = 0; i < targetSkeletonBones.Count; i++) EnsureWorld(i);

            int totalAdded = 0;
            foreach (var ch in bundle.PreservedChains)
            {
                if (ch == null || ch.Policy != ChainPolicy.PreservePhysicsChain || ch.Simulation == SimulationKind.Jiggle)
                    continue;
                var chainBones = (ch.AppendedHeroBoneNames != null && ch.AppendedHeroBoneNames.Count > 0)
                    ? ch.AppendedHeroBoneNames
                    : ch.SourceBoneNames;
                if (chainBones == null || chainBones.Count == 0) continue;

                string leafName = chainBones[^1];
                if (!nameToSmdIdx.TryGetValue(leafName, out int leafSmdIdx)) continue;
                int leafBundleIdx = bundle.BoneNames.FindIndex(b => b.Equals(leafName, StringComparison.OrdinalIgnoreCase));
                if (leafBundleIdx < 0) continue;

                var leafPos = worldPos[leafSmdIdx];
                var leafRot = worldRot[leafSmdIdx];

                System.Numerics.Vector3 tailDir;
                if (chainBones.Count >= 2 && nameToSmdIdx.TryGetValue(chainBones[^2], out int prevSmdIdx))
                {
                    tailDir = leafPos - worldPos[prevSmdIdx];
                }
                else if (targetSkeletonBones[leafSmdIdx].ParentIndex >= 0)
                {
                    tailDir = leafPos - worldPos[targetSkeletonBones[leafSmdIdx].ParentIndex];
                }
                else
                {
                    tailDir = new System.Numerics.Vector3(0, 0, -1);
                }
                if (tailDir.Z > -0.2f * tailDir.Length())
                    tailDir = new System.Numerics.Vector3(tailDir.X * 0.25f, tailDir.Y * 0.25f, -1.0f);
                float tdLen = tailDir.Length();
                tailDir = tdLen > 0.01f ? tailDir / tdLen : new System.Numerics.Vector3(0, 0, -1);
                // Blend strongly toward vertical gravity drape (0, 0, -1) so shoulder-clearing side strands
                // (e.g. hairside_l/r) hang straight down instead of flaring out to X = +-25 in!
                tailDir = System.Numerics.Vector3.Normalize(
                    new System.Numerics.Vector3(tailDir.X * 0.15f, tailDir.Y * 0.15f, -1.0f));

                float maxReach = 0f;
                foreach (var sm in bundle.Submeshes)
                {
                    if (sm?.Vertices == null || sm.BoneIndices == null || sm.BoneWeights == null) continue;
                    int vc = sm.Vertices.Length;
                    for (int vi = 0; vi < vc; vi++)
                    {
                        float lw = 0f;
                        for (int k = 0; k < 4; k++)
                            if (sm.BoneIndices[vi * 4 + k] == leafBundleIdx) lw += sm.BoneWeights[vi * 4 + k];
                        if (lw < 0.25f) continue;
                        Vector3 vSrc = GodotToSourcePosition(sm.Vertices[vi]);
                        float proj = System.Numerics.Vector3.Dot(new System.Numerics.Vector3(vSrc.X, vSrc.Y, vSrc.Z) - leafPos, tailDir);
                        if (proj > maxReach) maxReach = proj;
                    }
                }

                // Only extend if mesh hangs > 8 in beyond the leaf joint
                if (maxReach <= 8.0f) continue;

                float totalExt = MathF.Min(maxReach * 0.85f, 36.0f);
                int extCount = Math.Clamp((int)MathF.Floor(totalExt / 11.0f), 1, 3);
                float stepIn = totalExt / extCount;

                var extBundleIndices = new List<int>(extCount + 1) { leafBundleIdx };
                int prevParentSmdIdx = leafSmdIdx;
                string prevParentName = leafName;
                var qInvLeaf = System.Numerics.Quaternion.Inverse(leafRot);
                var stepLocalSrc = System.Numerics.Vector3.Transform(tailDir * stepIn, qInvLeaf);
                Vector3 stepLocalOriginIn = new Vector3(stepLocalSrc.X, stepLocalSrc.Y, stepLocalSrc.Z);
                Vector3 stepLocalGodotM = stepLocalOriginIn / MetersToInches;

                if (ch.AppendedHeroBoneNames == null || ch.AppendedHeroBoneNames.Count == 0)
                    ch.AppendedHeroBoneNames = new List<string>(chainBones);

                for (int e = 1; e <= extCount; e++)
                {
                    string extName = $"{leafName}_ext{e}";
                    if (nameToSmdIdx.ContainsKey(extName))
                    {
                        int existingBundleIdx = bundle.BoneNames.FindIndex(b => b.Equals(extName, StringComparison.OrdinalIgnoreCase));
                        if (existingBundleIdx >= 0) extBundleIndices.Add(existingBundleIdx);
                        continue;
                    }

                    int newBundleIdx = bundle.BoneNames.Count;
                    bundle.BoneNames.Add(extName);
                    bundle.BoneHierarchy[extName] = prevParentName;
                    bundle.BoneLocalRestTransforms[extName] = new Transform3D(Basis.Identity, stepLocalGodotM);
                    bundle.PreservedCustomBoneNames.Add(extName);
                    ch.AppendedHeroBoneNames.Add(extName);

                    int newSmdIdx = targetSkeletonBones.Count;
                    nameToSmdIdx[extName] = newSmdIdx;
                    targetSkeletonBones.Add(new VmdlSkeletonBone
                    {
                        Index = newSmdIdx,
                        Name = extName,
                        ParentIndex = prevParentSmdIdx,
                        Origin = stepLocalOriginIn,
                        AnglesDeg = Vector3.Zero,
                        ParentRot = System.Numerics.Quaternion.Identity,
                        HasParentRot = true
                    });
                    worldPos.Add(leafPos + tailDir * (stepIn * e));
                    worldRot.Add(leafRot);
                    computed.Add(true);

                    extBundleIndices.Add(newBundleIdx);
                    prevParentSmdIdx = newSmdIdx;
                    prevParentName = extName;
                    totalAdded++;
                }

                if (extBundleIndices.Count <= 1) continue;

                // Smoothly redistribute leafBundleIdx weight across [leaf, ext1, ..., extN] by projection along tailDir
                foreach (var sm in bundle.Submeshes)
                {
                    if (sm?.Vertices == null || sm.BoneIndices == null || sm.BoneWeights == null) continue;
                    int vc = sm.Vertices.Length;
                    for (int vi = 0; vi < vc; vi++)
                    {
                        int baseOff = vi * 4;
                        float lw = 0f;
                        int leafSlot = -1;
                        for (int k = 0; k < 4; k++)
                        {
                            if (sm.BoneIndices[baseOff + k] == leafBundleIdx)
                            {
                                lw += sm.BoneWeights[baseOff + k];
                                if (leafSlot < 0) leafSlot = k;
                            }
                        }
                        if (lw < 0.20f || leafSlot < 0) continue;

                        Vector3 vSrc = GodotToSourcePosition(sm.Vertices[vi]);
                        float proj = System.Numerics.Vector3.Dot(new System.Numerics.Vector3(vSrc.X, vSrc.Y, vSrc.Z) - leafPos, tailDir);
                        if (proj <= 0.5f) continue;

                        float fSeg = Math.Clamp(proj / stepIn, 0f, extBundleIndices.Count - 1);
                        int segA = (int)MathF.Floor(fSeg);
                        int segB = Math.Min(segA + 1, extBundleIndices.Count - 1);
                        float frac = fSeg - segA;

                        int bA = extBundleIndices[segA];
                        int bB = extBundleIndices[segB];
                        if (bA == leafBundleIdx && frac < 0.05f) continue;

                        // Collect non-leaf influences and insert (bA, lw*(1-frac)) + (bB, lw*frac)
                        var merged = new Dictionary<int, float>();
                        for (int k = 0; k < 4; k++)
                        {
                            int bi = sm.BoneIndices[baseOff + k];
                            float bw = sm.BoneWeights[baseOff + k];
                            if (bw <= 1e-5f || bi == leafBundleIdx) continue;
                            merged[bi] = merged.GetValueOrDefault(bi) + bw;
                        }
                        if (bA == bB)
                        {
                            merged[bA] = merged.GetValueOrDefault(bA) + lw;
                        }
                        else
                        {
                            float wA = lw * (1f - frac);
                            float wB = lw * frac;
                            if (wA > 1e-4f) merged[bA] = merged.GetValueOrDefault(bA) + wA;
                            if (wB > 1e-4f) merged[bB] = merged.GetValueOrDefault(bB) + wB;
                        }

                        var top = merged.OrderByDescending(kv => kv.Value).Take(4).ToList();
                        float sum = top.Sum(kv => kv.Value);
                        for (int k = 0; k < 4; k++)
                        {
                            if (k < top.Count && sum > 1e-6f)
                            {
                                sm.BoneIndices[baseOff + k] = top[k].Key;
                                sm.BoneWeights[baseOff + k] = top[k].Value / sum;
                            }
                            else
                            {
                                sm.BoneIndices[baseOff + k] = 0;
                                sm.BoneWeights[baseOff + k] = 0f;
                            }
                        }
                    }
                }
            }

            if (totalAdded > 0)
            {
                GD.Print($"[CustomModelExporter] Synthesized {totalAdded} leaf-tail extension bone(s) for long cloth/hair NodeBases articulation.");
            }
        }

        private static string PatchSkeletonBlockWithCustomBones(string skeletonBlock, CustomModelExportBundle bundle)
        {
            if (string.IsNullOrEmpty(skeletonBlock) || bundle == null || bundle.PreservedCustomBoneNames.Count == 0)
                return skeletonBlock;

            var ic = CultureInfo.InvariantCulture;
            var toEmit = new List<string>();
            foreach (string bName in bundle.BoneNames)
            {
                if (!bundle.PreservedCustomBoneNames.Contains(bName)) continue;
                if (Regex.IsMatch(skeletonBlock, $@"\bname\s*=\s*""{Regex.Escape(bName)}""", RegexOptions.IgnoreCase)) continue;
                toEmit.Add(bName);
            }
            if (toEmit.Count == 0) return skeletonBlock;

            var toEmitSet = new HashSet<string>(toEmit, StringComparer.OrdinalIgnoreCase);
            var childrenByParent = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var topCustomRoots = new List<(string BoneName, string VanillaParentName)>();

            foreach (string bName in toEmit)
            {
                string parentName = bundle.BoneHierarchy.TryGetValue(bName, out var p) ? (p ?? "") : "";
                if (!string.IsNullOrEmpty(parentName) && toEmitSet.Contains(parentName))
                {
                    if (!childrenByParent.TryGetValue(parentName, out var list))
                    {
                        list = new List<string>();
                        childrenByParent[parentName] = list;
                    }
                    list.Add(bName);
                }
                else
                {
                    topCustomRoots.Add((bName, parentName));
                }
            }

            string FormatBoneSubtree(string bName, string indent, bool isRootLevel)
            {
                Transform3D localT = bundle.BoneLocalRestTransforms.TryGetValue(bName, out var lt) ? lt : Transform3D.Identity;
                var (originIn, _, ang) = GodotLocalBoneToSource(localT, isRootBone: isRootLevel);

                var sb = new StringBuilder();
                sb.AppendLine($"{indent}{{");
                sb.AppendLine($"{indent}\t_class = \"Bone\"");
                sb.AppendLine($"{indent}\tname = \"{bName}\"");
                sb.AppendLine(string.Format(ic, "{0}\torigin = [ {1:F4}, {2:F4}, {3:F4} ]", indent, originIn.X, originIn.Y, originIn.Z));
                sb.AppendLine(string.Format(ic, "{0}\tangles = [ {1:F4}, {2:F4}, {3:F4} ]", indent, ang.X, ang.Y, ang.Z));
                sb.AppendLine($"{indent}\tdo_not_discard = true");
                if (childrenByParent.TryGetValue(bName, out var subChildren) && subChildren.Count > 0)
                {
                    sb.AppendLine($"{indent}\tchildren = ");
                    sb.AppendLine($"{indent}\t[");
                    foreach (string ch in subChildren)
                    {
                        sb.Append(FormatBoneSubtree(ch, indent + "\t\t", isRootLevel: false));
                    }
                    sb.AppendLine($"{indent}\t]");
                }
                sb.AppendLine($"{indent}}},");
                return sb.ToString();
            }

            // Collect edits by character position in skeletonBlock, then apply from highest offset to lowest.
            var edits = new List<(int InsertPos, string Text)>();
            var fallbackRoots = new List<string>();

            foreach (var group in topCustomRoots.GroupBy(t => t.VanillaParentName ?? "", StringComparer.OrdinalIgnoreCase))
            {
                string parentName = group.Key;
                int boneOpen = -1;
                int boneClose = -1;
                if (!string.IsNullOrEmpty(parentName) && TryFindBoneObjectSpan(skeletonBlock, parentName, out boneOpen, out boneClose))
                {
                    // Check if this Bone object already has a direct `children = [ ... ]` block
                    int directChildrenClose = FindDirectChildrenArrayClose(skeletonBlock, boneOpen, boneClose);
                    string indent = InferLineIndent(skeletonBlock, boneOpen);
                    if (directChildrenClose >= 0)
                    {
                        var insSb = new StringBuilder();
                        foreach (var item in group)
                        {
                            insSb.Append(FormatBoneSubtree(item.BoneName, indent + "\t\t", isRootLevel: false));
                        }
                        edits.Add((directChildrenClose, "\r\n" + insSb.ToString() + indent + "\t"));
                    }
                    else
                    {
                        var insSb = new StringBuilder();
                        insSb.AppendLine($"\t{indent}children = ");
                        insSb.AppendLine($"\t{indent}[");
                        foreach (var item in group)
                        {
                            insSb.Append(FormatBoneSubtree(item.BoneName, indent + "\t\t", isRootLevel: false));
                        }
                        insSb.AppendLine($"\t{indent}]");
                        edits.Add((boneClose, insSb.ToString() + indent));
                    }
                }
                else
                {
                    foreach (var item in group)
                    {
                        fallbackRoots.Add(item.BoneName);
                    }
                }
            }

            if (fallbackRoots.Count > 0)
            {
                int lastClose = skeletonBlock.LastIndexOf(']');
                if (lastClose >= 0)
                {
                    var insSb = new StringBuilder();
                    foreach (string bName in fallbackRoots)
                    {
                        insSb.Append(FormatBoneSubtree(bName, "\t\t\t\t\t", isRootLevel: true));
                    }
                    edits.Add((lastClose, "\r\n" + insSb.ToString() + "\t\t\t\t"));
                }
            }

            if (edits.Count == 0) return skeletonBlock;
            string result = skeletonBlock;
            foreach (var edit in edits.OrderByDescending(e => e.InsertPos))
            {
                result = result.Insert(edit.InsertPos, edit.Text);
            }
            return result;
        }

        private static bool TryFindBoneObjectSpan(string skeletonBlock, string boneName, out int openBrace, out int closeBrace)
        {
            openBrace = -1;
            closeBrace = -1;
            var rx = new Regex($@"name\s*=\s*""{Regex.Escape(boneName)}""", RegexOptions.IgnoreCase);
            var m = rx.Match(skeletonBlock);
            if (!m.Success) return false;

            // Walk backward to the enclosing `{` that owns this `name = "..."`
            int depth = 0;
            bool inQuotes = false;
            for (int i = m.Index - 1; i >= 0; i--)
            {
                char c = skeletonBlock[i];
                if (c == '"' && (i == 0 || skeletonBlock[i - 1] != '\\')) { inQuotes = !inQuotes; continue; }
                if (inQuotes) continue;
                if (c == '}') depth++;
                else if (c == '{')
                {
                    if (depth == 0)
                    {
                        openBrace = i;
                        closeBrace = FindMatchingBracket(skeletonBlock, openBrace, '{', '}');
                        return closeBrace > openBrace;
                    }
                    depth--;
                }
            }
            return false;
        }

        private static int FindDirectChildrenArrayClose(string text, int objOpen, int objClose)
        {
            int depth = 0;
            bool inQuotes = false;
            for (int i = objOpen + 1; i < objClose; i++)
            {
                char c = text[i];
                if (c == '"' && (i == 0 || text[i - 1] != '\\')) { inQuotes = !inQuotes; continue; }
                if (inQuotes) continue;
                if (c == '{') { depth++; continue; }
                if (c == '}') { depth--; continue; }
                if (depth == 0 && c == 'c' && i + 8 < objClose &&
                    text.AsSpan(i).StartsWith("children", StringComparison.Ordinal))
                {
                    int j = i + 8;
                    while (j < objClose && char.IsWhiteSpace(text[j])) j++;
                    if (j < objClose && text[j] == '=')
                    {
                        int arrOpen = text.IndexOf('[', j + 1);
                        if (arrOpen > j && arrOpen < objClose)
                        {
                            return FindMatchingBracket(text, arrOpen, '[', ']');
                        }
                    }
                }
            }
            return -1;
        }

        private static string InferLineIndent(string text, int pos)
        {
            int lineStart = text.LastIndexOf('\n', Math.Max(0, pos - 1));
            lineStart = lineStart < 0 ? 0 : lineStart + 1;
            int end = lineStart;
            while (end < pos && (text[end] == '\t' || text[end] == ' ')) end++;
            return text.Substring(lineStart, end - lineStart);
        }

        private static string BuildJigglePhysicsVmdlBlock(
            List<BoneChainDef> chains,
            Dictionary<string, (Vector3 Pos, System.Numerics.Quaternion Rot)> srcWorldByName = null,
            Dictionary<string, string> boneParentByName = null)
        {
            if (chains == null || chains.Count == 0) return "";
            var ic = CultureInfo.InvariantCulture;
            var entries = new StringBuilder();
            int count = 0;

            foreach (var ch in chains)
            {
                if (ch == null || ch.Policy != ChainPolicy.PreservePhysicsChain || ch.Simulation != SimulationKind.Jiggle) continue;
                var bones = ch.AppendedHeroBoneNames.Count > 0 ? ch.AppendedHeroBoneNames : ch.SourceBoneNames;

                for (int i = 0; i < bones.Count; i++)
                {
                    string bName = bones[i];
                    string srcName = (ch.SourceBoneNames != null && i < ch.SourceBoneNames.Count) ? ch.SourceBoneNames[i] : bName;
                    var jov = ch.FindJointOverride(bName, srcName);
                    if (jov?.Simulate.HasValue == true && !jov.Simulate.Value)
                        continue;

                    float t = bones.Count > 1 ? i / (float)(bones.Count - 1) : 0f;
                    var jp = PhysicsParamResolver.ResolveJiggleNode(ch, t, bones.Count);
                    float stiff = jp.Stiffness;
                    float damp = jp.Damping;
                    float perJointAngle = jp.AngleLimitDeg;

                    float boneLen = 2.5f;
                    Vector3 point1Local = new Vector3(boneLen, 0f, 0f);
                    if (srcWorldByName != null && srcWorldByName.TryGetValue(bName, out var bXf))
                    {
                        Vector3 dirSrcW = Vector3.Zero;
                        if (i + 1 < bones.Count && srcWorldByName.TryGetValue(bones[i + 1], out var childXf))
                        {
                            dirSrcW = childXf.Pos - bXf.Pos;
                        }
                        else if (boneParentByName != null &&
                                 boneParentByName.TryGetValue(bName, out string pName) &&
                                 !string.IsNullOrEmpty(pName) &&
                                 srcWorldByName.TryGetValue(pName, out var parentXf))
                        {
                            dirSrcW = bXf.Pos - parentXf.Pos;
                        }

                        if (dirSrcW.LengthSquared() > 0.0025f)
                        {
                            float rawLen = dirSrcW.Length();
                            boneLen = Mathf.Clamp(rawLen, 1.5f, 8.0f);
                            var qInv = System.Numerics.Quaternion.Inverse(bXf.Rot);
                            Vector3 localDir = RotateVecBySysQuat(qInv, dirSrcW / rawLen);
                            point1Local = localDir * boneLen;
                        }
                    }

                    entries.AppendLine("\t\t\t\t\t{");
                    entries.AppendLine("\t\t\t\t\t\t_class = \"JiggleBone\"");
                    entries.AppendLine($"\t\t\t\t\t\tname = \"jiggle_{EscapeKvString(bName)}\"");
                    entries.AppendLine($"\t\t\t\t\t\tjiggle_root_bone = \"{EscapeKvString(bName)}\"");
                    entries.AppendLine("\t\t\t\t\t\tjiggle_type = 0");
                    entries.AppendLine(string.Format(ic, "\t\t\t\t\t\tlength = {0:F3}", boneLen));
                    entries.AppendLine("\t\t\t\t\t\ttip_mass = 1.000");
                    entries.AppendLine("\t\t\t\t\t\thas_yaw_constraint = true");
                    entries.AppendLine(string.Format(ic, "\t\t\t\t\t\tmin_yaw = -{0:F1}", perJointAngle));
                    entries.AppendLine(string.Format(ic, "\t\t\t\t\t\tmax_yaw = {0:F1}", perJointAngle));
                    entries.AppendLine("\t\t\t\t\t\thas_pitch_constraint = true");
                    entries.AppendLine(string.Format(ic, "\t\t\t\t\t\tmin_pitch = -{0:F1}", perJointAngle));
                    entries.AppendLine(string.Format(ic, "\t\t\t\t\t\tmax_pitch = {0:F1}", perJointAngle));
                    entries.AppendLine("\t\t\t\t\t\thas_angle_constraint = true");
                    entries.AppendLine(string.Format(ic, "\t\t\t\t\t\tangle_limit = {0:F1}", perJointAngle));
                    entries.AppendLine(string.Format(ic, "\t\t\t\t\t\tyaw_stiffness = {0:F2}", stiff));
                    entries.AppendLine(string.Format(ic, "\t\t\t\t\t\tpitch_stiffness = {0:F2}", stiff));
                    entries.AppendLine(string.Format(ic, "\t\t\t\t\t\tyaw_damping = {0:F2}", damp));
                    entries.AppendLine(string.Format(ic, "\t\t\t\t\t\tpitch_damping = {0:F2}", damp));
                    entries.AppendLine(string.Format(ic, "\t\t\t\t\t\talong_stiffness = {0:F2}", Mathf.Clamp(stiff * 0.85f, 35f, 800f)));
                    entries.AppendLine(string.Format(ic, "\t\t\t\t\t\talong_damping = {0:F2}", damp));
                    entries.AppendLine(string.Format(ic, "\t\t\t\t\t\tpoint1 = [ {0:F3}, {1:F3}, {2:F3} ]", point1Local.X, point1Local.Y, point1Local.Z));
                    entries.AppendLine("\t\t\t\t\t},");
                    count++;
                }
            }

            if (count == 0) return "";
            var sb = new StringBuilder();
            sb.AppendLine("\t\t\t{");
            sb.AppendLine("\t\t\t\t_class = \"JiggleBoneList\"");
            sb.AppendLine("\t\t\t\tchildren = ");
            sb.AppendLine("\t\t\t\t[");
            sb.Append(entries);
            sb.AppendLine("\t\t\t\t]");
            sb.Append("\t\t\t}");
            return sb.ToString();
        }

        private static string BuildClothProxyMeshFileEntry(string clothProxyRelPath, string entryName = "custom_cloth_proxy")
        {
            return
                "\t\t\t\t\t{\r\n" +
                "\t\t\t\t\t\t_class = \"ClothProxyMeshFile\"\r\n" +
                $"\t\t\t\t\t\tname = \"{entryName}\"\r\n" +
                $"\t\t\t\t\t\tfilename = \"{clothProxyRelPath}\"\r\n" +
                "\t\t\t\t\t\timport_scale = 1.0\r\n" +
                "\t\t\t\t\t\tback_solve_joints = true\r\n" +
                "\t\t\t\t\t\tback_solve_joints_drive_meshes = true\r\n" +
                "\t\t\t\t\t\tflex_cloth_borders = true\r\n" +
                "\t\t\t\t\t\tadd_bones_to_render_mesh = false\r\n" +
                "\t\t\t\t\t\tback_solve_influence_threshold = 0.05\r\n" +
                "\t\t\t\t\t\tcloth_friction_bias = 0.0\r\n" +
                "\t\t\t\t\t\tcloth_friction_scale = 1.0\r\n" +
                "\t\t\t\t\t\tcloth_goal_strength_bias = 0.0\r\n" +
                "\t\t\t\t\t\tcloth_goal_strength_scale = 1.0\r\n" +
                "\t\t\t\t\t\tcloth_gravity_scale = 1.0\r\n" +
                "\t\t\t\t\t\tcloth_drag_scale = 1.0\r\n" +
                "\t\t\t\t\t\tcloth_collision_radius_scale = 1.0\r\n" +
                "\t\t\t\t\t\tcloth_anchor_free_rotate_scale = 1.0\r\n" +
                "\t\t\t\t\t\tcloth_suspenders_scale = 1.0\r\n" +
                "\t\t\t\t\t\tcloth_bend_stiffness_scale = 1.0\r\n" +
                "\t\t\t\t\t},\r\n";
        }

        private static string BuildClothProxyMeshListVmdlBlock(string clothProxyRelPath)
        {
            var sb = new StringBuilder();
            sb.AppendLine("\t\t\t{");
            sb.AppendLine("\t\t\t\t_class = \"ClothProxyMeshList\"");
            sb.AppendLine("\t\t\t\tchildren = ");
            sb.AppendLine("\t\t\t\t[");
            sb.Append(BuildClothProxyMeshFileEntry(clothProxyRelPath));
            sb.AppendLine("\t\t\t\t]");
            sb.Append("\t\t\t}");
            return sb.ToString();
        }

        private static string PatchClothProxyMeshListBlock(string existingBlock, string clothProxyRelPath, bool keepVanillaEntries)
        {
            if (!keepVanillaEntries)
            {
                return BuildClothProxyMeshListVmdlBlock(clothProxyRelPath);
            }
            int chIdx = IndexOfChildrenArrayOutsideQuotes(existingBlock);
            if (chIdx < 0) return BuildClothProxyMeshListVmdlBlock(clothProxyRelPath);
            int openArr = existingBlock.IndexOf('[', chIdx);
            int closeArr = openArr >= 0 ? FindMatchingBracket(existingBlock, openArr, '[', ']') : -1;
            if (closeArr < 0) return BuildClothProxyMeshListVmdlBlock(clothProxyRelPath);
            return existingBlock.Insert(closeArr, "\r\n" + BuildClothProxyMeshFileEntry(clothProxyRelPath) + "\t\t\t\t");
        }

        private static string FmtKvFloat(float v) => v.ToString("0.0###", CultureInfo.InvariantCulture);

        private static string EscapeKvString(string s) =>
            (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");

        private static System.Numerics.Quaternion SourceQAngleDegreesToQuaternion(Vector3 anglesDeg)
        {
            const float degToRad = MathF.PI / 180.0f;
            float pitch = anglesDeg.X * degToRad; // around Y
            float yaw = anglesDeg.Y * degToRad;   // around Z
            float roll = anglesDeg.Z * degToRad;  // around X
            var qz = System.Numerics.Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitZ, yaw);
            var qy = System.Numerics.Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitY, pitch);
            var qx = System.Numerics.Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitX, roll);
            return System.Numerics.Quaternion.Normalize(qz * qy * qx);
        }

        private static Vector3 RotateVecBySysQuat(System.Numerics.Quaternion q, Vector3 v)
        {
            var r = System.Numerics.Vector3.Transform(new System.Numerics.Vector3(v.X, v.Y, v.Z), q);
            return new Vector3(r.X, r.Y, r.Z);
        }

        private static string BuildSoftbodyClothParamsVmdlBlock()
        {
            return BuildSoftbodyVmdlBlock(null, null, includeClothChains: false);
        }

        private static string BuildSoftbodyVmdlBlock(
            CustomModelExportBundle bundle,
            Dictionary<string, string> boneParentByName,
            bool includeClothChains,
            Dictionary<string, (Vector3 Pos, System.Numerics.Quaternion Rot)> srcWorldByName = null,
            bool skipProxyDesignated = false)
        {
            var cp = PhysicsParamResolver.ValveBaselineClothParams;
            var sb = new StringBuilder();
            sb.AppendLine("\t\t\t{");
            sb.AppendLine("\t\t\t\t_class = \"Softbody\"");
            sb.AppendLine("\t\t\t\tchildren = ");
            sb.AppendLine("\t\t\t\t[");
            sb.AppendLine("\t\t\t\t\t{");
            sb.AppendLine("\t\t\t\t\t\t_class = \"ClothParams\"");
            sb.AppendLine($"\t\t\t\t\t\textra_iterations = {cp.ExtraIterations}");
            sb.AppendLine($"\t\t\t\t\t\tdefault_gravity_scale = {FmtKvFloat(cp.DefaultGravityScale)}");
            sb.AppendLine($"\t\t\t\t\t\tdefault_vel_air_drag = {FmtKvFloat(cp.DefaultVelAirDrag)}");
            sb.AppendLine($"\t\t\t\t\t\tdefault_exp_air_drag = {FmtKvFloat(cp.DefaultExpAirDrag)}");
            sb.AppendLine($"\t\t\t\t\t\tvelocity_smooth_rate = {FmtKvFloat(cp.VelocitySmoothRate)}");
            sb.AppendLine($"\t\t\t\t\t\tvelocity_smooth_iterations = {cp.VelocitySmoothIterations}");
            sb.AppendLine($"\t\t\t\t\t\tquad_bend_tolerance = {FmtKvFloat(cp.QuadBendTolerance)}");
            sb.AppendLine($"\t\t\t\t\t\tlocal_force = {FmtKvFloat(cp.LocalForce)}");
            sb.AppendLine($"\t\t\t\t\t\tlocal_rotation = {FmtKvFloat(cp.LocalRotation)}");
            sb.AppendLine($"\t\t\t\t\t\tlocal_drag1 = {FmtKvFloat(cp.LocalDrag1)}");
            sb.AppendLine("\t\t\t\t\t\tadd_stiffness_rods = true");
            sb.AppendLine("\t\t\t\t\t\tnew_style = true");
            sb.AppendLine("\t\t\t\t\t\tunitless_damping = true");
            sb.AppendLine("\t\t\t\t\t},");

            bool anyPlanarizeClothChain = false;
            if (includeClothChains && bundle?.PreservedChains != null && boneParentByName != null && boneParentByName.Count > 0)
            {
                var chainBlocks = new List<string>();
                foreach (var ch in bundle.PreservedChains)
                {
                    if (ch == null || ch.Policy != ChainPolicy.PreservePhysicsChain) continue;
                    // Jiggle chains run strictly via FeJiggleBone (CollisionMask=0) to prevent body/hair collisions.
                    if (ch.Simulation == SimulationKind.Jiggle) continue;
                    if (skipProxyDesignated && ch.Simulation == SimulationKind.ProxyCloth) continue;
                    if (ch.PlanarizeBackBarrier) anyPlanarizeClothChain = true;
                    string cc = BuildClothChainVmdlBlock(ch, boneParentByName);
                    if (!string.IsNullOrEmpty(cc))
                        chainBlocks.Add(cc);
                }

                if (chainBlocks.Count > 0)
                {
                    sb.AppendLine("\t\t\t\t\t{");
                    sb.AppendLine("\t\t\t\t\t\t_class = \"Folder\"");
                    sb.AppendLine("\t\t\t\t\t\tname = \"cloth\"");
                    sb.AppendLine("\t\t\t\t\t\tchildren = ");
                    sb.AppendLine("\t\t\t\t\t\t[");
                    foreach (var cc in chainBlocks)
                    {
                        sb.AppendLine(cc + ",");
                    }
                    sb.AppendLine("\t\t\t\t\t\t]");
                    sb.AppendLine("\t\t\t\t\t},");
                }
            }

            // Emit real CSDK13 ClothVertexMap for planarized back-barrier CollisionPlanes (matching unicorn/yamato)
            bool hasPlanarizeBackMap = (bundle?.PlanarizeBackProxyNodes != null && bundle.PlanarizeBackProxyNodes.Count > 0)
                                       || anyPlanarizeClothChain;
            bool hasPlanarizeWrapMap = bundle?.PlanarizeWrapProxyNodes != null && bundle.PlanarizeWrapProxyNodes.Count > 0;
            if (bundle?.PlanarizeBackProxyNodes != null && bundle.PlanarizeBackProxyNodes.Count > 0)
            {
                sb.AppendLine("\t\t\t\t\t{");
                sb.AppendLine("\t\t\t\t\t\t_class = \"ClothVertexMap\"");
                sb.AppendLine("\t\t\t\t\t\tchildren = [  ]");
                sb.AppendLine("\t\t\t\t\t\tname = \"planarize_back\"");
                sb.AppendLine("\t\t\t\t\t\tdata = ");
                sb.AppendLine("\t\t\t\t\t\t{");
                sb.AppendLine("\t\t\t\t\t\t\tnodes = ");
                sb.AppendLine("\t\t\t\t\t\t\t{");
                foreach (string nodeName in bundle.PlanarizeBackProxyNodes)
                {
                    sb.AppendLine($"\t\t\t\t\t\t\t\t\"{EscapeKvString(nodeName)}\" = true");
                }
                sb.AppendLine("\t\t\t\t\t\t\t}");
                sb.AppendLine("\t\t\t\t\t\t}");
                sb.AppendLine("\t\t\t\t\t},");
            }
            if (hasPlanarizeWrapMap)
            {
                sb.AppendLine("\t\t\t\t\t{");
                sb.AppendLine("\t\t\t\t\t\t_class = \"ClothVertexMap\"");
                sb.AppendLine("\t\t\t\t\t\tchildren = [  ]");
                sb.AppendLine("\t\t\t\t\t\tname = \"planarize_wrap\"");
                sb.AppendLine("\t\t\t\t\t\tdata = ");
                sb.AppendLine("\t\t\t\t\t\t{");
                sb.AppendLine("\t\t\t\t\t\t\tnodes = ");
                sb.AppendLine("\t\t\t\t\t\t\t{");
                foreach (string nodeName in bundle.PlanarizeWrapProxyNodes)
                {
                    sb.AppendLine($"\t\t\t\t\t\t\t\t\"{EscapeKvString(nodeName)}\" = true");
                }
                sb.AppendLine("\t\t\t\t\t\t\t}");
                sb.AppendLine("\t\t\t\t\t\t}");
                sb.AppendLine("\t\t\t\t\t},");
            }

            if (boneParentByName != null && boneParentByName.Count > 0)
            {
                foreach (var capBlock in BuildClothShapeCapsulesVmdlBlocks(bundle, boneParentByName, srcWorldByName, hasPlanarizeBackMap, hasPlanarizeWrapMap))
                {
                    sb.AppendLine(capBlock + ",");
                }
            }

            sb.AppendLine("\t\t\t\t]");
            sb.Append("\t\t\t}");
            return sb.ToString();
        }

        private static string BuildClothChainVmdlBlock(BoneChainDef ch, Dictionary<string, string> boneParentByName)
        {
            var rawNames = (ch.AppendedHeroBoneNames != null && ch.AppendedHeroBoneNames.Count > 0)
                ? ch.AppendedHeroBoneNames
                : ch.SourceBoneNames;
            if (rawNames == null || rawNames.Count == 0) return "";

            var validBones = new List<string>();
            foreach (var bn in rawNames)
            {
                if (!string.IsNullOrWhiteSpace(bn) && boneParentByName.ContainsKey(bn) &&
                    !validBones.Contains(bn, StringComparer.OrdinalIgnoreCase))
                {
                    validBones.Add(bn);
                }
            }
            if (validBones.Count == 0) return "";

            string rootBone = validBones[0];
            string anchorBone = null;
            if (boneParentByName.TryGetValue(rootBone, out string directParent) && !string.IsNullOrEmpty(directParent))
                anchorBone = directParent;
            else if (!string.IsNullOrEmpty(ch.AnchorHeroBone) && boneParentByName.ContainsKey(ch.AnchorHeroBone))
                anchorBone = ch.AnchorHeroBone;
            if (string.IsNullOrEmpty(anchorBone) || !boneParentByName.ContainsKey(anchorBone))
                return "";

            float gravZ = Mathf.Clamp(ch.Gravity, 0.0f, 4.0f);
            float hingeAngle = Mathf.Clamp(ch.AngleLimitDeg > 1.0f ? ch.AngleLimitDeg : 58.0f, 10.0f, 120.0f);

            var sb = new StringBuilder();
            sb.AppendLine("\t\t\t\t\t\t\t{");
            sb.AppendLine("\t\t\t\t\t\t\t\t_class = \"ClothChain\"");
            sb.AppendLine($"\t\t\t\t\t\t\t\tname = \"{EscapeKvString(rootBone)}\"");
            sb.AppendLine($"\t\t\t\t\t\t\t\troot_bone = \"{EscapeKvString(rootBone)}\"");
            sb.AppendLine("\t\t\t\t\t\t\t\tchain = ");
            sb.AppendLine("\t\t\t\t\t\t\t\t{");
            sb.AppendLine("\t\t\t\t\t\t\t\t\tjoints = ");
            sb.AppendLine("\t\t\t\t\t\t\t\t\t[");

            // Anchor bone (pinned static frame)
            sb.AppendLine("\t\t\t\t\t\t\t\t\t\t{");
            sb.AppendLine($"\t\t\t\t\t\t\t\t\t\t\tjoint_name = \"{EscapeKvString(anchorBone)}\"");
            sb.AppendLine("\t\t\t\t\t\t\t\t\t\t\tsimulate = false");
            sb.AppendLine("\t\t\t\t\t\t\t\t\t\t\tallow_rotation = false");
            sb.AppendLine("\t\t\t\t\t\t\t\t\t\t\tgoal_strength = 0.99");
            sb.AppendLine("\t\t\t\t\t\t\t\t\t\t\tgoal_damping = 0.0");
            sb.AppendLine($"\t\t\t\t\t\t\t\t\t\t\tgravity_z = {FmtKvFloat(gravZ)}");
            sb.AppendLine("\t\t\t\t\t\t\t\t\t\t},");

            for (int k = 0; k < validBones.Count; k++)
            {
                string jName = validBones[k];
                string srcName = (ch.SourceBoneNames != null && k < ch.SourceBoneNames.Count) ? ch.SourceBoneNames[k] : jName;
                var jov = ch.FindJointOverride(jName, srcName);
                string jParent = (k == 0) ? anchorBone : validBones[k - 1];
                if (k > 0 && boneParentByName.TryGetValue(jName, out string actualP) &&
                    !string.IsNullOrEmpty(actualP) && boneParentByName.ContainsKey(actualP))
                {
                    jParent = actualP;
                }

                float t = validBones.Count > 1 ? k / (float)(validBones.Count - 1) : 0.0f;
                bool simJoint = jov?.Simulate ?? true;
                var rp = PhysicsParamResolver.ResolveClothNode(ch, t, !simJoint, jov);

                sb.AppendLine("\t\t\t\t\t\t\t\t\t\t{");
                sb.AppendLine($"\t\t\t\t\t\t\t\t\t\t\tjoint_name = \"{EscapeKvString(jName)}\"");
                sb.AppendLine($"\t\t\t\t\t\t\t\t\t\t\tjoint_parent = \"{EscapeKvString(jParent)}\"");
                sb.AppendLine($"\t\t\t\t\t\t\t\t\t\t\tsimulate = {(simJoint ? "true" : "false")}");
                sb.AppendLine($"\t\t\t\t\t\t\t\t\t\t\tgoal_strength = {FmtKvFloat(rp.GoalStrengthInput)}");
                sb.AppendLine($"\t\t\t\t\t\t\t\t\t\t\tgoal_damping = {FmtKvFloat(rp.GoalDamping)}");
                sb.AppendLine($"\t\t\t\t\t\t\t\t\t\t\tdrag = {FmtKvFloat(rp.Drag)}");
                sb.AppendLine($"\t\t\t\t\t\t\t\t\t\t\tfriction = {FmtKvFloat(rp.Friction)}");
                sb.AppendLine($"\t\t\t\t\t\t\t\t\t\t\tgravity_z = {FmtKvFloat(rp.GravityScale)}");
                sb.AppendLine($"\t\t\t\t\t\t\t\t\t\t\tcollision_radius = {FmtKvFloat(rp.CollisionRadiusInches)}");
                if (rp.StrayRadiusInches > 0.01f)
                {
                    sb.AppendLine($"\t\t\t\t\t\t\t\t\t\t\tstray_radius = {FmtKvFloat(rp.StrayRadiusInches)}");
                    sb.AppendLine("\t\t\t\t\t\t\t\t\t\t\tstray_radius_stretchiness = 0.25");
                }
                if (ch.PlanarizeBackBarrier && simJoint)
                {
                    sb.AppendLine("\t\t\t\t\t\t\t\t\t\t\tvertex_map = \"planarize_back\"");
                }
                sb.AppendLine("\t\t\t\t\t\t\t\t\t\t\tbend_spring = 1.0");
                if (k > 0) sb.AppendLine($"\t\t\t\t\t\t\t\t\t\t\ttorsion_spring = {FmtKvFloat(rp.Torsion)}");
                sb.AppendLine("\t\t\t\t\t\t\t\t\t\t\tstiff_hinge = 1.0");
                sb.AppendLine($"\t\t\t\t\t\t\t\t\t\t\tstiff_hinge_angle = {FmtKvFloat(hingeAngle)}");
                sb.AppendLine("\t\t\t\t\t\t\t\t\t\t\textrude_sides = 2");
                sb.AppendLine("\t\t\t\t\t\t\t\t\t\t\textrude_radius = 1.0");
                sb.AppendLine("\t\t\t\t\t\t\t\t\t\t},");
            }

            sb.AppendLine("\t\t\t\t\t\t\t\t\t]");
            sb.AppendLine("\t\t\t\t\t\t\t\t\tversion = 2");
            sb.AppendLine("\t\t\t\t\t\t\t\t}");
            sb.Append("\t\t\t\t\t\t\t}");
            return sb.ToString();
        }

        private static List<string> BuildClothShapeCapsulesVmdlBlocks(
            CustomModelExportBundle bundle,
            Dictionary<string, string> boneParentByName,
            Dictionary<string, (Vector3 Pos, System.Numerics.Quaternion Rot)> srcWorldByName = null,
            bool hasPlanarizeBackMap = false,
            bool hasPlanarizeWrapMap = false)
        {
            var result = new List<string>();
            if (boneParentByName == null || boneParentByName.Count == 0) return result;

            var capsules = new List<CustomModelExportBundle.ClothCollisionCapsuleDef>();
            var bonesCovered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1. Prefer mesh-fitted custom body collision capsules (converted from Godot Hero World meters
            //    to exact Source 2 bone-local inches using the patched skeleton's world transform).
            if (bundle?.CustomFittedWorldCapsules != null && srcWorldByName != null && srcWorldByName.Count > 0)
            {
                foreach (var wc in bundle.CustomFittedWorldCapsules)
                {
                    if (wc == null || string.IsNullOrWhiteSpace(wc.ParentBone)) continue;
                    if (!boneParentByName.ContainsKey(wc.ParentBone)) continue;
                    if (!srcWorldByName.TryGetValue(wc.ParentBone, out var bXf)) continue;

                    var qInv = System.Numerics.Quaternion.Inverse(bXf.Rot);
                    Vector3 p0SrcW = GodotToSourcePosition(wc.Point0);
                    Vector3 p1SrcW = GodotToSourcePosition(wc.Point1);
                    capsules.Add(new CustomModelExportBundle.ClothCollisionCapsuleDef
                    {
                        ParentBone = wc.ParentBone,
                        Point0 = RotateVecBySysQuat(qInv, p0SrcW - bXf.Pos),
                        Radius0 = MathF.Max(0.5f, wc.Radius0 * MetersToInches),
                        Point1 = RotateVecBySysQuat(qInv, p1SrcW - bXf.Pos),
                        Radius1 = MathF.Max(0.5f, wc.Radius1 * MetersToInches)
                    });
                    bonesCovered.Add(wc.ParentBone);
                }
            }

            // 2. Append non-overlapping vanilla capsules (e.g. leg/lower-arm capsules not in custom torso fit)
            if (bundle?.VanillaCollisionCapsules != null)
            {
                foreach (var c in bundle.VanillaCollisionCapsules)
                {
                    if (c == null || string.IsNullOrWhiteSpace(c.ParentBone)) continue;
                    if (!boneParentByName.ContainsKey(c.ParentBone)) continue;
                    if (bonesCovered.Contains(c.ParentBone)) continue;
                    capsules.Add(c);
                    bonesCovered.Add(c.ParentBone);
                }
            }

            // 3. Ensure core head & torso collision capsules exist even when no mesh/vanilla capsules were present
            void EnsureFallbackCapsule(string boneName, Vector3 p0, float r0, Vector3 p1, float r1)
            {
                if (!bonesCovered.Contains(boneName) && boneParentByName.ContainsKey(boneName))
                {
                    capsules.Add(new CustomModelExportBundle.ClothCollisionCapsuleDef
                    {
                        ParentBone = boneName,
                        Point0 = p0,
                        Radius0 = r0,
                        Point1 = p1,
                        Radius1 = r1
                    });
                    bonesCovered.Add(boneName);
                }
            }

            EnsureFallbackCapsule("head", new Vector3(-1.5f, 0f, 0f), 4.4f, new Vector3(4.5f, 0f, 0f), 4.4f);
            EnsureFallbackCapsule("neck_0", new Vector3(-0.5f, 0f, 0f), 3.4f, new Vector3(3.5f, 0f, 0f), 3.4f);
            EnsureFallbackCapsule("spine_3", new Vector3(0f, 0f, 0f), 6.8f, new Vector3(6.0f, 0f, 0f), 6.8f);
            EnsureFallbackCapsule("spine_2", new Vector3(0f, 0f, 0f), 6.4f, new Vector3(6.0f, 0f, 0f), 6.4f);
            EnsureFallbackCapsule("pelvis", new Vector3(0f, -3.5f, 0f), 6.5f, new Vector3(0f, 3.5f, 0f), 6.5f);

            // Collect all dynamic (non-root) simulated cloth bone positions & collision radii in Source 2
            // world space (inches) so we can run a universal, model-agnostic bind-pose clearance pass.
            var dynamicClothNodes = new List<(string BoneName, Vector3 PosSrcW, float ColRIn, string AnchorBone)>();
            if (bundle?.PreservedChains != null && srcWorldByName != null && srcWorldByName.Count > 0)
            {
                foreach (var ch in bundle.PreservedChains)
                {
                    if (ch == null || ch.Policy != ChainPolicy.PreservePhysicsChain ||
                        ch.Simulation == SimulationKind.Jiggle)
                        continue;
                    var names = (ch.AppendedHeroBoneNames != null && ch.AppendedHeroBoneNames.Count > 0)
                        ? ch.AppendedHeroBoneNames
                        : ch.SourceBoneNames;
                    if (names == null || names.Count < 2) continue;

                    float tipColIn = MathF.Max(0.4f, ch.CollisionRadiusTipInches);
                    int nBones = names.Count;
                    for (int j = 1; j < nBones; j++) // j == 0 is pinned root
                    {
                        string bn = names[j];
                        if (string.IsNullOrWhiteSpace(bn)) continue;
                        string srcBn = (ch.SourceBoneNames != null && j < ch.SourceBoneNames.Count) ? ch.SourceBoneNames[j] : null;
                        var jOv = ch.FindJointOverride(bn, srcBn);
                        if (jOv != null && jOv.Simulate.HasValue && !jOv.Simulate.Value) continue;
                        if (!srcWorldByName.TryGetValue(bn, out var nodeXf)) continue;

                        float frac = nBones > 1 ? (float)j / (nBones - 1) : 1f;
                        float colIn = (jOv != null && jOv.CollisionRadiusInches.HasValue)
                            ? MathF.Max(0.2f, jOv.CollisionRadiusInches.Value)
                            : Mathf.Lerp(tipColIn * 0.45f, tipColIn, frac);
                        dynamicClothNodes.Add((bn, nodeXf.Pos, colIn, ch.AnchorHeroBone ?? ""));
                    }
                }
            }

            // Helper: closest point distance + segment parameter t in [0,1] from point P to segment [A,B]
            static float DistPointToSegment(Vector3 p, Vector3 a, Vector3 b, out float t)
            {
                Vector3 ab = b - a;
                float lenSq = ab.LengthSquared();
                t = lenSq > 1e-6f ? Mathf.Clamp((p - a).Dot(ab) / lenSq, 0f, 1f) : 0.5f;
                return (p - (a + ab * t)).Length();
            }

            // Optional inner-thigh bridge capsule (closes the X=0 gap between leg_upper_L/R for long
            // back hair): add it ONLY if no dynamic cloth node spawns inside it at bind pose!
            if (boneParentByName.ContainsKey("pelvis") && srcWorldByName != null &&
                srcWorldByName.TryGetValue("pelvis", out var pelXf))
            {
                Vector3 bridgeW0 = pelXf.Pos + new Vector3(0f, 0f, -6.5f);
                Vector3 bridgeW1 = pelXf.Pos + new Vector3(0f, 0f, -15.5f);
                float br0 = 7.4f;
                float br1 = 6.6f;

                bool bridgeBlockedByCloth = false;
                foreach (var dn in dynamicClothNodes)
                {
                    float d = DistPointToSegment(dn.PosSrcW, bridgeW0, bridgeW1, out float t);
                    float rAtT = Mathf.Lerp(br0, br1, t);
                    if (d < rAtT + MathF.Min(dn.ColRIn * 0.5f, 0.8f))
                    {
                        bridgeBlockedByCloth = true;
                        break;
                    }
                }

                if (!bridgeBlockedByCloth)
                {
                    var qInvPel = System.Numerics.Quaternion.Inverse(pelXf.Rot);
                    capsules.Add(new CustomModelExportBundle.ClothCollisionCapsuleDef
                    {
                        ParentBone = "pelvis",
                        Point0 = RotateVecBySysQuat(qInvPel, bridgeW0 - pelXf.Pos),
                        Radius0 = br0,
                        Point1 = RotateVecBySysQuat(qInvPel, bridgeW1 - pelXf.Pos),
                        Radius1 = br1
                    });
                }
            }

            // Universal Bind-Pose Clearance Pass: for EVERY capsule against EVERY dynamic cloth node
            // (skirts, coats, capes, robes, hair, tails), if a dynamic cloth bone spawns inside a
            // capsule at bind pose, shrink that capsule's radius just enough to clear the cloth node
            // so Source 2 never blows the garment outward/upward on frame 1.
            if (dynamicClothNodes.Count > 0 && srcWorldByName != null && srcWorldByName.Count > 0)
            {
                foreach (var cap in capsules)
                {
                    if (!srcWorldByName.TryGetValue(cap.ParentBone, out var capXf)) continue;
                    Vector3 p0W = capXf.Pos + RotateVecBySysQuat(capXf.Rot, cap.Point0);
                    Vector3 p1W = capXf.Pos + RotateVecBySysQuat(capXf.Rot, cap.Point1);
                    float origR0 = cap.Radius0;
                    float origR1 = cap.Radius1;
                    // Never crush anatomical body capsules into pencil sticks (e.g. 2.17 in thighs):
                    // preserve at least 72% of fitted body silhouette + anatomical minimum radius.
                    bool isHeadOrNeck = cap.ParentBone.Equals("head", StringComparison.OrdinalIgnoreCase) ||
                                        cap.ParentBone.Equals("neck_0", StringComparison.OrdinalIgnoreCase);
                    bool isArm = cap.ParentBone.StartsWith("arm_", StringComparison.OrdinalIgnoreCase);
                    float anatMinIn = isArm ? 2.2f : (isHeadOrNeck ? 2.8f : 3.4f);
                    float floorR0 = MathF.Max(anatMinIn, origR0 * 0.72f);
                    float floorR1 = MathF.Max(anatMinIn, origR1 * 0.72f);
                    string blockerBone = null;

                    foreach (var dn in dynamicClothNodes)
                    {
                        float d = DistPointToSegment(dn.PosSrcW, p0W, p1W, out float t);
                        // Ignore a node sitting practically on top of its own anchor bone's centerline
                        // (e.g. a scalp tuft right on the head bone origin) so we don't collapse that capsule.
                        if (d < 2.2f && cap.ParentBone.Equals(dn.AnchorBone, StringComparison.OrdinalIgnoreCase))
                            continue;

                        float rAtT = Mathf.Lerp(cap.Radius0, cap.Radius1, t);
                        float margin = MathF.Min(dn.ColRIn * 0.45f, 0.60f);
                        if (d < rAtT + margin)
                        {
                            float rawSafe = d - margin;
                            if (t <= 0.75f)
                            {
                                float clamped0 = MathF.Max(floorR0, rawSafe);
                                if (clamped0 < cap.Radius0) { cap.Radius0 = clamped0; blockerBone = dn.BoneName; }
                            }
                            if (t >= 0.25f)
                            {
                                float clamped1 = MathF.Max(floorR1, rawSafe);
                                if (clamped1 < cap.Radius1) { cap.Radius1 = clamped1; blockerBone = dn.BoneName; }
                            }
                        }
                    }

                    if (blockerBone != null && (cap.Radius0 < origR0 - 0.1f || cap.Radius1 < origR1 - 0.1f))
                    {
                        GD.Print($"[ClothExport] Bind-pose clearance shrunk '{cap.ParentBone}' capsule radius ({origR0:F1}/{origR1:F1} -> {cap.Radius0:F1}/{cap.Radius1:F1} in) to clear cloth bone '{blockerBone}'.");
                    }
                }
            }

            int idx = 0;
            foreach (var cap in capsules)
            {
                bool isUpperArm = cap.ParentBone.Equals("arm_upper_L", StringComparison.OrdinalIgnoreCase) ||
                                  cap.ParentBone.Equals("arm_upper_R", StringComparison.OrdinalIgnoreCase);
                int priority = isUpperArm ? 0 : 1;

                var sb = new StringBuilder();
                sb.AppendLine("\t\t\t\t\t{");
                sb.AppendLine("\t\t\t\t\t\t_class = \"ClothShapeCapsule\"");
                sb.AppendLine($"\t\t\t\t\t\tname = \"{EscapeKvString(cap.ParentBone)}_clothCapsule_{idx}\"");
                sb.AppendLine($"\t\t\t\t\t\tparent_bone = \"{EscapeKvString(cap.ParentBone)}\"");
                sb.AppendLine("\t\t\t\t\t\tcloth_collision_layer0 = true");
                sb.AppendLine("\t\t\t\t\t\tcloth_collision_layer1 = true");
                sb.AppendLine("\t\t\t\t\t\tcloth_collision_layer2 = true");
                sb.AppendLine("\t\t\t\t\t\tcloth_collision_layer3 = true");
                sb.AppendLine($"\t\t\t\t\t\tcloth_collision_priority = {priority}");
                sb.AppendLine("\t\t\t\t\t\tvertex_map = \"\"");
                sb.AppendLine("\t\t\t\t\t\tinverted_collision = false");
                sb.AppendLine("\t\t\t\t\t\tplanarize = false");
                sb.AppendLine("\t\t\t\t\t\tbounciness = 0.0");
                sb.AppendLine($"\t\t\t\t\t\tradius0 = {FmtKvFloat(cap.Radius0)}");
                sb.AppendLine($"\t\t\t\t\t\tradius1 = {FmtKvFloat(cap.Radius1)}");
                sb.AppendLine($"\t\t\t\t\t\tpoint0 = [ {FmtKvFloat(cap.Point0.X)}, {FmtKvFloat(cap.Point0.Y)}, {FmtKvFloat(cap.Point0.Z)} ]");
                sb.AppendLine($"\t\t\t\t\t\tpoint1 = [ {FmtKvFloat(cap.Point1.X)}, {FmtKvFloat(cap.Point1.Y)}, {FmtKvFloat(cap.Point1.Z)} ]");
                sb.Append("\t\t\t\t\t}");
                result.Add(sb.ToString());
                idx++;

                // CSDK13-verified CollisionPlanes recipe (matching Valve's unicorn.vmdl_c):
                // A planarized capsule is consumed out of TaperedCapsuleRigids into per-node FeCollisionPlane entries,
                // so emit a dedicated planarized back barrier on spine_2 / spine_3 alongside the normal capsule.
                if (hasPlanarizeBackMap &&
                    (cap.ParentBone.Equals("spine_2", StringComparison.OrdinalIgnoreCase) ||
                     cap.ParentBone.Equals("spine_3", StringComparison.OrdinalIgnoreCase)))
                {
                    var pb = new StringBuilder();
                    pb.AppendLine("\t\t\t\t\t{");
                    pb.AppendLine("\t\t\t\t\t\t_class = \"ClothShapeCapsule\"");
                    pb.AppendLine($"\t\t\t\t\t\tname = \"{EscapeKvString(cap.ParentBone)}_clothPlanarizedBarrier_{idx}\"");
                    pb.AppendLine($"\t\t\t\t\t\tparent_bone = \"{EscapeKvString(cap.ParentBone)}\"");
                    pb.AppendLine("\t\t\t\t\t\tcloth_collision_layer0 = true");
                    pb.AppendLine("\t\t\t\t\t\tcloth_collision_layer1 = true");
                    pb.AppendLine("\t\t\t\t\t\tcloth_collision_layer2 = true");
                    pb.AppendLine("\t\t\t\t\t\tcloth_collision_layer3 = true");
                    pb.AppendLine("\t\t\t\t\t\tcloth_collision_priority = 0");
                    pb.AppendLine("\t\t\t\t\t\tvertex_map = \"planarize_back\"");
                    pb.AppendLine("\t\t\t\t\t\tinverted_collision = false");
                    pb.AppendLine("\t\t\t\t\t\tplanarize = true");
                    pb.AppendLine("\t\t\t\t\t\tbounciness = 0.0");
                    pb.AppendLine($"\t\t\t\t\t\tradius0 = {FmtKvFloat(cap.Radius0)}");
                    pb.AppendLine($"\t\t\t\t\t\tradius1 = {FmtKvFloat(cap.Radius1)}");
                    pb.AppendLine($"\t\t\t\t\t\tpoint0 = [ {FmtKvFloat(cap.Point0.X)}, {FmtKvFloat(cap.Point0.Y)}, {FmtKvFloat(cap.Point0.Z)} ]");
                    pb.AppendLine($"\t\t\t\t\t\tpoint1 = [ {FmtKvFloat(cap.Point1.X)}, {FmtKvFloat(cap.Point1.Y)}, {FmtKvFloat(cap.Point1.Z)} ]");
                    pb.Append("\t\t\t\t\t}");
                    result.Add(pb.ToString());
                    idx++;
                }

                // Yamato-style planarized thigh collision planes for closed wrap-around skirts/dresses:
                // keeps full-size leg_upper_L/R capsules AND generates per-node CollisionPlanes so moving
                // thighs push the skirt cleanly without exploding it at rest pose.
                if (hasPlanarizeWrapMap &&
                    (cap.ParentBone.Equals("leg_upper_L", StringComparison.OrdinalIgnoreCase) ||
                     cap.ParentBone.Equals("leg_upper_R", StringComparison.OrdinalIgnoreCase)))
                {
                    var pw = new StringBuilder();
                    pw.AppendLine("\t\t\t\t\t{");
                    pw.AppendLine("\t\t\t\t\t\t_class = \"ClothShapeCapsule\"");
                    pw.AppendLine($"\t\t\t\t\t\tname = \"{EscapeKvString(cap.ParentBone)}_clothPlanarizedWrap_{idx}\"");
                    pw.AppendLine($"\t\t\t\t\t\tparent_bone = \"{EscapeKvString(cap.ParentBone)}\"");
                    pw.AppendLine("\t\t\t\t\t\tcloth_collision_layer0 = true");
                    pw.AppendLine("\t\t\t\t\t\tcloth_collision_layer1 = true");
                    pw.AppendLine("\t\t\t\t\t\tcloth_collision_layer2 = true");
                    pw.AppendLine("\t\t\t\t\t\tcloth_collision_layer3 = true");
                    pw.AppendLine("\t\t\t\t\t\tcloth_collision_priority = 0");
                    pw.AppendLine("\t\t\t\t\t\tvertex_map = \"planarize_wrap\"");
                    pw.AppendLine("\t\t\t\t\t\tinverted_collision = false");
                    pw.AppendLine("\t\t\t\t\t\tplanarize = true");
                    pw.AppendLine("\t\t\t\t\t\tbounciness = 0.0");
                    pw.AppendLine($"\t\t\t\t\t\tradius0 = {FmtKvFloat(cap.Radius0)}");
                    pw.AppendLine($"\t\t\t\t\t\tradius1 = {FmtKvFloat(cap.Radius1)}");
                    pw.AppendLine($"\t\t\t\t\t\tpoint0 = [ {FmtKvFloat(cap.Point0.X)}, {FmtKvFloat(cap.Point0.Y)}, {FmtKvFloat(cap.Point0.Z)} ]");
                    pw.AppendLine($"\t\t\t\t\t\tpoint1 = [ {FmtKvFloat(cap.Point1.X)}, {FmtKvFloat(cap.Point1.Y)}, {FmtKvFloat(cap.Point1.Z)} ]");
                    pw.Append("\t\t\t\t\t}");
                    result.Add(pw.ToString());
                    idx++;
                }
            }

            return result;
        }

        /// <summary>
        /// Writes a binary/text Valve DMX (format model 22) ClothProxyMesh containing quad-ribbon strips
        /// with per-vertex cloth_enable$0 (0.0 pinned on anchor/root, 1.0 simulated on chain links),
        /// cloth_goal_strength_v2$0, and cloth_goal_damping$0 so Source 2 FeModel back_solve_joints
        /// drives dynamic hair/cape/skirt bones inside Deadlock.
        /// </summary>
        public static bool WriteProceduralClothProxyDmx(
            string dmxFullPath,
            string clothProxyRelPath,
            CustomModelExportBundle bundle,
            List<VmdlSkeletonBone> smdBones)
        {
            if (bundle?.PreservedChains == null || smdBones == null || smdBones.Count == 0)
                return false;

            var activeChains = bundle.PreservedChains
                .Where(c => c != null && c.Policy == ChainPolicy.PreservePhysicsChain)
                .ToList();
            if (activeChains.Count == 0)
                return false;

            var nameToSmdIdx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < smdBones.Count; i++)
                nameToSmdIdx[smdBones[i].Name] = i;

            // Compute Source-2 world positions for every skeleton bone in inches
            var srcWorldPos = new System.Numerics.Vector3[smdBones.Count];
            var srcWorldRot = new System.Numerics.Quaternion[smdBones.Count];
            var computed = new bool[smdBones.Count];

            void EnsureWorldBone(int bi)
            {
                if (bi < 0 || bi >= smdBones.Count || computed[bi]) return;
                computed[bi] = true; // cycle guard
                var b = smdBones[bi];
                var localQ = b.HasParentRot ? b.ParentRot : System.Numerics.Quaternion.Identity;
                var localP = new System.Numerics.Vector3(b.Origin.X, b.Origin.Y, b.Origin.Z);
                if (b.ParentIndex >= 0 && b.ParentIndex < smdBones.Count)
                {
                    EnsureWorldBone(b.ParentIndex);
                    srcWorldRot[bi] = System.Numerics.Quaternion.Normalize(srcWorldRot[b.ParentIndex] * localQ);
                    srcWorldPos[bi] = srcWorldPos[b.ParentIndex] + System.Numerics.Vector3.Transform(localP, srcWorldRot[b.ParentIndex]);
                }
                else
                {
                    srcWorldRot[bi] = System.Numerics.Quaternion.Normalize(localQ);
                    srcWorldPos[bi] = localP;
                }
            }
            for (int i = 0; i < smdBones.Count; i++) EnsureWorldBone(i);

            var dm = new Datamodel.Datamodel("model", 22);
            string stem = Path.GetFileNameWithoutExtension(string.IsNullOrEmpty(clothProxyRelPath) ? "custom_cloth_proxy" : clothProxyRelPath);

            var dmeModel = new Datamodel.Element(dm, stem, null, "DmeModel");
            var modelXf = new Datamodel.Element(dm, "transform", null, "DmeTransform");
            modelXf["position"] = System.Numerics.Vector3.Zero;
            modelXf["orientation"] = System.Numerics.Quaternion.Identity;
            dmeModel["transform"] = modelXf;
            dmeModel["shape"] = null;
            dmeModel["visible"] = true;
            dmeModel["axisSystem"] = CreateDefaultAxisSystem(dm);

            // Build full DmeJoint hierarchy matching smdBones so jointList indices match 1:1
            var dmeJoints = new Datamodel.Element[smdBones.Count];
            var jointChildren = new Datamodel.ElementArray[smdBones.Count];
            for (int i = 0; i < smdBones.Count; i++)
            {
                var b = smdBones[i];
                var jEl = new Datamodel.Element(dm, b.Name, null, "DmeJoint");
                var jXf = new Datamodel.Element(dm, "transform", null, "DmeTransform");
                jXf["position"] = new System.Numerics.Vector3(b.Origin.X, b.Origin.Y, b.Origin.Z);
                var q = b.HasParentRot ? System.Numerics.Quaternion.Normalize(b.ParentRot) : System.Numerics.Quaternion.Identity;
                jXf["orientation"] = q;
                jEl["transform"] = jXf;
                jEl["visible"] = true;
                var chArr = new Datamodel.ElementArray();
                jEl["children"] = chArr;
                dmeJoints[i] = jEl;
                jointChildren[i] = chArr;
            }

            var modelChildren = new Datamodel.ElementArray();
            var jointList = new Datamodel.ElementArray();
            for (int i = 0; i < smdBones.Count; i++)
            {
                jointList.Add(dmeJoints[i]);
                int p = smdBones[i].ParentIndex;
                if (p >= 0 && p < smdBones.Count && p != i)
                    jointChildren[p].Add(dmeJoints[i]);
                else
                    modelChildren.Add(dmeJoints[i]);
            }

            // Build quad-strip cloth ribbons across all enabled physics chains
            var positions = new Datamodel.Vector3Array();
            var normals = new Datamodel.Vector3Array();
            var uvs = new Datamodel.Vector2Array();
            var clothEnable = new Datamodel.FloatArray();
            var clothGoalStrength = new Datamodel.FloatArray();
            var clothGoalDamping = new Datamodel.FloatArray();
            var clothBendStiff = new Datamodel.FloatArray();
            var clothCollisionRadius = new Datamodel.FloatArray();
            var clothStrayRadius = new Datamodel.FloatArray();
            var clothSuspenders = new Datamodel.FloatArray();
            var clothDrag = new Datamodel.FloatArray();
            var clothGravity = new Datamodel.FloatArray();
            var clothFriction = new Datamodel.FloatArray();
            var clothAnchorFreeRotate = new Datamodel.FloatArray();
            var blendIndices = new Datamodel.IntArray();
            var blendWeights = new Datamodel.FloatArray();
            var vertIndices = new Datamodel.IntArray();
            var faceIndices = new Datamodel.IntArray();

            var proxyDesignated = activeChains.Where(c => c.Simulation == SimulationKind.ProxyCloth).ToList();
            var chainsForProxy = proxyDesignated.Count > 0 ? proxyDesignated : activeChains;

            int emittedStrips = 0;

            bool TryBuildStrandRows(
                BoneChainDef ch,
                out List<System.Numerics.Vector3> rowPositions,
                out List<int> rowJointIdx,
                out List<bool> rowPin,
                out int pinParentBi)
            {
                rowPositions = new List<System.Numerics.Vector3>();
                rowJointIdx = new List<int>();
                rowPin = new List<bool>();
                pinParentBi = -1;

                var chainBoneNames = (ch.AppendedHeroBoneNames != null && ch.AppendedHeroBoneNames.Count > 0)
                    ? ch.AppendedHeroBoneNames
                    : ch.SourceBoneNames;
                if (chainBoneNames == null || chainBoneNames.Count == 0) return false;

                var boneIndices = new List<int>();
                foreach (string bn in chainBoneNames)
                {
                    if (nameToSmdIdx.TryGetValue(bn, out int bi))
                        boneIndices.Add(bi);
                }
                if (boneIndices.Count == 0) return false;

                int firstBi = boneIndices[0];
                pinParentBi = smdBones[firstBi].ParentIndex;
                if (pinParentBi < 0 && !string.IsNullOrEmpty(ch.AnchorHeroBone) && nameToSmdIdx.TryGetValue(ch.AnchorHeroBone, out int anchorBi))
                    pinParentBi = anchorBi;

                if (pinParentBi >= 0 && pinParentBi < smdBones.Count)
                {
                    rowPositions.Add(srcWorldPos[pinParentBi]);
                    rowJointIdx.Add(pinParentBi);
                    rowPin.Add(true);
                }
                else
                {
                    rowPositions.Add(srcWorldPos[firstBi]);
                    rowJointIdx.Add(firstBi);
                    rowPin.Add(true);
                }

                for (int k = 0; k < boneIndices.Count; k++)
                {
                    int bi = boneIndices[k];
                    if (k == 0 && pinParentBi < 0)
                    {
                        rowPositions.Add(srcWorldPos[bi] + new System.Numerics.Vector3(0, 0, -4.0f));
                        rowJointIdx.Add(bi);
                        rowPin.Add(false);
                        continue;
                    }
                    var pos = srcWorldPos[bi];
                    if ((pos - rowPositions[^1]).LengthSquared() < 0.04f)
                        pos = rowPositions[^1] + new System.Numerics.Vector3(0, 0, -3.0f);
                    rowPositions.Add(pos);
                    rowJointIdx.Add(bi);
                    rowPin.Add(false);
                }

                if (rowPositions.Count == 2)
                {
                    var segDir = rowPositions[1] - rowPositions[0];
                    float len = segDir.Length();
                    segDir = len > 0.1f ? segDir / len : new System.Numerics.Vector3(0, 0, -1);
                    rowPositions.Add(rowPositions[1] + segDir * 4.0f);
                    rowJointIdx.Add(rowJointIdx[1]);
                    rowPin.Add(false);
                }

                // Extend proxy sheet past short leaf bones (e.g. ARP hair ending at Z=60 while mesh reaches Z=16)
                // so pelvis/thigh/calf capsules push the entire hanging mesh outward instead of clipping!
                if (rowPositions.Count >= 2 && bundle.Submeshes != null && bundle.Submeshes.Count > 0 && bundle.BoneNames != null)
                {
                    string leafName = smdBones[rowJointIdx[^1]].Name;
                    int leafBundleIdx = bundle.BoneNames.FindIndex(b => b.Equals(leafName, StringComparison.OrdinalIgnoreCase));
                    if (leafBundleIdx >= 0)
                    {
                        var leafPos = rowPositions[^1];
                        var tailDir = rowPositions[^1] - rowPositions[^2];
                        if (tailDir.Z > -0.2f * tailDir.Length())
                            tailDir = new System.Numerics.Vector3(tailDir.X * 0.25f, tailDir.Y * 0.25f, -1.0f);
                        float tLen = tailDir.Length();
                        tailDir = tLen > 0.01f ? tailDir / tLen : new System.Numerics.Vector3(0, 0, -1);

                        float maxReach = 0f;
                        foreach (var sm in bundle.Submeshes)
                        {
                            if (sm?.Vertices == null || sm.BoneIndices == null || sm.BoneWeights == null) continue;
                            int vc = sm.Vertices.Length;
                            for (int vi = 0; vi < vc; vi++)
                            {
                                float lw = 0f;
                                for (int k = 0; k < 4; k++)
                                {
                                    if (sm.BoneIndices[vi * 4 + k] == leafBundleIdx)
                                        lw += sm.BoneWeights[vi * 4 + k];
                                }
                                if (lw < 0.25f) continue;
                                Vector3 vSrc = GodotToSourcePosition(sm.Vertices[vi]);
                                var d = new System.Numerics.Vector3(vSrc.X, vSrc.Y, vSrc.Z) - leafPos;
                                float proj = System.Numerics.Vector3.Dot(d, tailDir);
                                if (proj > maxReach) maxReach = proj;
                            }
                        }

                        if (maxReach > 5.0f)
                        {
                            float extLen = MathF.Min(maxReach * 0.80f, 34.0f);
                            rowPositions.Add(leafPos + tailDir * extLen);
                            rowJointIdx.Add(rowJointIdx[^1]);
                            rowPin.Add(false);
                        }
                    }
                }

                // Subdivide segments (> 4.0 in) so neck/spine/pelvis/leg capsules collide densely along the span
                // instead of letting straight edges slice through the upper back when looking upward.
                if (rowPositions.Count >= 3)
                {
                    var subPos = new List<System.Numerics.Vector3> { rowPositions[0] };
                    var subJoint = new List<int> { rowJointIdx[0] };
                    var subPin = new List<bool> { rowPin[0] };
                    for (int r = 0; r < rowPositions.Count - 1; r++)
                    {
                        var a = rowPositions[r];
                        var b = rowPositions[r + 1];
                        float segLen = (b - a).Length();
                        float maxSeg = (r == 0) ? 5.0f : 4.0f;
                        int steps = segLen <= maxSeg ? 1 : Math.Clamp((int)MathF.Ceiling(segLen / maxSeg), 2, 6);
                        for (int s = 1; s <= steps; s++)
                        {
                            float u = s / (float)steps;
                            subPos.Add(System.Numerics.Vector3.Lerp(a, b, u));
                            subJoint.Add(r == 0 ? rowJointIdx[1] : (u < 0.5f ? rowJointIdx[r] : rowJointIdx[r + 1]));
                            subPin.Add(s == steps ? rowPin[r + 1] : false);
                        }
                    }
                    rowPositions = subPos;
                    rowJointIdx = subJoint;
                    rowPin = subPin;
                }
                return rowPositions.Count >= 3;
            }

            var anyExplicitStray = false;

            static bool IsCrownOrBangTuftChain(BoneChainDef ch)
            {
                if (ch == null) return false;
                string nm = $"{ch.Name} {ch.RootSourceBone}".ToLowerInvariant();
                if (nm.Contains("poof") || nm.Contains("bang") || nm.Contains("fringe") ||
                    nm.Contains("tuft") || nm.Contains("ahoge") || nm.Contains("front") ||
                    nm.Contains("brow") || nm.Contains("crown"))
                    return true;
                int bc = (ch.AppendedHeroBoneNames != null && ch.AppendedHeroBoneNames.Count > 0)
                    ? ch.AppendedHeroBoneNames.Count
                    : (ch.SourceBoneNames?.Count ?? 0);
                return bc <= 1;
            }

            bundle.PlanarizeBackProxyNodes.Clear();
            bundle.PlanarizeWrapProxyNodes.Clear();

            void PushClothVertex(
                BoneChainDef ch,
                System.Numerics.Vector3 vPos,
                System.Numerics.Vector3 nrm,
                float u,
                float t,
                bool isPinned,
                int jointIdx)
            {
                string jName = (jointIdx >= 0 && jointIdx < smdBones.Count) ? smdBones[jointIdx].Name : "";
                var jov = isPinned ? null : ch.FindJointOverride(jName);

                bool effPinned = isPinned || (jov?.Simulate.HasValue == true && !jov.Simulate.Value);
                float enable = effPinned ? 0.0f : 1.0f;

                var rp = PhysicsParamResolver.ResolveClothNode(ch, t, effPinned, jov);

                float strayRad;
                if (effPinned)
                {
                    strayRad = 0.0f;
                }
                else if (rp.StrayRadiusInches < 0f)
                {
                    // Disabled (-1.0): use 220 in only if another chain forces cloth_stray_radius$0 into the DMX
                    strayRad = 220.0f;
                }
                else
                {
                    anyExplicitStray = true;
                    strayRad = rp.StrayRadiusInches;
                }

                // Disable suspenders when AnchorFreeRotate is enabled (e.g. head-anchored hair)
                float susp = (effPinned || ch.AnchorFreeRotate) ? 0.0f : 1.0f;

                int vi = positions.Count;
                positions.Add(vPos);
                normals.Add(nrm);
                uvs.Add(new System.Numerics.Vector2(u, t));
                clothEnable.Add(enable);
                clothGoalStrength.Add(rp.GoalStrengthInput);
                clothGoalDamping.Add(rp.GoalDamping);
                clothBendStiff.Add(rp.BendStiffness);
                clothCollisionRadius.Add(rp.CollisionRadiusInches);
                clothStrayRadius.Add(strayRad);
                clothSuspenders.Add(susp);
                clothDrag.Add(rp.Drag);
                clothGravity.Add(rp.GravityInchesPerSecSq);
                clothFriction.Add(effPinned ? 0.0f : rp.Friction);
                clothAnchorFreeRotate.Add(rp.AnchorFreeRotate);
                blendIndices.Add(jointIdx);
                blendWeights.Add(1.0f);
                vertIndices.Add(vi);

                if (!effPinned && ch.PlanarizeBackBarrier)
                {
                    bundle.PlanarizeBackProxyNodes.Add($"$cloth_m0p{vi}");
                }
                if (!effPinned && !ch.OpenSheet)
                {
                    bundle.PlanarizeWrapProxyNodes.Add($"$cloth_m0p{vi}");
                }
            }

            void EmitRibbonStrip(BoneChainDef ch, List<System.Numerics.Vector3> rowPositions, List<int> rowJointIdx, List<bool> rowPin)
            {
                int baseVert = positions.Count;
                for (int r = 0; r < rowPositions.Count; r++)
                {
                    System.Numerics.Vector3 segDir = r + 1 < rowPositions.Count
                        ? rowPositions[r + 1] - rowPositions[r]
                        : rowPositions[r] - rowPositions[r - 1];
                    if (segDir.LengthSquared() < 0.0001f) segDir = new System.Numerics.Vector3(0, 0, -1);
                    segDir = System.Numerics.Vector3.Normalize(segDir);

                    var upRef = MathF.Abs(segDir.Z) < 0.9f
                        ? new System.Numerics.Vector3(0, 0, 1)
                        : new System.Numerics.Vector3(1, 0, 0);
                    var side = System.Numerics.Vector3.Cross(segDir, upRef);
                    if (side.LengthSquared() < 0.0001f) side = new System.Numerics.Vector3(1, 0, 0);
                    side = System.Numerics.Vector3.Normalize(side) * 1.25f;
                    var nrm = System.Numerics.Vector3.Normalize(System.Numerics.Vector3.Cross(side, segDir));
                    float t = rowPositions.Count > 1 ? r / (float)(rowPositions.Count - 1) : 0f;

                    PushClothVertex(ch, rowPositions[r] - side, nrm, 0.0f, t, rowPin[r], rowJointIdx[r]);
                    PushClothVertex(ch, rowPositions[r] + side, nrm, 1.0f, t, rowPin[r], rowJointIdx[r]);
                }

                for (int r = 0; r < rowPositions.Count - 1; r++)
                {
                    int v00 = baseVert + r * 2;
                    int v01 = v00 + 1;
                    int v10 = v00 + 2;
                    int v11 = v00 + 3;
                    faceIndices.Add(v00); faceIndices.Add(v01); faceIndices.Add(v10); faceIndices.Add(-1);
                    faceIndices.Add(v01); faceIndices.Add(v11); faceIndices.Add(v10); faceIndices.Add(-1);
                }
                emittedStrips++;
            }

            if (proxyDesignated.Count > 0)
            {
                var groups = chainsForProxy.GroupBy(c => $"{c.SlotId ?? ""}::{c.AnchorHeroBone ?? ""}", StringComparer.OrdinalIgnoreCase);
                foreach (var grp in groups)
                {
                    var built = new List<(BoneChainDef Ch, List<System.Numerics.Vector3> Pos, List<int> JointIdx, List<bool> Pin, int PinBi, float Angle)>();
                    bool isHeadGroup = grp.Any(c =>
                        (c.AnchorHeroBone ?? "").Equals("head", StringComparison.OrdinalIgnoreCase) ||
                        (c.AnchorHeroBone ?? "").Equals("neck_0", StringComparison.OrdinalIgnoreCase));

                    foreach (var ch in grp)
                    {
                        if (!TryBuildStrandRows(ch, out var rp, out var rj, out var rpin, out int pinBi))
                            continue;
                        // Never stitch short crown/bang tufts (e.g. hairpoof_l/r) into the back-hair sheet;
                        // emit them as independent strips so they cannot form a 360° collar or 25+ in cross-head rods.
                        if (isHeadGroup && IsCrownOrBangTuftChain(ch))
                        {
                            EmitRibbonStrip(ch, rp, rj, rpin);
                            continue;
                        }
                        built.Add((ch, rp, rj, rpin, pinBi, 0f));
                    }
                    if (built.Count == 0) continue;

                    if (built.Count == 1)
                    {
                        EmitRibbonStrip(built[0].Ch, built[0].Pos, built[0].JointIdx, built[0].Pin);
                        continue;
                    }
                    if (built.Count == 2)
                    {
                        // Avoid bridging across opposite left/right hip tails (>12 in apart at mid/root)
                        var p0 = built[0].Pos[Math.Min(1, built[0].Pos.Count - 1)];
                        var p1 = built[1].Pos[Math.Min(1, built[1].Pos.Count - 1)];
                        if ((p0 - p1).Length() > 12.0f)
                        {
                            EmitRibbonStrip(built[0].Ch, built[0].Pos, built[0].JointIdx, built[0].Pin);
                            EmitRibbonStrip(built[1].Ch, built[1].Pos, built[1].JointIdx, built[1].Pin);
                            continue;
                        }
                    }

                    // Order multi-strand proxy sheets radially in Source 2 XY around the anchor bone origin
                    // using mid-strand positions (where strands are well-separated) and relative angle around
                    // the group's mean outward direction so open back-hair curtains NEVER suffer a +-180 deg
                    // branch-cut scramble (which previously stitched hairside_r directly to hairside_l across 57 in!).
                    var center = System.Numerics.Vector3.Zero;
                    bool foundAnchorCenter = false;
                    foreach (var s in built)
                    {
                        if (s.PinBi >= 0 && s.PinBi < srcWorldPos.Length)
                        {
                            center = srcWorldPos[s.PinBi];
                            foundAnchorCenter = true;
                            break;
                        }
                    }
                    if (!foundAnchorCenter)
                    {
                        foreach (var s in built) center += s.Pos[Math.Min(1, s.Pos.Count - 1)];
                        center /= built.Count;
                    }

                    // Compute mean outward XY direction at mid-strand so we can unwrap angles without a +-180 cut
                    var meanDirXY = System.Numerics.Vector2.Zero;
                    for (int i = 0; i < built.Count; i++)
                    {
                        var midP = built[i].Pos[Math.Clamp(built[i].Pos.Count / 2, 1, built[i].Pos.Count - 1)];
                        var dxy = new System.Numerics.Vector2(midP.X - center.X, midP.Y - center.Y);
                        float len = dxy.Length();
                        if (len > 0.01f) meanDirXY += dxy / len;
                    }
                    float meanAngle = meanDirXY.LengthSquared() > 1e-4f
                        ? MathF.Atan2(meanDirXY.Y, meanDirXY.X)
                        : 0f;

                    for (int i = 0; i < built.Count; i++)
                    {
                        var midP = built[i].Pos[Math.Clamp(built[i].Pos.Count / 2, 1, built[i].Pos.Count - 1)];
                        var d = midP - center;
                        float rawAng = MathF.Atan2(d.Y, d.X);
                        float relAng = rawAng - meanAngle;
                        while (relAng > MathF.PI) relAng -= MathF.Tau;
                        while (relAng < -MathF.PI) relAng += MathF.Tau;
                        var item = built[i];
                        item.Angle = isHeadGroup ? relAng : rawAng;
                        built[i] = item;
                    }
                    built.Sort((a, b) => a.Angle.CompareTo(b.Angle));

                    // Close 360° ring only when chains explicitly request a closed sheet (e.g. wrap-around skirt)
                    // and angular gaps are <= 135 deg; open curtains/capes stay open.
                    bool anyWantsClosed = built.Any(s => !s.Ch.OpenSheet);
                    bool closeLoop = anyWantsClosed && !isHeadGroup && built.Count >= 4;
                    if (closeLoop)
                    {
                        float maxGap = 0f;
                        for (int i = 0; i < built.Count; i++)
                        {
                            float gap = built[(i + 1) % built.Count].Angle - built[i].Angle;
                            if (gap < 0f) gap += MathF.Tau;
                            if (gap > maxGap) maxGap = gap;
                        }
                        if (maxGap > Mathf.DegToRad(135f)) closeLoop = false;
                    }

                    // Resample every strand in this multi-strand sheet to the EXACT same row count (targetRows)
                    // so the proxy mesh is a 100% regular N x targetRows manifold quad grid:
                    // - ZERO orphaned lower rows (so _ext1..3 are NEVER left as unsimulated StaticNodes!)
                    // - ZERO diagonal fan-bridges across mismatched row counts!
                    int targetRows = built.Max(s => s.Pos.Count);
                    for (int i = 0; i < built.Count; i++)
                    {
                        var st = built[i];
                        if (st.Pos.Count >= targetRows || st.Pos.Count < 2) continue;

                        int srcCount = st.Pos.Count;
                        var cumLen = new float[srcCount];
                        for (int r = 1; r < srcCount; r++)
                            cumLen[r] = cumLen[r - 1] + (st.Pos[r] - st.Pos[r - 1]).Length();
                        float totalLen = MathF.Max(0.01f, cumLen[^1]);

                        var newPos = new List<System.Numerics.Vector3>(targetRows);
                        var newJoint = new List<int>(targetRows);
                        var newPin = new List<bool>(targetRows);
                        for (int r = 0; r < targetRows; r++)
                        {
                            if (r == 0)
                            {
                                newPos.Add(st.Pos[0]);
                                newJoint.Add(st.JointIdx[0]);
                                newPin.Add(st.Pin[0]);
                                continue;
                            }
                            if (r == targetRows - 1)
                            {
                                newPos.Add(st.Pos[^1]);
                                newJoint.Add(st.JointIdx[^1]);
                                newPin.Add(st.Pin[^1]);
                                continue;
                            }
                            float targetDist = totalLen * (r / (float)(targetRows - 1));
                            int seg = 0;
                            while (seg + 1 < srcCount - 1 && cumLen[seg + 1] < targetDist) seg++;
                            float segLen = MathF.Max(1e-5f, cumLen[seg + 1] - cumLen[seg]);
                            float uSeg = Math.Clamp((targetDist - cumLen[seg]) / segLen, 0f, 1f);
                            newPos.Add(System.Numerics.Vector3.Lerp(st.Pos[seg], st.Pos[seg + 1], uSeg));
                            newJoint.Add(seg == 0 ? st.JointIdx[1] : (uSeg < 0.5f ? st.JointIdx[seg] : st.JointIdx[seg + 1]));
                            newPin.Add(false);
                        }
                        built[i] = (st.Ch, newPos, newJoint, newPin, st.PinBi, st.Angle);
                    }

                    var colStartVerts = new List<int>(built.Count);
                    var colRowCounts = new List<int>(built.Count);
                    for (int i = 0; i < built.Count; i++)
                    {
                        var st = built[i];
                        colStartVerts.Add(positions.Count);
                        colRowCounts.Add(st.Pos.Count);
                        float u = built.Count > 1 ? i / (float)(built.Count - 1) : 0f;
                        for (int r = 0; r < st.Pos.Count; r++)
                        {
                            var radial = st.Pos[r] - center;
                            radial.Z = 0f;
                            var nrm = radial.LengthSquared() > 0.01f
                                ? System.Numerics.Vector3.Normalize(radial)
                                : new System.Numerics.Vector3(0, 1, 0);
                            float t = st.Pos.Count > 1 ? r / (float)(st.Pos.Count - 1) : 0f;
                            // Avoid overlapping column root verts all on pinParentBi; place row 0 slightly toward strand column 1
                            var vPos = (r == 0 && st.Pos.Count > 1)
                                ? System.Numerics.Vector3.Lerp(st.Pos[0], st.Pos[1], 0.15f)
                                : st.Pos[r];
                            PushClothVertex(st.Ch, vPos, nrm, u, t, st.Pin[r], st.JointIdx[r]);
                        }
                    }

                    int quadCols = closeLoop ? built.Count : built.Count - 1;
                    for (int c = 0; c < quadCols; c++)
                    {
                        int cNext = (c + 1) % built.Count;
                        // Skip stitching across large lateral gaps (> 16 in at root) to prevent cross-head/cross-back rods
                        var rootA = built[c].Pos[Math.Min(1, built[c].Pos.Count - 1)];
                        var rootB = built[cNext].Pos[Math.Min(1, built[cNext].Pos.Count - 1)];
                        if ((rootA - rootB).Length() > 16.0f) continue;

                        int baseA = colStartVerts[c];
                        int baseB = colStartVerts[cNext];
                        int rowsA = colRowCounts[c];
                        int rowsB = colRowCounts[cNext];
                        int minRows = Math.Min(rowsA, rowsB);

                        for (int r = 0; r < minRows - 1; r++)
                        {
                            int a0 = baseA + r;
                            int a1 = baseA + r + 1;
                            int b0 = baseB + r;
                            int b1 = baseB + r + 1;
                            faceIndices.Add(a0); faceIndices.Add(b0); faceIndices.Add(a1); faceIndices.Add(-1);
                            faceIndices.Add(b0); faceIndices.Add(b1); faceIndices.Add(a1); faceIndices.Add(-1);
                        }
                    }
                    emittedStrips += built.Count;
                }
            }
            else
            {
                foreach (var ch in chainsForProxy)
                {
                    if (TryBuildStrandRows(ch, out var rowPositions, out var rowJointIdx, out var rowPin, out _))
                        EmitRibbonStrip(ch, rowPositions, rowJointIdx, rowPin);
                }
            }

            if (emittedStrips == 0 || positions.Count < 6)
                return false;

            var vertexData = new Datamodel.Element(dm, "bind", null, "DmeVertexData");
            var vFmt = new Datamodel.StringArray
            {
                "position$0", "normal$0", "texcoord$0",
                "cloth_enable$0", "cloth_goal_strength_v2$0", "cloth_goal_damping$0",
                "cloth_bend_stiffness$0", "cloth_collision_radius$0",
                "cloth_suspenders$0", "cloth_drag$0", "cloth_gravity$0",
                "cloth_friction$0", "cloth_anchor_free_rotate$0",
                "blendindices$0", "blendweights$0"
            };
            if (anyExplicitStray)
                vFmt.Insert(8, "cloth_stray_radius$0");
            vertexData["vertexFormat"] = vFmt;
            vertexData["jointCount"] = 1;
            vertexData["flipVCoordinates"] = false;
            vertexData["position$0"] = positions;
            vertexData["position$0Indices"] = vertIndices;
            vertexData["normal$0"] = normals;
            vertexData["normal$0Indices"] = vertIndices;
            vertexData["texcoord$0"] = uvs;
            vertexData["texcoord$0Indices"] = vertIndices;
            vertexData["cloth_enable$0"] = clothEnable;
            vertexData["cloth_enable$0Indices"] = vertIndices;
            vertexData["cloth_goal_strength_v2$0"] = clothGoalStrength;
            vertexData["cloth_goal_strength_v2$0Indices"] = vertIndices;
            vertexData["cloth_goal_damping$0"] = clothGoalDamping;
            vertexData["cloth_goal_damping$0Indices"] = vertIndices;
            vertexData["cloth_bend_stiffness$0"] = clothBendStiff;
            vertexData["cloth_bend_stiffness$0Indices"] = vertIndices;
            vertexData["cloth_collision_radius$0"] = clothCollisionRadius;
            vertexData["cloth_collision_radius$0Indices"] = vertIndices;
            if (anyExplicitStray)
            {
                vertexData["cloth_stray_radius$0"] = clothStrayRadius;
                vertexData["cloth_stray_radius$0Indices"] = vertIndices;
            }
            vertexData["cloth_suspenders$0"] = clothSuspenders;
            vertexData["cloth_suspenders$0Indices"] = vertIndices;
            vertexData["cloth_drag$0"] = clothDrag;
            vertexData["cloth_drag$0Indices"] = vertIndices;
            vertexData["cloth_gravity$0"] = clothGravity;
            vertexData["cloth_gravity$0Indices"] = vertIndices;
            vertexData["cloth_friction$0"] = clothFriction;
            vertexData["cloth_friction$0Indices"] = vertIndices;
            vertexData["cloth_anchor_free_rotate$0"] = clothAnchorFreeRotate;
            vertexData["cloth_anchor_free_rotate$0Indices"] = vertIndices;
            vertexData["blendindices$0"] = blendIndices;
            vertexData["blendweights$0"] = blendWeights;

            var faceMat = new Datamodel.Element(dm, "cloth_proxy_mat", null, "DmeMaterial");
            faceMat["mtlName"] = "materials/dev/reflectivity_30.vmat";

            var faceSet = new Datamodel.Element(dm, "cloth", null, "DmeFaceSet");
            faceSet["faces"] = faceIndices;
            faceSet["material"] = faceMat;

            var dmeMesh = new Datamodel.Element(dm, stem, null, "DmeMesh");
            dmeMesh["visible"] = true;
            dmeMesh["bindState"] = vertexData;
            dmeMesh["currentState"] = vertexData;
            dmeMesh["baseStates"] = new Datamodel.ElementArray { vertexData };
            dmeMesh["deltaStates"] = new Datamodel.ElementArray();
            dmeMesh["faceSets"] = new Datamodel.ElementArray { faceSet };

            var meshDag = new Datamodel.Element(dm, stem, null, "DmeDag");
            var meshDagXf = new Datamodel.Element(dm, "transform", null, "DmeTransform");
            meshDagXf["position"] = System.Numerics.Vector3.Zero;
            meshDagXf["orientation"] = System.Numerics.Quaternion.Identity;
            meshDag["transform"] = meshDagXf;
            meshDag["shape"] = dmeMesh;
            meshDag["visible"] = true;
            meshDag["children"] = new Datamodel.ElementArray();
            modelChildren.Add(meshDag);

            dmeModel["children"] = modelChildren;
            dmeModel["jointList"] = jointList;
            dmeModel["baseStates"] = new Datamodel.ElementArray();

            var rootEl = new Datamodel.Element(dm, "root", null, "DmElement");
            rootEl["skeleton"] = dmeModel;
            rootEl["model"] = dmeModel;
            dm.Root = rootEl;

            string dir = Path.GetDirectoryName(dmxFullPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            dm.Save(dmxFullPath, "keyvalues2", 4);
            return true;
        }

        private static Datamodel.Element CreateDefaultAxisSystem(Datamodel.Datamodel dm)
        {
            var ax = new Datamodel.Element(dm, "axisSystem", null, "DmeAxisSystem");
            ax["upAxis"] = 3;      // Z up
            ax["forwardAxis"] = 1; // X forward
            ax["coordSys"] = 0;    // Right-handed
            return ax;
        }

        private static List<VmdlSkeletonBone> ResolveSmdSkeletonBones(
            CustomModelExportBundle bundle,
            List<VmdlSkeletonBone> vmdlSkeletonBones)
        {
            List<VmdlSkeletonBone> smdBones;
            if (vmdlSkeletonBones != null && vmdlSkeletonBones.Count > 0)
            {
                smdBones = vmdlSkeletonBones;
            }
            else
            {
                smdBones = new List<VmdlSkeletonBone>();
                var nameToIdx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < bundle.BoneNames.Count; i++)
                {
                    string bName = bundle.BoneNames[i];
                    if (IsClothBoneName(bName) && !bundle.PreservedCustomBoneNames.Contains(bName)) continue;
                    int newIdx = smdBones.Count;
                    nameToIdx[bName] = newIdx;
                    smdBones.Add(new VmdlSkeletonBone
                    {
                        Index = newIdx,
                        Name = bName
                    });
                }
                for (int i = 0; i < smdBones.Count; i++)
                {
                    string bName = smdBones[i].Name;
                    int pIdx = -1;
                    if (bundle.BoneHierarchy.TryGetValue(bName, out string pName) && !string.IsNullOrEmpty(pName))
                    {
                        nameToIdx.TryGetValue(pName, out pIdx);
                    }
                    smdBones[i].ParentIndex = pIdx;
                    if (bundle.BoneLocalRestTransforms.TryGetValue(bName, out Transform3D localT))
                    {
                        bool isRoot = pIdx < 0;
                        Vector3 srcPos = isRoot ? GodotToSourcePosition(localT.Origin) : localT.Origin * MetersToInches;
                        smdBones[i].Origin = srcPos;
                    }
                }
            }

            if (smdBones.Count == 0)
            {
                smdBones.Add(new VmdlSkeletonBone { Index = 0, Name = "root_motion", ParentIndex = -1 });
            }

            return smdBones;
        }

        private static void AppendSmdHeaderAndSkeleton(StringBuilder sb, List<VmdlSkeletonBone> smdBones)
        {
            var ic = CultureInfo.InvariantCulture;
            const float degToRad = MathF.PI / 180.0f;

            sb.AppendLine("version 1");
            sb.AppendLine("nodes");
            for (int i = 0; i < smdBones.Count; i++)
            {
                var b = smdBones[i];
                sb.AppendLine($"{i} \"{b.Name}\" {b.ParentIndex}");
            }
            sb.AppendLine("end");

            sb.AppendLine("skeleton");
            sb.AppendLine("time 0");
            for (int i = 0; i < smdBones.Count; i++)
            {
                var b = smdBones[i];
                float rotXRad, rotYRad, rotZRad;
                if (b.HasParentRot)
                {
                    (rotXRad, rotYRad, rotZRad) = QuaternionToSourceSmdEuler(b.ParentRot);
                }
                else
                {
                    // ModelDoc Bone.angles = [pitch (Y), yaw (Z), roll (X)] in degrees.
                    // SMD skeleton rotation columns are (rotX_rad = roll, rotY_rad = pitch, rotZ_rad = yaw).
                    rotXRad = b.AnglesDeg.Z * degToRad;
                    rotYRad = b.AnglesDeg.X * degToRad;
                    rotZRad = b.AnglesDeg.Y * degToRad;
                }

                sb.AppendLine(string.Format(ic,
                    "{0} {1:F6} {2:F6} {3:F6} {4:F6} {5:F6} {6:F6}",
                    i, b.Origin.X, b.Origin.Y, b.Origin.Z, rotXRad, rotYRad, rotZRad));
            }
            sb.AppendLine("end");
        }

        /// <summary>
        /// Writes a minimal 1-frame rest animation SMD referencing the hero skeleton so ModelDoc
        /// preserves ModelBoneFlags.Animation across all bones during headless compilation.
        /// </summary>
        public static void WriteRestBindAnimationSmd(
            string outputSmdPath,
            CustomModelExportBundle bundle,
            List<VmdlSkeletonBone> vmdlSkeletonBones = null)
        {
            var smdBones = ResolveSmdSkeletonBones(bundle, vmdlSkeletonBones);
            var sb = new StringBuilder(16 * 1024);
            AppendSmdHeaderAndSkeleton(sb, smdBones);
            sb.AppendLine("triangles");
            sb.AppendLine("end");
            File.WriteAllText(outputSmdPath, sb.ToString(), new UTF8Encoding(false));
        }

        /// <summary>
        /// Writes a Source 2 bind-pose SMD file whose nodes & skeleton time 0 match the decompiled
        /// .vmdl's _class = "Skeleton" block 1:1, and whose vertex positions/normals are transformed
        /// via the exact inverse of VRF's SourceToGltfRotation (-0.5, -0.5, -0.5, 0.5) & InchesToMeters (0.0254).
        /// </summary>
        public static void WriteSourceSpaceSmd(
            string outputSmdPath,
            CustomModelExportBundle bundle,
            List<CustomModelExportBundle.SubmeshData> submeshesToWrite = null,
            List<VmdlSkeletonBone> vmdlSkeletonBones = null)
        {
            submeshesToWrite ??= bundle.Submeshes;

            // Build authoritative bone list for SMD nodes & skeleton time 0
            List<VmdlSkeletonBone> smdBones = ResolveSmdSkeletonBones(bundle, vmdlSkeletonBones);

            var smdBoneNameToIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < smdBones.Count; i++)
            {
                smdBoneNameToIndex[smdBones[i].Name] = i;
            }

            int pelvisSmdIdx = smdBoneNameToIndex.TryGetValue("pelvis", out int pSmd) ? pSmd : 0;

            // Map each bundle.BoneNames index -> smdBones index (walking up parent hierarchy if helper/cloth bone)
            int[] godotBoneToSmdBone = new int[Math.Max(1, bundle.BoneNames.Count)];
            for (int b = 0; b < bundle.BoneNames.Count; b++)
            {
                string bName = bundle.BoneNames[b];
                int resolved = -1;
                string curr = bName;
                int guard = 0;
                while (!string.IsNullOrEmpty(curr) && guard++ < 64)
                {
                    bool allowCloth = bundle.PreservedCustomBoneNames.Contains(curr);
                    if ((!IsClothBoneName(curr) || allowCloth) && smdBoneNameToIndex.TryGetValue(curr, out int idx))
                    {
                        resolved = idx;
                        break;
                    }
                    if (!bundle.BoneHierarchy.TryGetValue(curr, out curr))
                        break;
                }
                godotBoneToSmdBone[b] = resolved >= 0 ? resolved : pelvisSmdIdx;
            }

            var sb = new StringBuilder(1024 * 1024);
            var ic = CultureInfo.InvariantCulture;
            AppendSmdHeaderAndSkeleton(sb, smdBones);

            sb.AppendLine("triangles");
            var mergedSmdWeights = new Dictionary<int, float>(4);

            foreach (var sub in submeshesToWrite)
            {
                if (sub.Vertices == null || sub.Vertices.Length == 0 || sub.Indices == null || sub.Indices.Length < 3)
                    continue;

                string matName = SanitizeMaterialName(sub.MaterialName);
                int vCount = sub.Vertices.Length;

                for (int t = 0; t + 2 < sub.Indices.Length; t += 3)
                {
                    int i0 = sub.Indices[t];
                    int i1 = sub.Indices[t + 1];
                    int i2 = sub.Indices[t + 2];
                    if (i0 < 0 || i0 >= vCount || i1 < 0 || i1 >= vCount || i2 < 0 || i2 >= vCount)
                        continue;

                    // In Godot ArrayMesh (imported via GltfDocument), triangle indices (i0, i1, i2) are stored CW,
                    // whereas Source 2 .smd expects right-handed CCW winding (i0, i2, i1).
                    // Never flip per-triangle based on faceCross.Dot(avgNorm), which corrupts custom bent anime
                    // normals on faces/bangs and flips inverted-hull outline shells outward!
                    int[] triVerts = new[] { i0, i2, i1 };

                    sb.AppendLine(matName);

                    for (int corner = 0; corner < 3; corner++)
                    {
                        int vi = triVerts[corner];
                        Vector3 p = GodotToSourcePosition(sub.Vertices[vi]);
                        Vector3 n = GodotToSourceNormal(vi < sub.Normals.Length ? sub.Normals[vi] : Vector3.Up);
                        Vector2 uv = vi < sub.UVs.Length ? sub.UVs[vi] : Vector2.Zero;

                        float u = uv.X;
                        float v = 1.0f - uv.Y; // Flip V from glTF top-left to Source 2 bottom-left SMD convention

                        mergedSmdWeights.Clear();
                        if (sub.BoneIndices != null && sub.BoneWeights != null && sub.BoneIndices.Length >= (vi + 1) * 4)
                        {
                            for (int k = 0; k < 4; k++)
                            {
                                float w = sub.BoneWeights[vi * 4 + k];
                                if (w <= 0.0001f) continue;
                                int gBone = sub.BoneIndices[vi * 4 + k];
                                int smdBone = (gBone >= 0 && gBone < godotBoneToSmdBone.Length)
                                    ? godotBoneToSmdBone[gBone]
                                    : pelvisSmdIdx;
                                mergedSmdWeights[smdBone] = mergedSmdWeights.GetValueOrDefault(smdBone, 0f) + w;
                            }
                        }

                        if (mergedSmdWeights.Count == 0)
                        {
                            mergedSmdWeights[pelvisSmdIdx] = 1.0f;
                        }

                        var links = mergedSmdWeights.OrderByDescending(kv => kv.Value).Take(4).ToList();
                        float wSum = 0f;
                        for (int l = 0; l < links.Count; l++) wSum += links[l].Value;
                        if (wSum <= 0.0001f) wSum = 1.0f;

                        int parentBone = links[0].Key;
                        sb.Append(string.Format(ic,
                            "{0} {1:F6} {2:F6} {3:F6} {4:F6} {5:F6} {6:F6} {7:F6} {8:F6} {9}",
                            parentBone, p.X, p.Y, p.Z, n.X, n.Y, n.Z, u, v, links.Count));

                        float assigned = 0f;
                        for (int l = 0; l < links.Count; l++)
                        {
                            float nw = (l == links.Count - 1)
                                ? Math.Max(0f, 1.0f - assigned)
                                : (links[l].Value / wSum);
                            assigned += nw;
                            sb.Append(string.Format(ic, " {0} {1:F6}", links[l].Key, nw));
                        }
                        sb.AppendLine();
                    }
                }
            }

            sb.AppendLine("end");
            File.WriteAllText(outputSmdPath, sb.ToString(), new UTF8Encoding(false));
        }

        /// <summary>
        /// Exact inverse of VRF's GltfModelExporter.BakeConversion:
        /// In VRF, SourceToGltfRotation = Quaternion(-0.5, -0.5, -0.5, 0.5) and InchesToMeters = 0.0254f,
        /// which maps Source (x_s, y_s, z_s) -> Godot (y_s, z_s, x_s) * 0.0254f.
        /// Therefore, the inverse from Godot (x_g, y_g, z_g) to Source 2 (x_s, y_s, z_s) is:
        /// x_s = z_g / 0.0254f, y_s = x_g / 0.0254f, z_s = y_g / 0.0254f.
        /// </summary>
        public static Vector3 GodotToSourcePosition(Vector3 p)
        {
            return new Vector3(p.Z * MetersToInches, p.X * MetersToInches, p.Y * MetersToInches);
        }

        public static Vector3 GodotToSourceNormal(Vector3 n)
        {
            Vector3 sn = new Vector3(n.Z, n.X, n.Y);
            float lenSq = sn.LengthSquared();
            return lenSq > 0.00001f ? sn / MathF.Sqrt(lenSq) : new Vector3(0, 0, 1);
        }

        private static Vector3 QuaternionToSourceQAngleDegrees(System.Numerics.Quaternion q)
        {
            // Extrinsic X-Y-Z (Roll-Pitch-Yaw) -> QAngle(pitch_deg, yaw_deg, roll_deg)
            float sinrCosp = 2f * (q.W * q.X + q.Y * q.Z);
            float cosrCosp = 1f - 2f * (q.X * q.X + q.Y * q.Y);
            float roll = MathF.Atan2(sinrCosp, cosrCosp);

            float sinp = 2f * (q.W * q.Y - q.Z * q.X);
            float pitch = MathF.Abs(sinp) >= 1f ? MathF.CopySign(MathF.PI / 2f, sinp) : MathF.Asin(sinp);

            float sinyCosp = 2f * (q.W * q.Z + q.X * q.Y);
            float cosyCosp = 1f - 2f * (q.Y * q.Y + q.Z * q.Z);
            float yaw = MathF.Atan2(sinyCosp, cosyCosp);

            const float radToDeg = 180.0f / MathF.PI;
            return new Vector3(pitch * radToDeg, yaw * radToDeg, roll * radToDeg);
        }

        /// <summary>
        /// Converts native Source 2 parent-local bone quaternion into SMD skeleton Euler angles in radians.
        /// Uses Valve's exact MatrixAngles algorithm from Source SDK mathlib.
        /// </summary>
        public static (float rotX, float rotY, float rotZ) QuaternionToSourceSmdEuler(System.Numerics.Quaternion q)
        {
            float x2 = q.X + q.X;
            float y2 = q.Y + q.Y;
            float z2 = q.Z + q.Z;
            float xx = q.X * x2;
            float xy = q.X * y2;
            float xz = q.X * z2;
            float yy = q.Y * y2;
            float yz = q.Y * z2;
            float zz = q.Z * z2;
            float wx = q.W * x2;
            float wy = q.W * y2;
            float wz = q.W * z2;

            float m00 = 1.0f - (yy + zz);
            float m01 = xy - wz;
            float m02 = xz + wy;

            float m10 = xy + wz;
            float m11 = 1.0f - (xx + zz);
            float m12 = yz - wx;

            float m20 = xz - wy;
            float m21 = yz + wx;
            float m22 = 1.0f - (xx + yy);

            // Valve Source SDK MatrixAngles (RadianEuler):
            // angles.z = atan2( m10, m00 ); // yaw
            // angles.y = atan2( -m20, sqrt(m00*m00 + m10*m10) ); // pitch
            // angles.x = atan2( m21, m22 ); // roll
            float xyDist = MathF.Sqrt(m00 * m00 + m10 * m10);
            float rotX, rotY, rotZ;
            if (xyDist > 0.001f)
            {
                rotX = MathF.Atan2(m21, m22);
                rotY = MathF.Atan2(-m20, xyDist);
                rotZ = MathF.Atan2(m10, m00);
            }
            else
            {
                rotX = 0.0f;
                rotY = MathF.Atan2(-m20, xyDist);
                rotZ = MathF.Atan2(-m01, m11);
            }
            return (rotX, rotY, rotZ);
        }

        /// <summary>
        /// Writes deduplicated custom materials (.vmat with shader "pbr.vfx") and power-of-2
        /// texture PNGs into contentMatDir. Shared flat normal map is reused across materials without custom normal maps.
        /// </summary>
        private static Dictionary<string, string> WriteAllCustomMaterialsAndTextures(
            CustomModelExportBundle bundle,
            string contentMatDir,
            string materialsFolderRel,
            out List<string> writtenVtexPaths,
            out List<string> writtenVmatPaths)
        {
            writtenVtexPaths = new List<string>();
            writtenVmatPaths = new List<string>();
            var matMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // Write explicit neutral fallback textures into contentMatDir so resourcecompiler.exe never leaves
            // g_tAmbientOcclusion, g_tNormalRoughness, g_tSelfIllumMask, or g_tTintMask unbound or pointing to missing engine files.
            string defaultNormalFile = "default_flat_normal.png";
            string defaultNormalRel = $"{materialsFolderRel}/{defaultNormalFile}";
            File.WriteAllBytes(Path.Combine(contentMatDir, defaultNormalFile), CreateSolidColorPngBytes(64, 64, new Color(0.5f, 0.5f, 1.0f, 1.0f)));

            string defaultWhiteAoFile = "default_white_ao.png";
            string defaultWhiteAoRel = $"{materialsFolderRel}/{defaultWhiteAoFile}";
            File.WriteAllBytes(Path.Combine(contentMatDir, defaultWhiteAoFile), CreateSolidColorPngBytes(64, 64, new Color(1.0f, 1.0f, 1.0f, 1.0f)));

            string defaultRoughFile = "default_roughness.png";
            string defaultRoughRel = $"{materialsFolderRel}/{defaultRoughFile}";
            File.WriteAllBytes(Path.Combine(contentMatDir, defaultRoughFile), CreateSolidColorPngBytes(64, 64, new Color(0.85f, 0.85f, 0.85f, 1.0f)));

            string defaultBlackMaskFile = "default_black_mask.png";
            string defaultBlackMaskRel = $"{materialsFolderRel}/{defaultBlackMaskFile}";
            File.WriteAllBytes(Path.Combine(contentMatDir, defaultBlackMaskFile), CreateSolidColorPngBytes(64, 64, new Color(0.0f, 0.0f, 0.0f, 1.0f)));

            string defaultNprTransFile = "default_npr_transmissive.png";
            string defaultNprTransRel = $"{materialsFolderRel}/{defaultNprTransFile}";
            File.WriteAllBytes(Path.Combine(contentMatDir, defaultNprTransFile), CreateSolidColorPngBytes(64, 64, new Color(0.42f, 0.42f, 0.46f, 1.0f)));

            foreach (var sub in bundle.Submeshes)
            {
                string matName = SanitizeMaterialName(sub.MaterialName);
                if (matMap.ContainsKey(matName)) continue;

                string colorPngFile = $"{matName}_color.png";
                string colorPngFull = Path.Combine(contentMatDir, colorPngFile);
                string colorPngRel = $"{materialsFolderRel}/{colorPngFile}";

                byte[] albedoBytes = sub.AlbedoPngBytes != null && sub.AlbedoPngBytes.Length > 0
                    ? sub.AlbedoPngBytes
                    : CreateSolidColorPngBytes(64, 64, sub.AlbedoTint);
                File.WriteAllBytes(colorPngFull, albedoBytes);

                string normalPngRel = defaultNormalRel;
                if (sub.NormalPngBytes != null && sub.NormalPngBytes.Length > 0)
                {
                    string normalPngFile = $"{matName}_normal.png";
                    File.WriteAllBytes(Path.Combine(contentMatDir, normalPngFile), sub.NormalPngBytes);
                    normalPngRel = $"{materialsFolderRel}/{normalPngFile}";
                }

                string roughValueOrPath = defaultRoughRel;
                if (sub.RoughnessPngBytes != null && sub.RoughnessPngBytes.Length > 0)
                {
                    string roughPngFile = $"{matName}_rough.png";
                    File.WriteAllBytes(Path.Combine(contentMatDir, roughPngFile), sub.RoughnessPngBytes);
                    roughValueOrPath = $"{materialsFolderRel}/{roughPngFile}";
                }

                string aoValueOrPath = defaultWhiteAoRel;
                if (sub.AmbientOcclusionPngBytes != null && sub.AmbientOcclusionPngBytes.Length > 0)
                {
                    string aoPngFile = $"{matName}_ao.png";
                    File.WriteAllBytes(Path.Combine(contentMatDir, aoPngFile), sub.AmbientOcclusionPngBytes);
                    aoValueOrPath = $"{materialsFolderRel}/{aoPngFile}";
                }

                bool hasCustomSelfIllum = sub.SelfIllumPngBytes != null && sub.SelfIllumPngBytes.Length > 0;
                string selfIllumValueOrPath = defaultBlackMaskRel;
                if (hasCustomSelfIllum)
                {
                    string selfIllumPngFile = $"{matName}_selfillum.png";
                    File.WriteAllBytes(Path.Combine(contentMatDir, selfIllumPngFile), sub.SelfIllumPngBytes);
                    selfIllumValueOrPath = $"{materialsFolderRel}/{selfIllumPngFile}";
                }

                // Unlit / outline-shell materials require uniform self-illumination so map shadows do not crush flat-shaded anime or ink colors.
                if (sub.IsOutlineShell || sub.IsUnlit)
                {
                    selfIllumValueOrPath = defaultWhiteAoRel;
                }

                bool hasCustomTintMask = sub.TintMaskPngBytes != null && sub.TintMaskPngBytes.Length > 0;
                string tintMaskValueOrPath = defaultBlackMaskRel;
                if (hasCustomTintMask)
                {
                    string tintPngFile = $"{matName}_tintmask.png";
                    File.WriteAllBytes(Path.Combine(contentMatDir, tintPngFile), sub.TintMaskPngBytes);
                    tintMaskValueOrPath = $"{materialsFolderRel}/{tintPngFile}";
                }

                string shadowValueOrPath = defaultNprTransRel;
                if (sub.ShadowPngBytes != null && sub.ShadowPngBytes.Length > 0)
                {
                    string shadowPngFile = $"{matName}_shadow.png";
                    File.WriteAllBytes(Path.Combine(contentMatDir, shadowPngFile), sub.ShadowPngBytes);
                    shadowValueOrPath = $"{materialsFolderRel}/{shadowPngFile}";
                }

                string vmatFull = Path.Combine(contentMatDir, $"{matName}.vmat");
                string vmatContent = GenerateDeadlockPbrVmat(
                    colorPngRel,
                    normalPngRel,
                    roughValueOrPath,
                    sub.MetallicScalar,
                    aoValueOrPath,
                    sub.IsTranslucent,
                    sub.IsAlphaTest,
                    sub.IsDoubleSided && !sub.IsOutlineShell,
                    selfIllumValueOrPath,
                    tintMaskValueOrPath,
                    shadowValueOrPath,
                    sub.IsUnlit,
                    sub.IsOutlineShell,
                    hasCustomSelfIllum,
                    hasCustomTintMask,
                    defaultBlackMaskRel);
                File.WriteAllText(vmatFull, vmatContent, new UTF8Encoding(false));
                writtenVmatPaths.Add(vmatFull);

                matMap[matName] = $"{materialsFolderRel}/{matName}.vmat";
            }

            return matMap;
        }

        public static string GenerateDeadlockPbrVmat(
            string colorTexturePngRel,
            string normalTexturePngRel,
            string roughnessValueOrPngRel,
            float metallicScalar,
            string aoValueOrPngRel,
            bool isTranslucent = false,
            bool isAlphaTest = false,
            bool renderBackfaces = false,
            string selfIllumMaskRel = null,
            string tintMaskRel = null,
            string shadowMapRel = null,
            bool isUnlit = false,
            bool isOutlineShell = false,
            bool hasCustomSelfIllum = false,
            bool hasCustomTintMask = false,
            string blackMaskRel = null)
        {
            var ic = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.AppendLine("// THIS FILE IS AUTO-GENERATED BY DEADLOCK PLAYGROUND");
            sb.AppendLine();
            sb.AppendLine("Layer0");
            sb.AppendLine("{");
            sb.AppendLine("\tshader \"pbr.vfx\"");
            sb.AppendLine();
            sb.AppendLine("\t//---- Rendering & NPR Lighting ----");
            sb.AppendLine("\tF_USE_NPR_LIGHTING 1");
            sb.AppendLine("\tF_USE_STATUS_EFFECTS_PROXY 1");
            sb.AppendLine("\tF_WRITE_DEPTH_BEFORE_ALPHA_BLENDING 1");
            if (isOutlineShell)
            {
                sb.AppendLine("\tF_DISABLE_NPR_OUTLINE 1");
            }
            else if (!isTranslucent)
            {
                sb.AppendLine("\tF_SOLID_COLOR_OUTLINE 1");
            }
            if (isOutlineShell || isUnlit || hasCustomSelfIllum)
            {
                sb.AppendLine("\tF_SELF_ILLUM 1");
            }
            if (renderBackfaces)
            {
                sb.AppendLine("\tF_RENDER_BACKFACES 1");
            }
            sb.AppendLine();
            if (isTranslucent)
            {
                sb.AppendLine("\t//---- Translucent ----");
                sb.AppendLine("\tF_TRANSLUCENT 1");
                sb.AppendLine($"\tTextureTranslucency1 \"{colorTexturePngRel}\"");
                sb.AppendLine($"\tTextureTranslucency \"{colorTexturePngRel}\"");
                sb.AppendLine();
            }
            else if (isAlphaTest)
            {
                sb.AppendLine("\t//---- Alpha Test ----");
                sb.AppendLine("\tF_ALPHA_TEST 1");
                sb.AppendLine($"\tTextureTranslucency1 \"{colorTexturePngRel}\"");
                sb.AppendLine($"\tTextureTranslucency \"{colorTexturePngRel}\"");
                sb.AppendLine();
            }
            sb.AppendLine("\t//---- Color ----");
            sb.AppendLine(hasCustomTintMask ? "\tg_flModelTintAmount \"1.000\"" : "\tg_flModelTintAmount \"0.000\"");
            sb.AppendLine("\tg_vColorTint \"[1.000000 1.000000 1.000000 0.000000]\"");
            sb.AppendLine("\tg_vColorTint1 \"[1.000000 1.000000 1.000000 0.000000]\"");
            sb.AppendLine($"\tTextureColor1 \"{colorTexturePngRel}\"");
            sb.AppendLine($"\tTextureColor \"{colorTexturePngRel}\"");
            sb.AppendLine();
            sb.AppendLine("\t//---- Normal ----");
            sb.AppendLine($"\tTextureNormal1 \"{normalTexturePngRel}\"");
            sb.AppendLine($"\tTextureNormal \"{normalTexturePngRel}\"");
            sb.AppendLine();
            sb.AppendLine("\t//---- Roughness / Metalness / AO ----");
            sb.AppendLine($"\tTextureRoughness1 \"{roughnessValueOrPngRel}\"");
            sb.AppendLine($"\tTextureRoughness \"{roughnessValueOrPngRel}\"");
            sb.AppendLine(string.Format(ic, "\tTextureMetalness1 \"{0:F3}\"", metallicScalar));
            sb.AppendLine(string.Format(ic, "\tTextureMetalness \"[{0:F6} {0:F6} {0:F6} 0.000000]\"", metallicScalar));
            sb.AppendLine($"\tTextureAmbientOcclusion1 \"{aoValueOrPngRel}\"");
            sb.AppendLine($"\tTextureAmbientOcclusion \"{aoValueOrPngRel}\"");
            if (isOutlineShell || isUnlit || hasCustomSelfIllum)
            {
                sb.AppendLine("\t//---- Self Illum / Unlit ----");
                sb.AppendLine("\tg_flSelfIllumAlbedoFactor1 \"1.000\"");
                sb.AppendLine("\tg_flSelfIllumAlbedoFactor \"1.000\"");
                // Keep unlit albedo at 0.85 so anime/unlit skin does not clip under Deadlock's HDR bloom,
                // while explicit emissive masks and ink outline shells use 1.0.
                string siScale = (isUnlit && !isOutlineShell) ? "0.850" : "1.000";
                sb.AppendLine($"\tg_flSelfIllumScale1 \"{siScale}\"");
                sb.AppendLine($"\tg_flSelfIllumScale \"{siScale}\"");
            }
            if (!string.IsNullOrEmpty(selfIllumMaskRel))
            {
                sb.AppendLine($"\tTextureSelfIllumMask1 \"{selfIllumMaskRel}\"");
                sb.AppendLine($"\tTextureSelfIllumMask \"{selfIllumMaskRel}\"");
            }
            // Never route selfIllumMaskRel into NPR transmissive shadow color!
            if (!string.IsNullOrEmpty(shadowMapRel))
            {
                sb.AppendLine($"\tTextureNprTramsissiveColor1 \"{shadowMapRel}\"");
                sb.AppendLine($"\tTextureNprTransmissiveColor \"{shadowMapRel}\"");
            }
            string effectiveTintMask = hasCustomTintMask && !string.IsNullOrEmpty(tintMaskRel)
                ? tintMaskRel
                : blackMaskRel;
            string effectiveRimMask = (isOutlineShell || isUnlit || !hasCustomTintMask)
                ? blackMaskRel
                : tintMaskRel;
            if (!string.IsNullOrEmpty(effectiveTintMask))
            {
                sb.AppendLine($"\tTextureTintMask1 \"{effectiveTintMask}\"");
                sb.AppendLine($"\tTextureTintMask \"{effectiveTintMask}\"");
            }
            else
            {
                sb.AppendLine("\tTextureTintMask1 \"[0.000000 0.000000 0.000000 0.000000]\"");
                sb.AppendLine("\tTextureTintMask \"[0.000000 0.000000 0.000000 0.000000]\"");
            }
            if (!string.IsNullOrEmpty(effectiveRimMask))
            {
                sb.AppendLine($"\tTextureRimLightMask1 \"{effectiveRimMask}\"");
                sb.AppendLine($"\tTextureTintMaskRimLightMask \"{effectiveRimMask}\"");
            }
            else
            {
                sb.AppendLine("\tTextureRimLightMask1 \"[0.000000 0.000000 0.000000 0.000000]\"");
                sb.AppendLine("\tTextureTintMaskRimLightMask \"[0.000000 0.000000 0.000000 0.000000]\"");
            }
            if (!string.IsNullOrEmpty(blackMaskRel))
            {
                sb.AppendLine($"\tTextureNprOutlineMask1 \"{blackMaskRel}\"");
                sb.AppendLine($"\tTextureNprOutlineMask \"{blackMaskRel}\"");
            }
            sb.AppendLine("}");
            return sb.ToString();
        }

        private static byte[] CreateSolidColorPngBytes(int width, int height, Color color)
        {
            var img = Image.CreateEmpty(width, height, false, Image.Format.Rgba8);
            img.Fill(color);
            return img.SavePngToBuffer();
        }

        /// <summary>
        /// Sanitizes a material name for Source 2 (strips Blender .001 suffixes, spaces, and invalid path chars).
        /// </summary>
        public static string SanitizeMaterialName(string rawName)
        {
            if (string.IsNullOrWhiteSpace(rawName))
                return "custom_mat";

            string s = rawName.Trim();
            if (s.EndsWith(".vmat", StringComparison.OrdinalIgnoreCase))
                s = s[..^5];

            // Strip Blender numeric duplicate suffix (.001, .002)
            int dotIdx = s.LastIndexOf('.');
            if (dotIdx > 0 && dotIdx < s.Length - 1 && s[(dotIdx + 1)..].All(char.IsDigit))
            {
                s = s[..dotIdx];
            }

            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (char.IsLetterOrDigit(c) || c == '_')
                    sb.Append(char.ToLowerInvariant(c));
                else
                    sb.Append('_');
            }

            string clean = sb.ToString().Trim('_');
            while (clean.Contains("__"))
                clean = clean.Replace("__", "_");

            return string.IsNullOrEmpty(clean) ? "custom_mat" : clean;
        }

        /// <summary>
        /// Validates that a single file path to be packaged in the VPK matches the strict allowlist:
        /// must reside under models/, materials/, or particles/ and end with .vmdl_c, .vmesh_c, .vmat_c, .vtex_c, or .vpcf_c.
        /// </summary>
        public static bool ValidateAllowlist(string rawPath, out string rejectedReason)
        {
            rejectedReason = null;
            if (string.IsNullOrWhiteSpace(rawPath))
            {
                rejectedReason = "Empty VPK entry path.";
                return false;
            }

            string norm = rawPath.Replace('\\', '/').TrimStart('/');
            bool validPrefix = norm.StartsWith("models/", StringComparison.OrdinalIgnoreCase)
                            || norm.StartsWith("materials/", StringComparison.OrdinalIgnoreCase)
                            || norm.StartsWith("particles/", StringComparison.OrdinalIgnoreCase);

            bool validExt = norm.EndsWith(".vmdl_c", StringComparison.OrdinalIgnoreCase)
                         || norm.EndsWith(".vmesh_c", StringComparison.OrdinalIgnoreCase)
                         || norm.EndsWith(".vmat_c", StringComparison.OrdinalIgnoreCase)
                         || norm.EndsWith(".vtex_c", StringComparison.OrdinalIgnoreCase)
                         || norm.EndsWith(".vpcf_c", StringComparison.OrdinalIgnoreCase);

            if (!validPrefix || !validExt)
            {
                rejectedReason = $"Disallowed file in VPK: '{norm}' (only compiled .vmdl_c, .vmesh_c, .vmat_c, .vtex_c, .vpcf_c under models/, materials/, or particles/ are permitted).";
                return false;
            }

            return true;
        }

        /// <summary>
        /// Validates that every file path to be packaged in the VPK matches the strict allowlist.
        /// </summary>
        public static bool ValidateAllowlist(IEnumerable<string> relativeVpkPaths, out string rejectedReason)
        {
            rejectedReason = null;
            if (relativeVpkPaths == null) return true;

            foreach (var rawPath in relativeVpkPaths)
            {
                if (!ValidateAllowlist(rawPath, out rejectedReason))
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Overload matching (bundle, outputSmdPath) signature.
        /// </summary>
        public static void WriteSourceSpaceSmd(CustomModelExportBundle bundle, string outputSmdPath)
        {
            WriteSourceSpaceSmd(outputSmdPath, bundle);
        }

        /// <summary>
        /// Exports a live rigged Godot Node3D hierarchy (meshes, materials, textures, and skeleton)
        /// natively to a standard glTF 2.0 binary (.glb) file via GltfDocument.
        /// </summary>
        public static bool ExportSceneToGlb(Node3D rootNode, string outputGlbPath, out string errorMessage)
        {
            errorMessage = null;
            if (rootNode == null || !GodotObject.IsInstanceValid(rootNode) || string.IsNullOrWhiteSpace(outputGlbPath))
            {
                errorMessage = "Invalid scene root or output path.";
                return false;
            }

            try
            {
                string outDir = Path.GetDirectoryName(outputGlbPath);
                if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir))
                    Directory.CreateDirectory(outDir);

                var gltfDoc = new GltfDocument();
                var gltfState = new GltfState();
                var errAppend = gltfDoc.AppendFromScene(rootNode, gltfState);
                if (errAppend != Error.Ok)
                {
                    errorMessage = $"GltfDocument.AppendFromScene failed ({errAppend}).";
                    return false;
                }

                var errWrite = gltfDoc.WriteToFilesystem(gltfState, outputGlbPath);
                if (errWrite != Error.Ok)
                {
                    errorMessage = $"GltfDocument.WriteToFilesystem failed ({errWrite}).";
                    return false;
                }

                GD.Print($"[CustomModelExporter] Exported native GLB -> {outputGlbPath}");
                return true;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                GD.PrintErr($"[CustomModelExporter] ExportSceneToGlb failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Emits a valid binary glTF 2.0 (.glb) file containing the bundle's Source-space geometry,
        /// normals, UVs, indices, and node/mesh definitions.
        /// </summary>
        public static void WriteSourceSpaceGlb(CustomModelExportBundle bundle, string outputGlbPath)
        {
            if (bundle == null || string.IsNullOrWhiteSpace(outputGlbPath)) return;
            string outDir = Path.GetDirectoryName(outputGlbPath);
            if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir))
                Directory.CreateDirectory(outDir);

            using var binStream = new MemoryStream();
            using var bw = new BinaryWriter(binStream);
            var bufferViews = new List<object>();
            var accessors = new List<object>();
            var gltfMeshes = new List<object>();
            var gltfNodes = new List<object>();
            var sceneNodes = new List<int>();

            void Align4()
            {
                while (binStream.Position % 4 != 0) bw.Write((byte)0);
            }

            foreach (var sm in bundle.Submeshes)
            {
                if (sm?.Vertices == null || sm.Vertices.Length == 0) continue;
                int vCount = sm.Vertices.Length;

                Align4();
                int posOff = (int)binStream.Position;
                Vector3 minV = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                Vector3 maxV = new Vector3(float.MinValue, float.MinValue, float.MinValue);
                for (int i = 0; i < vCount; i++)
                {
                    Vector3 v = GodotToSourcePosition(sm.Vertices[i]);
                    minV = minV.Min(v);
                    maxV = maxV.Max(v);
                    bw.Write(v.X); bw.Write(v.Y); bw.Write(v.Z);
                }
                int posLen = (int)binStream.Position - posOff;
                int bvPos = bufferViews.Count;
                bufferViews.Add(new { buffer = 0, byteOffset = posOff, byteLength = posLen, target = 34962 });
                int accPos = accessors.Count;
                accessors.Add(new
                {
                    bufferView = bvPos,
                    componentType = 5126,
                    count = vCount,
                    type = "VEC3",
                    min = new[] { minV.X, minV.Y, minV.Z },
                    max = new[] { maxV.X, maxV.Y, maxV.Z }
                });

                int accNorm = -1;
                if (sm.Normals != null && sm.Normals.Length == vCount)
                {
                    Align4();
                    int nOff = (int)binStream.Position;
                    for (int i = 0; i < vCount; i++)
                    {
                        Vector3 n = GodotToSourceNormal(sm.Normals[i]);
                        bw.Write(n.X); bw.Write(n.Y); bw.Write(n.Z);
                    }
                    int nLen = (int)binStream.Position - nOff;
                    int bvN = bufferViews.Count;
                    bufferViews.Add(new { buffer = 0, byteOffset = nOff, byteLength = nLen, target = 34962 });
                    accNorm = accessors.Count;
                    accessors.Add(new { bufferView = bvN, componentType = 5126, count = vCount, type = "VEC3" });
                }

                int accUv = -1;
                if (sm.UVs != null && sm.UVs.Length == vCount)
                {
                    Align4();
                    int uOff = (int)binStream.Position;
                    for (int i = 0; i < vCount; i++)
                    {
                        bw.Write(sm.UVs[i].X); bw.Write(sm.UVs[i].Y);
                    }
                    int uLen = (int)binStream.Position - uOff;
                    int bvU = bufferViews.Count;
                    bufferViews.Add(new { buffer = 0, byteOffset = uOff, byteLength = uLen, target = 34962 });
                    accUv = accessors.Count;
                    accessors.Add(new { bufferView = bvU, componentType = 5126, count = vCount, type = "VEC2" });
                }

                int accIdx = -1;
                if (sm.Indices != null && sm.Indices.Length > 0)
                {
                    Align4();
                    int iOff = (int)binStream.Position;
                    for (int i = 0; i < sm.Indices.Length; i++) bw.Write((uint)sm.Indices[i]);
                    int iLen = (int)binStream.Position - iOff;
                    int bvI = bufferViews.Count;
                    bufferViews.Add(new { buffer = 0, byteOffset = iOff, byteLength = iLen, target = 34963 });
                    accIdx = accessors.Count;
                    accessors.Add(new { bufferView = bvI, componentType = 5125, count = sm.Indices.Length, type = "SCALAR" });
                }

                var attrs = new Dictionary<string, int> { ["POSITION"] = accPos };
                if (accNorm >= 0) attrs["NORMAL"] = accNorm;
                if (accUv >= 0) attrs["TEXCOORD_0"] = accUv;

                var prim = new Dictionary<string, object> { ["attributes"] = attrs };
                if (accIdx >= 0) prim["indices"] = accIdx;

                int meshIdx = gltfMeshes.Count;
                gltfMeshes.Add(new { name = string.IsNullOrEmpty(sm.SubmeshName) ? $"Mesh_{meshIdx}" : sm.SubmeshName, primitives = new[] { prim } });
                int nodeIdx = gltfNodes.Count;
                gltfNodes.Add(new { name = string.IsNullOrEmpty(sm.SubmeshName) ? $"Node_{meshIdx}" : sm.SubmeshName, mesh = meshIdx });
                sceneNodes.Add(nodeIdx);
            }

            Align4();
            byte[] binChunkData = binStream.ToArray();
            var gltfObj = new
            {
                asset = new { version = "2.0", generator = "DeadlockPlayground" },
                scene = 0,
                scenes = new[] { new { nodes = sceneNodes } },
                nodes = gltfNodes,
                meshes = gltfMeshes,
                buffers = new[] { new { byteLength = binChunkData.Length } },
                bufferViews,
                accessors
            };
            string jsonText = System.Text.Json.JsonSerializer.Serialize(gltfObj);
            byte[] jsonBytes = Encoding.UTF8.GetBytes(jsonText);
            int jsonPad = (4 - (jsonBytes.Length % 4)) % 4;
            int jsonChunkLen = jsonBytes.Length + jsonPad;

            uint totalLength = (uint)(12 + 8 + jsonChunkLen + 8 + binChunkData.Length);

            using var fs = new FileStream(outputGlbPath, FileMode.Create, System.IO.FileAccess.Write);
            using var glbWriter = new BinaryWriter(fs);
            glbWriter.Write(0x46546C67u);
            glbWriter.Write(2u);
            glbWriter.Write(totalLength);
            glbWriter.Write((uint)jsonChunkLen);
            glbWriter.Write(0x4E4F534Au);
            glbWriter.Write(jsonBytes);
            for (int i = 0; i < jsonPad; i++) glbWriter.Write((byte)0x20);
            glbWriter.Write((uint)binChunkData.Length);
            glbWriter.Write(0x004E4942u);
            glbWriter.Write(binChunkData);
        }

        private static bool RunResourceCompiler(string compilerExe, string inputFilePath, out string logOutput)
        {
            logOutput = "";
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = compilerExe,
                    Arguments = $"-i \"{inputFilePath}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(compilerExe) ?? ""
                };

                using var proc = Process.Start(psi);
                if (proc == null) return false;

                string stdout = proc.StandardOutput.ReadToEnd();
                string stderr = proc.StandardError.ReadToEnd();
                proc.WaitForExit(120_000);

                logOutput = stdout + "\n" + stderr;
                return proc.ExitCode == 0;
            }
            catch (Exception ex)
            {
                logOutput = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Fallback for legacy CSDK12 compilers that lack NmSkeletonList / AnimGraph2List classes:
        /// logs guidance to upgrade to CSDK13 where NmSkeletonList and AnimGraph2List compile natively.
        /// </summary>
        private static void InjectAnimGraph2ReferencesIntoCompiledVmdlC(
            string compiledVmdlCPath,
            CustomModelExportBundle bundle,
            string vmdlCPath)
        {
            GD.Print($"[CustomModelExporter] Legacy CSDK12 fallback compiled '{compiledVmdlCPath}' for '{vmdlCPath}'. Recommend CSDK13 for native AnimGraph2 compilation.");
        }

        private static void WriteStandaloneVpk(string outputVpkPath, Dictionary<string, byte[]> entries)
        {
            string outDir = Path.GetDirectoryName(outputVpkPath);
            if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir))
                Directory.CreateDirectory(outDir);

            using var pkg = new Package();
            foreach (var kv in entries)
            {
                string normPath = kv.Key.Replace('\\', '/').TrimStart('/');
                pkg.AddFile(normPath, kv.Value);
            }
            pkg.Write(outputVpkPath);
        }
    }
}
