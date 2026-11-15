using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace DeadlockPlayground.Workspace
{
    public class WorkspaceManifest
    {
        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        [JsonPropertyName("last_active_hero_id")]
        public string LastActiveHeroId { get; set; } = "";

        [JsonPropertyName("last_active_asset_type")]
        public string LastActiveAssetType { get; set; } = "CharacterModel";

        [JsonPropertyName("last_active_asset_path")]
        public string LastActiveAssetPath { get; set; } = "";

        [JsonPropertyName("heroes")]
        public Dictionary<string, WorkspaceHeroEntry> Heroes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public class WorkspaceHeroEntry
    {
        [JsonPropertyName("hero_id")]
        public string HeroId { get; set; } = "";

        [JsonPropertyName("display_name")]
        public string DisplayName { get; set; } = "";

        [JsonPropertyName("source_addon_vpk_path")]
        public string SourceAddonVpkPath { get; set; } = "";

        [JsonPropertyName("assets")]
        public List<WorkspaceAssetEntry> Assets { get; set; } = new();

        [JsonPropertyName("custom_model")]
        public WorkspaceCustomModelEntry CustomModel { get; set; } = null;

        [JsonPropertyName("accessories")]
        public List<WorkspaceAccessoryEntry> Accessories { get; set; } = new();
    }

    public class WorkspaceAccessoryEntry
    {
        [JsonPropertyName("slot_id")]
        public string SlotId { get; set; } = "";

        [JsonPropertyName("display_name")]
        public string DisplayName { get; set; } = "";

        [JsonPropertyName("intent")]
        public int Intent { get; set; }

        [JsonPropertyName("visible")]
        public bool Visible { get; set; } = true;

        [JsonPropertyName("model_config")]
        public WorkspaceCustomModelEntry ModelConfig { get; set; }
    }

    public class WorkspaceSkinWeightOverrideEntry
    {
        [JsonPropertyName("vertex_count")]
        public int VertexCount { get; set; }

        [JsonPropertyName("bone_palette")]
        public string[] BonePalette { get; set; } = Array.Empty<string>();

        [JsonPropertyName("palette_indices")]
        public int[] PaletteIndices { get; set; } = Array.Empty<int>();

        [JsonPropertyName("weights")]
        public float[] Weights { get; set; } = Array.Empty<float>();
    }

    public class WorkspaceHeroSculptEntry
    {
        [JsonPropertyName("vertex_indices")]
        public int[] VertexIndices { get; set; } = Array.Empty<int>();

        [JsonPropertyName("deltas_xyz")]
        public float[] DeltasXyz { get; set; } = Array.Empty<float>();
    }

    public class WorkspaceCustomModelEntry
    {
        [JsonPropertyName("relative_model_path")]
        public string RelativeModelPath { get; set; } = "";

        [JsonPropertyName("original_file_name")]
        public string OriginalFileName { get; set; } = "";

        [JsonPropertyName("slot_id")]
        public string SlotId { get; set; } = "";

        [JsonPropertyName("accessory_name")]
        public string AccessoryName { get; set; } = "";

        [JsonPropertyName("intent")]
        public int Intent { get; set; }

        [JsonPropertyName("explicit_replaced_targets")]
        public List<string> ExplicitReplacedTargets { get; set; } = new();

        [JsonPropertyName("method")]
        public int Method { get; set; }

        [JsonPropertyName("preset")]
        public int Preset { get; set; }

        [JsonPropertyName("mode")]
        public int Mode { get; set; }

        [JsonPropertyName("rigid_bone_name")]
        public string RigidBoneName { get; set; } = "head";

        [JsonPropertyName("custom_arm_angle_deg")]
        public float CustomArmAngleDeg { get; set; }

        [JsonPropertyName("custom_arm_forward_deg")]
        public float CustomArmForwardDeg { get; set; }

        [JsonPropertyName("custom_elbow_straighten_pct")]
        public float CustomElbowStraightenPct { get; set; }

        [JsonPropertyName("custom_leg_spread_deg")]
        public float CustomLegSpreadDeg { get; set; }

        [JsonPropertyName("auto_snap_arms")]
        public bool AutoSnapArmsToHero { get; set; } = true;

        [JsonPropertyName("auto_snap_legs")]
        public bool AutoSnapLegsToHero { get; set; } = true;

        [JsonPropertyName("auto_snap_head")]
        public bool AutoSnapHeadToHero { get; set; } = true;

        [JsonPropertyName("smoothing_iterations")]
        public int SmoothingIterations { get; set; } = 1;

        [JsonPropertyName("search_radius")]
        public float SearchRadius { get; set; } = 0.20f;

        [JsonPropertyName("k_nearest")]
        public int KNearest { get; set; } = 4;

        [JsonPropertyName("exclude_cloth_hair_bones")]
        public bool ExcludeClothHairBones { get; set; } = true;

        [JsonPropertyName("keep_fingers_rigid")]
        public bool KeepFingersRigidOnHand { get; set; } = false;

        [JsonPropertyName("clamp_limb_stretching")]
        public bool ClampLimbStretching { get; set; } = true;

        [JsonPropertyName("stabilize_neck_floating_head")]
        public bool StabilizeNeckFloatingHead { get; set; } = true;

        [JsonPropertyName("auto_counter_twist_weights")]
        public bool AutoCounterTwistWeights { get; set; } = true;

        [JsonPropertyName("include_baked_outline_shells")]
        public bool IncludeBakedOutlineShells { get; set; } = true;

        [JsonPropertyName("root_position")]
        public float[] RootPosition { get; set; } = new float[] { 0f, 0f, 0f };

        [JsonPropertyName("root_rotation_deg")]
        public float[] RootRotationDeg { get; set; } = new float[] { 0f, 0f, 0f };

        [JsonPropertyName("root_scale")]
        public float RootScale { get; set; } = 1.0f;

        [JsonPropertyName("root_scale_xyz")]
        public float[] RootScaleXyz { get; set; } = new float[] { 1f, 1f, 1f };

        [JsonPropertyName("orig_deform_enabled")]
        public bool OriginalMeshDeformEnabled { get; set; } = false;

        [JsonPropertyName("orig_deform_target")]
        public string OriginalMeshDeformTargetFilter { get; set; } = "hair";

        [JsonPropertyName("orig_deform_bone")]
        public string OriginalMeshDeformBoneName { get; set; } = "head";

        [JsonPropertyName("orig_deform_mode")]
        public int OriginalMeshDeformMode { get; set; } = 0;

        [JsonPropertyName("orig_deform_strength")]
        public float OriginalMeshDeformStrength { get; set; } = 0.55f;

        [JsonPropertyName("orig_deform_radius")]
        public float OriginalMeshDeformRadius { get; set; } = 0.24f;

        [JsonPropertyName("orig_deform_height_offset")]
        public float OriginalMeshDeformHeightOffset { get; set; } = 0.06f;

        [JsonPropertyName("orig_deform_sculpts")]
        public Dictionary<string, WorkspaceHeroSculptEntry> OriginalMeshDeformSculpts { get; set; } = new();

        [JsonPropertyName("aligned_transform")]
        public float[] AlignedTransformFloats { get; set; }

        [JsonPropertyName("included_imported_submeshes")]
        public List<string> IncludedImportedSubmeshNames { get; set; } = new();

        [JsonPropertyName("kept_original_submeshes")]
        public List<string> KeptOriginalSubmeshNames { get; set; } = new();

        [JsonPropertyName("submesh_attachment_sockets")]
        public Dictionary<string, string> SubmeshAttachmentSockets { get; set; } = new();

        [JsonPropertyName("submesh_replacement_map")]
        public Dictionary<string, string> SubmeshReplacementMap { get; set; } = new();

        [JsonPropertyName("submesh_snap_to_socket")]
        public Dictionary<string, bool> SubmeshSnapToSocket { get; set; } = new();

        [JsonPropertyName("submesh_socket_offset_pos")]
        public Dictionary<string, float[]> SubmeshSocketOffsetPos { get; set; } = new();

        [JsonPropertyName("submesh_socket_offset_rot")]
        public Dictionary<string, float[]> SubmeshSocketOffsetRot { get; set; } = new();

        [JsonPropertyName("submesh_socket_scale")]
        public Dictionary<string, float> SubmeshSocketScale { get; set; } = new();

        [JsonPropertyName("submesh_is_outline_shell")]
        public Dictionary<string, bool> SubmeshIsOutlineShell { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        [JsonPropertyName("custom_bone_remap")]
        public Dictionary<string, string> CustomBoneRemap { get; set; } = new();

        [JsonPropertyName("landmark_pins")]
        public Dictionary<string, float[]> LandmarkPins { get; set; }

        [JsonPropertyName("skin_weight_overrides")]
        public Dictionary<string, WorkspaceSkinWeightOverrideEntry> CustomSkinWeightOverrides { get; set; }

        public static WorkspaceCustomModelEntry FromRiggingOptions(
            string relativeModelPath,
            string originalFileName,
            DeadlockPlayground.Importer.CustomModelAutoRigger.RiggingOptions o)
        {
            o ??= new DeadlockPlayground.Importer.CustomModelAutoRigger.RiggingOptions();
            var t = o.AlignedTransform;
            var entry = new WorkspaceCustomModelEntry
            {
                RelativeModelPath = relativeModelPath ?? "",
                OriginalFileName = originalFileName ?? "",
                SlotId = o.SlotId ?? "",
                AccessoryName = o.AccessoryName ?? "",
                Intent = (int)o.Intent,
                ExplicitReplacedTargets = o.ExplicitReplacedTargets != null ? new List<string>(o.ExplicitReplacedTargets) : new(),
                Method = (int)o.Method,
                Preset = (int)o.Preset,
                Mode = (int)o.Mode,
                RigidBoneName = o.RigidBoneName ?? "head",
                CustomArmAngleDeg = o.CustomArmAngleDeg,
                CustomArmForwardDeg = o.CustomArmForwardDeg,
                CustomElbowStraightenPct = o.CustomElbowStraightenPct,
                CustomLegSpreadDeg = o.CustomLegSpreadDeg,
                AutoSnapArmsToHero = o.AutoSnapArmsToHero,
                AutoSnapLegsToHero = o.AutoSnapLegsToHero,
                AutoSnapHeadToHero = o.AutoSnapHeadToHero,
                SmoothingIterations = o.SmoothingIterations,
                SearchRadius = o.SearchRadius,
                KNearest = o.KNearest,
                ExcludeClothHairBones = o.ExcludeClothHairBones,
                KeepFingersRigidOnHand = o.KeepFingersRigidOnHand,
                ClampLimbStretching = o.ClampLimbStretching,
                StabilizeNeckFloatingHead = o.StabilizeNeckFloatingHead,
                AutoCounterTwistWeights = o.AutoCounterTwistWeights,
                IncludeBakedOutlineShells = o.IncludeBakedOutlineShells,
                RootPosition = new[] { o.RootPosition.X, o.RootPosition.Y, o.RootPosition.Z },
                RootRotationDeg = new[] { o.RootRotationDeg.X, o.RootRotationDeg.Y, o.RootRotationDeg.Z },
                RootScale = o.RootScale,
                RootScaleXyz = new[] { o.RootScaleXYZ.X, o.RootScaleXYZ.Y, o.RootScaleXYZ.Z },
                OriginalMeshDeformEnabled = o.OriginalMeshDeformEnabled,
                OriginalMeshDeformTargetFilter = o.OriginalMeshDeformTargetFilter ?? "hair",
                OriginalMeshDeformBoneName = o.OriginalMeshDeformBoneName ?? "head",
                OriginalMeshDeformMode = (int)o.OriginalMeshDeformMode,
                OriginalMeshDeformStrength = o.OriginalMeshDeformStrength,
                OriginalMeshDeformRadius = o.OriginalMeshDeformRadius,
                OriginalMeshDeformHeightOffset = o.OriginalMeshDeformHeightOffset,
                AlignedTransformFloats = new[]
                {
                    t.Basis.X.X, t.Basis.X.Y, t.Basis.X.Z,
                    t.Basis.Y.X, t.Basis.Y.Y, t.Basis.Y.Z,
                    t.Basis.Z.X, t.Basis.Z.Y, t.Basis.Z.Z,
                    t.Origin.X, t.Origin.Y, t.Origin.Z
                },
                IncludedImportedSubmeshNames = o.IncludedImportedSubmeshNames != null ? new List<string>(o.IncludedImportedSubmeshNames) : new(),
                KeptOriginalSubmeshNames = o.KeptOriginalSubmeshNames != null ? new List<string>(o.KeptOriginalSubmeshNames) : new(),
                SubmeshAttachmentSockets = o.SubmeshAttachmentSockets != null ? new Dictionary<string, string>(o.SubmeshAttachmentSockets) : new(),
                SubmeshReplacementMap = o.SubmeshReplacementMap != null ? new Dictionary<string, string>(o.SubmeshReplacementMap) : new(),
                SubmeshSnapToSocket = o.SubmeshSnapToSocket != null ? new Dictionary<string, bool>(o.SubmeshSnapToSocket) : new(),
                SubmeshSocketScale = o.SubmeshSocketScale != null ? new Dictionary<string, float>(o.SubmeshSocketScale) : new(),
                SubmeshIsOutlineShell = o.SubmeshIsOutlineShell != null ? new Dictionary<string, bool>(o.SubmeshIsOutlineShell, StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase),
                CustomBoneRemap = o.CustomBoneRemap != null ? new Dictionary<string, string>(o.CustomBoneRemap) : new()
            };

            if (o.SubmeshSocketOffsetPos != null)
            {
                foreach (var kv in o.SubmeshSocketOffsetPos)
                    entry.SubmeshSocketOffsetPos[kv.Key] = new[] { kv.Value.X, kv.Value.Y, kv.Value.Z };
            }
            if (o.SubmeshSocketOffsetRot != null)
            {
                foreach (var kv in o.SubmeshSocketOffsetRot)
                    entry.SubmeshSocketOffsetRot[kv.Key] = new[] { kv.Value.X, kv.Value.Y, kv.Value.Z };
            }
            if (o.LandmarkPins != null)
            {
                entry.LandmarkPins = new Dictionary<string, float[]>();
                foreach (var kv in o.LandmarkPins)
                    entry.LandmarkPins[kv.Key] = new[] { kv.Value.X, kv.Value.Y, kv.Value.Z };
            }
            if (o.CustomSkinWeightOverrides != null)
            {
                entry.CustomSkinWeightOverrides = new Dictionary<string, WorkspaceSkinWeightOverrideEntry>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in o.CustomSkinWeightOverrides)
                {
                    if (kv.Value == null) continue;
                    entry.CustomSkinWeightOverrides[kv.Key] = new WorkspaceSkinWeightOverrideEntry
                    {
                        VertexCount = kv.Value.VertexCount,
                        BonePalette = kv.Value.BonePalette != null ? (string[])kv.Value.BonePalette.Clone() : Array.Empty<string>(),
                        PaletteIndices = kv.Value.PaletteIndices != null ? (int[])kv.Value.PaletteIndices.Clone() : Array.Empty<int>(),
                        Weights = kv.Value.Weights != null ? (float[])kv.Value.Weights.Clone() : Array.Empty<float>()
                    };
                }
            }
            if (o.OriginalMeshDeformSculpts != null && o.OriginalMeshDeformSculpts.Count > 0)
            {
                foreach (var kv in o.OriginalMeshDeformSculpts)
                {
                    if (kv.Value == null || kv.Value.Count == 0) continue;
                    var idxArr = new int[kv.Value.Count];
                    var deltaArr = new float[kv.Value.Count * 3];
                    int k = 0;
                    foreach (var vkv in kv.Value)
                    {
                        idxArr[k] = vkv.Key;
                        deltaArr[k * 3 + 0] = vkv.Value.X;
                        deltaArr[k * 3 + 1] = vkv.Value.Y;
                        deltaArr[k * 3 + 2] = vkv.Value.Z;
                        k++;
                    }
                    entry.OriginalMeshDeformSculpts[kv.Key] = new WorkspaceHeroSculptEntry
                    {
                        VertexIndices = idxArr,
                        DeltasXyz = deltaArr
                    };
                }
            }
            return entry;
        }

        public DeadlockPlayground.Importer.CustomModelAutoRigger.RiggingOptions ToRiggingOptions()
        {
            var o = new DeadlockPlayground.Importer.CustomModelAutoRigger.RiggingOptions
            {
                SlotId = SlotId ?? "",
                AccessoryName = AccessoryName ?? "",
                Intent = (DeadlockPlayground.Importer.CustomModelAutoRigger.AccessoryIntent)Intent,
                ExplicitReplacedTargets = ExplicitReplacedTargets != null ? new List<string>(ExplicitReplacedTargets) : new(),
                Method = (DeadlockPlayground.Importer.CustomModelAutoRigger.RiggingMethod)Method,
                Preset = (DeadlockPlayground.Importer.CustomModelAutoRigger.PosePreset)Preset,
                Mode = (DeadlockPlayground.Importer.CustomModelAutoRigger.ReplaceMode)Mode,
                RigidBoneName = RigidBoneName ?? "head",
                CustomArmAngleDeg = CustomArmAngleDeg,
                CustomArmForwardDeg = CustomArmForwardDeg,
                CustomElbowStraightenPct = CustomElbowStraightenPct,
                CustomLegSpreadDeg = CustomLegSpreadDeg,
                AutoSnapArmsToHero = AutoSnapArmsToHero,
                AutoSnapLegsToHero = AutoSnapLegsToHero,
                AutoSnapHeadToHero = AutoSnapHeadToHero,
                SmoothingIterations = SmoothingIterations,
                SearchRadius = SearchRadius,
                KNearest = KNearest,
                ExcludeClothHairBones = ExcludeClothHairBones,
                KeepFingersRigidOnHand = KeepFingersRigidOnHand,
                ClampLimbStretching = ClampLimbStretching,
                StabilizeNeckFloatingHead = StabilizeNeckFloatingHead,
                AutoCounterTwistWeights = AutoCounterTwistWeights,
                IncludeBakedOutlineShells = IncludeBakedOutlineShells,
                RootScale = RootScale > 0f ? RootScale : 1.0f,
                RootScaleXYZ = (RootScaleXyz != null && RootScaleXyz.Length == 3)
                    ? new Vector3(
                        RootScaleXyz[0] > 0.0001f ? RootScaleXyz[0] : 1f,
                        RootScaleXyz[1] > 0.0001f ? RootScaleXyz[1] : 1f,
                        RootScaleXyz[2] > 0.0001f ? RootScaleXyz[2] : 1f)
                    : Vector3.One,
                OriginalMeshDeformEnabled = OriginalMeshDeformEnabled,
                OriginalMeshDeformTargetFilter = string.IsNullOrWhiteSpace(OriginalMeshDeformTargetFilter) ? "hair" : OriginalMeshDeformTargetFilter,
                OriginalMeshDeformBoneName = string.IsNullOrWhiteSpace(OriginalMeshDeformBoneName) ? "head" : OriginalMeshDeformBoneName,
                OriginalMeshDeformMode = (DeadlockPlayground.Importer.CustomModelAutoRigger.HeroDeformMode)OriginalMeshDeformMode,
                OriginalMeshDeformStrength = OriginalMeshDeformStrength,
                OriginalMeshDeformRadius = OriginalMeshDeformRadius > 0.01f ? OriginalMeshDeformRadius : 0.24f,
                OriginalMeshDeformHeightOffset = OriginalMeshDeformHeightOffset,
                IncludedImportedSubmeshNames = IncludedImportedSubmeshNames != null ? new List<string>(IncludedImportedSubmeshNames) : new(),
                KeptOriginalSubmeshNames = KeptOriginalSubmeshNames != null ? new List<string>(KeptOriginalSubmeshNames) : new(),
                SubmeshAttachmentSockets = SubmeshAttachmentSockets != null ? new Dictionary<string, string>(SubmeshAttachmentSockets) : new(),
                SubmeshReplacementMap = SubmeshReplacementMap != null ? new Dictionary<string, string>(SubmeshReplacementMap) : new(),
                SubmeshSnapToSocket = SubmeshSnapToSocket != null ? new Dictionary<string, bool>(SubmeshSnapToSocket) : new(),
                SubmeshSocketScale = SubmeshSocketScale != null ? new Dictionary<string, float>(SubmeshSocketScale) : new(),
                SubmeshIsOutlineShell = SubmeshIsOutlineShell != null ? new Dictionary<string, bool>(SubmeshIsOutlineShell, StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase),
                CustomBoneRemap = CustomBoneRemap != null ? new Dictionary<string, string>(CustomBoneRemap) : new()
            };

            if (RootPosition != null && RootPosition.Length == 3)
                o.RootPosition = new Vector3(RootPosition[0], RootPosition[1], RootPosition[2]);
            if (RootRotationDeg != null && RootRotationDeg.Length == 3)
                o.RootRotationDeg = new Vector3(RootRotationDeg[0], RootRotationDeg[1], RootRotationDeg[2]);
            if (AlignedTransformFloats != null && AlignedTransformFloats.Length == 12)
            {
                var basis = new Basis(
                    new Vector3(AlignedTransformFloats[0], AlignedTransformFloats[1], AlignedTransformFloats[2]),
                    new Vector3(AlignedTransformFloats[3], AlignedTransformFloats[4], AlignedTransformFloats[5]),
                    new Vector3(AlignedTransformFloats[6], AlignedTransformFloats[7], AlignedTransformFloats[8]));
                var orig = new Vector3(AlignedTransformFloats[9], AlignedTransformFloats[10], AlignedTransformFloats[11]);
                o.AlignedTransform = new Transform3D(basis, orig);
            }
            if (SubmeshSocketOffsetPos != null)
            {
                foreach (var kv in SubmeshSocketOffsetPos)
                    if (kv.Value != null && kv.Value.Length == 3)
                        o.SubmeshSocketOffsetPos[kv.Key] = new Vector3(kv.Value[0], kv.Value[1], kv.Value[2]);
            }
            if (SubmeshSocketOffsetRot != null)
            {
                foreach (var kv in SubmeshSocketOffsetRot)
                    if (kv.Value != null && kv.Value.Length == 3)
                        o.SubmeshSocketOffsetRot[kv.Key] = new Vector3(kv.Value[0], kv.Value[1], kv.Value[2]);
            }
            if (LandmarkPins != null)
            {
                o.LandmarkPins = new Dictionary<string, Vector3>();
                foreach (var kv in LandmarkPins)
                    if (kv.Value != null && kv.Value.Length == 3)
                        o.LandmarkPins[kv.Key] = new Vector3(kv.Value[0], kv.Value[1], kv.Value[2]);
            }
            if (CustomSkinWeightOverrides != null)
            {
                o.CustomSkinWeightOverrides = new Dictionary<string, DeadlockPlayground.Importer.CustomModelAutoRigger.SkinWeightOverride>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in CustomSkinWeightOverrides)
                {
                    if (kv.Value == null) continue;
                    o.CustomSkinWeightOverrides[kv.Key] = new DeadlockPlayground.Importer.CustomModelAutoRigger.SkinWeightOverride
                    {
                        VertexCount = kv.Value.VertexCount,
                        BonePalette = kv.Value.BonePalette != null ? (string[])kv.Value.BonePalette.Clone() : Array.Empty<string>(),
                        PaletteIndices = kv.Value.PaletteIndices != null ? (int[])kv.Value.PaletteIndices.Clone() : Array.Empty<int>(),
                        Weights = kv.Value.Weights != null ? (float[])kv.Value.Weights.Clone() : Array.Empty<float>()
                    };
                }
            }
            if (OriginalMeshDeformSculpts != null && OriginalMeshDeformSculpts.Count > 0)
            {
                o.OriginalMeshDeformSculpts ??= new Dictionary<string, Dictionary<int, Vector3>>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in OriginalMeshDeformSculpts)
                {
                    if (kv.Value?.VertexIndices == null || kv.Value.DeltasXyz == null) continue;
                    int count = Math.Min(kv.Value.VertexIndices.Length, kv.Value.DeltasXyz.Length / 3);
                    if (count <= 0) continue;
                    var map = new Dictionary<int, Vector3>(count);
                    for (int k = 0; k < count; k++)
                    {
                        map[kv.Value.VertexIndices[k]] = new Vector3(
                            kv.Value.DeltasXyz[k * 3 + 0],
                            kv.Value.DeltasXyz[k * 3 + 1],
                            kv.Value.DeltasXyz[k * 3 + 2]);
                    }
                    o.OriginalMeshDeformSculpts[kv.Key] = map;
                }
            }
            return o;
        }
    }

    public class WorkspaceAssetEntry
    {
        [JsonPropertyName("vpk_path")]
        public string VpkPath { get; set; } = "";

        [JsonPropertyName("category")]
        public string Category { get; set; } = "CharacterModel"; // CharacterModel, ExtraModel, ParticleVFX

        [JsonPropertyName("display_name")]
        public string DisplayName { get; set; } = "";

        [JsonPropertyName("owner_asset_path")]
        public string OwnerAssetPath { get; set; } = "";

        [JsonPropertyName("materials")]
        public List<WorkspaceMaterialEntry> Materials { get; set; } = new();

        [JsonPropertyName("particle_overrides_file")]
        public string ParticleOverridesFile { get; set; } = "";

        [JsonPropertyName("modified_textures")]
        public Dictionary<string, string> ModifiedTextures { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        [JsonPropertyName("animations")]
        public List<WorkspaceAnimationEntry> Animations { get; set; } = new();
    }

    public class WorkspaceAnimationEntry
    {
        [JsonPropertyName("anim_name")]
        public string AnimName { get; set; } = "";

        [JsonPropertyName("clean_name")]
        public string CleanName { get; set; } = "";

        [JsonPropertyName("relative_json_path")]
        public string RelativeJsonPath { get; set; } = "";

        [JsonPropertyName("relative_vnmclip_path")]
        public string RelativeVnmclipPath { get; set; } = "";

        [JsonPropertyName("target_vpk_path")]
        public string TargetVpkPath { get; set; } = "";

        [JsonPropertyName("fps")]
        public float Fps { get; set; } = 30f;

        [JsonPropertyName("duration")]
        public float Duration { get; set; } = 1f;

        [JsonPropertyName("is_looping")]
        public bool IsLooping { get; set; } = true;

        [JsonPropertyName("is_additive")]
        public bool IsAdditive { get; set; } = false;
    }

    public class WorkspaceAnimationTrackData
    {
        [JsonPropertyName("path")]
        public string Path { get; set; } = "";

        [JsonPropertyName("type")]
        public string Type { get; set; } = "";

        [JsonPropertyName("times")]
        public List<double> Times { get; set; } = new();

        [JsonPropertyName("values")]
        public List<float[]> Values { get; set; } = new();
    }

    public class WorkspaceAnimationFileModel
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("fps")]
        public float Fps { get; set; } = 30f;

        [JsonPropertyName("length")]
        public double Length { get; set; } = 1.0;

        [JsonPropertyName("loop_mode")]
        public string LoopMode { get; set; } = "Linear";

        [JsonPropertyName("is_additive")]
        public bool IsAdditive { get; set; } = false;

        [JsonPropertyName("tracks")]
        public List<WorkspaceAnimationTrackData> Tracks { get; set; } = new();
    }

   public class WorkspaceMaterialEntry
    {
        [JsonPropertyName("material_path")]
        public string MaterialPath { get; set; } = "";

        [JsonPropertyName("display_name")]
        public string DisplayName { get; set; } = "";

        [JsonPropertyName("textures")]
        public Dictionary<string, string> Textures { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        [JsonPropertyName("texture_channels")]
        public Dictionary<string, string> TextureChannels { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        [JsonPropertyName("active_layer_index")]
        public int ActiveLayerIndex { get; set; } = 0;

        [JsonPropertyName("width")]
        public int Width { get; set; } = 2048;

        [JsonPropertyName("height")]
        public int Height { get; set; } = 2048;

        [JsonPropertyName("layers")]
        public List<WorkspaceLayerEntry> Layers { get; set; } = new();

        [JsonPropertyName("channel_layers")]
        public Dictionary<string, List<WorkspaceLayerEntry>> ChannelLayers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        [JsonPropertyName("channel_active_layer_indices")]
        public Dictionary<string, int> ChannelActiveLayerIndices { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public class WorkspaceLayerEntry
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "Paint Layer 1";

        [JsonPropertyName("opacity")]
        public float Opacity { get; set; } = 1.0f;

        [JsonPropertyName("visible")]
        public bool Visible { get; set; } = true;

        [JsonPropertyName("blend_mode")]
        public int BlendMode { get; set; } = 0;

        [JsonPropertyName("locked")]
        public bool Locked { get; set; } = false;

        [JsonPropertyName("layer_png_rel_path")]
        public string LayerPngRelPath { get; set; } = "";
    }

    public class WorkspaceParticleOverrides
    {
        [JsonPropertyName("vpcf_path")]
        public string VpcfPath { get; set; } = "";

        [JsonPropertyName("constant_color_rgba")]
        public float[] ConstantColorRgba { get; set; } = new float[] { 1f, 1f, 1f, 1f };

        [JsonPropertyName("radius_scale")]
        public float RadiusScale { get; set; } = 1.0f;

        [JsonPropertyName("alpha_scale")]
        public float AlphaScale { get; set; } = 1.0f;

        [JsonPropertyName("emitters")]
        public Dictionary<string, EmitterOverrideData> Emitters { get; set; } = new();
    }

    public class EmitterOverrideData
    {
        [JsonPropertyName("child_vpcf_path")]
        public string ChildVpcfPath { get; set; } = "";

        [JsonPropertyName("visible")]
        public bool Visible { get; set; } = true;

        [JsonPropertyName("tint_color_rgba")]
        public float[] TintColorRgba { get; set; } = new float[] { 1f, 1f, 1f, 1f };

        [JsonPropertyName("has_random_color")]
        public bool HasRandomColor { get; set; } = false;

        [JsonPropertyName("color_min_rgb")]
        public float[] ColorMinRgb { get; set; } = null;

        [JsonPropertyName("color_max_rgb")]
        public float[] ColorMaxRgb { get; set; } = null;

        [JsonPropertyName("has_color_fade")]
        public bool HasColorFade { get; set; } = false;

        [JsonPropertyName("color_fade_rgb")]
        public float[] ColorFadeRgb { get; set; } = null;

        [JsonPropertyName("size_scale")]
        public float SizeScale { get; set; } = 1.0f;

        [JsonPropertyName("alpha_scale")]
        public float AlphaScale { get; set; } = 1.0f;
    }

    /// <summary>
    /// Centralized singleton service that manages disk-backed staging (user://workspace_session/)
    /// across multiple heroes, models, and particle systems.
    /// Keeps heavy texture buffers out of RAM when switching assets.
    /// </summary>
    public class ModWorkspaceSession
    {
        private static ModWorkspaceSession _instance;
        public static ModWorkspaceSession Instance => _instance ??= new ModWorkspaceSession();

        public string SessionDir { get; private set; }
        public string TexturesDir { get; private set; }
        public string ParticlesDir { get; private set; }
        public string AnimationsDir { get; private set; }
        public string CustomModelsDir { get; private set; }
        public string ManifestPath { get; private set; }

        public WorkspaceManifest Manifest { get; private set; } = new();
        public bool HasUnsavedChanges { get; set; } = false;

        private readonly Dictionary<string, Image> _stagedImagesInMemory = new(StringComparer.OrdinalIgnoreCase);

        public event Action OnWorkspaceChanged;
        public event Action<string, string> OnAssetDiscarded; // (heroId, vpkPath)
        public event Action<string, string> OnAnimationDiscarded; // (heroId, cleanName)
        public event Action OnWorkspaceCleared;

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private ModWorkspaceSession()
        {
            Initialize();
        }

        public void Initialize()
        {
            try
            {
                SessionDir = ProjectSettings.GlobalizePath("user://workspace_session");
                TexturesDir = Path.Combine(SessionDir, "textures");
                ParticlesDir = Path.Combine(SessionDir, "particles");
                AnimationsDir = Path.Combine(SessionDir, "animations");
                CustomModelsDir = Path.Combine(SessionDir, "custom_models");
                ManifestPath = Path.Combine(SessionDir, "session_manifest.json");

                Directory.CreateDirectory(SessionDir);
                Directory.CreateDirectory(TexturesDir);
                Directory.CreateDirectory(ParticlesDir);
                Directory.CreateDirectory(AnimationsDir);
                Directory.CreateDirectory(CustomModelsDir);

                if (File.Exists(ManifestPath))
                {
                    string json = File.ReadAllText(ManifestPath);
                    Manifest = JsonSerializer.Deserialize<WorkspaceManifest>(json, JsonOpts) ?? new WorkspaceManifest();
                    PruneGhostEntries();
                }
                else
                {
                    Manifest = new WorkspaceManifest();
                    SaveManifest();
                }
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[ModWorkspaceSession] Initialization error: {ex.Message}");
                Manifest = new WorkspaceManifest();
            }
        }

        public void PruneGhostEntries()
        {
            if (Manifest?.Heroes == null) return;

            bool changed = false;
            var emptyHeroes = new List<string>();

            foreach (var (heroId, heroEntry) in Manifest.Heroes)
            {
                var emptyAssets = new List<WorkspaceAssetEntry>();
                foreach (var asset in heroEntry.Assets)
                {
                    if (asset.Animations != null && asset.Animations.Count > 0)
                    {
                        var ghostAnims = asset.Animations.Where(a =>
                        {
                            bool hasJson = !string.IsNullOrEmpty(a.RelativeJsonPath) && File.Exists(Path.Combine(SessionDir, a.RelativeJsonPath));
                            bool hasClip = !string.IsNullOrEmpty(a.RelativeVnmclipPath) && File.Exists(Path.Combine(SessionDir, a.RelativeVnmclipPath));
                            return !hasJson && !hasClip;
                        }).ToList();

                        foreach (var ga in ghostAnims)
                        {
                            asset.Animations.Remove(ga);
                            changed = true;
                        }
                    }

                    if (asset.Materials.Count == 0 &&
                        asset.ModifiedTextures.Count == 0 &&
                        (asset.Animations == null || asset.Animations.Count == 0) &&
                        string.IsNullOrEmpty(asset.ParticleOverridesFile))
                    {
                        emptyAssets.Add(asset);
                        changed = true;
                    }
                }

                foreach (var ea in emptyAssets)
                {
                    heroEntry.Assets.Remove(ea);
                }

                if (heroEntry.CustomModel != null)
                {
                    bool hasModelFile = !string.IsNullOrEmpty(heroEntry.CustomModel.RelativeModelPath) &&
                                        File.Exists(Path.Combine(SessionDir, heroEntry.CustomModel.RelativeModelPath));
                    if (!hasModelFile)
                    {
                        heroEntry.CustomModel = null;
                        changed = true;
                    }
                }

                if (heroEntry.Assets.Count == 0 && heroEntry.CustomModel == null)
                {
                    emptyHeroes.Add(heroId);
                    changed = true;
                }
            }

            foreach (var eh in emptyHeroes)
            {
                Manifest.Heroes.Remove(eh);
            }

            if (changed)
            {
                SaveManifest();
            }
        }

        public void SaveManifest()
        {
            try
            {
                Directory.CreateDirectory(SessionDir);
                string json = JsonSerializer.Serialize(Manifest, JsonOpts);
                File.WriteAllText(ManifestPath, json);
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[ModWorkspaceSession] Failed to save manifest: {ex.Message}");
            }
        }

        public void CleanupTempSession()
        {
            try
            {
                Manifest = new WorkspaceManifest();
                _stagedImagesInMemory.Clear();
                HasUnsavedChanges = false;

                if (Directory.Exists(SessionDir))
                {
                    try
                    {
                        Directory.Delete(SessionDir, true);
                    }
                    catch (Exception delEx)
                    {
                        GD.PrintErr($"[ModWorkspaceSession] Non-fatal directory delete lock during cleanup: {delEx.Message}");
                        try
                        {
                            if (Directory.Exists(TexturesDir))
                            {
                                foreach (var f in Directory.GetFiles(TexturesDir))
                                {
                                    try { File.Delete(f); } catch { }
                                }
                            }
                        }
                        catch { }
                    }
                }

                Initialize();
                SaveManifest();
                OnWorkspaceChanged?.Invoke();
                OnWorkspaceCleared?.Invoke();
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[ModWorkspaceSession] Error cleaning session: {ex.Message}");
            }
        }

        public void ClearSession() => CleanupTempSession();

        public void RegisterHeroSourceAddonVpk(string heroId, string heroDisplayName, string addonVpkPath)
        {
            if (string.IsNullOrEmpty(heroId) || string.IsNullOrEmpty(addonVpkPath)) return;
            if (!Manifest.Heroes.TryGetValue(heroId, out var heroEntry))
            {
                heroEntry = new WorkspaceHeroEntry
                {
                    HeroId = heroId,
                    DisplayName = !string.IsNullOrEmpty(heroDisplayName) ? heroDisplayName : heroId,
                    SourceAddonVpkPath = addonVpkPath
                };
                Manifest.Heroes[heroId] = heroEntry;
            }
            else
            {
                heroEntry.SourceAddonVpkPath = addonVpkPath;
            }
            SaveManifest();
        }

        public WorkspaceParticleOverrides GetParticleOverrides(string vpcfPath) => GetStagedParticleOverrides(vpcfPath);

        public string SanitizePathToFileName(string vpkPath, string extension = ".png")
        {
            if (string.IsNullOrWhiteSpace(vpkPath)) return "texture" + extension;
            string clean = vpkPath.Replace('\\', '_').Replace('/', '_').Replace(':', '_').Replace('.', '_');
            return clean + extension;
        }

        public void StageTexture(
            string heroId,
            string heroDisplayName,
            string assetCategory,
            string ownerAssetPath,
            string materialPath,
            string materialDisplayName,
            string vtexPath,
            Image image)
        {
            if (string.IsNullOrEmpty(heroId) || string.IsNullOrEmpty(vtexPath) || image == null || image.IsEmpty()) return;

            try
            {
                if (assetCategory == "ParticleVFX" && !string.IsNullOrEmpty(ownerAssetPath))
                {
                    string existingHero = GetParticleOwnerHeroId(ownerAssetPath);
                    if (!string.IsNullOrEmpty(existingHero))
                    {
                        heroId = existingHero;
                        if (Manifest.Heroes.TryGetValue(existingHero, out var eh))
                        {
                            heroDisplayName = eh.DisplayName;
                        }
                    }
                }

                Directory.CreateDirectory(TexturesDir);

                string fileName = SanitizePathToFileName(vtexPath, ".png");
                string diskPath = Path.Combine(TexturesDir, fileName);
                string relPath = Path.Combine("textures", fileName).Replace('\\', '/');

                var imageCopy = (Image)image.Duplicate();

                // Synchronously cache in memory on main thread to prevent race conditions on fast hero switches
                string normKey = vtexPath.Replace('\\', '/').Trim().ToLowerInvariant();
                _stagedImagesInMemory[normKey] = (Image)imageCopy.Duplicate();
                _stagedImagesInMemory[fileName.ToLowerInvariant()] = (Image)imageCopy.Duplicate();

                Task.Run(() =>
                {
                    try
                    {
                        var err = imageCopy.SavePng(diskPath);
                        if (err != Error.Ok)
                        {
                            GD.PrintErr($"[ModWorkspaceSession] Failed to save PNG to {diskPath}: {err}");
                        }
                    }
                    catch (Exception ex)
                    {
                        GD.PrintErr($"[ModWorkspaceSession] Background PNG save error: {ex.Message}");
                    }
                });

                // Ensure hero entry
                if (!Manifest.Heroes.TryGetValue(heroId, out var heroEntry))
                {
                    heroEntry = new WorkspaceHeroEntry
                    {
                        HeroId = heroId,
                        DisplayName = !string.IsNullOrEmpty(heroDisplayName) ? heroDisplayName : heroId
                    };
                    Manifest.Heroes[heroId] = heroEntry;
                }

                // Ensure asset entry
                string assetVpkPath = !string.IsNullOrEmpty(ownerAssetPath) ? ownerAssetPath : materialPath;
                var assetEntry = heroEntry.Assets.FirstOrDefault(a => string.Equals(a.VpkPath, assetVpkPath, StringComparison.OrdinalIgnoreCase));
                if (assetEntry == null)
                {
                    assetEntry = new WorkspaceAssetEntry
                    {
                        VpkPath = assetVpkPath,
                        Category = assetCategory,
                        DisplayName = !string.IsNullOrEmpty(ownerAssetPath) ? Path.GetFileNameWithoutExtension(ownerAssetPath) : materialDisplayName,
                        OwnerAssetPath = ownerAssetPath
                    };
                    heroEntry.Assets.Add(assetEntry);
                }

                assetEntry.ModifiedTextures[vtexPath] = relPath;

                // Ensure material entry
                if (!string.IsNullOrEmpty(materialPath))
                {
                    var matEntry = assetEntry.Materials.FirstOrDefault(m => string.Equals(m.MaterialPath, materialPath, StringComparison.OrdinalIgnoreCase));
                    if (matEntry == null)
                    {
                        matEntry = new WorkspaceMaterialEntry
                        {
                            MaterialPath = materialPath,
                            DisplayName = !string.IsNullOrEmpty(materialDisplayName) ? materialDisplayName : Path.GetFileName(materialPath)
                        };
                        assetEntry.Materials.Add(matEntry);
                    }
                    matEntry.Textures[vtexPath] = relPath;
                }

                Manifest.LastActiveHeroId = heroId;
                Manifest.LastActiveAssetType = assetCategory;
                Manifest.LastActiveAssetPath = assetVpkPath;

                SaveManifest();
                HasUnsavedChanges = true;
                OnWorkspaceChanged?.Invoke();

                GD.Print($"[ModWorkspaceSession] Staged texture '{vtexPath}' for hero '{heroId}' to disk ({relPath})");
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[ModWorkspaceSession] Error staging texture: {ex.Message}");
            }
        }

        public void RegisterPendingModifiedTexture(
            string heroId,
            string heroDisplayName,
            string assetCategory,
            string ownerAssetPath,
            string materialPath,
            string materialDisplayName,
            string vtexPath)
        {
            if (string.IsNullOrEmpty(heroId) || string.IsNullOrEmpty(vtexPath)) return;

            try
            {
                string fileName = SanitizePathToFileName(vtexPath, ".png");
                string relPath = Path.Combine("textures", fileName).Replace('\\', '/');

                // Ensure hero entry
                if (!Manifest.Heroes.TryGetValue(heroId, out var heroEntry))
                {
                    heroEntry = new WorkspaceHeroEntry
                    {
                        HeroId = heroId,
                        DisplayName = !string.IsNullOrEmpty(heroDisplayName) ? heroDisplayName : heroId
                    };
                    Manifest.Heroes[heroId] = heroEntry;
                }

                // Ensure asset entry
                string assetVpkPath = !string.IsNullOrEmpty(ownerAssetPath) ? ownerAssetPath : materialPath;
                var assetEntry = heroEntry.Assets.FirstOrDefault(a => string.Equals(a.VpkPath, assetVpkPath, StringComparison.OrdinalIgnoreCase));
                if (assetEntry == null)
                {
                    assetEntry = new WorkspaceAssetEntry
                    {
                        VpkPath = assetVpkPath,
                        Category = assetCategory,
                        DisplayName = !string.IsNullOrEmpty(ownerAssetPath) ? Path.GetFileNameWithoutExtension(ownerAssetPath) : materialDisplayName,
                        OwnerAssetPath = ownerAssetPath
                    };
                    heroEntry.Assets.Add(assetEntry);
                }

                bool isNew = !assetEntry.ModifiedTextures.ContainsKey(vtexPath);
                assetEntry.ModifiedTextures[vtexPath] = relPath;

                // Ensure material entry
                if (!string.IsNullOrEmpty(materialPath))
                {
                    var matEntry = assetEntry.Materials.FirstOrDefault(m => string.Equals(m.MaterialPath, materialPath, StringComparison.OrdinalIgnoreCase));
                    if (matEntry == null)
                    {
                        matEntry = new WorkspaceMaterialEntry
                        {
                            MaterialPath = materialPath,
                            DisplayName = !string.IsNullOrEmpty(materialDisplayName) ? materialDisplayName : Path.GetFileName(materialPath)
                        };
                        assetEntry.Materials.Add(matEntry);
                        isNew = true;
                    }
                    if (!matEntry.Textures.ContainsKey(vtexPath))
                    {
                        matEntry.Textures[vtexPath] = relPath;
                        isNew = true;
                    }
                }

                Manifest.LastActiveHeroId = heroId;
                Manifest.LastActiveAssetType = assetCategory;
                Manifest.LastActiveAssetPath = assetVpkPath;
                HasUnsavedChanges = true;

               if (isNew)
                {
                    HasUnsavedChanges = true;
                    SaveManifest();
                    OnWorkspaceChanged?.Invoke();
                }
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[ModWorkspaceSession] Error registering pending texture: {ex.Message}");
            }
        }

        public void StageParticleOverrides(
            string heroId,
            string heroDisplayName,
            string vpcfPath,
            string vpcfDisplayName,
            WorkspaceParticleOverrides overrides,
            Dictionary<string, Image> modifiedTextures = null)
        {
            if (string.IsNullOrEmpty(heroId) || string.IsNullOrEmpty(vpcfPath) || overrides == null) return;

            try
            {
                // Preserve original particle owner if this particle is already tracked under an existing hero
                string existingHeroId = GetParticleOwnerHeroId(vpcfPath);
                if (!string.IsNullOrEmpty(existingHeroId))
                {
                    heroId = existingHeroId;
                    if (Manifest.Heroes.TryGetValue(existingHeroId, out var existingHero))
                    {
                        heroDisplayName = existingHero.DisplayName;
                    }
                }

                Directory.CreateDirectory(ParticlesDir);

                string fileName = SanitizePathToFileName(vpcfPath, "_overrides.json");
                string diskPath = Path.Combine(ParticlesDir, fileName);
                string relPath = Path.Combine("particles", fileName).Replace('\\', '/');

                string json = JsonSerializer.Serialize(overrides, JsonOpts);
                File.WriteAllText(diskPath, json);

                if (!Manifest.Heroes.TryGetValue(heroId, out var heroEntry))
                {
                    heroEntry = new WorkspaceHeroEntry
                    {
                        HeroId = heroId,
                        DisplayName = !string.IsNullOrEmpty(heroDisplayName) ? heroDisplayName : heroId
                    };
                    Manifest.Heroes[heroId] = heroEntry;
                }

                var assetEntry = heroEntry.Assets.FirstOrDefault(a => string.Equals(a.VpkPath, vpcfPath, StringComparison.OrdinalIgnoreCase));
                if (assetEntry == null)
                {
                    assetEntry = new WorkspaceAssetEntry
                    {
                        VpkPath = vpcfPath,
                        Category = "ParticleVFX",
                        DisplayName = !string.IsNullOrEmpty(vpcfDisplayName) ? vpcfDisplayName : Path.GetFileName(vpcfPath),
                        OwnerAssetPath = vpcfPath
                    };
                    heroEntry.Assets.Add(assetEntry);
                }

                assetEntry.ParticleOverridesFile = relPath;

                if (modifiedTextures != null)
                {
                    foreach (var (texPath, img) in modifiedTextures)
                    {
                        if (img != null && !img.IsEmpty())
                        {
                            string texFile = SanitizePathToFileName(texPath, ".png");
                            string texDisk = Path.Combine(TexturesDir, texFile);
                            string texRel = Path.Combine("textures", texFile).Replace('\\', '/');
                            var imgCopy = (Image)img.Duplicate();

                            string normTex = texPath.Replace('\\', '/').Trim().ToLowerInvariant();
                            _stagedImagesInMemory[normTex] = (Image)imgCopy.Duplicate();
                            _stagedImagesInMemory[texFile.ToLowerInvariant()] = (Image)imgCopy.Duplicate();

                            Task.Run(() =>
                            {
                                try
                                {
                                    imgCopy.SavePng(texDisk);
                                }
                                catch (Exception ex)
                                {
                                    GD.PrintErr($"[ModWorkspaceSession] Background particle PNG save error: {ex.Message}");
                                }
                            });
                            assetEntry.ModifiedTextures[texPath] = texRel;
                        }
                    }
                }

                Manifest.LastActiveHeroId = heroId;
                Manifest.LastActiveAssetType = "ParticleVFX";
                Manifest.LastActiveAssetPath = vpcfPath;

                SaveManifest();
                HasUnsavedChanges = true;
                OnWorkspaceChanged?.Invoke();

                GD.Print($"[ModWorkspaceSession] Staged particle overrides for '{vpcfPath}' ({heroId})");
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[ModWorkspaceSession] Error staging particle overrides: {ex.Message}");
            }
        }

        public void StageAnimation(
            string heroId,
            string heroDisplayName,
            string modelVpkPath,
            string animName,
            string cleanName,
            Godot.Animation godotAnim,
            float fps,
            Skeleton3D skeleton,
            string targetVpkPath = null)
        {
            if (string.IsNullOrEmpty(heroId) || godotAnim == null || string.IsNullOrEmpty(cleanName)) return;

            try
            {
                if (string.IsNullOrEmpty(AnimationsDir) || !Directory.Exists(AnimationsDir))
                {
                    Directory.CreateDirectory(AnimationsDir);
                }

                // Isolate animation files per hero to prevent cross-hero retarget bone deformation collisions!
                string heroAnimDir = Path.Combine(AnimationsDir, heroId);
                if (!Directory.Exists(heroAnimDir))
                {
                    Directory.CreateDirectory(heroAnimDir);
                }

                string safeName = SanitizePathToFileName(cleanName, "");
                string jsonFileName = $"{safeName}.json";
                string fullJsonPath = Path.Combine(heroAnimDir, jsonFileName);
                string relJsonPath = Path.Combine("animations", heroId, jsonFileName).Replace('\\', '/');

                string clipFileName = $"{safeName}.vnmclip_c";
                string fullClipPath = Path.Combine(heroAnimDir, clipFileName);
                string relClipPath = Path.Combine("animations", heroId, clipFileName).Replace('\\', '/');

                if (string.IsNullOrEmpty(targetVpkPath))
                {
                    targetVpkPath = DeadlockPlayground.Tools.DeadlockAnimLoader.ResolveHeroClipVpkPath(heroId, modelVpkPath, cleanName);
                }

                // 1. Build and save JSON representation
                var fileModel = new WorkspaceAnimationFileModel
                {
                    Name = cleanName,
                    Fps = fps > 0 ? fps : 30f,
                    Length = godotAnim.Length,
                    LoopMode = godotAnim.LoopMode.ToString(),
                    IsAdditive = godotAnim.HasMeta("is_additive") && godotAnim.GetMeta("is_additive").AsBool()
                };

                for (int t = 0; t < godotAnim.GetTrackCount(); t++)
                {
                    var trackType = godotAnim.TrackGetType(t);
                    var tData = new WorkspaceAnimationTrackData
                    {
                        Path = godotAnim.TrackGetPath(t).ToString(),
                        Type = trackType.ToString()
                    };

                    int keyCount = godotAnim.TrackGetKeyCount(t);
                    for (int k = 0; k < keyCount; k++)
                    {
                        double tTime = godotAnim.TrackGetKeyTime(t, k);
                        tData.Times.Add(tTime);
                        var val = godotAnim.TrackGetKeyValue(t, k);

                        if (trackType == Godot.Animation.TrackType.Position3D || trackType == Godot.Animation.TrackType.Scale3D)
                        {
                            Vector3 v = val.AsVector3();
                            tData.Values.Add(new float[] { v.X, v.Y, v.Z });
                        }
                        else if (trackType == Godot.Animation.TrackType.Rotation3D)
                        {
                            Quaternion q = val.AsQuaternion();
                            tData.Values.Add(new float[] { q.X, q.Y, q.Z, q.W });
                        }
                    }

                    fileModel.Tracks.Add(tData);
                }

                string json = JsonSerializer.Serialize(fileModel, JsonOpts);
                File.WriteAllText(fullJsonPath, json);

                // 2. Build and save binary Source 2 .vnmclip_c using Source2ClipPatcher
                try
                {
                    byte[] clipBytes = DeadlockPlayground.Animation.Source2ClipPatcher.CreateOrPatchClip(
                        existingResourceBytes: null,
                        godotAnim: godotAnim,
                        skeleton: skeleton,
                        animName: cleanName,
                        fps: fps > 0 ? fps : 30f,
                        targetVpkPath: targetVpkPath,
                        heroId: heroId
                    );

                    if (clipBytes != null && clipBytes.Length > 0)
                    {
                        File.WriteAllBytes(fullClipPath, clipBytes);
                        GD.Print($"[ModWorkspaceSession] Successfully compiled binary clip '{clipFileName}' ({clipBytes.Length} bytes) -> Target VPK Path: {targetVpkPath}");
                    }
                    else
                    {
                        GD.PrintErr($"[ModWorkspaceSession] Failed to compile binary clip '{clipFileName}' - CreateOrPatchClip returned null.");
                    }
                }
                catch (Exception clipEx)
                {
                    GD.PrintErr($"[ModWorkspaceSession] Failed to compile binary clip '{clipFileName}': {clipEx.Message}");
                }

                // 3. Ensure hero entry
                if (!Manifest.Heroes.TryGetValue(heroId, out var heroEntry))
                {
                    heroEntry = new WorkspaceHeroEntry
                    {
                        HeroId = heroId,
                        DisplayName = !string.IsNullOrEmpty(heroDisplayName) ? heroDisplayName : heroId
                    };
                    Manifest.Heroes[heroId] = heroEntry;
                }

                // 4. Ensure asset entry
                string assetVpkPath = !string.IsNullOrEmpty(modelVpkPath) ? modelVpkPath : $"models/heroes/{heroId}/{heroId}.vmdl_c";
                var assetEntry = heroEntry.Assets.FirstOrDefault(a => string.Equals(a.VpkPath, assetVpkPath, StringComparison.OrdinalIgnoreCase));
                if (assetEntry == null)
                {
                    assetEntry = new WorkspaceAssetEntry
                    {
                        VpkPath = assetVpkPath,
                        Category = "CharacterModel",
                        DisplayName = !string.IsNullOrEmpty(modelVpkPath) ? Path.GetFileNameWithoutExtension(modelVpkPath) : heroDisplayName,
                        OwnerAssetPath = assetVpkPath
                    };
                    heroEntry.Assets.Add(assetEntry);
                }

                // 5. Update or add WorkspaceAnimationEntry
                var animEntry = assetEntry.Animations.FirstOrDefault(a => string.Equals(a.CleanName, cleanName, StringComparison.OrdinalIgnoreCase));
                if (animEntry == null)
                {
                    animEntry = new WorkspaceAnimationEntry
                    {
                        AnimName = animName,
                        CleanName = cleanName
                    };
                    assetEntry.Animations.Add(animEntry);
                }

                animEntry.RelativeJsonPath = relJsonPath;
                animEntry.RelativeVnmclipPath = File.Exists(fullClipPath) ? relClipPath : "";
                animEntry.TargetVpkPath = !string.IsNullOrEmpty(targetVpkPath)
                    ? targetVpkPath
                    : DeadlockPlayground.Tools.DeadlockAnimLoader.ResolveHeroClipVpkPath(heroId, modelVpkPath, cleanName);
                animEntry.Fps = fps > 0 ? fps : 30f;
                animEntry.Duration = (float)godotAnim.Length;
                animEntry.IsLooping = godotAnim.LoopMode != Godot.Animation.LoopModeEnum.None;
                animEntry.IsAdditive = fileModel.IsAdditive;

                Manifest.LastActiveHeroId = heroId;
                Manifest.LastActiveAssetType = "CharacterModel";
                Manifest.LastActiveAssetPath = assetVpkPath;

                SaveManifest();
                HasUnsavedChanges = true;
                OnWorkspaceChanged?.Invoke();

                GD.Print($"[ModWorkspaceSession] Staged animation '{cleanName}' for hero '{heroId}' ({relJsonPath})");
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[ModWorkspaceSession] Error staging animation: {ex.Message}");
            }
        }

        public void DiscardAnimation(string heroId, string cleanName)
        {
            if (string.IsNullOrEmpty(heroId) || string.IsNullOrEmpty(cleanName)) return;

            if (Manifest.Heroes.TryGetValue(heroId, out var heroEntry))
            {
                var assetsToRemove = new List<WorkspaceAssetEntry>();
                foreach (var asset in heroEntry.Assets)
                {
                    var anim = asset.Animations?.FirstOrDefault(a => 
                        string.Equals(a.CleanName, cleanName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(a.AnimName, cleanName, StringComparison.OrdinalIgnoreCase));

                    if (anim != null)
                    {
                        asset.Animations.Remove(anim);
                        if (!string.IsNullOrEmpty(anim.RelativeJsonPath))
                        {
                            string full = Path.Combine(SessionDir, anim.RelativeJsonPath);
                            if (File.Exists(full)) { try { File.Delete(full); } catch { } }
                        }
                        if (!string.IsNullOrEmpty(anim.RelativeVnmclipPath))
                        {
                            string full = Path.Combine(SessionDir, anim.RelativeVnmclipPath);
                            if (File.Exists(full)) { try { File.Delete(full); } catch { } }
                        }

                        // Check if asset is now completely empty
                        if (asset.Materials.Count == 0 &&
                            asset.ModifiedTextures.Count == 0 &&
                            (asset.Animations == null || asset.Animations.Count == 0) &&
                            string.IsNullOrEmpty(asset.ParticleOverridesFile))
                        {
                            assetsToRemove.Add(asset);
                        }
                    }
                }

                foreach (var a in assetsToRemove)
                {
                    heroEntry.Assets.Remove(a);
                }

                if (heroEntry.Assets.Count == 0)
                {
                    Manifest.Heroes.Remove(heroId);
                }

                SaveManifest();
                HasUnsavedChanges = true;
                OnWorkspaceChanged?.Invoke();
                OnAnimationDiscarded?.Invoke(heroId, cleanName);
            }
        }

public Godot.Animation LoadStagedAnimationToGodot(WorkspaceAnimationEntry entry, Skeleton3D skeleton = null)
        {
            if (entry == null || string.IsNullOrEmpty(entry.RelativeJsonPath)) return null;

            try
            {
                string fullJsonPath = Path.Combine(SessionDir, entry.RelativeJsonPath);
                if (!File.Exists(fullJsonPath))
                {
                    string legacyPath = Path.Combine(SessionDir, "animations", Path.GetFileName(entry.RelativeJsonPath));
                    if (File.Exists(legacyPath))
                    {
                        fullJsonPath = legacyPath;
                    }
                    else
                    {
                        GD.PrintErr($"[ModWorkspaceSession] Cannot load staged animation: file not found '{fullJsonPath}'");
                        return null;
                    }
                }

                string json = File.ReadAllText(fullJsonPath);
                var fileModel = JsonSerializer.Deserialize<WorkspaceAnimationFileModel>(json, JsonOpts);
                if (fileModel == null) return null;

                var anim = new Godot.Animation();
                anim.Length = fileModel.Length > 0 ? fileModel.Length : (entry.Duration > 0 ? entry.Duration : 1.0);
                anim.LoopMode = Enum.TryParse<Godot.Animation.LoopModeEnum>(fileModel.LoopMode, true, out var loopMode)
                    ? loopMode
                    : (entry.IsLooping ? Godot.Animation.LoopModeEnum.Linear : Godot.Animation.LoopModeEnum.None);

                anim.SetMeta("is_additive", fileModel.IsAdditive || entry.IsAdditive);
                anim.SetMeta("is_retargeted", true);
                anim.SetMeta("decoded_by_anim_loader", true);
                anim.SetMeta("anim_loader_version", 5);

                // Resolve the EXACT relative node path from the AnimationPlayer's RootNode to the Skeleton3D (NEVER use '%' unless UniqueNameInOwner is true!)
                string skelPathPrefix = "";
                bool hasValidSkeleton = skeleton != null && GodotObject.IsInstanceValid(skeleton) && !skeleton.IsQueuedForDeletion();
                if (hasValidSkeleton)
                {
                    Node animRoot = null;
                    var parent = skeleton.GetParent();
                    while (parent != null)
                    {
                        var ap = parent.FindChild("AnimationPlayer", true, false) as AnimationPlayer;
                        if (ap != null)
                        {
                            animRoot = ap.GetNodeOrNull(ap.RootNode) ?? ap.GetParent();
                            break;
                        }
                        if (parent.Name.ToString().StartsWith("Hero_", StringComparison.OrdinalIgnoreCase))
                        {
                            break;
                        }
                        parent = parent.GetParent();
                    }

                    if (animRoot != null && animRoot.IsInsideTree() && skeleton.IsInsideTree())
                    {
                        skelPathPrefix = animRoot.GetPathTo(skeleton).ToString();
                    }
                    else if (skeleton.UniqueNameInOwner)
                    {
                        skelPathPrefix = "%" + skeleton.Name;
                    }
                    else
                    {
                        skelPathPrefix = skeleton.Name.ToString();
                    }
                }

                foreach (var trackData in fileModel.Tracks)
                {
                    if (string.IsNullOrEmpty(trackData.Path)) continue;

                    string trackPath = trackData.Path;
                    int colonIdx = trackPath.IndexOf(':');
                    if (colonIdx >= 0)
                    {
                        string boneName = trackPath.Substring(colonIdx + 1);

                        // Skip bones that do not exist on this hero's Skeleton3D (e.g. spine_3 on a 3-spine hero) so Godot never logs track resolution warnings!
                        if (hasValidSkeleton && skeleton.FindBone(boneName) < 0)
                        {
                            int matchedBoneIdx = -1;
                            for (int b = 0; b < skeleton.GetBoneCount(); b++)
                            {
                                if (string.Equals(skeleton.GetBoneName(b), boneName, StringComparison.OrdinalIgnoreCase))
                                {
                                    matchedBoneIdx = b;
                                    boneName = skeleton.GetBoneName(b);
                                    break;
                                }
                            }
                            if (matchedBoneIdx < 0)
                            {
                                continue;
                            }
                        }

                        if (!string.IsNullOrEmpty(skelPathPrefix))
                        {
                            trackPath = $"{skelPathPrefix}:{boneName}";
                        }
                        else if (trackPath.StartsWith("%"))
                        {
                            trackPath = trackPath.Substring(1);
                        }
                    }

                    Godot.Animation.TrackType tType;
                    if (Enum.TryParse<Godot.Animation.TrackType>(trackData.Type, true, out var parsedType))
                    {
                        tType = parsedType;
                    }
                    else if (trackData.Type.Contains("Rotation", StringComparison.OrdinalIgnoreCase))
                    {
                        tType = Godot.Animation.TrackType.Rotation3D;
                    }
                    else if (trackData.Type.Contains("Position", StringComparison.OrdinalIgnoreCase))
                    {
                        tType = Godot.Animation.TrackType.Position3D;
                    }
                    else if (trackData.Type.Contains("Scale", StringComparison.OrdinalIgnoreCase))
                    {
                        tType = Godot.Animation.TrackType.Scale3D;
                    }
                    else
                    {
                        continue;
                    }

                    int trackIdx = anim.AddTrack(tType);
                    anim.TrackSetPath(trackIdx, trackPath);

                    int count = Math.Min(trackData.Times.Count, trackData.Values.Count);
                    for (int k = 0; k < count; k++)
                    {
                        double time = trackData.Times[k];
                        var rawVal = trackData.Values[k];
                        if (rawVal == null) continue;

                        if (tType == Godot.Animation.TrackType.Rotation3D && rawVal.Length >= 4)
                        {
                            var q = new Quaternion(rawVal[0], rawVal[1], rawVal[2], rawVal[3]);
                            anim.TrackInsertKey(trackIdx, time, q);
                        }
                        else if ((tType == Godot.Animation.TrackType.Position3D || tType == Godot.Animation.TrackType.Scale3D) && rawVal.Length >= 3)
                        {
                            var v = new Vector3(rawVal[0], rawVal[1], rawVal[2]);
                            anim.TrackInsertKey(trackIdx, time, v);
                        }
                    }
                }

                return anim;
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[ModWorkspaceSession] Failed to reconstruct animation from JSON for '{entry.CleanName}': {ex.Message}");
                return null;
            }
        }

        public string GetParticleOwnerHeroId(string vpcfPath)
        {
            if (string.IsNullOrEmpty(vpcfPath)) return null;
            string norm = vpcfPath.Replace('\\', '/').Trim();
            foreach (var hero in Manifest.Heroes.Values)
            {
                if (hero.Assets.Any(a => string.Equals(a.VpkPath?.Replace('\\', '/')?.Trim(), norm, StringComparison.OrdinalIgnoreCase) ||
                                         string.Equals(a.OwnerAssetPath?.Replace('\\', '/')?.Trim(), norm, StringComparison.OrdinalIgnoreCase)))
                {
                    return hero.HeroId;
                }
            }
            return null;
        }

        public WorkspaceMaterialEntry FindMaterialEntryForTexture(string vtexPath)
        {
            if (string.IsNullOrEmpty(vtexPath)) return null;
            string norm = vtexPath.Replace('\\', '/').Trim();
            foreach (var hero in Manifest.Heroes.Values)
            {
                foreach (var asset in hero.Assets)
                {
                    foreach (var mat in asset.Materials)
                    {
                        if (string.Equals(mat.MaterialPath?.Replace('\\', '/')?.Trim(), norm, StringComparison.OrdinalIgnoreCase) ||
                            mat.Textures.ContainsKey(vtexPath) ||
                            mat.Textures.ContainsKey(norm))
                        {
                            return mat;
                        }
                    }
                }
            }
            return null;
        }

        public Image GetStagedTexture(string vtexPath)
        {
            if (string.IsNullOrEmpty(vtexPath)) return null;

            // 1. Check in-memory cache first (0ms, zero disk latency, immune to async Task.Run race conditions)
            string normKey = vtexPath.Replace('\\', '/').Trim().ToLowerInvariant();
            if (_stagedImagesInMemory.TryGetValue(normKey, out var memImg) && memImg != null && !memImg.IsEmpty())
            {
                return (Image)memImg.Duplicate();
            }

            string directName = SanitizePathToFileName(vtexPath, ".png").ToLowerInvariant();
            if (_stagedImagesInMemory.TryGetValue(directName, out var memSan) && memSan != null && !memSan.IsEmpty())
            {
                return (Image)memSan.Duplicate();
            }

            string vtexBase = Path.GetFileName(normKey);
            foreach (var kvp in _stagedImagesInMemory)
            {
                if (string.Equals(Path.GetFileName(kvp.Key), vtexBase, StringComparison.OrdinalIgnoreCase) && kvp.Value != null && !kvp.Value.IsEmpty())
                {
                    return (Image)kvp.Value.Duplicate();
                }
            }

            // 2. Fallback to manifest relative paths on disk
            foreach (var hero in Manifest.Heroes.Values)
            {
                foreach (var asset in hero.Assets)
                {
                    if (asset.ModifiedTextures.TryGetValue(vtexPath, out var relPath))
                    {
                        string fullPath = Path.Combine(SessionDir, relPath);
                        if (File.Exists(fullPath))
                        {
                            var img = Image.LoadFromFile(fullPath);
                            if (img != null && !img.IsEmpty())
                            {
                                _stagedImagesInMemory[normKey] = (Image)img.Duplicate();
                                return img;
                            }
                        }
                    }
                }
            }

            // 3. Fallback direct file check
            string directFile = SanitizePathToFileName(vtexPath, ".png");
            string directPath = Path.Combine(TexturesDir, directFile);
            if (File.Exists(directPath))
            {
                var img = Image.LoadFromFile(directPath);
                if (img != null && !img.IsEmpty())
                {
                    _stagedImagesInMemory[normKey] = (Image)img.Duplicate();
                    return img;
                }
            }

            return null;
        }

        public WorkspaceParticleOverrides GetStagedParticleOverrides(string vpcfPath)
        {
            if (string.IsNullOrEmpty(vpcfPath)) return null;

            foreach (var hero in Manifest.Heroes.Values)
            {
                var asset = hero.Assets.FirstOrDefault(a => string.Equals(a.VpkPath, vpcfPath, StringComparison.OrdinalIgnoreCase));
                if (asset != null && !string.IsNullOrEmpty(asset.ParticleOverridesFile))
                {
                    string fullPath = Path.Combine(SessionDir, asset.ParticleOverridesFile);
                    if (File.Exists(fullPath))
                    {
                        string json = File.ReadAllText(fullPath);
                        return JsonSerializer.Deserialize<WorkspaceParticleOverrides>(json, JsonOpts);
                    }
                }
            }

            string directName = SanitizePathToFileName(vpcfPath, "_overrides.json");
            string directPath = Path.Combine(ParticlesDir, directName);
            if (File.Exists(directPath))
            {
                string json = File.ReadAllText(directPath);
                return JsonSerializer.Deserialize<WorkspaceParticleOverrides>(json, JsonOpts);
            }

            return null;
        }

        public bool HasStagedModifications(string vpkPath)
        {
            if (string.IsNullOrEmpty(vpkPath)) return false;
            foreach (var hero in Manifest.Heroes.Values)
            {
                if (hero.Assets.Any(a => string.Equals(a.VpkPath, vpkPath, StringComparison.OrdinalIgnoreCase) ||
                                        string.Equals(a.OwnerAssetPath, vpkPath, StringComparison.OrdinalIgnoreCase) ||
                                        a.ModifiedTextures.ContainsKey(vpkPath)))
                {
                    return true;
                }
            }
            return false;
        }

        public void RemoveMaterial(string heroId, string ownerAssetPath, string materialPath)
        {
            if (string.IsNullOrEmpty(heroId) || !Manifest.Heroes.TryGetValue(heroId, out var hero)) return;

            var asset = !string.IsNullOrEmpty(ownerAssetPath)
                ? hero.Assets.FirstOrDefault(a => string.Equals(a.VpkPath, ownerAssetPath, StringComparison.OrdinalIgnoreCase) ||
                                                  string.Equals(a.OwnerAssetPath, ownerAssetPath, StringComparison.OrdinalIgnoreCase))
                : null;
            asset ??= hero.Assets.FirstOrDefault(a => a.Materials.Any(m => string.Equals(m.MaterialPath, materialPath, StringComparison.OrdinalIgnoreCase)));
            if (asset != null)
            {
                var mat = asset.Materials.FirstOrDefault(m => string.Equals(m.MaterialPath, materialPath, StringComparison.OrdinalIgnoreCase));
                if (mat != null)
                {
                    foreach (var (vtex, rel) in mat.Textures)
                    {
                        string fullPath = Path.Combine(SessionDir, rel);
                        if (File.Exists(fullPath))
                        {
                            try { File.Delete(fullPath); } catch { }
                        }
                        asset.ModifiedTextures.Remove(vtex);
                        string normVtex = vtex.Replace('\\', '/').Trim().ToLowerInvariant();
                        _stagedImagesInMemory.Remove(normVtex);
                        _stagedImagesInMemory.Remove(SanitizePathToFileName(vtex, ".png").ToLowerInvariant());
                    }
                    asset.Materials.Remove(mat);
                }

                if (asset.Materials.Count == 0 && asset.ModifiedTextures.Count == 0 && string.IsNullOrEmpty(asset.ParticleOverridesFile))
                {
                    hero.Assets.Remove(asset);
                }
            }

            if (hero.Assets.Count == 0 && hero.CustomModel == null && (hero.Accessories == null || hero.Accessories.Count == 0))
            {
                Manifest.Heroes.Remove(heroId);
            }

            SaveManifest();
            HasUnsavedChanges = true;
            OnWorkspaceChanged?.Invoke();
            OnAssetDiscarded?.Invoke(heroId, materialPath);
        }

        public void RemoveAsset(string heroId, string vpkPath)
        {
            if (string.IsNullOrEmpty(heroId) || !Manifest.Heroes.TryGetValue(heroId, out var hero)) return;

            var asset = hero.Assets.FirstOrDefault(a => string.Equals(a.VpkPath, vpkPath, StringComparison.OrdinalIgnoreCase) ||
                                                       string.Equals(a.OwnerAssetPath, vpkPath, StringComparison.OrdinalIgnoreCase));
            if (asset != null)
            {
                foreach (var vtex in asset.ModifiedTextures.Keys)
                {
                    string normVtex = vtex.Replace('\\', '/').Trim().ToLowerInvariant();
                    _stagedImagesInMemory.Remove(normVtex);
                    _stagedImagesInMemory.Remove(SanitizePathToFileName(vtex, ".png").ToLowerInvariant());
                }
                foreach (var rel in asset.ModifiedTextures.Values)
                {
                    string fullPath = Path.Combine(SessionDir, rel);
                    if (File.Exists(fullPath))
                    {
                        try { File.Delete(fullPath); } catch { }
                    }
                }
                if (!string.IsNullOrEmpty(asset.ParticleOverridesFile))
                {
                    string fullPath = Path.Combine(SessionDir, asset.ParticleOverridesFile);
                    if (File.Exists(fullPath))
                    {
                        try { File.Delete(fullPath); } catch { }
                    }
                }
                if (asset.Animations != null)
                {
                    foreach (var anim in asset.Animations)
                    {
                        if (!string.IsNullOrEmpty(anim.RelativeJsonPath))
                        {
                            string full = Path.Combine(SessionDir, anim.RelativeJsonPath);
                            if (File.Exists(full)) { try { File.Delete(full); } catch { } }
                        }
                        if (!string.IsNullOrEmpty(anim.RelativeVnmclipPath))
                        {
                            string full = Path.Combine(SessionDir, anim.RelativeVnmclipPath);
                            if (File.Exists(full)) { try { File.Delete(full); } catch { } }
                        }
                        OnAnimationDiscarded?.Invoke(heroId, anim.CleanName);
                    }
                    asset.Animations.Clear();
                }
                hero.Assets.Remove(asset);
            }

            if (hero.Assets.Count == 0 && hero.CustomModel == null && (hero.Accessories == null || hero.Accessories.Count == 0))
            {
                Manifest.Heroes.Remove(heroId);
            }

            SaveManifest();
            HasUnsavedChanges = true;
            OnWorkspaceChanged?.Invoke();
            OnAssetDiscarded?.Invoke(heroId, vpkPath);
        }

        public void StageCustomModel(
            string heroId,
            string heroDisplayName,
            string sourceModelFilePath,
            DeadlockPlayground.Importer.CustomModelAutoRigger.RiggingOptions options)
        {
            if (string.IsNullOrEmpty(heroId) || string.IsNullOrEmpty(sourceModelFilePath) || !File.Exists(sourceModelFilePath))
                return;

            try
            {
                if (string.IsNullOrEmpty(CustomModelsDir))
                    CustomModelsDir = Path.Combine(SessionDir, "custom_models");
                Directory.CreateDirectory(CustomModelsDir);

                string safeHero = heroId.Replace('/', '_').Replace('\\', '_').Trim('_');
                string origFileName = Path.GetFileName(sourceModelFilePath);
                string cleanLeaf = origFileName;
                if (cleanLeaf.StartsWith(safeHero + "_", StringComparison.OrdinalIgnoreCase))
                    cleanLeaf = cleanLeaf[(safeHero.Length + 1)..];
                string stagedFileName = $"{safeHero}_{cleanLeaf}";
                string destDiskPath = Path.Combine(CustomModelsDir, stagedFileName);
                string relPath = Path.Combine("custom_models", stagedFileName).Replace('\\', '/');

                if (!string.Equals(Path.GetFullPath(sourceModelFilePath), Path.GetFullPath(destDiskPath), StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(sourceModelFilePath, destDiskPath, overwrite: true);
                }

                if (!Manifest.Heroes.TryGetValue(heroId, out var heroEntry) || heroEntry == null)
                {
                    heroEntry = new WorkspaceHeroEntry
                    {
                        HeroId = heroId,
                        DisplayName = !string.IsNullOrEmpty(heroDisplayName) ? heroDisplayName : heroId
                    };
                    Manifest.Heroes[heroId] = heroEntry;
                }

                heroEntry.CustomModel = WorkspaceCustomModelEntry.FromRiggingOptions(relPath, cleanLeaf, options);
                heroEntry.CustomModel.SlotId = "";
                Manifest.LastActiveHeroId = heroId;
                Manifest.LastActiveAssetType = "CustomModel";
                Manifest.LastActiveAssetPath = relPath;

                SaveManifest();
                HasUnsavedChanges = true;
                OnWorkspaceChanged?.Invoke();
                GD.Print($"[ModWorkspaceSession] Staged custom 3D model replacement for '{heroId}': {relPath}");
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[ModWorkspaceSession] Failed to stage custom model: {ex.Message}");
            }
        }

        public void StageAccessory(
            string heroId,
            string heroDisplayName,
            string slotId,
            string accessoryDisplayName,
            string sourceModelFilePath,
            DeadlockPlayground.Importer.CustomModelAutoRigger.RiggingOptions options)
        {
            if (string.IsNullOrEmpty(heroId) || string.IsNullOrEmpty(slotId) ||
                string.IsNullOrEmpty(sourceModelFilePath) || !File.Exists(sourceModelFilePath))
                return;

            try
            {
                if (string.IsNullOrEmpty(CustomModelsDir))
                    CustomModelsDir = Path.Combine(SessionDir, "custom_models");
                string accDir = Path.Combine(CustomModelsDir, "accessories", slotId);
                Directory.CreateDirectory(accDir);

                string safeHero = heroId.Replace('/', '_').Replace('\\', '_').Trim('_');
                string origFileName = Path.GetFileName(sourceModelFilePath);
                string stagedFileName = $"{safeHero}_{origFileName}";
                string destDiskPath = Path.Combine(accDir, stagedFileName);
                string relPath = Path.Combine("custom_models", "accessories", slotId, stagedFileName).Replace('\\', '/');

                if (!string.Equals(Path.GetFullPath(sourceModelFilePath), Path.GetFullPath(destDiskPath), StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(sourceModelFilePath, destDiskPath, overwrite: true);
                }

                if (!Manifest.Heroes.TryGetValue(heroId, out var heroEntry) || heroEntry == null)
                {
                    heroEntry = new WorkspaceHeroEntry
                    {
                        HeroId = heroId,
                        DisplayName = !string.IsNullOrEmpty(heroDisplayName) ? heroDisplayName : heroId
                    };
                    Manifest.Heroes[heroId] = heroEntry;
                }

                heroEntry.Accessories ??= new List<WorkspaceAccessoryEntry>();
                var modelCfg = WorkspaceCustomModelEntry.FromRiggingOptions(relPath, origFileName, options);
                modelCfg.SlotId = slotId;
                modelCfg.AccessoryName = accessoryDisplayName ?? Path.GetFileNameWithoutExtension(origFileName);

                var existing = heroEntry.Accessories.FirstOrDefault(a => string.Equals(a.SlotId, slotId, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    existing.DisplayName = modelCfg.AccessoryName;
                    existing.Intent = (int)(options?.Intent ?? DeadlockPlayground.Importer.CustomModelAutoRigger.AccessoryIntent.AddAccessory);
                    existing.ModelConfig = modelCfg;
                }
                else
                {
                    heroEntry.Accessories.Add(new WorkspaceAccessoryEntry
                    {
                        SlotId = slotId,
                        DisplayName = modelCfg.AccessoryName,
                        Intent = (int)(options?.Intent ?? DeadlockPlayground.Importer.CustomModelAutoRigger.AccessoryIntent.AddAccessory),
                        Visible = true,
                        ModelConfig = modelCfg
                    });
                }

                Manifest.LastActiveHeroId = heroId;
                Manifest.LastActiveAssetType = "Accessory";
                Manifest.LastActiveAssetPath = relPath;

                SaveManifest();
                HasUnsavedChanges = true;
                OnWorkspaceChanged?.Invoke();
                GD.Print($"[ModWorkspaceSession] Staged accessory '{slotId}' ({modelCfg.AccessoryName}) for '{heroId}': {relPath}");
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[ModWorkspaceSession] Failed to stage accessory '{slotId}': {ex.Message}");
            }
        }

        public void DiscardAccessory(string heroId, string slotId)
        {
            if (string.IsNullOrEmpty(heroId) || string.IsNullOrEmpty(slotId)) return;
            if (!Manifest.Heroes.TryGetValue(heroId, out var heroEntry) || heroEntry?.Accessories == null)
                return;

            try
            {
                var target = heroEntry.Accessories.FirstOrDefault(a => string.Equals(a.SlotId, slotId, StringComparison.OrdinalIgnoreCase));
                if (target != null)
                {
                    string diskPath = ResolveCustomModelDiskPath(target.ModelConfig);
                    heroEntry.Accessories.Remove(target);

                    if (!string.IsNullOrEmpty(diskPath) && File.Exists(diskPath))
                    {
                        try { File.Delete(diskPath); } catch { }
                    }
                    string accSlotDir = Path.GetDirectoryName(diskPath);
                    if (!string.IsNullOrEmpty(accSlotDir) && Directory.Exists(accSlotDir) && Directory.GetFiles(accSlotDir).Length == 0)
                    {
                        try { Directory.Delete(accSlotDir, false); } catch { }
                    }
                }

                if (heroEntry.Assets.Count == 0 && heroEntry.CustomModel == null && heroEntry.Accessories.Count == 0)
                {
                    Manifest.Heroes.Remove(heroId);
                }

                SaveManifest();
                HasUnsavedChanges = true;
                OnWorkspaceChanged?.Invoke();
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[ModWorkspaceSession] Failed to discard accessory '{slotId}': {ex.Message}");
            }
        }

        public void RenameAccessorySlot(string heroId, string slotId, string newName)
        {
            if (string.IsNullOrEmpty(heroId) || string.IsNullOrEmpty(slotId) || string.IsNullOrWhiteSpace(newName))
                return;
            if (!Manifest.Heroes.TryGetValue(heroId, out var heroEntry) || heroEntry?.Accessories == null)
                return;

            var acc = heroEntry.Accessories.FirstOrDefault(a => string.Equals(a.SlotId, slotId, StringComparison.OrdinalIgnoreCase));
            if (acc == null) return;

            string trimmed = newName.Trim();
            acc.DisplayName = trimmed;
            if (acc.ModelConfig != null)
            {
                acc.ModelConfig.AccessoryName = trimmed;
            }
            SaveManifest();
            HasUnsavedChanges = true;
            OnWorkspaceChanged?.Invoke();
        }

        public void SetAccessoryVisibility(string heroId, string slotId, bool visible)
        {
            if (string.IsNullOrEmpty(heroId) || string.IsNullOrEmpty(slotId)) return;
            if (!Manifest.Heroes.TryGetValue(heroId, out var heroEntry) || heroEntry?.Accessories == null) return;
            var target = heroEntry.Accessories.FirstOrDefault(a => string.Equals(a.SlotId, slotId, StringComparison.OrdinalIgnoreCase));
            if (target == null || target.Visible == visible) return;
            target.Visible = visible;
            SaveManifest();
            HasUnsavedChanges = true;
            OnWorkspaceChanged?.Invoke();
        }

        public void DiscardCustomModel(string heroId, bool includeAccessories = true)
        {
            if (string.IsNullOrEmpty(heroId)) return;
            if (!Manifest.Heroes.TryGetValue(heroId, out var heroEntry) || heroEntry == null)
                return;

            try
            {
                if (heroEntry.CustomModel != null)
                {
                    string diskPath = ResolveCustomModelDiskPath(heroEntry.CustomModel);
                    heroEntry.CustomModel = null;

                    if (!string.IsNullOrEmpty(diskPath) && File.Exists(diskPath))
                    {
                        try { File.Delete(diskPath); } catch { }
                    }
                }

                if (includeAccessories && heroEntry.Accessories != null && heroEntry.Accessories.Count > 0)
                {
                    foreach (var acc in heroEntry.Accessories.ToList())
                    {
                        string accDisk = ResolveCustomModelDiskPath(acc?.ModelConfig);
                        if (!string.IsNullOrEmpty(accDisk) && File.Exists(accDisk))
                        {
                            try { File.Delete(accDisk); } catch { }
                        }
                    }
                    heroEntry.Accessories.Clear();
                }

                if (heroEntry.Assets.Count == 0 && heroEntry.CustomModel == null && (heroEntry.Accessories == null || heroEntry.Accessories.Count == 0))
                {
                    Manifest.Heroes.Remove(heroId);
                }

                SaveManifest();
                HasUnsavedChanges = true;
                OnWorkspaceChanged?.Invoke();
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[ModWorkspaceSession] Failed to discard custom model: {ex.Message}");
            }
        }

        public string ResolveCustomModelDiskPath(WorkspaceCustomModelEntry entry)
        {
            if (entry == null || string.IsNullOrEmpty(entry.RelativeModelPath) || string.IsNullOrEmpty(SessionDir))
                return null;
            return Path.Combine(SessionDir, entry.RelativeModelPath.Replace('/', Path.DirectorySeparatorChar));
        }

        public int GetTotalModifiedCount()
        {
            int count = 0;
            foreach (var hero in Manifest.Heroes.Values)
            {
                if (hero.CustomModel != null)
                {
                    count++;
                }

                if (hero.Accessories != null)
                {
                    count += hero.Accessories.Count;
                }

                foreach (var asset in hero.Assets)
                {
                    if (asset.Category == "ParticleVFX")
                    {
                        count++;
                    }
                    else
                    {
                        count += Math.Max(1, asset.Materials.Count);
                    }

                    if (asset.Animations != null)
                    {
                        count += asset.Animations.Count;
                    }
                }
            }
            return count;
        }

        public List<WorkspaceAssetEntry> GetAllModifiedAssets()
        {
            var list = new List<WorkspaceAssetEntry>();
            foreach (var hero in Manifest.Heroes.Values)
            {
                list.AddRange(hero.Assets);
            }
            return list;
        }

        public void StageRawLayerImage(string layerKey, string fileName, Image layerImg)
        {
            if (layerImg == null || layerImg.IsEmpty()) return;
            Directory.CreateDirectory(TexturesDir);
            var copy = (Image)layerImg.Duplicate();
            if (copy.HasMipmaps()) copy.ClearMipmaps();
            string normKey = layerKey.Replace('\\', '/').Trim().ToLowerInvariant();
            _stagedImagesInMemory[normKey] = (Image)copy.Duplicate();
            _stagedImagesInMemory[fileName.ToLowerInvariant()] = (Image)copy.Duplicate();
            string diskPath = Path.Combine(TexturesDir, fileName);
            copy.SavePng(diskPath);
        }

       public bool PackageProject(string targetZipOrDirPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(targetZipOrDirPath)) return false;

                // Forzar extensión .dpp si no tiene extensión válida
                if (!targetZipOrDirPath.EndsWith(".dpp", StringComparison.OrdinalIgnoreCase) &&
                    !targetZipOrDirPath.EndsWith(".dlproj", StringComparison.OrdinalIgnoreCase) &&
                    !targetZipOrDirPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    targetZipOrDirPath += ".dpp";
                }

                Directory.CreateDirectory(TexturesDir);

                // Asegurar que TODAS las imágenes en memoria (_stagedImagesInMemory) estén escritas en disco antes de zipear!
                foreach (var hero in Manifest.Heroes.Values)
                {
                    foreach (var asset in hero.Assets)
                    {
                        foreach (var (vtexPath, relPath) in asset.ModifiedTextures)
                        {
                            string fullDiskPath = Path.Combine(SessionDir, relPath);
                            if (!File.Exists(fullDiskPath))
                            {
                                var memImg = GetStagedTexture(vtexPath);
                                if (memImg != null && !memImg.IsEmpty())
                                {
                                    if (memImg.HasMipmaps()) memImg.ClearMipmaps();
                                    Directory.CreateDirectory(Path.GetDirectoryName(fullDiskPath));
                                    memImg.SavePng(fullDiskPath);
                                }
                            }
                        }

                        foreach (var mat in asset.Materials)
                        {
                            if (mat.Layers == null) continue;
                            foreach (var le in mat.Layers)
                            {
                                if (string.IsNullOrEmpty(le.LayerPngRelPath)) continue;
                                string fullLayerDisk = Path.Combine(SessionDir, le.LayerPngRelPath);
                                if (!File.Exists(fullLayerDisk))
                                {
                                    string fn = Path.GetFileName(le.LayerPngRelPath).ToLowerInvariant();
                                    if (_stagedImagesInMemory.TryGetValue(fn, out var lMem) && lMem != null && !lMem.IsEmpty())
                                    {
                                        Directory.CreateDirectory(Path.GetDirectoryName(fullLayerDisk));
                                        lMem.SavePng(fullLayerDisk);
                                    }
                                }
                            }
                        }
                    }
                }

                SaveManifest();

                if (File.Exists(targetZipOrDirPath))
                {
                    File.Delete(targetZipOrDirPath);
                }
                ZipFile.CreateFromDirectory(SessionDir, targetZipOrDirPath);

                HasUnsavedChanges = false;
                GD.Print($"[ModWorkspaceSession] Successfully packaged project to '{targetZipOrDirPath}'");
                return true;
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[ModWorkspaceSession] Failed to package project: {ex.Message}");
                return false;
            }
        }

        public bool UnpackProject(string sourceZipOrDirPath)
        {
            try
            {
                if (!File.Exists(sourceZipOrDirPath) && !Directory.Exists(sourceZipOrDirPath))
                {
                    GD.PrintErr($"[ModWorkspaceSession] Source project path does not exist: {sourceZipOrDirPath}");
                    return false;
                }

                // Limpiar caché en RAM y carpeta de sesión actual
                _stagedImagesInMemory.Clear();
                if (Directory.Exists(SessionDir))
                {
                    Directory.Delete(SessionDir, true);
                }
                Directory.CreateDirectory(SessionDir);

                if (File.Exists(sourceZipOrDirPath))
                {
                    ZipFile.ExtractToDirectory(sourceZipOrDirPath, SessionDir);
                }
                else if (Directory.Exists(sourceZipOrDirPath))
                {
                    CopyDirectory(sourceZipOrDirPath, SessionDir);
                }

                Initialize();
                HasUnsavedChanges = false;
                OnWorkspaceChanged?.Invoke();

                GD.Print($"[ModWorkspaceSession] Successfully unpacked project from '{sourceZipOrDirPath}' ({Manifest.Heroes.Count} heroes)");
                return true;
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[ModWorkspaceSession] Failed to unpack project: {ex.Message}");
                return false;
            }
        }

        private static void CopyDirectory(string sourceDir, string destinationDir)
        {
            var dir = new DirectoryInfo(sourceDir);
            DirectoryInfo[] dirs = dir.GetDirectories();
            Directory.CreateDirectory(destinationDir);

            foreach (FileInfo file in dir.GetFiles())
            {
                string targetFilePath = Path.Combine(destinationDir, file.Name);
                file.CopyTo(targetFilePath, true);
            }

            foreach (DirectoryInfo subDir in dirs)
            {
                string newDestinationDir = Path.Combine(destinationDir, subDir.Name);
                CopyDirectory(subDir.FullName, newDestinationDir);
            }
        }
    }
}
