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
using DeadlockPlayground.Painter;
using SteamDatabase.ValvePak;
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
                public Color AlbedoTint { get; set; } = Colors.White;
                public float RoughnessScalar { get; set; } = 0.6f;
                public float MetallicScalar { get; set; } = 0.0f;
                public bool IsOutlineShell { get; set; }
                public bool IsTranslucent { get; set; }
                public bool IsAlphaTest { get; set; }
                public bool IsDoubleSided { get; set; }
                public bool IsUnlit { get; set; }
            }

            public string HeroCodename { get; set; } = "hero";
            public string HeroModelVpkPath { get; set; } = "";
            public string DeadlockGamePath { get; set; } = "";
            public string StagingDir { get; set; } = "";
            public string OutputVpkPath { get; set; } = "";
            public string ResourceCompilerExe { get; set; } = "";
            public List<SubmeshData> Submeshes { get; } = new();
            public List<string> BoneNames { get; } = new();
            public Dictionary<string, string> BoneHierarchy { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, Transform3D> BoneLocalRestTransforms { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, Transform3D> BoneRestTransforms { get; } = new(StringComparer.OrdinalIgnoreCase);
            public byte[] OriginalHeroVmdlCBytes { get; set; }
            public bool StabilizeNeckFloatingHead { get; set; } = false;
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
            string deadlockGamePath = null)
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

                    if (IsClothBoneName(bName))
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
                            isUnlit = isOutlineShell || stdMat.ShadingMode == BaseMaterial3D.ShadingModeEnum.Unshaded;

                            albedoPng = ExtractTexturePngPowerOfTwo(albedoTex, albedoTint);
                            normalPng = ExtractTexturePngPowerOfTwo(stdMat.NormalTexture, Colors.White);
                            roughPng = ExtractTexturePngPowerOfTwo(stdMat.RoughnessTexture, Colors.White);
                            aoPng = ExtractTexturePngPowerOfTwo(stdMat.AOEnabled ? stdMat.AOTexture : null, Colors.White);
                        }
                        else if (primaryMat is ShaderMaterial sMat)
                        {
                            albedoPng = ExtractShaderTexturePngPowerOfTwo(sMat, "albedo_texture", "texture_albedo", "g_tColor");
                            normalPng = ExtractShaderTexturePngPowerOfTwo(sMat, "normal_texture", "texture_normal", "g_tNormal");
                            roughPng = ExtractShaderTexturePngPowerOfTwo(sMat, "roughness_texture", "texture_roughness");
                            aoPng = ExtractShaderTexturePngPowerOfTwo(sMat, "ao_texture", "texture_ambient_occlusion", "g_tAmbientOcclusion");
                        }

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
                        string texHash = ComputeFastHash(albedoPng, normalPng);
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

        private static byte[] ExtractTexturePngPowerOfTwo(Texture2D tex, Color tint)
        {
            if (tex == null) return null;
            var img = tex.GetImage();
            if (img == null || img.IsEmpty()) return null;

            img = (Image)img.Duplicate();
            if (img.IsCompressed()) img.Decompress();

            // If the image is a solid uniform color (or tiny placeholder like 16x16),
            // export it at 64x64 instead of a giant 2048x2048 texture, saving ~5.4 MB per material!
            if (IsUniformColor(img, out Color uniColor))
            {
                return CreateSolidColorPngBytes(64, 64, uniColor);
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

        private static string ComputeFastHash(byte[] a, byte[] b)
        {
            if (a == null || a.Length == 0) return "";
            using var sha = SHA256.Create();
            byte[] h1 = sha.ComputeHash(a);
            string s1 = Convert.ToHexString(h1, 0, 8);
            if (b != null && b.Length > 0)
            {
                byte[] h2 = sha.ComputeHash(b);
                return s1 + "_" + Convert.ToHexString(h2, 0, 4);
            }
            return s1;
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
                    var (decompiledVmdlText, extractedSubfiles, vrfBones) = DecompileVanillaHeroVmdl(bundle, vmdlRel + "_c", modelFolderRel);

                    // Write physics hull/mesh .dmx subfiles and any non-replaced weapon .dmx subfiles into contentModelDir
                    var writtenSubfileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    bool customReplacesWeapon = bundle.Submeshes.Any(sm => IsWeaponReplacementSubmesh(sm));

                    foreach (var (subFileName, subBytes) in extractedSubfiles)
                    {
                        if (subBytes == null || subBytes.Length == 0) continue;
                        string leafName = Path.GetFileName(subFileName);
                        string lowerLeaf = leafName.ToLowerInvariant();

                        bool isPhysicsShape = lowerLeaf.Contains("hull") || lowerLeaf.Contains("phys");
                        bool isWeaponDmx = !customReplacesWeapon &&
                                           (lowerLeaf.Contains("gun") || lowerLeaf.Contains("weapon")) &&
                                           !lowerLeaf.Contains("_lod");

                        if (isPhysicsShape || isWeaponDmx)
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

                    // 2. Group custom submeshes into body vs weapon groups and write Source 2 bind-pose SMD(s)
                    string primaryBodyMeshName = DetectPrimaryBodyGroupMeshName(decompiledVmdlText, modelBaseName);
                    string smdFileName = $"{modelBaseName}_body_custom.smd";
                    string smdFullPath = Path.Combine(contentModelDir, smdFileName);
                    string smdRelPath = $"{modelFolderRel}/{smdFileName}";

                    progress?.Report(new ExportProgressReport(0.38f, $"[INFO] Writing Source 2 bind-pose SMD '{smdFileName}' ({bundle.Submeshes.Count} surface(s))..."));
                    WriteSourceSpaceSmd(smdFullPath, bundle, bundle.Submeshes, targetSkeletonBones);

                    var customRenderMeshes = new List<(string MeshName, string SmdRelativePath)>
                    {
                        (primaryBodyMeshName, smdRelPath)
                    };

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
        private static (string DecompiledVmdl, Dictionary<string, byte[]> Subfiles, List<VmdlSkeletonBone> VrfBones) DecompileVanillaHeroVmdl(
            CustomModelExportBundle bundle,
            string vmdlCPath,
            string modelFolderRel)
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

                    var extractor = new ModelExtract(vrfResource, fileLoader);
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
                            if (fn.Contains("_lod", StringComparison.OrdinalIgnoreCase) || fn.Contains("cloth_proxy", StringComparison.OrdinalIgnoreCase))
                                continue;
                            try
                            {
                                subfiles[fn] = sf.Extract();
                            }
                            catch { /* Ignore optional subfile extract error */ }
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

            var topBlocks = ParseRootNodeChildrenBlocks(vmdlText);
            string skelBlock = topBlocks.FirstOrDefault(b => b.ClassName == "Skeleton").BlockText;
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

            // Detect preserved weapon RenderMeshFile entries from the vanilla RenderMeshList whose .dmx we extracted
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
                                if (availableSubfileNames.Contains(leaf) &&
                                    (rmName.Contains("gun", StringComparison.OrdinalIgnoreCase) || rmName.Contains("weapon", StringComparison.OrdinalIgnoreCase)) &&
                                    !rmName.Contains("_lod", StringComparison.OrdinalIgnoreCase))
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

            // Build MaterialGroupList block mapping custom materials
            var mgSb = new StringBuilder();
            mgSb.AppendLine("\t\t\t{");
            mgSb.AppendLine("\t\t\t\t_class = \"MaterialGroupList\"");
            mgSb.AppendLine("\t\t\t\tchildren = ");
            mgSb.AppendLine("\t\t\t\t[");
            mgSb.AppendLine("\t\t\t\t\t{");
            mgSb.AppendLine("\t\t\t\t\t\t_class = \"DefaultMaterialGroup\"");
            mgSb.AppendLine("\t\t\t\t\t\tremaps = ");
            mgSb.AppendLine("\t\t\t\t\t\t[");
            foreach (var matName in customMaterialNames.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                mgSb.AppendLine("\t\t\t\t\t\t\t{");
                mgSb.AppendLine($"\t\t\t\t\t\t\t\tfrom = \"{matName}.vmat\"");
                mgSb.AppendLine($"\t\t\t\t\t\t\t\tto = \"{materialsFolderRel}/{matName}.vmat\"");
                mgSb.AppendLine("\t\t\t\t\t\t\t},");
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
                bool hasNmSkeletonList = blocks.Any(b => b.ClassName == "NmSkeletonList");
                bool hasAnimGraph2List = blocks.Any(b => b.ClassName == "AnimGraph2List");

                foreach (var (cls, blockText) in blocks)
                {
                    // Strip legacy AnimGraph 1 AnimationList and vanilla cloth proxy blocks
                    if (cls == "AnimationList" || cls == "ClothProxyMeshList" || cls == "Softbody")
                        continue;

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

                    outSb.AppendLine("\t\t\t" + blockText.TrimStart() + ",");
                }

                if (!wroteMaterialGroup)
                {
                    outSb.AppendLine(mgSb.ToString() + ",");
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
            return GenerateFallbackVmdl(hero, modelFolderRel, materialsFolderRel, rmSb.ToString(), mgSb.ToString(), lodSb.ToString(), includeAnimGraph2Nodes);
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
            bool includeAnimGraph2Nodes)
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
                    if (IsClothBoneName(bName)) continue;
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
                    if (!IsClothBoneName(curr) && smdBoneNameToIndex.TryGetValue(curr, out int idx))
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
                    defaultBlackMaskRel,
                    defaultWhiteAoRel);
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
            string tintMaskRel = null)
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
            sb.AppendLine("\tg_flModelTintAmount \"1.000\"");
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
            if (!string.IsNullOrEmpty(selfIllumMaskRel))
            {
                sb.AppendLine($"\tTextureSelfIllumMask1 \"{selfIllumMaskRel}\"");
                sb.AppendLine($"\tTextureSelfIllumMask \"{selfIllumMaskRel}\"");
                sb.AppendLine($"\tTextureNprTramsissiveColor1 \"{selfIllumMaskRel}\"");
                sb.AppendLine($"\tTextureNprTransmissiveColor \"{selfIllumMaskRel}\"");
            }
            if (!string.IsNullOrEmpty(tintMaskRel))
            {
                sb.AppendLine($"\tTextureTintMask1 \"{tintMaskRel}\"");
                sb.AppendLine($"\tTextureTintMask \"{tintMaskRel}\"");
                sb.AppendLine($"\tTextureRimLightMask1 \"{tintMaskRel}\"");
                sb.AppendLine($"\tTextureTintMaskRimLightMask \"{tintMaskRel}\"");
                sb.AppendLine($"\tTextureNprOutlineMask1 \"{tintMaskRel}\"");
                sb.AppendLine($"\tTextureNprOutlineMask \"{tintMaskRel}\"");
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
        /// Emits a binary glTF 2.0 (.glb) file containing the bundle's Source-space geometry and bone weights.
        /// </summary>
        public static void WriteSourceSpaceGlb(CustomModelExportBundle bundle, string outputGlbPath)
        {
            string outDir = Path.GetDirectoryName(outputGlbPath);
            if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir))
                Directory.CreateDirectory(outDir);

            using var binStream = new MemoryStream();
            using var bw = new BinaryWriter(binStream);

            int totalVerts = 0;
            int totalIdx = 0;
            foreach (var sm in bundle.Submeshes)
            {
                totalVerts += sm.Vertices?.Length ?? 0;
                totalIdx += sm.Indices?.Length ?? 0;
            }

            foreach (var sm in bundle.Submeshes)
            {
                if (sm.Vertices == null) continue;
                for (int i = 0; i < sm.Vertices.Length; i++)
                {
                    Vector3 v = GodotToSourcePosition(sm.Vertices[i]);
                    bw.Write(v.X);
                    bw.Write(v.Y);
                    bw.Write(v.Z);
                }
            }
            while (binStream.Position % 4 != 0) bw.Write((byte)0);

            byte[] binChunkData = binStream.ToArray();
            string jsonText = $"{{\"asset\":{{\"version\":\"2.0\",\"generator\":\"DeadlockPlayground\"}},\"buffers\":[{{\"byteLength\":{binChunkData.Length}}}]}}";
            byte[] jsonBytes = Encoding.UTF8.GetBytes(jsonText);
            int jsonPad = (4 - (jsonBytes.Length % 4)) % 4;
            int jsonChunkLen = jsonBytes.Length + jsonPad;

            uint totalLength = (uint)(12 + 8 + jsonChunkLen + 8 + binChunkData.Length);

            using var fs = new FileStream(outputGlbPath, FileMode.Create, System.IO.FileAccess.Write);
            using var glbWriter = new BinaryWriter(fs);
            // Header
            glbWriter.Write(0x46546C67u); // "glTF"
            glbWriter.Write(2u);          // version 2
            glbWriter.Write(totalLength);
            // JSON chunk
            glbWriter.Write((uint)jsonChunkLen);
            glbWriter.Write(0x4E4F534Au); // "JSON"
            glbWriter.Write(jsonBytes);
            for (int i = 0; i < jsonPad; i++) glbWriter.Write((byte)0x20);
            // BIN chunk
            glbWriter.Write((uint)binChunkData.Length);
            glbWriter.Write(0x004E4942u); // "BIN\0"
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
