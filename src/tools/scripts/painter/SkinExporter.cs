using Godot;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using System.Runtime.InteropServices;
using SkiaSharp;
using ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using VrfMaterial = ValveResourceFormat.ResourceTypes.Material;
using DeadlockPlayground.Catalog;
using DeadlockPlayground.Materials;
using DeadlockPlayground.Workspace;
using DeadlockPlayground.Importer;

namespace DeadlockPlayground.Painter
{
    public class OriginalVtexMetadata
    {
        public string VtexCPath { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public string Format { get; set; } = "BC7";
        public VTexFlags Flags { get; set; } = VTexFlags.SUGGEST_CLAMPS | VTexFlags.SUGGEST_CLAMPT;
        public int NumMipLevels { get; set; }
        public byte[] RawAlpha { get; set; }
        public Image BaseRgbaImage { get; set; }
        public bool HasNonUniformAlpha { get; set; }
    }

    public enum ExportLogLevel
    {
        Info,
        Success,
        Warning,
        Error
    }

    public class ExportProgressReport
    {
        public float Progress { get; set; } // 0.0 to 1.0
        public string Message { get; set; }
        public ExportLogLevel Level { get; set; } = ExportLogLevel.Info;

        public ExportProgressReport(float progress, string message, ExportLogLevel level = ExportLogLevel.Info)
        {
            Progress = progress;
            Message = message;
            Level = level;
        }
    }

    public partial class ModExportConfig : RefCounted
    {
        public string ModName { get; set; } = "CustomSkin";
        public string HeroCodename { get; set; } = "hero";
        public string HeroDisplayName { get; set; } = "Hero";
        public List<SubmeshNodeInfo> TargetSubmeshes { get; set; } = new();
        public int Resolution { get; set; } = 2048; // 1024, 2048, 4096
        public bool InstallDirectlyToGame { get; set; } = true;
        public string CustomExportFolder { get; set; } = "";
        public string DeadlockGamePath { get; set; } = "";
        public string CsdkPath { get; set; } = "";
        public string ResourceCompilerPath { get; set; } = "";
        public string SourceModVpkPath { get; set; } = "";
        public bool MergeWithSourceModVpk { get; set; } = true;
        public bool PrioritizeNewModOverSource { get; set; } = true;
        public bool OverwriteSourceModInAddons { get; set; } = false;
        public Image PreBakedAtlas { get; set; } // Pre-extracted on Godot main thread
        public Dictionary<int, Image> PreBakedAtlasesBySurface { get; set; } = new();
        public Dictionary<int, Image> PreExtractedBaseImages { get; set; } = new();
        public Dictionary<int, (Vector2 pos, Vector2 size)> SubmeshAtlasRects { get; set; } = new();
        public Dictionary<string, Image> PreBakedAtlasesBySubmesh { get; set; } = new();
        public Dictionary<string, Image> PreExtractedBaseImagesBySubmesh { get; set; } = new();
        public Dictionary<string, (Vector2 pos, Vector2 size)> SubmeshAtlasRectsBySubmesh { get; set; } = new();
        public Dictionary<(string subKey, MaterialTextureChannel channel), Image> PreBakedChannelsBySubmesh { get; set; } = new();
        public Dictionary<(string subKey, MaterialTextureChannel channel), Image> PreExtractedChannelOriginalsBySubmesh { get; set; } = new();
        public HashSet<string> ExcludedAssetVpkPaths { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public bool PreserveBaseAlpha { get; set; } = true;
        public HashSet<string> SubmeshesWithEditedAlpha { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> SubmeshesWithEditedBaseTexture { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public class SkinExportResult
    {
        public bool Success { get; set; }
        public string PngPath { get; set; }
        public string VmatPath { get; set; }
        public string VpkPath { get; set; }
        public string ModDirectory { get; set; }
        public string ErrorMessage { get; set; }
    }

    public partial class SkinExporter : RefCounted
    {
        private readonly SkinLayerManager _layerManager;

        public SkinExporter(SkinLayerManager layerManager)
        {
            _layerManager = layerManager;
        }

        /// <summary>
        /// Captures the active GPU composite atlas image on the main thread before starting background compilation.
        /// </summary>
        public Image CaptureAtlasImage()
        {
            return _layerManager?.BakeCompositeImage();
        }

        /// <summary>
        /// Executes the end-to-end automated mod export pipeline in a background thread.
        /// Step A: Bake composite textures to PNG.
        /// Step B: Generate KV3 .vmat files.
        /// Step C: Compile via headless resourcecompiler.exe CLI.
        /// Step D: Package into VPK archive with ValvePak (merging base mod VPK if editing a modded hero) and clean up staging.
        /// </summary>
        public async Task<SkinExportResult> ExportModAsync(
            ModExportConfig config,
            IProgress<ExportProgressReport> progress,
            CancellationToken ct)
        {
            var result = new SkinExportResult();

            if (config == null)
            {
                result.Success = false;
                result.ErrorMessage = "Export configuration is null.";
                return result;
            }

            // Auto-discover SourceModVpkPath on the main thread if not explicitly populated on config
            if (string.IsNullOrWhiteSpace(config.SourceModVpkPath))
            {
                if (config.TargetSubmeshes != null)
                {
                    foreach (var sm in config.TargetSubmeshes)
                    {
                        if (!string.IsNullOrWhiteSpace(sm?.SourceAddonVpkPath) && File.Exists(sm.SourceAddonVpkPath))
                        {
                            config.SourceModVpkPath = sm.SourceAddonVpkPath;
                            break;
                        }
                        if (sm?.Mesh != null && GodotObject.IsInstanceValid(sm.Mesh) && sm.Mesh.HasMeta("SourceAddonVpkPath"))
                        {
                            string metaVpk = sm.Mesh.GetMeta("SourceAddonVpkPath").AsString();
                            if (!string.IsNullOrWhiteSpace(metaVpk) && File.Exists(metaVpk))
                            {
                                config.SourceModVpkPath = metaVpk;
                                break;
                            }
                        }
                    }
                }

                if (string.IsNullOrWhiteSpace(config.SourceModVpkPath) &&
                    ModWorkspaceSession.Instance != null &&
                    !string.IsNullOrWhiteSpace(config.HeroCodename) &&
                    ModWorkspaceSession.Instance.Manifest.Heroes.TryGetValue(config.HeroCodename, out var wsHero) &&
                    !string.IsNullOrWhiteSpace(wsHero?.SourceAddonVpkPath) &&
                    File.Exists(wsHero.SourceAddonVpkPath))
                {
                    config.SourceModVpkPath = wsHero.SourceAddonVpkPath;
                }

                if (string.IsNullOrWhiteSpace(config.SourceModVpkPath) && Engine.GetMainLoop() is SceneTree tree && tree.Root != null)
                {
                    var vpkLoader = tree.Root.FindChild("VpkLoaderTest", true, false) as global::VpkLoaderTest;
                    if (vpkLoader != null && !string.IsNullOrWhiteSpace(vpkLoader.ActiveAddonVpkPath) && File.Exists(vpkLoader.ActiveAddonVpkPath))
                    {
                        config.SourceModVpkPath = vpkLoader.ActiveAddonVpkPath;
                    }
                }
            }

            // Pre-extract all textures, materials, and atlas rects on the Godot MAIN THREAD before entering worker thread
            var exportableChannels = new[]
            {
                MaterialTextureChannel.AmbientOcclusion,
                MaterialTextureChannel.Normal,
                MaterialTextureChannel.Roughness,
                MaterialTextureChannel.Shadow,
                MaterialTextureChannel.SelfIllum,
                MaterialTextureChannel.TintMask,
                MaterialTextureChannel.Metalness,
                MaterialTextureChannel.RimMask,
                MaterialTextureChannel.OutlineMask
            };

            bool HasAnyChannelEdits(SubmeshNodeInfo smInfo)
            {
                if (smInfo == null) return false;
                foreach (var ch in exportableChannels)
                {
                    if ((_layerManager != null && _layerManager.HasChannelEdits(smInfo, ch)) ||
                        (smInfo.Channels?.GetChannel(ch)?.IsDirty ?? false))
                    {
                        return true;
                    }
                }
                return _layerManager != null && _layerManager.HasChannelEdits(smInfo, MaterialTextureChannel.Color);
            }

var actuallyPaintedNonCustomKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
         if (config.TargetSubmeshes != null)
         {
             foreach (var sm in config.TargetSubmeshes)
             {
                 if (sm?.Mesh == null || sm.IsCustomImportedMesh) continue;
                 string matKeySm = _layerManager?.GetMaterialKey(sm.Mesh, sm.SurfaceIndex) ?? sm.MaterialName;
                 bool hasActualPaint = (_layerManager != null && (
                     _layerManager.HasPaintStrokes(matKeySm) ||
                     _layerManager.HasEditedBaseTextureForMaterial(matKeySm) ||
                     _layerManager.HasEditedAlphaForMaterial(matKeySm))) || HasAnyChannelEdits(sm);
                 if (hasActualPaint)
                     actuallyPaintedNonCustomKeys.Add(ResolveSubmeshKey(sm));
             }
         }

            var candidateSubmeshes = config.TargetSubmeshes?.Where(s => s.IsSelected && (s.IsDirty || HasAnyChannelEdits(s))).ToList();
            if (candidateSubmeshes == null || candidateSubmeshes.Count == 0)
            {
                candidateSubmeshes = config.TargetSubmeshes ?? new List<SubmeshNodeInfo>();
            }

            if (_layerManager != null)
            {
                config.PreserveBaseAlpha = _layerManager.PreserveBaseAlpha;
            }

            foreach (var submesh in candidateSubmeshes)
            {
                if (submesh == null) continue;
                int sIdx = submesh.SurfaceIndex;
                string subKey = ResolveSubmeshKey(submesh);
                string matKeySub = submesh.Mesh != null ? (_layerManager?.GetMaterialKey(submesh.Mesh, sIdx) ?? submesh.OriginalVmatPath) : submesh.OriginalVmatPath;

                if (_layerManager != null && _layerManager.HasEditedAlphaForMaterial(matKeySub))
                {
                    config.SubmeshesWithEditedAlpha.Add(subKey);
                }
                if (_layerManager != null && _layerManager.HasEditedBaseTextureForMaterial(matKeySub))
                {
                    config.SubmeshesWithEditedBaseTexture.Add(subKey);
                }

                if (!config.PreBakedAtlasesBySubmesh.ContainsKey(subKey))
                {
                    var baked = _layerManager?.BakeCompositeImageForSubmesh(submesh);
                    if (baked != null && !baked.IsEmpty())
                    {
                        config.PreBakedAtlasesBySubmesh[subKey] = baked;
                        config.PreBakedAtlasesBySurface[sIdx] = baked;
                    }
                    else if (candidateSubmeshes.Count == 1 && config.PreBakedAtlas != null && !config.PreBakedAtlas.IsEmpty())
                    {
                        // Only allow the single-atlas fallback when exporting a single submesh;
                        // on multi-material heroes, assigning PreBakedAtlas (the active material's atlas)
                        // to other submeshes duplicates the active material's texture across all materials!
                        config.PreBakedAtlasesBySubmesh[subKey] = config.PreBakedAtlas;
                        config.PreBakedAtlasesBySurface[sIdx] = config.PreBakedAtlas;
                    }
                }

                // Pre-bake edited non-color material channels on the main thread
                foreach (var ch in exportableChannels)
                {
                    bool hasEdits = (_layerManager != null && _layerManager.HasChannelEdits(submesh, ch))
                                 || (submesh.Channels?.GetChannel(ch)?.IsDirty ?? false);
                    if (hasEdits && !config.PreBakedChannelsBySubmesh.ContainsKey((subKey, ch)))
                    {
                        var chBaked = _layerManager?.BakeChannelImageForSubmesh(submesh, ch)
                                   ?? submesh.Channels?.GetChannel(ch)?.SourceImage;
                        if (chBaked != null && !chBaked.IsEmpty())
                        {
                            config.PreBakedChannelsBySubmesh[(subKey, ch)] = (Image)chBaked.Duplicate();
                        }
                    }

                    // Also snapshot Normal/Roughness originals on main thread if either side is edited (for g_tNormalRoughness repacking)
                    if ((ch == MaterialTextureChannel.Normal || ch == MaterialTextureChannel.Roughness) &&
                        !config.PreExtractedChannelOriginalsBySubmesh.ContainsKey((subKey, ch)))
                    {
                        var origImg = submesh.Channels?.GetChannel(ch)?.SourceImage
                                   ?? submesh.Channels?.GetChannel(ch)?.Texture?.GetImage();
                        if (origImg != null && !origImg.IsEmpty())
                        {
                            config.PreExtractedChannelOriginalsBySubmesh[(subKey, ch)] = (Image)origImg.Duplicate();
                        }
                    }
                }

                if (!config.PreExtractedBaseImagesBySubmesh.ContainsKey(subKey) && submesh.Mesh != null)
                {
                    if (config.SubmeshesWithEditedBaseTexture.Contains(subKey) && _layerManager != null)
                    {
                        var editedBaseImg = _layerManager.ExportBaseTextureToImage(matKeySub);
                        if (editedBaseImg != null && !editedBaseImg.IsEmpty())
                        {
                            config.PreExtractedBaseImagesBySubmesh[subKey] = editedBaseImg;
                            config.PreExtractedBaseImages[sIdx] = editedBaseImg;
                        }
                    }

                    if (!config.PreExtractedBaseImagesBySubmesh.ContainsKey(subKey))
                    {
                        var mat = HeroMeshHierarchy.GetAuthenticMaterial(submesh.Mesh, submesh.SurfaceIndex)
                               ?? submesh.Mesh.GetSurfaceOverrideMaterial(submesh.SurfaceIndex) 
                               ?? submesh.Mesh.Mesh?.SurfaceGetMaterial(submesh.SurfaceIndex)
                               ?? submesh.Mesh.MaterialOverride;

                        if (mat != null)
                        {
                            var baseTex = SkinLayerManager.ExtractBaseTexture(mat);
                            if (baseTex != null)
                            {
                                var bImg = baseTex.GetImage();
                                if (bImg != null && !bImg.IsEmpty())
                                {
                                    if (bImg.IsCompressed()) bImg.Decompress();
                                    var dup = (Image)bImg.Duplicate();
                                    if (dup.HasMipmaps()) dup.ClearMipmaps();
                                    if (dup.GetFormat() != Image.Format.Rgba8) dup.Convert(Image.Format.Rgba8);
                                    byte[] bData = dup.GetData();
                                    if (bData != null)
                                    {
                                        int expectedBytes = dup.GetWidth() * dup.GetHeight() * 4;
                                        if (bData.Length > expectedBytes)
                                        {
                                            Array.Resize(ref bData, expectedBytes);
                                            dup = Image.CreateFromData(dup.GetWidth(), dup.GetHeight(), false, Image.Format.Rgba8, bData);
                                        }
                                    }
                                    config.PreExtractedBaseImagesBySubmesh[subKey] = dup;
                                    config.PreExtractedBaseImages[sIdx] = dup;
                                }
                            }
                        }
                    }
                }

                if (!config.SubmeshAtlasRectsBySubmesh.ContainsKey(subKey) && submesh.Mesh != null && submesh.Mesh.MaterialOverlay is ShaderMaterial sm)
                {
                    var posVar = sm.GetShaderParameter("position_in_atlas");
                    var sizeVar = sm.GetShaderParameter("size_in_atlas");
                    if (posVar.VariantType == Variant.Type.Vector2 && sizeVar.VariantType == Variant.Type.Vector2)
                    {
                        var rect = (posVar.AsVector2(), sizeVar.AsVector2());
                        config.SubmeshAtlasRectsBySubmesh[subKey] = rect;
                        config.SubmeshAtlasRects[sIdx] = rect;
                    }
                }
            }

            // Ensure IsCustomImportedMesh is populated on the main thread for all custom submeshes
            if (config.TargetSubmeshes != null)
            {
                foreach (var sm in config.TargetSubmeshes)
                {
                    if (sm == null || sm.IsCustomImportedMesh) continue;
                    if (sm.Mesh != null && GodotObject.IsInstanceValid(sm.Mesh))
                    {
                        if ((sm.Mesh.HasMeta("IsCustomImportedMesh") && sm.Mesh.GetMeta("IsCustomImportedMesh").AsBool()) ||
                            sm.Mesh.Name.ToString().StartsWith("Custom_"))
                        {
                            sm.IsCustomImportedMesh = true;
                        }
                        else
                        {
                            Node curr = sm.Mesh.GetParent();
                            while (curr != null)
                            {
                                if (curr.Name.ToString().Contains("Custom_Rigged_Character") ||
                                    curr.Name.ToString().StartsWith("Custom_Accessory_", StringComparison.Ordinal) ||
                                    curr.Name.ToString() == "Custom_Rigid_Accessory" ||
                                    (curr.HasMeta("IsCustomImportedMesh") && curr.GetMeta("IsCustomImportedMesh").AsBool()))
                                {
                                    sm.IsCustomImportedMesh = true;
                                    break;
                                }
                                curr = curr.GetParent();
                            }
                        }
                    }
                }
            }

            // Pre-extract custom imported meshes on the main thread to prevent cross-thread Godot crashes
            var preExtractedCustomMeshes = config.TargetSubmeshes?
                .Where(sm => sm.Mesh != null && GodotObject.IsInstanceValid(sm.Mesh) && sm.IsCustomImportedMesh && sm.Mesh.Visible)
                .Select(sm => sm.Mesh)
                .Distinct()
                .ToList() ?? new List<MeshInstance3D>();

            CustomModelExporter.CustomModelExportBundle preExtractedCustomBundle = null;
            if (preExtractedCustomMeshes.Count > 0)
            {
                var firstMesh = preExtractedCustomMeshes.FirstOrDefault() ?? config.TargetSubmeshes?.FirstOrDefault()?.Mesh;
                Skeleton3D skeleton = null;
                if (firstMesh != null && !firstMesh.Skeleton.IsEmpty)
                {
                    skeleton = firstMesh.GetNodeOrNull<Skeleton3D>(firstMesh.Skeleton);
                }
                if (skeleton == null && firstMesh != null)
                {
                    Node curr = firstMesh;
                    while (curr != null)
                    {
                        if (curr is Skeleton3D s) { skeleton = s; break; }
                        curr = curr.GetParent();
                    }
                    if (skeleton == null && firstMesh.GetTree()?.Root != null)
                    {
                        skeleton = CustomModelAutoRigger.FindSkeleton(firstMesh.GetTree().Root);
                    }
                }

                string resolvedHeroVmdlPath = DeadlockHeroCatalog.ResolveHero(config.HeroCodename)?.VmdlRelativePath;
                if (string.IsNullOrEmpty(resolvedHeroVmdlPath))
                {
                    resolvedHeroVmdlPath = $"models/heroes/{config.HeroCodename}/{config.HeroCodename}.vmdl_c";
                }

                // Resolve active custom physics/jiggle chains from the hero's CustomJiggleBoneDriver (or skeleton metadata)
                IEnumerable<BoneChainDef> exportedPhysicsChains = null;
                Node walk = skeleton?.GetParent() ?? firstMesh?.GetParent();
                while (walk != null && GodotObject.IsInstanceValid(walk))
                {
                    var jiggleDriver = walk.FindChild("CustomJiggleBoneDriver", false, false) as DeadlockPlayground.Animation.CustomJiggleBoneDriver;
                    if (jiggleDriver != null && GodotObject.IsInstanceValid(jiggleDriver))
                    {
                        exportedPhysicsChains = jiggleDriver.GetActiveChainDefinitions();
                        break;
                    }
                    walk = walk.GetParent();
                }

                preExtractedCustomBundle = CustomModelExporter.BuildExportBundle(
                    preExtractedCustomMeshes,
                    skeleton,
                    config.HeroCodename,
                    heroModelVpkPath: resolvedHeroVmdlPath,
                    compilerExe: config.ResourceCompilerPath,
                    deadlockGamePath: config.DeadlockGamePath,
                    preservedChains: exportedPhysicsChains
                );

                // Register kept visible vanilla props & split composite surfaces (e.g. shotgun, crossbow, stake)
                // Optimization: if ALL surfaces of a composite hero mesh are kept & unpainted (e.g. "Add Hat Only" mod),
                // keep the original standalone .dmx file untouched rather than decomposing vanilla body geometry to SMD.
                if (preExtractedCustomBundle != null && config.TargetSubmeshes != null)
                {
                    bool anySelectedFlag = config.TargetSubmeshes.Any(s => s.IsSelected);
                    var keptCompositeMeshes = new List<MeshInstance3D>();
                    var keptStandaloneNames = new List<string>();
                    var fullyKeptParents = new HashSet<MeshInstance3D>();

                    var byParent = config.TargetSubmeshes
                        .Where(sm => sm?.Mesh != null && GodotObject.IsInstanceValid(sm.Mesh) && !sm.IsCustomImportedMesh && sm.Mesh.HasMeta("ParentCompositeMesh"))
                        .GroupBy(sm => sm.Mesh.GetMeta("ParentCompositeMesh").As<MeshInstance3D>())
                        .Where(g => g.Key != null && GodotObject.IsInstanceValid(g.Key));

                    foreach (var grp in byParent)
                    {
                        var parentMi = grp.Key;
                        int totalSurfs = parentMi.Mesh?.GetSurfaceCount() ?? 0;
                        if (totalSurfs <= 0) continue;

                        bool allSurfsKept = true;
                        for (int s = 0; s < totalSurfs; s++)
                        {
                            if (parentMi.HasMeta($"SurfaceKept_{s}") && !parentMi.GetMeta($"SurfaceKept_{s}").AsBool())
                            {
                                allSurfsKept = false;
                                break;
                            }
                        }
                        if (!allSurfsKept) continue;

                        bool isParentDeformed = parentMi.HasMeta("IsDeformedOriginalMesh") && parentMi.GetMeta("IsDeformedOriginalMesh").AsBool();
                        bool allSelectedAndVisible = grp.Count() >= totalSurfs && grp.All(sm =>
                            sm.Mesh.Visible &&
                            (!anySelectedFlag || sm.IsSelected) &&
                            (_layerManager == null || !_layerManager.HasPaintStrokes(_layerManager.GetMaterialKey(sm.Mesh, sm.SurfaceIndex) ?? sm.MaterialName)) &&
                            !sm.IsDirty &&
                            !(sm.Mesh.HasMeta("IsDeformedOriginalMesh") && sm.Mesh.GetMeta("IsDeformedOriginalMesh").AsBool()));

                        if (allSelectedAndVisible && !isParentDeformed)
                        {
                            fullyKeptParents.Add(parentMi);
                            keptStandaloneNames.Add(parentMi.Name.ToString());
                        }
                    }

                    foreach (var sm in config.TargetSubmeshes)
                    {
                        if (sm?.Mesh == null || !GodotObject.IsInstanceValid(sm.Mesh) || sm.IsCustomImportedMesh || !sm.Mesh.Visible)
                            continue;
                        if (anySelectedFlag && !sm.IsSelected)
                            continue;
                        bool isDeformed = sm.Mesh.HasMeta("IsDeformedOriginalMesh") && sm.Mesh.GetMeta("IsDeformedOriginalMesh").AsBool();
                        if (sm.Mesh.HasMeta("ParentCompositeMesh"))
                        {
                            var pMi = sm.Mesh.GetMeta("ParentCompositeMesh").As<MeshInstance3D>();
                            if (pMi != null && fullyKeptParents.Contains(pMi))
                                continue;
                            keptCompositeMeshes.Add(sm.Mesh);
                        }
                        else if (isDeformed)
                        {
                            keptCompositeMeshes.Add(sm.Mesh);
                        }
                        else
                        {
                            keptStandaloneNames.Add(!string.IsNullOrEmpty(sm.RawName) ? sm.RawName : sm.Mesh.Name.ToString());
                        }
                    }
                    CustomModelExporter.AddPreservedVanillaMeshes(preExtractedCustomBundle, keptCompositeMeshes, keptStandaloneNames, skeleton);
                }

                // If the user actually painted on a specific custom submesh in the 3D painter, merge the baked paint strokes
                // directly into preExtractedCustomBundle.Submeshes so CustomModelExporter compiles the painted texture.
                // If the user did NOT paint strokes on that submesh, leave the original custom texture/color untouched!
  if (preExtractedCustomBundle != null && config.TargetSubmeshes != null)
             {
                 foreach (var sm in config.TargetSubmeshes)
                 {
                     if (sm?.Mesh == null || !sm.IsCustomImportedMesh) continue;
                     string smKey = !string.IsNullOrEmpty(sm.DisplayName) ? sm.DisplayName : sm.Mesh.Name.ToString();
                     string matKey = _layerManager?.GetMaterialKey(sm.Mesh, sm.SurfaceIndex) ?? sm.MaterialName;
                     bool hasPaint = (_layerManager != null && (
                         _layerManager.HasPaintStrokes(matKey) ||
                         _layerManager.HasEditedBaseTextureForMaterial(matKey) ||
                         _layerManager.HasEditedAlphaForMaterial(matKey))) || sm.IsDirty;
                     if (!hasPaint) continue;

                     int targetRes = config.Resolution > 0 ? config.Resolution : 2048;
                     Image blendedImg = ExtractAndBlendSubmeshImage(config, sm, targetRes);
                     if (blendedImg != null && !blendedImg.IsEmpty())
                     {
                         byte[] paintedPng = blendedImg.SavePngToBuffer();
                         if (paintedPng != null && paintedPng.Length > 0)
                         {
                             string meshName = sm.Mesh.Name.ToString();
                             string rawName = sm.RawName ?? "";
                             string smMatStem = SkinLayerManager.ExtractMaterialStem(matKey);
                             var matchedMatNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                             foreach (var bundleSub in preExtractedCustomBundle.Submeshes)
                             {
                                 string bMatStem = SkinLayerManager.ExtractMaterialStem(bundleSub.MaterialName);
                                 bool match =
                                     string.Equals(bundleSub.SubmeshName, smKey, StringComparison.OrdinalIgnoreCase) ||
                                     string.Equals(bundleSub.SubmeshName, meshName, StringComparison.OrdinalIgnoreCase) ||
                                     string.Equals(bundleSub.SubmeshName, rawName, StringComparison.OrdinalIgnoreCase) ||
                                     bundleSub.SubmeshName.StartsWith(meshName + "_s", StringComparison.OrdinalIgnoreCase) ||
                                     meshName.StartsWith(bundleSub.SubmeshName + "_surf_", StringComparison.OrdinalIgnoreCase) ||
                                     (!string.IsNullOrEmpty(smMatStem) && string.Equals(bMatStem, smMatStem, StringComparison.OrdinalIgnoreCase));

                                 if (match)
                                 {
                                     bundleSub.AlbedoPngBytes = paintedPng;
                                     if (!string.IsNullOrWhiteSpace(bundleSub.MaterialName))
                                         matchedMatNames.Add(bundleSub.MaterialName);
                                 }
                             }

                             if (matchedMatNames.Count > 0)
                             {
                                 foreach (var bundleSub in preExtractedCustomBundle.Submeshes)
                                 {
                                     if (!string.IsNullOrWhiteSpace(bundleSub.MaterialName) && matchedMatNames.Contains(bundleSub.MaterialName))
                                     {
                                         bundleSub.AlbedoPngBytes = paintedPng;
                                     }
                                 }
                             }
                         }
                     }
                 }
             }
            }

            return await Task.Run(async () =>
            {
                string csdkPath = ResolveEffectiveCsdkPath(config.CsdkPath, config.DeadlockGamePath);
                string deadlockPath = config.DeadlockGamePath;

                progress?.Report(new ExportProgressReport(0.05f, $"[INFO] Starting export for '{config.HeroDisplayName}' ({config.ModName})..."));

                // Validate CSDK / ResourceCompiler
                string compilerExe = ResolveCompilerExecutable(csdkPath, config.ResourceCompilerPath);
                if (string.IsNullOrEmpty(compilerExe) || !File.Exists(compilerExe))
                {
                    var pm = new GamePathManager();
                    pm.LoadConfig();
                    compilerExe = pm.ResolveResourceCompilerExe();
                }

                if (string.IsNullOrEmpty(compilerExe) || !File.Exists(compilerExe))
                {
                    string err = $"resourcecompiler.exe not found. Please specify the path to resourcecompiler.exe in the Export dialog or Settings.";
                    progress?.Report(new ExportProgressReport(0.05f, $"[ERROR] {err}", ExportLogLevel.Error));
                    result.Success = false;
                    result.ErrorMessage = err;
                    return result;
                }

                // Auto-derive csdkPath if not set or invalid
                if (string.IsNullOrEmpty(csdkPath) || !Directory.Exists(csdkPath))
                {
                    string norm = compilerExe.Replace('\\', '/');
                    int binIdx = norm.IndexOf("/game/bin/win64", StringComparison.OrdinalIgnoreCase);
                    if (binIdx > 0)
                    {
                        csdkPath = norm.Substring(0, binIdx);
                    }
                    else
                    {
                        csdkPath = Path.GetDirectoryName(compilerExe);
                    }
                }

                progress?.Report(new ExportProgressReport(0.10f, $"[INFO] Compiler located: {compilerExe}"));
                progress?.Report(new ExportProgressReport(0.11f, $"[INFO] Staging root: {csdkPath}"));

                // Resolve Target Destination Directory
                string targetDir = config.InstallDirectlyToGame
                    ? Path.Combine(deadlockPath, "game", "citadel", "addons")
                    : config.CustomExportFolder;

                if (!Directory.Exists(targetDir))
                {
                    try { Directory.CreateDirectory(targetDir); }
                    catch (Exception ex)
                    {
                        string err = $"Failed to create target directory '{targetDir}': {ex.Message}";
                        progress?.Report(new ExportProgressReport(0.10f, $"[ERROR] {err}", ExportLogLevel.Error));
                        result.Success = false;
                        result.ErrorMessage = err;
                        return result;
                    }
                }

                // Check if we are modifying a base mod that already lives in targetDir (game/citadel/addons)
bool isSourceModInTargetAddons = false;
                if (!string.IsNullOrWhiteSpace(config.SourceModVpkPath) && File.Exists(config.SourceModVpkPath))
                {
                    string srcDir = Path.GetDirectoryName(Path.GetFullPath(config.SourceModVpkPath))?.TrimEnd('\\', '/') ?? "";
                    string tgtDirFull = Path.GetFullPath(targetDir).TrimEnd('\\', '/');
                    isSourceModInTargetAddons = string.Equals(srcDir, tgtDirFull, StringComparison.OrdinalIgnoreCase);
                }

                string vpkFileName;
                string shiftedOriginalModDestinationPath = null;

                if (config.InstallDirectlyToGame)
                {
                    if (isSourceModInTargetAddons && config.MergeWithSourceModVpk && config.OverwriteSourceModInAddons)
                    {
                        // Opción 1: Reemplazar el archivo original in-place
                        vpkFileName = Path.GetFileName(config.SourceModVpkPath);
                        progress?.Report(new ExportProgressReport(0.12f, $"[INFO] Overwriting existing addon in-place: {vpkFileName}"));
                    }
                    else if (isSourceModInTargetAddons && config.MergeWithSourceModVpk && config.PrioritizeNewModOverSource)
                    {
                        string srcFileName = Path.GetFileName(config.SourceModVpkPath);
                        string nextFreeSlotName = VpkIndexResolver.ResolveNextFileName(targetDir);
                        if (VpkIndexResolver.IsStandardPakFileName(srcFileName))
                        {
                            // Standard pakXX_dir.vpk: new mod claims the lower-index priority slot, original shifts to nextFreeSlotName
                            shiftedOriginalModDestinationPath = Path.Combine(targetDir, nextFreeSlotName);
                            vpkFileName = srcFileName;
                            progress?.Report(new ExportProgressReport(0.12f, $"[INFO] Priority Mode: New export (animation/recolor/VFX/misc) will take '{vpkFileName}' (higher priority) and original mod will be preserved as '{nextFreeSlotName}'."));
                        }
                        else
                        {
                            // Non-pakXX_dir.vpk (e.g. vansdicta_blue.vpk): moving old mod to pakXX_dir.vpk would give the OLD mod priority in Source 2!
                            // Install the new merged export into the standard pakXX_dir.vpk slot and archive the old VPK as .vpk.bak.
                            vpkFileName = nextFreeSlotName;
                            shiftedOriginalModDestinationPath = config.SourceModVpkPath + ".bak";
                            progress?.Report(new ExportProgressReport(0.12f, $"[INFO] Priority Mode: New export will take standard slot '{vpkFileName}' and original '{srcFileName}' will be archived as '{srcFileName}.bak'."));
                        }
                    }
                    else
                    {
                        // Opción 3: Guardar en el siguiente pakXX_dir.vpk libre sin mover el original
                        vpkFileName = VpkIndexResolver.ResolveNextFileName(targetDir);
                        int highest = VpkIndexResolver.GetExistingIndices(targetDir).LastOrDefault();
                        progress?.Report(new ExportProgressReport(0.12f, $"[INFO] Detected highest addon: pak{highest:D2}_dir.vpk. Assigned: {vpkFileName}"));
                    }
                }
                else
                {
                    vpkFileName = $"{config.ModName}.vpk";
                    progress?.Report(new ExportProgressReport(0.12f, $"[INFO] Target custom output file: {vpkFileName}"));
                }

                string finalVpkPath = Path.Combine(targetDir, vpkFileName);

                // Determine internal hero folder: models/heroes_staging/<hero> or models/heroes_wip/<hero>
                string heroFolder = ResolveHeroInternalFolder(config.HeroCodename, config.TargetSubmeshes);
                progress?.Report(new ExportProgressReport(0.14f, $"[INFO] Resolved internal hero path: {heroFolder}"));

                string contentStagingRoot = Path.Combine(csdkPath, "content", "citadel_addons", "_temp_export");
                string gameStagingRoot = Path.Combine(csdkPath, "game", "citadel_addons", "_temp_export");
                string contentMaterialsDir = Path.Combine(contentStagingRoot, heroFolder, "materials");
                string gameMaterialsDir = Path.Combine(gameStagingRoot, heroFolder, "materials");

                try
                {
                    if (Directory.Exists(contentStagingRoot)) Directory.Delete(contentStagingRoot, true);
                    if (Directory.Exists(gameStagingRoot)) Directory.Delete(gameStagingRoot, true);
                    Directory.CreateDirectory(contentMaterialsDir);
                }
                catch (Exception ex)
                {
                    string err = $"Failed to initialize staging folders: {ex.Message}";
                    progress?.Report(new ExportProgressReport(0.14f, $"[ERROR] {err}", ExportLogLevel.Error));
                    result.Success = false;
                    result.ErrorMessage = err;
                    return result;
                }

                bool hasAnyPreBakedChannels = config.PreBakedChannelsBySubmesh.Count > 0;
                var submeshesToExport = config.TargetSubmeshes != null && config.TargetSubmeshes.Count > 0
                    ? config.TargetSubmeshes.Where(s => s.IsSelected && (s.IsDirty || HasAnySubmeshPreBakedChannels(config, s))).ToList()
                    : new List<SubmeshNodeInfo>();

                bool hasCustomModel = preExtractedCustomBundle != null && preExtractedCustomBundle.Submeshes.Count > 0;

                if (submeshesToExport.Count == 0 && config.TargetSubmeshes != null && config.TargetSubmeshes.Count > 0)
                {
                    // Only fallback to all selected submeshes if we don't have a custom model bundle.
                    // For custom models, unpainted meshes should not generate dummy vtex files.
                    if (!hasCustomModel)
                    {
                        submeshesToExport = config.TargetSubmeshes.Where(s => s.IsSelected).ToList();
                    }
                }
                else if (hasCustomModel)
                {
                    // Custom imported meshes are compiled with their own .vmat_c and .vtex_c in Step 2.5 (CustomModelExporter).
                    // Kept vanilla weapons use vanilla .vmat remaps directly unless the user actually painted them in 3D,
                    // preventing spurious .vtex_c overrides on unpainted props.
                    submeshesToExport = submeshesToExport
                        .Where(s => !s.IsCustomImportedMesh &&
                                    (actuallyPaintedNonCustomKeys.Contains(ResolveSubmeshKey(s)) ||
                                     (_layerManager == null && s.IsDirty)))
                        .ToList();
                }

                var workspaceSession = ModWorkspaceSession.Instance;
                var workspaceAssets = new List<DeadlockPlayground.Workspace.WorkspaceAssetEntry>();
                if (workspaceSession != null && workspaceSession.Manifest.Heroes.TryGetValue(config.HeroCodename, out var targetHeroEntry) && targetHeroEntry != null)
                {
                    workspaceAssets.AddRange(targetHeroEntry.Assets);
                }                
                int workspaceTexCount = workspaceAssets.Sum(a => a.ModifiedTextures.Count);
                int workspaceVpcfCount = workspaceAssets.Count(a => a.Category == "ParticleVFX" && !string.IsNullOrEmpty(a.ParticleOverridesFile));
                int workspaceAnimCount = workspaceAssets.Sum(a => a.Animations?.Count ?? 0);
                int workspaceMatParamCount = workspaceAssets.Sum(a => a.Materials?.Count(m => m?.ParamOverrides != null && m.ParamOverrides.HasAnyOverride()) ?? 0);

                if (submeshesToExport.Count == 0 && workspaceTexCount == 0 && workspaceVpcfCount == 0 && workspaceAnimCount == 0 && workspaceMatParamCount == 0 && !hasCustomModel)
                {
                    string err = "No modified submeshes, custom models, or workspace assets selected for compilation.";
                    progress?.Report(new ExportProgressReport(0.15f, $"[WARNING] {err}", ExportLogLevel.Warning));
                    result.Success = false;
                    result.ErrorMessage = err;
                    return result;
                }

                Package baseVpkPackage = null;
                Package sourceModPackage = null;
                try
                {
                    // 1. Open Source Mod VPK if editing a modded hero (Priority 1 for custom .vmat_c / .vtex_c resolution & merging)
                    if (!string.IsNullOrWhiteSpace(config.SourceModVpkPath) && File.Exists(config.SourceModVpkPath))
                    {
                        try
                        {
                            sourceModPackage = OpenSharedReadPackage(config.SourceModVpkPath);
                            progress?.Report(new ExportProgressReport(0.14f, $"[INFO] Mounted source mod VPK: {Path.GetFileName(config.SourceModVpkPath)}"));
                        }
                        catch (Exception ex)
                        {
                            GD.PrintErr($"[SkinExporter] Notice: Could not pre-read SourceModVpkPath '{config.SourceModVpkPath}': {ex.Message}");
                            sourceModPackage?.Dispose();
                            sourceModPackage = null;
                        }
                    }

                    // 2. Open base pak01_dir.vpk if available (Priority 2 fallback for vanilla textures/materials)
                    string baseVpkPath = !string.IsNullOrEmpty(config.DeadlockGamePath)
                        ? Path.Combine(config.DeadlockGamePath, "game", "citadel", "pak01_dir.vpk")
                        : null;

                    if (!string.IsNullOrEmpty(baseVpkPath) && File.Exists(baseVpkPath))
                    {
                        try
                        {
                            baseVpkPackage = OpenSharedReadPackage(baseVpkPath);
                        }
                        catch (Exception ex)
                        {
                            GD.Print($"[SkinExporter] Notice: Could not pre-read base pak01_dir.vpk: {ex.Message}");
                            baseVpkPackage?.Dispose();
                            baseVpkPackage = null;
                        }
                    }

                    // =========================================================================
                    // STEP A: Bake Composite Texture to High-Fidelity PNG & DMX VTEX (20%)
                    // =========================================================================
                    ct.ThrowIfCancellationRequested();
                    progress?.Report(new ExportProgressReport(0.15f, $"[INFO] Baking {submeshesToExport.Count} composite texture(s) ({config.Resolution}x{config.Resolution})..."));
                    var stagedTextures = new List<(string materialName, string pngPath, string vtexPath, string originalVtexCPath)>();
                    var processedHeroVtexCPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    foreach (var submesh in submeshesToExport)
                    {
                        ct.ThrowIfCancellationRequested();

                        string matName = ResolveSubmeshMaterialName(submesh, config.HeroCodename);
                        var allColorPaths = ResolveAllColorVtexCPaths(submesh, config.HeroCodename, sourceModPackage ?? baseVpkPackage, baseVpkPackage);

                        foreach (var colorVtexC in allColorPaths)
                        {
                            if (Source2MaterialHelper.IsEngineDummyOrPlaceholderTexture(colorVtexC))
                            {
                                progress?.Report(new ExportProgressReport(0.18f, $"[INFO] Skipping engine placeholder texture: {colorVtexC}"));
                                continue;
                            }

                            if (!processedHeroVtexCPaths.Add(colorVtexC))
                            {
                                progress?.Report(new ExportProgressReport(0.18f, $"[INFO] Skipping already staged texture for submesh '{submesh.DisplayName}' ({Path.GetFileName(colorVtexC)})"));
                                continue;
                            }

                            string cleanTexBaseName = Path.GetFileNameWithoutExtension(colorVtexC);
                            if (cleanTexBaseName.EndsWith("_c", StringComparison.OrdinalIgnoreCase))
                                cleanTexBaseName = cleanTexBaseName[..^2];

                            // Load original .vtex_c metadata (prioritizing sourceModPackage so modded hero textures/alpha are used!)
                            var origMeta = (sourceModPackage != null ? LoadOriginalVtexMetadata(colorVtexC, sourceModPackage, deadlockPath) : null)
                                        ?? LoadOriginalVtexMetadata(colorVtexC, baseVpkPackage, deadlockPath);

                            try
                            {
                                bool isTinySwatch = origMeta != null && (origMeta.Width < 64 || origMeta.Height < 64);
                                if (isTinySwatch && !submesh.IsDirty && !actuallyPaintedNonCustomKeys.Contains(ResolveSubmeshKey(submesh)))
                                {
                                    progress?.Report(new ExportProgressReport(0.18f, $"[INFO] Skipping untouched low-res swatch texture ({origMeta.Width}x{origMeta.Height}): {colorVtexC}"));
                                    continue;
                                }

                                progress?.Report(new ExportProgressReport(0.18f, $"[INFO] Submesh '{submesh.DisplayName}' intercepts: {colorVtexC}"));

                                string pngFileName = $"{cleanTexBaseName}.png";
                                string fullPngPath = Path.Combine(contentMaterialsDir, pngFileName);

                                if (origMeta != null)
                                {
                                    progress?.Report(new ExportProgressReport(0.19f, $"[INFO] Loaded original texture specs for '{cleanTexBaseName}': {origMeta.Width}x{origMeta.Height}, Format={origMeta.Format}, Flags={origMeta.Flags}, NonUniformAlpha={origMeta.HasNonUniformAlpha}"));
                                }

                                progress?.Report(new ExportProgressReport(0.20f, $"[INFO] Baking {cleanTexBaseName} composite -> {pngFileName}..."));

                                int targetExportRes = isTinySwatch ? Math.Max(512, Math.Min(config.Resolution, 1024)) : config.Resolution;
                                using Image submeshImage = ExtractAndBlendSubmeshImage(config, submesh, targetExportRes, origMeta);
                                if (submeshImage == null)
                                {
                                    string err = $"Failed to extract image for submesh '{submesh.DisplayName}' ({cleanTexBaseName}).";
                                    progress?.Report(new ExportProgressReport(0.20f, $"[ERROR] {err}", ExportLogLevel.Error));
                                    result.Success = false;
                                    result.ErrorMessage = err;
                                    return result;
                                }

                                Error saveErr = submeshImage.SavePng(fullPngPath);
                                if (saveErr != Error.Ok)
                                {
                                    string err = $"Failed to write PNG '{fullPngPath}' (Code: {saveErr}).";
                                    progress?.Report(new ExportProgressReport(0.25f, $"[ERROR] {err}", ExportLogLevel.Error));
                                    result.Success = false;
                                    result.ErrorMessage = err;
                                    return result;
                                }

                                string vtexFileName = $"{cleanTexBaseName}.vtex";
                                string fullVtexPath = Path.Combine(contentMaterialsDir, vtexFileName);
                                string relativePngPath = $"{heroFolder}/materials/{pngFileName}".Replace('\\', '/').TrimStart('/');

                                string outputFormat = !string.IsNullOrEmpty(origMeta?.Format) ? origMeta.Format : "BC7";
                                VTexFlags vtexFlags = origMeta != null ? origMeta.Flags : (VTexFlags.SUGGEST_CLAMPS | VTexFlags.SUGGEST_CLAMPT);

                                string vtexContent = GenerateSource2DmxVtex(relativePngPath, outputFormat, vtexFlags);
                                File.WriteAllText(fullVtexPath, vtexContent);
                                progress?.Report(new ExportProgressReport(0.28f, $"[INFO] Generated DMX VTEX descriptor: {vtexFileName} (Format: {outputFormat}, Flags: {vtexFlags})"));

                                stagedTextures.Add((cleanTexBaseName, fullPngPath, fullVtexPath, colorVtexC));

                                // Auto-sync companion SelfIllumMask (e.g. vindicta_props where g_flSelfIllumAlbedoFactor1 == 0)
                                // and hero-specific NprTransmissiveColor swatches (e.g. vindicta_head) when user didn't
                                // explicitly paint those channels, so in-game self-illum / shadow tint matches the recolor.
                                TryStageCompanionRecolorMaps(
                                    config,
                                    submesh,
                                    submeshImage,
                                    origMeta,
                                    heroFolder,
                                    contentMaterialsDir,
                                    sourceModPackage,
                                    baseVpkPackage,
                                    deadlockPath,
                                    stagedTextures,
                                    processedHeroVtexCPaths,
                                    progress);
                            }
                            finally
                            {
                                if (origMeta != null)
                                {
                                    origMeta.BaseRgbaImage?.Dispose();
                                    origMeta.BaseRgbaImage = null;
                                    origMeta.RawAlpha = null;
                                }
                            }
                        }

                        // Stage any modified non-Color channels (AO, Normal+Roughness, Shadow, Masks) for this submesh
                        StageEditedMultiChannelMaps(
                            config,
                            submesh,
                            heroFolder,
                            contentMaterialsDir,
                            sourceModPackage,
                            baseVpkPackage,
                            deadlockPath,
                            stagedTextures,
                            processedHeroVtexCPaths,
                            progress);
                    }

                    // =========================================================================
                    // STEP B.2: Incorporate Multi-Asset Workspace Staged Textures
                    // =========================================================================
                    try
                    {
                        var existingVtexPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var staged in stagedTextures)
                        {
                            if (string.IsNullOrEmpty(staged.originalVtexCPath)) continue;
                            string normExisting = staged.originalVtexCPath.Replace('\\', '/').TrimStart('/');
                            existingVtexPaths.Add(normExisting);
                            if (!normExisting.EndsWith("_c", StringComparison.OrdinalIgnoreCase))
                                existingVtexPaths.Add(normExisting + "_c");
                        }

                        static string ParseWorkspaceVtexKey(string rawKey, out MaterialTextureChannel? explicitCh)
                        {
                            explicitCh = null;
                            string clean = (rawKey ?? "").Replace('\\', '/').TrimStart('/');
                            if (clean.StartsWith("particle:", StringComparison.OrdinalIgnoreCase))
                                clean = clean.Substring("particle:".Length).TrimStart('/');
                            int chIdx = clean.IndexOf("::ch:", StringComparison.OrdinalIgnoreCase);
                            if (chIdx >= 0)
                            {
                                string chStr = clean.Substring(chIdx + 5);
                                if (Enum.TryParse<MaterialTextureChannel>(chStr, ignoreCase: true, out var parsed))
                                    explicitCh = parsed;
                                clean = clean.Substring(0, chIdx);
                            }
                            return clean;
                        }

                        foreach (var asset in workspaceAssets)
                        {
                            if (config.ExcludedAssetVpkPaths != null &&
                                (config.ExcludedAssetVpkPaths.Contains(asset.VpkPath) || (!string.IsNullOrEmpty(asset.OwnerAssetPath) && config.ExcludedAssetVpkPaths.Contains(asset.OwnerAssetPath))))
                            {
                                continue;
                            }

                            foreach (var (vtexKey, relPath) in asset.ModifiedTextures)
                            {
                                string normVtex = ParseWorkspaceVtexKey(vtexKey, out var explicitChannel);
                                if (!normVtex.Contains(".vtex", StringComparison.OrdinalIgnoreCase)) continue;

                                string normVtexC = normVtex.EndsWith("_c", StringComparison.OrdinalIgnoreCase) ? normVtex : normVtex + "_c";
                                if (existingVtexPaths.Contains(vtexKey) || existingVtexPaths.Contains(normVtex) || existingVtexPaths.Contains(normVtexC))
                                {
                                    continue;
                                }

                                string stagedPngFull = Path.Combine(workspaceSession.SessionDir, relPath);
                                var inMemImg = workspaceSession.GetStagedTexture(vtexKey);
                                if (!File.Exists(stagedPngFull) && (inMemImg == null || inMemImg.IsEmpty())) continue;

                                string assetFolder = Path.GetDirectoryName(normVtex)?.Replace('\\', '/');
                                if (string.IsNullOrEmpty(assetFolder)) assetFolder = "materials/particle";

                                string assetContentDir = Path.Combine(contentStagingRoot, assetFolder);
                                Directory.CreateDirectory(assetContentDir);

                                string baseName = Path.GetFileNameWithoutExtension(normVtex);
                                if (baseName.EndsWith("_c", StringComparison.OrdinalIgnoreCase))
                                {
                                    baseName = baseName.Substring(0, baseName.Length - 2);
                                }

                                var origMeta = (sourceModPackage != null ? LoadOriginalVtexMetadata(normVtexC, sourceModPackage, deadlockPath) : null)
                                            ?? (sourceModPackage != null ? LoadOriginalVtexMetadata(normVtex, sourceModPackage, deadlockPath) : null)
                                            ?? LoadOriginalVtexMetadata(normVtexC, baseVpkPackage, deadlockPath)
                                            ?? LoadOriginalVtexMetadata(normVtex, baseVpkPackage, deadlockPath);

                                string targetPngFileName = $"{baseName}.png";
                                string targetPngFull = Path.Combine(assetContentDir, targetPngFileName);
                                try
                                {
                                    // Packed Normal+Roughness stored as ::ch:Normal / ::ch:Roughness in workspace
                                    if (explicitChannel == MaterialTextureChannel.Normal || explicitChannel == MaterialTextureChannel.Roughness)
                                    {
                                        Image LoadWorkspaceChannelImg(MaterialTextureChannel ch)
                                        {
                                            string candidateKey = $"{normVtex}::ch:{ch}";
                                            string candidateKeyC = $"{normVtexC}::ch:{ch}";
                                            foreach (var tryKey in new[] { candidateKey, candidateKeyC })
                                            {
                                                if (asset.ModifiedTextures.TryGetValue(tryKey, out string chRel))
                                                {
                                                    string fullChPng = Path.Combine(workspaceSession.SessionDir, chRel);
                                                    if (File.Exists(fullChPng))
                                                    {
                                                        var loaded = Image.LoadFromFile(fullChPng);
                                                        if (loaded != null && !loaded.IsEmpty()) return loaded;
                                                    }
                                                }
                                                var mem = workspaceSession.GetStagedTexture(tryKey);
                                                if (mem != null && !mem.IsEmpty()) return (Image)mem.Duplicate();
                                            }
                                            return null;
                                        }

                                        using Image wsNorm = LoadWorkspaceChannelImg(MaterialTextureChannel.Normal);
                                        using Image wsRough = LoadWorkspaceChannelImg(MaterialTextureChannel.Roughness);
                                        Image normSrc = wsNorm ?? origMeta?.BaseRgbaImage;
                                        Image roughSrc = wsRough ?? origMeta?.BaseRgbaImage;
                                        using Image packedImg = MaterialChannelDiscovery.PackNormalRoughnessImage(normSrc, roughSrc, packAsBlueToAlpha: true);
                                        if (packedImg != null && !packedImg.IsEmpty())
                                        {
                                            packedImg.SavePng(targetPngFull);
                                        }
                                        else if (File.Exists(stagedPngFull))
                                        {
                                            File.Copy(stagedPngFull, targetPngFull, true);
                                        }
                                        else if (inMemImg != null)
                                        {
                                            inMemImg.SavePng(targetPngFull);
                                        }
                                    }
                                    else if (inMemImg != null && !inMemImg.IsEmpty())
                                    {
                                        // Prioritize fresh in-memory staged texture over disk file so background Task.Run
                                        // PNG writes or stale unpacked .dpp files on disk never overwrite recent paint edits.
                                        if (inMemImg.HasMipmaps()) inMemImg.ClearMipmaps();
                                        Image exportImg = (origMeta != null && origMeta.RawAlpha != null)
                                            ? ApplyOriginalAlphaChannel(inMemImg, origMeta)
                                            : inMemImg;
                                        try
                                        {
                                            exportImg.SavePng(targetPngFull);
                                        }
                                        finally
                                        {
                                            if (!ReferenceEquals(exportImg, inMemImg))
                                            {
                                                exportImg.Dispose();
                                            }
                                        }
                                    }
                                    else if (File.Exists(stagedPngFull))
                                    {
                                        if (origMeta != null && origMeta.RawAlpha != null)
                                        {
                                            using var loadedImg = Image.LoadFromFile(stagedPngFull);
                                            if (loadedImg != null)
                                            {
                                                var processedImg = ApplyOriginalAlphaChannel(loadedImg, origMeta);
                                                try
                                                {
                                                    processedImg.SavePng(targetPngFull);
                                                }
                                                finally
                                                {
                                                    if (!ReferenceEquals(processedImg, loadedImg))
                                                    {
                                                        processedImg.Dispose();
                                                    }
                                                }
                                            }
                                            else
                                            {
                                                File.Copy(stagedPngFull, targetPngFull, true);
                                            }
                                        }
                                        else
                                        {
                                            File.Copy(stagedPngFull, targetPngFull, true);
                                        }
                                    }

                                    string targetVtexFileName = $"{baseName}.vtex";
                                    string targetVtexFull = Path.Combine(assetContentDir, targetVtexFileName);
                                    string relPng = $"{assetFolder}/{targetPngFileName}".Replace('\\', '/').TrimStart('/');

                                    string outputFormat = !string.IsNullOrEmpty(origMeta?.Format) ? origMeta.Format : "BC7";
                                    VTexFlags vtexFlags = origMeta != null ? origMeta.Flags : (VTexFlags.SUGGEST_CLAMPS | VTexFlags.SUGGEST_CLAMPT);

                                    string vtexDesc = GenerateSource2DmxVtex(relPng, outputFormat, vtexFlags);
                                    File.WriteAllText(targetVtexFull, vtexDesc);

                                    string targetCompiledVtexCPath = origMeta != null && !string.IsNullOrEmpty(origMeta.VtexCPath)
                                        ? origMeta.VtexCPath
                                        : normVtexC;
                                    if (!targetCompiledVtexCPath.EndsWith("_c", StringComparison.OrdinalIgnoreCase))
                                    {
                                        targetCompiledVtexCPath += "_c";
                                    }

                                    stagedTextures.Add((baseName, targetPngFull, targetVtexFull, targetCompiledVtexCPath));
                                    existingVtexPaths.Add(vtexKey);
                                    existingVtexPaths.Add(normVtex);
                                    existingVtexPaths.Add(normVtexC);
                                    existingVtexPaths.Add(targetCompiledVtexCPath);

                                    progress?.Report(new ExportProgressReport(0.36f, $"[INFO] Included workspace staged texture: {targetCompiledVtexCPath}"));
                                }
                                finally
                                {
                                    if (origMeta != null)
                                    {
                                        origMeta.BaseRgbaImage?.Dispose();
                                        origMeta.BaseRgbaImage = null;
                                        origMeta.RawAlpha = null;
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        GD.PrintErr($"[SkinExporter] Notice: Non-fatal error collecting workspace textures: {ex.Message}");
                    }

                    progress?.Report(new ExportProgressReport(0.38f, $"[SUCCESS] All {stagedTextures.Count} custom texture(s) staged successfully across workspace.", ExportLogLevel.Success));

                    // =========================================================================
                    // STEP C: Compile Source 2 Textures via Headless resourcecompiler.exe (40% -> 85%)
                    // =========================================================================
                    ct.ThrowIfCancellationRequested();
                    progress?.Report(new ExportProgressReport(0.40f, $"[INFO] Beginning headless texture compilation via: {Path.GetFileName(compilerExe)}..."));

                    string compilerWorkingDir = Path.GetDirectoryName(compilerExe);
                    var compiledOutputFiles = new List<(string vtexC, string originalVtexCPath)>();

                    int totalTextures = stagedTextures.Count;
                    float texSlice = 0.45f / Math.Max(totalTextures, 1);

                    for (int tIdx = 0; tIdx < totalTextures; tIdx++)
                    {
                        var (matName, pngPath, vtexPath, originalVtexCPath) = stagedTextures[tIdx];
                        ct.ThrowIfCancellationRequested();

                        float texBase = 0.40f + (tIdx * texSlice);
                        float vtexProgress = texBase + (texSlice * 0.10f);
                        float vtexDoneProgress = texBase + (texSlice * 0.90f);

                        progress?.Report(new ExportProgressReport(vtexProgress, $"[INFO] Compiling texture ({tIdx + 1}/{totalTextures}): {Path.GetFileName(vtexPath)}..."));

                        var vtexArgs = new List<string>
                        {
                            "-i", $"\"{vtexPath.Replace('\\', '/')}\"",
                            "-nop4"
                        };

                        int vtexExitCode = RunCompilerProcess(compilerExe, compilerWorkingDir, string.Join(" ", vtexArgs), progress, ct, vtexProgress + 0.05f);

                        string expectedVtexFileName = $"{Path.GetFileNameWithoutExtension(vtexPath)}.vtex_c";
                        string expectedVtexC = LocateEmittedFile(gameMaterialsDir, contentMaterialsDir, expectedVtexFileName, gameStagingRoot, contentStagingRoot);
                        bool vtexEmitted = !string.IsNullOrEmpty(expectedVtexC) && File.Exists(expectedVtexC);

                        if (vtexExitCode != 0 && !vtexEmitted)
                        {
                            string err = $"resourcecompiler.exe failed (exit code {vtexExitCode}) while compiling {Path.GetFileName(vtexPath)}";
                            progress?.Report(new ExportProgressReport(vtexDoneProgress, $"[ERROR] {err}", ExportLogLevel.Error));
                            result.Success = false;
                            result.ErrorMessage = err;
                            return result;
                        }

                        if (!vtexEmitted)
                        {
                            string err = $"Compilation completed, but expected texture binary '{expectedVtexFileName}' was not found.";
                            progress?.Report(new ExportProgressReport(vtexDoneProgress, $"[ERROR] {err}", ExportLogLevel.Error));
                            result.Success = false;
                            result.ErrorMessage = err;
                            return result;
                        }

                        progress?.Report(new ExportProgressReport(vtexDoneProgress, $"[SUCCESS] Compiled texture ({tIdx + 1}/{totalTextures}): {Path.GetFileName(expectedVtexC)}", ExportLogLevel.Success));

                        compiledOutputFiles.Add((expectedVtexC, originalVtexCPath));
                    }

                    progress?.Report(new ExportProgressReport(0.85f, $"[SUCCESS] All {totalTextures} texture(s) compiled successfully to .vtex_c.", ExportLogLevel.Success));

                    // =========================================================================
                    // STEP C.2: Stage & Compile Modified Particle Systems (.vpcf -> .vpcf_c)
                    // =========================================================================
                    if (baseVpkPackage != null || sourceModPackage != null)
                    {
                        foreach (var asset in workspaceAssets)
                        {
                            if (asset.Category != "ParticleVFX" || string.IsNullOrEmpty(asset.ParticleOverridesFile)) continue;
                            if (config.ExcludedAssetVpkPaths != null &&
                                (config.ExcludedAssetVpkPaths.Contains(asset.VpkPath) || (!string.IsNullOrEmpty(asset.OwnerAssetPath) && config.ExcludedAssetVpkPaths.Contains(asset.OwnerAssetPath))))
                            {
                                continue;
                            }

                            var pOverrides = workspaceSession.GetStagedParticleOverrides(asset.VpkPath);
                            if (pOverrides == null || pOverrides.Emitters == null || pOverrides.Emitters.Count == 0) continue;

                            string normRootVpcf = asset.VpkPath.Replace('\\', '/').TrimStart('/');
                            if (normRootVpcf.EndsWith("_c", StringComparison.OrdinalIgnoreCase))
                                normRootVpcf = normRootVpcf[..^2];

                            var childTargets = new Dictionary<string, EmitterOverrideData>(StringComparer.OrdinalIgnoreCase);
                            foreach (var em in pOverrides.Emitters.Values)
                            {
                                if (string.IsNullOrEmpty(em.ChildVpcfPath)) continue;
                                string normChild = em.ChildVpcfPath.Replace('\\', '/').TrimStart('/');
                                if (normChild.EndsWith("_c", StringComparison.OrdinalIgnoreCase))
                                    normChild = normChild[..^2];

                                if (normChild.Equals(normRootVpcf, StringComparison.OrdinalIgnoreCase)) continue;

                                if (!childTargets.TryGetValue(normChild, out var existingEm) || (!existingEm.Visible && em.Visible))
                                {
                                    childTargets[normChild] = em;
                                }
                            }

                            EmitterOverrideData rootOverride = null;
                            foreach (var em in pOverrides.Emitters.Values)
                            {
                                if (!string.IsNullOrEmpty(em.ChildVpcfPath))
                                {
                                    string normChild = em.ChildVpcfPath.Replace('\\', '/').TrimStart('/');
                                    if (normChild.EndsWith("_c", StringComparison.OrdinalIgnoreCase))
                                        normChild = normChild[..^2];
                                    if (normChild.Equals(normRootVpcf, StringComparison.OrdinalIgnoreCase))
                                    {
                                        rootOverride = em;
                                        break;
                                    }
                                }
                            }

                            if (rootOverride == null)
                            {
                                rootOverride = new EmitterOverrideData
                                {
                                    ChildVpcfPath = normRootVpcf,
                                    Visible = true,
                                    TintColorRgba = pOverrides.ConstantColorRgba ?? new float[] { 1f, 1f, 1f, 1f },
                                    SizeScale = pOverrides.RadiusScale > 0f ? pOverrides.RadiusScale : 1.0f,
                                    AlphaScale = pOverrides.AlphaScale > 0f ? pOverrides.AlphaScale : 1.0f
                                };
                            }

                            var allCompileTargets = new List<(string TargetPath, EmitterOverrideData OverrideData, bool IsRoot)>();
                            foreach (var (childPath, em) in childTargets)
                            {
                                allCompileTargets.Add((childPath, em, false));
                            }
                            if (!string.IsNullOrEmpty(normRootVpcf))
                            {
                                allCompileTargets.Add((normRootVpcf, rootOverride, true));
                            }

                            foreach (var (rawVpcfPath, emOverride, isRoot) in allCompileTargets)
                            {
                                ct.ThrowIfCancellationRequested();
                                string normVpcf = rawVpcfPath.Replace('\\', '/').TrimStart('/');
                                if (normVpcf.EndsWith("_c", StringComparison.OrdinalIgnoreCase))
                                    normVpcf = normVpcf[..^2];
                                string compiledVpcfPath = normVpcf + "_c";

                                Package ownerPkg = null;
                                PackageEntry vpkEntry = null;
                                if (sourceModPackage != null)
                                {
                                    vpkEntry = sourceModPackage.FindEntry(compiledVpcfPath);
                                    if (vpkEntry != null) ownerPkg = sourceModPackage;
                                }
                                if (vpkEntry == null && baseVpkPackage != null)
                                {
                                    vpkEntry = baseVpkPackage.FindEntry(compiledVpcfPath);
                                    if (vpkEntry != null) ownerPkg = baseVpkPackage;
                                }

                                if (vpkEntry == null || ownerPkg == null)
                                {
                                    GD.PrintErr($"[SkinExporter] Could not find VPK entry for particle: {compiledVpcfPath}");
                                    continue;
                                }

                                using var vpcfStream = ownerPkg.GetMemoryMappedStreamIfPossible(vpkEntry);
                                using var vrfRes = new ValveResourceFormat.Resource { FileName = compiledVpcfPath };
                                vrfRes.Read(vpcfStream, leaveOpen: true, mode: ReadMode.Deferred);

                                string kv3Text = vrfRes.DataBlock?.ToString();
                                if (string.IsNullOrEmpty(kv3Text)) continue;

                                if (!isRoot && !HasMeaningfulVpcfOverrides(kv3Text, emOverride))
                                {
                                    GD.Print($"[SkinExporter] Skipping untouched particle definition: {normVpcf}");
                                    continue;
                                }

                                if (isRoot)
                                {
                                    foreach (var (childPath, childOv) in childTargets)
                                    {
                                        if (childOv != null && !childOv.Visible)
                                        {
                                            string escapedChild = System.Text.RegularExpressions.Regex.Escape(childPath);
                                            string childRefPattern = $@"(m_ChildRef\s*=\s*resource:""{escapedChild}"")";
                                            if (!System.Text.RegularExpressions.Regex.IsMatch(kv3Text, $@"m_bDisableChild\s*=\s*true[\s\S]*?{childRefPattern}"))
                                            {
                                                kv3Text = System.Text.RegularExpressions.Regex.Replace(kv3Text, childRefPattern, "m_bDisableChild = true\n\t\t\t$1");
                                            }
                                        }
                                    }
                                }

                                kv3Text = ApplyOverridesToVpcfKv3Text(kv3Text, emOverride);

                                string vpcfFolder = Path.GetDirectoryName(normVpcf)?.Replace('\\', '/') ?? "particles";
                                string vpcfContentDir = Path.Combine(contentStagingRoot, vpcfFolder);
                                string vpcfGameDir = Path.Combine(gameStagingRoot, vpcfFolder);
                                Directory.CreateDirectory(vpcfContentDir);

                                string vpcfFileName = Path.GetFileName(normVpcf);
                                string fullVpcfContentPath = Path.Combine(vpcfContentDir, vpcfFileName);
                                foreach (System.Text.RegularExpressions.Match resMatch in System.Text.RegularExpressions.Regex.Matches(kv3Text, @"resource:""([^""]+)"""))
                                {
                                    string rawDep = resMatch.Groups[1].Value.Replace('\\', '/').Trim().TrimStart('/');
                                    string compiledDep = rawDep.EndsWith("_c", StringComparison.OrdinalIgnoreCase) ? rawDep : rawDep + "_c";
                                    EnsureTextureStagedInGameTree(gameStagingRoot, compiledDep, sourceModPackage ?? baseVpkPackage);
                                }
                                File.WriteAllText(fullVpcfContentPath, kv3Text);

                                string expectedVpcfCName = vpcfFileName + "_c";
                                string stagingGameVpcfC = Path.Combine(vpcfGameDir, expectedVpcfCName);
                                if (File.Exists(stagingGameVpcfC))
                                {
                                    try { File.Delete(stagingGameVpcfC); } catch { }
                                }

                                progress?.Report(new ExportProgressReport(0.82f, $"[INFO] Compiling modified particle definition: {vpcfFileName}..."));

                                var vpcfArgs = new List<string>
                                {
                                    "-i", $"\"{fullVpcfContentPath.Replace('\\', '/')}\"",
                                    "-nop4",
                                    "-v"
                                };
                                int exitCode = RunCompilerProcess(compilerExe, compilerWorkingDir, string.Join(" ", vpcfArgs), progress, ct, 0.83f);

                                string emittedVpcfC = LocateEmittedFile(vpcfGameDir, vpcfContentDir, expectedVpcfCName, gameStagingRoot, contentStagingRoot);
                                if (exitCode == 0 && !string.IsNullOrEmpty(emittedVpcfC) && File.Exists(emittedVpcfC))
                                {
                                    compiledOutputFiles.Add((emittedVpcfC, compiledVpcfPath));
                                    progress?.Report(new ExportProgressReport(0.84f, $"[SUCCESS] Compiled particle: {expectedVpcfCName}", ExportLogLevel.Success));
                                }
                                else
                                {
                                    string err = $"resourcecompiler.exe failed (exit code {exitCode}) while compiling particle '{vpcfFileName}'. Check compiler log for details.";
                                    progress?.Report(new ExportProgressReport(0.84f, $"[ERROR] {err}", ExportLogLevel.Error));
                                    result.Success = false;
                                    result.ErrorMessage = err;
                                    return result;
                                }
                            }
                        }
                    }

                    // =========================================================================
                    // STEP C.3: Compile Patched Materials (.vmat -> .vmat_c) for Material Parameter Overrides
                    // =========================================================================
                    if (baseVpkPackage != null || sourceModPackage != null)
                    {
                        CompilePatchedMaterialsForParamOverrides(
                            workspaceAssets,
                            heroFolder,
                            stagedTextures,
                            compiledOutputFiles,
                            contentStagingRoot,
                            gameStagingRoot,
                            sourceModPackage,
                            baseVpkPackage,
                            deadlockPath,
                            compilerExe,
                            compilerWorkingDir,
                            progress,
                            ct);
                    }

                    // =========================================================================
                    // STEP D: Package Intercepted VTEX Binaries (+ Base Mod Merge) into VPK Archive (85% -> 100%)
                    // =========================================================================
                    ct.ThrowIfCancellationRequested();
                    progress?.Report(new ExportProgressReport(0.85f, $"[INFO] Packing assets into VPK archive '{vpkFileName}'..."));

                    try
                    {
                        // Pre-extract base mod entries into memory before writing in case finalVpkPath == config.SourceModVpkPath
                        var baseModFilesToMerge = new List<(string InternalPath, byte[] Data)>();
                        if (sourceModPackage != null && config.MergeWithSourceModVpk)
                        {
                            foreach (var extGroup in sourceModPackage.Entries)
                            {
                                foreach (var entry in extGroup.Value)
                                {
                                    string fullEntryPath = entry.GetFullPath().Replace('\\', '/').TrimStart('/');
                                    sourceModPackage.ReadEntry(entry, out byte[] rawEntryBytes);
                                    if (rawEntryBytes != null)
                                    {
                                        baseModFilesToMerge.Add((fullEntryPath, rawEntryBytes));
                                    }
                                }
                            }
                        }

                       // Always release read lock on sourceModPackage now that entries are buffered in memory
                        if (sourceModPackage != null)
                        {
                            sourceModPackage.Dispose();
                            sourceModPackage = null;
                        }

                        // Si estamos en Priority Mode (sin borrar el original), movemos el VPK original al destino de respaldo/siguiente slot
                        if (!string.IsNullOrWhiteSpace(config.SourceModVpkPath) &&
                            !string.IsNullOrEmpty(shiftedOriginalModDestinationPath) &&
                            File.Exists(config.SourceModVpkPath) &&
                            !string.Equals(Path.GetFullPath(config.SourceModVpkPath), Path.GetFullPath(shiftedOriginalModDestinationPath), StringComparison.OrdinalIgnoreCase))
                        {
                            if (File.Exists(shiftedOriginalModDestinationPath))
                            {
                                File.Delete(shiftedOriginalModDestinationPath);
                            }
                            try
                            {
                                File.Move(config.SourceModVpkPath, shiftedOriginalModDestinationPath);
                            }
                            catch (IOException)
                            {
                                GC.Collect();
                                GC.WaitForPendingFinalizers();
                                File.Move(config.SourceModVpkPath, shiftedOriginalModDestinationPath);
                            }
                            progress?.Report(new ExportProgressReport(0.88f, $"[INFO] Preserved original mod VPK -> moved to '{Path.GetFileName(shiftedOriginalModDestinationPath)}'."));
                        }

                        // If overwriting an existing VPK in-place, delete it first (forcing GC finalizers if a memory-mapped section was still pending)
                        if (File.Exists(finalVpkPath))
                        {
                            try
                            {
                                File.Delete(finalVpkPath);
                            }
                            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                            {
                                GC.Collect();
                                GC.WaitForPendingFinalizers();
                                File.Delete(finalVpkPath);
                            }
                        }

                        using (var package = new Package())
                        {
                            var addedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                            // 1. Add our newly compiled recolored textures (.vtex_c) and particles (.vpcf_c)
                            foreach (var (vtexC, originalVtexCPath) in compiledOutputFiles)
                            {
                                if (File.Exists(vtexC))
                                {
                                    byte[] vtexData = File.ReadAllBytes(vtexC);
                                    string internalVtexPath = originalVtexCPath.Replace('\\', '/').TrimStart('/');
                                    package.AddFile(internalVtexPath, vtexData);
                                    addedPaths.Add(internalVtexPath);
                                    progress?.Report(new ExportProgressReport(0.90f, $"[INFO] Added modified asset to VPK: {internalVtexPath} ({vtexData.Length / 1024} KB)"));
                                }
                            }

                            // 2. Include any staged workspace animations
                            if (workspaceSession != null)
                            {
                                foreach (var asset in workspaceAssets)
                                {
                                    if (asset.Animations != null)
                                    {
                                        foreach (var animEntry in asset.Animations)
                                        {
                                            if (!string.IsNullOrEmpty(animEntry.RelativeVnmclipPath))
                                            {
                                                string fullClipPath = Path.Combine(workspaceSession.SessionDir, animEntry.RelativeVnmclipPath);
                                                if (File.Exists(fullClipPath))
                                                {
                                                    byte[] clipData = File.ReadAllBytes(fullClipPath);
                                                    string targetVpkPath = !string.IsNullOrEmpty(animEntry.TargetVpkPath)
                                                        ? animEntry.TargetVpkPath
                                                        : DeadlockPlayground.Tools.DeadlockAnimLoader.ResolveHeroClipVpkPath(config.HeroCodename, null, animEntry.CleanName);
                                                    targetVpkPath = targetVpkPath.Replace('\\', '/').TrimStart('/');
                                                    if (config.ExcludedAssetVpkPaths != null && config.ExcludedAssetVpkPaths.Contains(targetVpkPath))
                                                    {
                                                        continue;
                                                    }
                                                    if (addedPaths.Add(targetVpkPath))
                                                    {
                                                        package.AddFile(targetVpkPath, clipData);
                                                        progress?.Report(new ExportProgressReport(0.92f, $"[INFO] Added patched animation to VPK: {targetVpkPath} ({clipData.Length / 1024} KB)"));
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }

                            // 2.5. Include compiled custom 3D model (.vmdl_c, .vmesh_c, .vmat_c, .vtex_c) if custom imported meshes exist
                            if (preExtractedCustomBundle != null && preExtractedCustomBundle.Submeshes.Count > 0)
                            {
                                preExtractedCustomBundle.StagingDir = csdkPath;
                                preExtractedCustomBundle.ResourceCompilerExe = compilerExe;
                                if (string.IsNullOrEmpty(preExtractedCustomBundle.DeadlockGamePath))
                                {
                                    preExtractedCustomBundle.DeadlockGamePath = deadlockPath;
                                }

                                var modelRes = await CustomModelExporter.ExportAndCompileCustomModelAsync(preExtractedCustomBundle, progress, ct);
                                if (!modelRes.Success)
                                {
                                    result.Success = false;
                                    result.ErrorMessage = modelRes.ErrorMessage ?? "Custom model compilation failed.";
                                    progress?.Report(new ExportProgressReport(0.93f, $"[ERROR] {result.ErrorMessage}", ExportLogLevel.Error));
                                    return result;
                                }

                                if (!CustomModelExporter.ValidateAllowlist(modelRes.AllowlistedVpkFiles.Keys, out string rejectedReason))
                                {
                                    result.Success = false;
                                    result.ErrorMessage = $"VPK allowlist validation failed: {rejectedReason}";
                                    progress?.Report(new ExportProgressReport(0.93f, $"[ERROR] {result.ErrorMessage}", ExportLogLevel.Error));
                                    return result;
                                }

                                if (modelRes.AllowlistedVpkFiles.Count > 256)
                                {
                                    result.Success = false;
                                    result.ErrorMessage = $"Custom model export exceeded strict file count limit ({modelRes.AllowlistedVpkFiles.Count} > 256).";
                                    progress?.Report(new ExportProgressReport(0.93f, $"[ERROR] {result.ErrorMessage}", ExportLogLevel.Error));
                                    return result;
                                }

                                foreach (var (relVpkPath, fullDiskPath) in modelRes.AllowlistedVpkFiles)
                                {
                                    if (File.Exists(fullDiskPath))
                                    {
                                        string normRel = relVpkPath.Replace('\\', '/').TrimStart('/');
                                        byte[] fileData = File.ReadAllBytes(fullDiskPath);
                                        if (addedPaths.Add(normRel))
                                        {
                                            package.AddFile(normRel, fileData);
                                            progress?.Report(new ExportProgressReport(0.94f, $"[INFO] Added compiled custom model asset to VPK: {normRel} ({fileData.Length / 1024} KB)"));
                                        }
                                    }
                                }
                            }

                            // 3. Merge all untouched files (.vmdl_c, .vmesh_c, .vmat_c, .vtex_c) from the Base Mod VPK!
                            if (baseModFilesToMerge.Count > 0)
                            {
                                int mergedCount = 0;
                                foreach (var (internalPath, rawData) in baseModFilesToMerge)
                                {
                                    if (addedPaths.Add(internalPath))
                                    {
                                        package.AddFile(internalPath, rawData);
                                        mergedCount++;
                                    }
                                }
                                progress?.Report(new ExportProgressReport(0.94f, $"[SUCCESS] Merged {mergedCount} base mod file(s) (.vmdl_c, .vmat_c, meshes) into standalone VPK!", ExportLogLevel.Success));
                            }

                            package.Write(finalVpkPath);
                            baseModFilesToMerge.Clear();
                        }

                        if (!File.Exists(finalVpkPath))
                        {
                            throw new FileNotFoundException($"VPK package write failed, file not found at: {finalVpkPath}");
                        }

                        long vpkSize = new FileInfo(finalVpkPath).Length;
                        progress?.Report(new ExportProgressReport(0.95f, $"[SUCCESS] VPK archive successfully created ({vpkSize / 1024} KB).", ExportLogLevel.Success));
                    }
                    catch (Exception ex)
                    {
                        string err = $"Failed to package VPK: {ex.Message}";
                        progress?.Report(new ExportProgressReport(0.90f, $"[ERROR] {err}", ExportLogLevel.Error));
                        result.Success = false;
                        result.ErrorMessage = err;
                        return result;
                    }

                    // Clean up temporary staging folders in CSDK
                    try
                    {
                        if (Directory.Exists(contentStagingRoot)) Directory.Delete(contentStagingRoot, true);
                        if (Directory.Exists(gameStagingRoot)) Directory.Delete(gameStagingRoot, true);
                        progress?.Report(new ExportProgressReport(0.98f, $"[INFO] Cleaned up temporary staging files."));
                    }
                    catch (Exception ex)
                    {
                        GD.Print($"[SkinExporter] Non-fatal staging cleanup notice: {ex.Message}");
                    }

                    string installNote = config.InstallDirectlyToGame
                        ? $"Mod successfully installed to citadel/addons/{vpkFileName}"
                        : $"Mod successfully exported to {finalVpkPath}";

                    progress?.Report(new ExportProgressReport(1.0f, $"[SUCCESS] {installNote}", ExportLogLevel.Success));

                    result.Success = true;
                    result.VpkPath = finalVpkPath;
                    result.ModDirectory = targetDir;
                    return result;
                }
                finally
                {
                    sourceModPackage?.Dispose();
                    baseVpkPackage?.Dispose();
                }
            }, ct);
        }

        private static string ResolveEffectiveCsdkPath(string userCsdkPath, string deadlockGamePath)
        {
            // Prioritize CSDK13 / Reduced_CSDK_13 if present on disk (even if userCsdkPath was previously set to CSDK12)
            if (!string.IsNullOrEmpty(userCsdkPath))
            {
                string up13 = userCsdkPath
                    .Replace("Reduced_CSDK_12", "Reduced_CSDK_13", StringComparison.OrdinalIgnoreCase)
                    .Replace("CSDK12", "CSDK13", StringComparison.OrdinalIgnoreCase);
                if (Directory.Exists(up13)) return up13;
            }

            string[] csdk13Defaults = {
                @"C:\CSDK13",
                @"C:\Reduced_CSDK_13",
                @"D:\CSDK13",
                @"D:\Reduced_CSDK_13",
                @"E:\CSDK13",
                @"E:\Reduced_CSDK_13",
                @"E:\SteamLibrary\steamapps\common\CSDK13"
            };
            foreach (var d13 in csdk13Defaults)
            {
                if (Directory.Exists(d13)) return d13;
            }

            if (!string.IsNullOrEmpty(deadlockGamePath))
            {
                string siblingCsdk13 = Path.GetFullPath(Path.Combine(deadlockGamePath, "..", "CSDK13"));
                if (Directory.Exists(siblingCsdk13)) return siblingCsdk13;

                string siblingReduced13 = Path.GetFullPath(Path.Combine(deadlockGamePath, "..", "Reduced_CSDK_13"));
                if (Directory.Exists(siblingReduced13)) return siblingReduced13;
            }

            if (!string.IsNullOrEmpty(userCsdkPath) && Directory.Exists(userCsdkPath))
            {
                return userCsdkPath;
            }

            if (!string.IsNullOrEmpty(deadlockGamePath))
            {
                string p1 = Path.Combine(deadlockGamePath, "game", "bin", "win64", "resourcecompiler.exe");
                if (File.Exists(p1)) return deadlockGamePath;

                string siblingCsdk = Path.GetFullPath(Path.Combine(deadlockGamePath, "..", "CSDK12"));
                if (Directory.Exists(siblingCsdk)) return siblingCsdk;
            }

            string[] defaults = {
                @"C:\CSDK12",
                @"D:\CSDK12",
                @"D:\Reduced_CSDK_12",
                @"E:\CSDK12",
                @"E:\SteamLibrary\steamapps\common\CSDK12"
            };

            foreach (var d in defaults)
            {
                if (Directory.Exists(d)) return d;
            }

            return !string.IsNullOrEmpty(deadlockGamePath) ? deadlockGamePath : "";
        }

        private static string ResolveCompilerExecutable(string csdkPath, string explicitPath = null)
        {
            string upgraded13 = GamePathManager.TryResolveCsdk13CompilerUpgrade(explicitPath, csdkPath);
            if (!string.IsNullOrEmpty(upgraded13) && File.Exists(upgraded13))
            {
                return upgraded13;
            }

            if (!string.IsNullOrEmpty(explicitPath) && File.Exists(explicitPath))
            {
                string norm = explicitPath.Replace('\\', '/');
                bool isCsdk13 = norm.Contains("CSDK13", StringComparison.OrdinalIgnoreCase) || norm.Contains("CSDK_13", StringComparison.OrdinalIgnoreCase);
                if (!isCsdk13 && norm.Contains("/game/bin/win64/resourcecompiler.exe", StringComparison.OrdinalIgnoreCase))
                {
                    string binCs2Sibling = norm.Replace("/game/bin/win64/resourcecompiler.exe", "/game/bin_cs2/win64/resourcecompiler.exe", StringComparison.OrdinalIgnoreCase);
                    if (File.Exists(binCs2Sibling))
                    {
                        return binCs2Sibling;
                    }
                }
                return explicitPath;
            }

            if (!string.IsNullOrEmpty(csdkPath))
            {
                bool isCsdk13 = csdkPath.Contains("CSDK13", StringComparison.OrdinalIgnoreCase) || csdkPath.Contains("CSDK_13", StringComparison.OrdinalIgnoreCase);
                if (isCsdk13)
                {
                    string p13 = Path.Combine(csdkPath, "game", "bin", "win64", "resourcecompiler.exe");
                    if (File.Exists(p13)) return p13;

                    string p13Sub = Path.Combine(csdkPath, "Reduced_CSDK_13", "game", "bin", "win64", "resourcecompiler.exe");
                    if (File.Exists(p13Sub)) return p13Sub;
                }

                string p1 = Path.Combine(csdkPath, "game", "bin_cs2", "win64", "resourcecompiler.exe");
                if (File.Exists(p1)) return p1;

                string p2 = Path.Combine(csdkPath, "Reduced_CSDK_12", "game", "bin_cs2", "win64", "resourcecompiler.exe");
                if (File.Exists(p2)) return p2;

                string p3 = Path.Combine(csdkPath, "game", "bin", "win64", "resourcecompiler.exe");
                if (File.Exists(p3)) return p3;

                string p4 = Path.Combine(csdkPath, "resourcecompiler.exe");
                if (File.Exists(p4)) return p4;
            }

            return null;
        }

        public static string ResolveHeroInternalFolder(string heroCodename, List<SubmeshNodeInfo> submeshes = null)
        {
            string cleanHero = string.IsNullOrEmpty(heroCodename) ? "custom_hero" : heroCodename.ToLowerInvariant();

            if (submeshes != null)
            {
                foreach (var sm in submeshes)
                {
                    if (!string.IsNullOrEmpty(sm.OriginalVmatPath))
                    {
                        string norm = sm.OriginalVmatPath.Replace('\\', '/').ToLowerInvariant();
                        int matIdx = norm.IndexOf("/materials/", StringComparison.OrdinalIgnoreCase);
                        if (matIdx > 0)
                        {
                            return norm.Substring(0, matIdx);
                        }
                    }
                }
            }

            var entry = DeadlockHeroCatalog.ResolveHero(cleanHero) ?? DeadlockHeroCatalog.GetByPath(cleanHero);
            if (entry != null && !string.IsNullOrEmpty(entry.VmdlRelativePath))
            {
                string vmdlDir = Path.GetDirectoryName(entry.VmdlRelativePath)?.Replace('\\', '/');
                if (!string.IsNullOrEmpty(vmdlDir))
                {
                    return vmdlDir;
                }
            }

            if (cleanHero.StartsWith("models/", StringComparison.OrdinalIgnoreCase))
            {
                return cleanHero.TrimEnd('/');
            }

            return $"models/heroes_staging/{cleanHero}";
        }

        private static string ResolveSubmeshMaterialName(SubmeshNodeInfo submesh, string heroCodename)
        {
            if (!string.IsNullOrEmpty(submesh.MaterialName))
            {
                return submesh.MaterialName.ToLowerInvariant();
            }

            if (!string.IsNullOrEmpty(submesh.OriginalVmatPath))
            {
                return Path.GetFileNameWithoutExtension(submesh.OriginalVmatPath).ToLowerInvariant();
            }

            string cleanMesh = string.IsNullOrEmpty(submesh.RawName) ? "body" : submesh.RawName.ToLowerInvariant().Replace(" ", "_");
            return $"{heroCodename.ToLowerInvariant()}_{cleanMesh}";
        }

        public static string ResolveSubmeshKey(SubmeshNodeInfo sm)
        {
            if (sm == null) return string.Empty;

            // Build a strictly unique key per submesh + material so modded heroes whose submeshes
            // share generic DisplayName values (e.g., "BODY", "MESH") never collide in PreBakedAtlasesBySubmesh.
            string matPart = !string.IsNullOrWhiteSpace(sm.OriginalVmatPath)
                ? sm.OriginalVmatPath.Replace('\\', '/').Trim().ToLowerInvariant()
                : (!string.IsNullOrWhiteSpace(sm.OriginalColorVtexCPath)
                    ? sm.OriginalColorVtexCPath.Replace('\\', '/').Trim().ToLowerInvariant()
                    : (!string.IsNullOrWhiteSpace(sm.MaterialName) ? sm.MaterialName.Trim().ToLowerInvariant() : ""));

            if (sm.Mesh != null && GodotObject.IsInstanceValid(sm.Mesh))
            {
                ulong id = sm.Mesh.GetInstanceId();
                return !string.IsNullOrEmpty(matPart)
                    ? $"{matPart}#{id}_{sm.SurfaceIndex}"
                    : $"{sm.Mesh.Name}#{id}_{sm.SurfaceIndex}";
            }

            if (!string.IsNullOrEmpty(matPart)) return $"{matPart}#{sm.SurfaceIndex}";
            if (!string.IsNullOrEmpty(sm.RawName)) return $"{sm.RawName}#{sm.SurfaceIndex}";
            if (!string.IsNullOrEmpty(sm.DisplayName)) return $"{sm.DisplayName}#{sm.SurfaceIndex}";
            return sm.SurfaceIndex.ToString();
        }

        private static Image ExtractAndBlendSubmeshImage(
            ModExportConfig config,
            SubmeshNodeInfo submesh,
            int targetRes,
            OriginalVtexMetadata origMeta = null)
        {
            if (config == null || submesh == null) return null;

            string subKey = ResolveSubmeshKey(submesh);
            Image atlasImage = null;
            if (config.PreBakedAtlasesBySubmesh.TryGetValue(subKey, out var subAtlas) && subAtlas != null && !subAtlas.IsEmpty())
            {
                atlasImage = subAtlas;
            }
            else if (config.PreBakedAtlasesBySubmesh.Count == 0 && config.PreBakedAtlasesBySurface.TryGetValue(submesh.SurfaceIndex, out var surfaceAtlas) && surfaceAtlas != null && !surfaceAtlas.IsEmpty())
            {
                atlasImage = surfaceAtlas;
            }
            else if (config.PreBakedAtlasesBySubmesh.Count == 0 && config.PreBakedAtlas != null && !config.PreBakedAtlas.IsEmpty())
            {
                atlasImage = config.PreBakedAtlas;
            }

            // If this submesh had no painted atlas of its own (e.g. an unedited material on a multi-material modded hero),
            // preserve its own base texture instead of returning null or duplicating another material's atlas!
            if (atlasImage == null || atlasImage.IsEmpty())
            {
                Image ownBase = (origMeta?.BaseRgbaImage != null && !origMeta.BaseRgbaImage.IsEmpty())
                    ? origMeta.BaseRgbaImage
                    : (config.PreExtractedBaseImagesBySubmesh.TryGetValue(subKey, out var subOwnBase) && subOwnBase != null && !subOwnBase.IsEmpty()
                        ? subOwnBase
                        : null);
                if (ownBase == null || ownBase.IsEmpty()) return null;

                var passthrough = (Image)ownBase.Duplicate();
                if (passthrough.IsCompressed()) passthrough.Decompress();
                if (passthrough.HasMipmaps()) passthrough.ClearMipmaps();
                if (passthrough.GetFormat() != Image.Format.Rgba8) passthrough.Convert(Image.Format.Rgba8);
                return passthrough;
            }

            Image isolatedImage = atlasImage;

            Vector2 pos = Vector2.Zero;
            Vector2 size = Vector2.One;
            if (config.SubmeshAtlasRectsBySubmesh.TryGetValue(subKey, out var subRect))
            {
                pos = subRect.pos;
                size = subRect.size;
            }
            else if (config.SubmeshAtlasRects.TryGetValue(submesh.SurfaceIndex, out var atlasRect))
            {
                pos = atlasRect.pos;
                size = atlasRect.size;
            }
            else if (submesh.Mesh != null && submesh.Mesh.MaterialOverlay is ShaderMaterial sm)
            {
                var posVar = sm.GetShaderParameter("position_in_atlas");
                var sizeVar = sm.GetShaderParameter("size_in_atlas");
                if (posVar.VariantType == Variant.Type.Vector2 && sizeVar.VariantType == Variant.Type.Vector2)
                {
                    pos = posVar.AsVector2();
                    size = sizeVar.AsVector2();
                }
            }

            if (size.X > 0 && size.Y > 0 && (size.X < 0.999f || size.Y < 0.999f))
            {
                int px = Mathf.Clamp(Mathf.RoundToInt(pos.X * atlasImage.GetWidth()), 0, atlasImage.GetWidth() - 1);
                int py = Mathf.Clamp(Mathf.RoundToInt(pos.Y * atlasImage.GetHeight()), 0, atlasImage.GetHeight() - 1);
                int pw = Mathf.Clamp(Mathf.RoundToInt(size.X * atlasImage.GetWidth()), 1, atlasImage.GetWidth() - px);
                int ph = Mathf.Clamp(Mathf.RoundToInt(size.Y * atlasImage.GetHeight()), 1, atlasImage.GetHeight() - py);

                isolatedImage = atlasImage.GetRegion(new Rect2I(px, py, pw, ph));
            }

            Image baseImg = null;
            bool hasEditedBase = config.SubmeshesWithEditedBaseTexture != null && config.SubmeshesWithEditedBaseTexture.Contains(subKey);
            bool hasEditedAlpha = config.SubmeshesWithEditedAlpha != null && config.SubmeshesWithEditedAlpha.Contains(subKey);
            if (hasEditedBase && config.PreExtractedBaseImagesBySubmesh.TryGetValue(subKey, out var editedSubBase) && editedSubBase != null && !editedSubBase.IsEmpty())
            {
                baseImg = editedSubBase;
            }
            else if (origMeta?.BaseRgbaImage != null && !origMeta.BaseRgbaImage.IsEmpty())
            {
                baseImg = origMeta.BaseRgbaImage;
            }
            else if (config.PreExtractedBaseImagesBySubmesh.TryGetValue(subKey, out var subBase) && subBase != null && !subBase.IsEmpty())
            {
                baseImg = subBase;
            }
            else if (config.PreExtractedBaseImages.TryGetValue(submesh.SurfaceIndex, out var preBase) && preBase != null && !preBase.IsEmpty())
            {
                baseImg = preBase;
            }

            int exportWidth = 2048;
            int exportHeight = 2048;

            if (origMeta != null && origMeta.Width > 0 && origMeta.Height > 0)
            {
                if (targetRes > 0)
                {
                    if (origMeta.Width >= origMeta.Height)
                    {
                        exportWidth = targetRes;
                        exportHeight = Mathf.Max(1, Mathf.RoundToInt((float)targetRes * origMeta.Height / origMeta.Width));
                    }
                    else
                    {
                        exportHeight = targetRes;
                        exportWidth = Mathf.Max(1, Mathf.RoundToInt((float)targetRes * origMeta.Width / origMeta.Height));
                    }
                }
                else
                {
                    exportWidth = origMeta.Width;
                    exportHeight = origMeta.Height;
                }
            }
            else if (baseImg != null && !baseImg.IsEmpty())
            {
                int baseW = baseImg.GetWidth();
                int baseH = baseImg.GetHeight();
                if (targetRes > 0)
                {
                    if (baseW >= baseH)
                    {
                        exportWidth = targetRes;
                        exportHeight = Mathf.Max(1, Mathf.RoundToInt((float)targetRes * baseH / baseW));
                    }
                    else
                    {
                        exportHeight = targetRes;
                        exportWidth = Mathf.Max(1, Mathf.RoundToInt((float)targetRes * baseW / baseH));
                    }
                }
                else
                {
                    exportWidth = baseW;
                    exportHeight = baseH;
                }
            }
            else if (isolatedImage != null && !isolatedImage.IsEmpty())
            {
                int isoW = isolatedImage.GetWidth();
                int isoH = isolatedImage.GetHeight();
                if (targetRes > 0)
                {
                    if (isoW >= isoH)
                    {
                        exportWidth = targetRes;
                        exportHeight = Mathf.Max(1, Mathf.RoundToInt((float)targetRes * isoH / isoW));
                    }
                    else
                    {
                        exportHeight = targetRes;
                        exportWidth = Mathf.Max(1, Mathf.RoundToInt((float)targetRes * isoW / isoH));
                    }
                }
                else
                {
                    exportWidth = isoW;
                    exportHeight = isoH;
                }
            }
            else
            {
                exportWidth = targetRes > 0 ? targetRes : 2048;
                exportHeight = targetRes > 0 ? targetRes : 2048;
            }

            if (isolatedImage == null || isolatedImage.IsEmpty()) return null;

            var finalImage = (Image)isolatedImage.Duplicate();
            if (finalImage.IsCompressed())
            {
                finalImage.Decompress();
            }
            if (finalImage.HasMipmaps())
            {
                finalImage.ClearMipmaps();
            }
            if (finalImage.GetWidth() != exportWidth || finalImage.GetHeight() != exportHeight)
            {
                finalImage.Resize(exportWidth, exportHeight, Image.Interpolation.Bilinear);
            }
            finalImage.Convert(Image.Format.Rgba8);

            if (baseImg != null && !baseImg.IsEmpty())
            {
                using var baseUnscaled = (Image)baseImg.Duplicate();
                if (baseUnscaled.IsCompressed())
                {
                    baseUnscaled.Decompress();
                }
                if (baseUnscaled.HasMipmaps())
                {
                    baseUnscaled.ClearMipmaps();
                }
                baseUnscaled.Convert(Image.Format.Rgba8);
                if (baseUnscaled.GetWidth() != exportWidth || baseUnscaled.GetHeight() != exportHeight)
                {
                    baseUnscaled.Resize(exportWidth, exportHeight, Image.Interpolation.Bilinear);
                }

                byte[] paintData = finalImage.GetData();
                byte[] baseData = baseUnscaled.GetData();
                int pixelCount = exportWidth * exportHeight;
                int expectedBytes = pixelCount * 4;

                if (paintData != null && baseData != null && paintData.Length >= expectedBytes && baseData.Length >= expectedBytes)
                {
                    bool sameRes = (origMeta != null && origMeta.Width == exportWidth && origMeta.Height == exportHeight && origMeta.RawAlpha != null && origMeta.RawAlpha.Length == pixelCount);
                    bool preserveOrigVtexAlpha = config.PreserveBaseAlpha && !hasEditedAlpha;

                    for (int i = 0; i < pixelCount; i++)
                    {
                        int idx = i * 4;
                        if (idx + 3 >= paintData.Length || idx + 3 >= baseData.Length) break;

                        byte origBaseA = baseData[idx + 3];
                        if (preserveOrigVtexAlpha && origMeta?.RawAlpha != null && origMeta.RawAlpha.Length > 0)
                        {
                            if (sameRes && i < origMeta.RawAlpha.Length)
                            {
                                origBaseA = origMeta.RawAlpha[i];
                            }
                            else if (origMeta.Width > 0 && origMeta.Height > 0)
                            {
                                int srcX = Mathf.Clamp(Mathf.FloorToInt((i % exportWidth) * (float)origMeta.Width / exportWidth), 0, origMeta.Width - 1);
                                int srcY = Mathf.Clamp(Mathf.FloorToInt((i / exportWidth) * (float)origMeta.Height / exportHeight), 0, origMeta.Height - 1);
                                int srcIdx = srcY * origMeta.Width + srcX;
                                if (srcIdx >= 0 && srcIdx < origMeta.RawAlpha.Length)
                                {
                                    origBaseA = origMeta.RawAlpha[srcIdx];
                                }
                            }
                        }

                        // NOTE: paintData from BakeCompositeImageForSubmesh ALREADY has the paint layers composited over the base texture RGB!
                        // Do NOT clamp pA to origBaseA before blending RGB, because in Source 2 Deadlock g_tColor.a often stores Metalness (0 on cloth/skin),
                        // which previously forced pA = 0 and overwrote 100% of the painted RGB with unedited baseData!
                        float pA = paintData[idx + 3] / 255.0f;
                        if (pA <= 0.001f)
                        {
                            paintData[idx] = baseData[idx];
                            paintData[idx + 1] = baseData[idx + 1];
                            paintData[idx + 2] = baseData[idx + 2];
                        }
                        else if (pA < 0.999f)
                        {
                            float bR = baseData[idx];
                            float bG = baseData[idx + 1];
                            float bB = baseData[idx + 2];

                            float pR = paintData[idx];
                            float pG = paintData[idx + 1];
                            float pB = paintData[idx + 2];

                            paintData[idx] = (byte)Mathf.Clamp(pR * pA + bR * (1.0f - pA), 0, 255);
                            paintData[idx + 1] = (byte)Mathf.Clamp(pG * pA + bG * (1.0f - pA), 0, 255);
                            paintData[idx + 2] = (byte)Mathf.Clamp(pB * pA + bB * (1.0f - pA), 0, 255);
                        }

                        if (preserveOrigVtexAlpha)
                        {
                            paintData[idx + 3] = origBaseA;
                        }
                        else if (config.PreserveBaseAlpha && hasEditedAlpha)
                        {
                            paintData[idx + 3] = baseData[idx + 3];
                        }
                        else
                        {
                            paintData[idx + 3] = (byte)Mathf.Clamp(Mathf.Max((int)paintData[idx + 3], (int)baseData[idx + 3]), 0, 255);
                        }
                    }

                    if (paintData.Length > expectedBytes)
                    {
                        Array.Resize(ref paintData, expectedBytes);
                    }
                    finalImage.Dispose();
                    finalImage = Image.CreateFromData(exportWidth, exportHeight, false, Image.Format.Rgba8, paintData);
                }
            }
            else if (origMeta?.RawAlpha != null && origMeta.RawAlpha.Length > 0)
            {
                byte[] paintData = finalImage.GetData();
                int pixelCount = exportWidth * exportHeight;
                int expectedBytes = pixelCount * 4;

                if (paintData != null && paintData.Length >= expectedBytes)
                {
                    bool sameRes = (origMeta.Width == exportWidth && origMeta.Height == exportHeight && origMeta.RawAlpha.Length == pixelCount);

                    for (int i = 0; i < pixelCount; i++)
                    {
                        int idx = i * 4;
                        if (idx + 3 >= paintData.Length) break;

                        if (sameRes && i < origMeta.RawAlpha.Length)
                        {
                            paintData[idx + 3] = origMeta.RawAlpha[i];
                        }
                        else if (origMeta.Width > 0 && origMeta.Height > 0)
                        {
                            int srcX = Mathf.Clamp(Mathf.FloorToInt((i % exportWidth) * (float)origMeta.Width / exportWidth), 0, origMeta.Width - 1);
                            int srcY = Mathf.Clamp(Mathf.FloorToInt((i / exportWidth) * (float)origMeta.Height / exportHeight), 0, origMeta.Height - 1);
                            int srcIdx = srcY * origMeta.Width + srcX;
                            if (srcIdx >= 0 && srcIdx < origMeta.RawAlpha.Length)
                            {
                                paintData[idx + 3] = origMeta.RawAlpha[srcIdx];
                            }
                        }
                    }

                    if (paintData.Length > expectedBytes)
                    {
                        Array.Resize(ref paintData, expectedBytes);
                    }
                    finalImage.Dispose();
                    finalImage = Image.CreateFromData(exportWidth, exportHeight, false, Image.Format.Rgba8, paintData);
                }
            }

            return finalImage;
        }

        public static Image ApplyOriginalAlphaChannel(Image targetImage, OriginalVtexMetadata origMeta)
        {
            if (targetImage == null || targetImage.IsEmpty() || origMeta?.RawAlpha == null || origMeta.RawAlpha.Length == 0)
                return targetImage;

            if (targetImage.IsCompressed()) targetImage.Decompress();
            if (targetImage.HasMipmaps()) targetImage.ClearMipmaps();

            // Never downscale a painted texture down to a tiny 4x4/16x16 solid color swatch (e.g. vindicta_head)
            if (origMeta.Width >= 64 && origMeta.Height >= 64 &&
                (targetImage.GetWidth() != origMeta.Width || targetImage.GetHeight() != origMeta.Height))
            {
                targetImage = (Image)targetImage.Duplicate();
                targetImage.Resize(origMeta.Width, origMeta.Height, Image.Interpolation.Bilinear);
            }

            if (targetImage.GetFormat() != Image.Format.Rgba8) targetImage.Convert(Image.Format.Rgba8);

            int w = targetImage.GetWidth();
            int h = targetImage.GetHeight();
            byte[] data = targetImage.GetData();
            int pixelCount = w * h;
            int expectedBytes = pixelCount * 4;
            if (data == null || data.Length < expectedBytes) return targetImage;
            if (data.Length > expectedBytes)
            {
                Array.Resize(ref data, expectedBytes);
            }

            bool sameRes = (origMeta.Width == w && origMeta.Height == h && origMeta.RawAlpha.Length == pixelCount);

            for (int i = 0; i < pixelCount; i++)
            {
                int idx = i * 4;
                if (idx + 3 >= data.Length) break;

                if (sameRes && i < origMeta.RawAlpha.Length)
                {
                    data[idx + 3] = origMeta.RawAlpha[i];
                }
                else if (origMeta.Width > 0 && origMeta.Height > 0)
                {
                    int srcX = Mathf.Clamp(Mathf.FloorToInt((i % w) * (float)origMeta.Width / w), 0, origMeta.Width - 1);
                    int srcY = Mathf.Clamp(Mathf.FloorToInt((i / w) * (float)origMeta.Height / h), 0, origMeta.Height - 1);
                    int srcIdx = srcY * origMeta.Width + srcX;
                    if (srcIdx >= 0 && srcIdx < origMeta.RawAlpha.Length)
                    {
                        data[idx + 3] = origMeta.RawAlpha[srcIdx];
                    }
                }
            }

            return Image.CreateFromData(w, h, false, Image.Format.Rgba8, data);
        }

        private static void CompilePatchedMaterialsForParamOverrides(
            List<WorkspaceAssetEntry> workspaceAssets,
            string heroFolder,
            List<(string materialName, string pngPath, string vtexPath, string originalVtexCPath)> stagedTextures,
            List<(string vtexC, string originalVtexCPath)> compiledOutputFiles,
            string contentStagingRoot,
            string gameStagingRoot,
            Package sourceModPackage,
            Package baseVpkPackage,
            string deadlockPath,
            string compilerExe,
            string compilerWorkingDir,
            IProgress<ExportProgressReport> progress,
            CancellationToken ct)
        {
            if (workspaceAssets == null || workspaceAssets.Count == 0) return;

            static string StripVtexHashSuffix(string stem)
            {
                if (string.IsNullOrEmpty(stem)) return "";
                // e.g. "vindicta_headv2_color_png_2b9a36b1" -> "vindicta_headv2_color"
                return System.Text.RegularExpressions.Regex.Replace(
                    stem, @"_(?:png|tga|psd|tif|jpg|exr)_[0-9a-fA-F]{6,10}$", "",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }

            var stagedPngByStem = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            
            foreach (var (_, pngPath, _, origVtexC) in stagedTextures)
            {
                if (!string.IsNullOrEmpty(origVtexC) && File.Exists(pngPath))
                {
                    string stem = Path.GetFileNameWithoutExtension(origVtexC);
                    if (stem.EndsWith("_c", StringComparison.OrdinalIgnoreCase)) stem = stem[..^2];
                    stagedPngByStem[stem] = pngPath;
                    string stripped = StripVtexHashSuffix(stem);
                    if (!string.IsNullOrEmpty(stripped)) stagedPngByStem[stripped] = pngPath;
                }
            }

            var processedVmats = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            int totalParamOverrides = workspaceAssets.Sum(a => a.Materials?.Count(m => m?.ParamOverrides != null && m.ParamOverrides.HasAnyOverride()) ?? 0);
            progress?.Report(new ExportProgressReport(0.835f, $"[INFO] Checking material parameter overrides: {totalParamOverrides} material(s) queued for .vmat_c compilation."));
            foreach (var asset in workspaceAssets)
            {
                if (asset?.Materials == null) continue;
                foreach (var matEntry in asset.Materials)
                {
                    var ov = matEntry?.ParamOverrides;
                    if (ov == null || !ov.HasAnyOverride()) continue;

                    string matNameOnly = Path.GetFileNameWithoutExtension(matEntry.MaterialPath ?? "");
                    var dummySub = new SubmeshNodeInfo
                    {
                        OriginalVmatPath = !string.IsNullOrEmpty(matEntry.MaterialPath) && matEntry.MaterialPath.Contains('/')
                            ? matEntry.MaterialPath
                            : (!string.IsNullOrEmpty(heroFolder) && !string.IsNullOrEmpty(matNameOnly)
                                ? $"{heroFolder}/materials/{matNameOnly}.vmat"
                                : matEntry.MaterialPath),
                        MaterialName = matNameOnly
                    };

                    Package ownerPkg = sourceModPackage;
                    PackageEntry vmatPkgEntry = FindVmatEntryInPackage(sourceModPackage, dummySub);
                    if (vmatPkgEntry == null && baseVpkPackage != null)
                    {
                        vmatPkgEntry = FindVmatEntryInPackage(baseVpkPackage, dummySub);
                        ownerPkg = baseVpkPackage;
                    }
                    if (vmatPkgEntry == null || ownerPkg == null)
                    {
                        progress?.Report(new ExportProgressReport(0.838f, $"[WARNING] Could not locate .vmat_c in VPK for MaterialPath='{matEntry.MaterialPath}' (dummy='{dummySub.OriginalVmatPath}').", ExportLogLevel.Warning));
                        continue;
                    }

                    string fullVmatCPath = vmatPkgEntry.GetFullPath().Replace('\\', '/').TrimStart('/');
                    if (!processedVmats.Add(fullVmatCPath)) continue;

                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        using var vmatStream = ownerPkg.GetMemoryMappedStreamIfPossible(vmatPkgEntry);
                        using var vrfRes = new ValveResourceFormat.Resource { FileName = fullVmatCPath };
                        // Lectura completa (sin ReadMode.Deferred) para que MaterialExtract tenga todos los bloques cargados
                        vrfRes.Read(vmatStream, leaveOpen: true);

                        var extract = new ValveResourceFormat.IO.MaterialExtract(vrfRes);
                        var contentFile = extract.ToContentFile();
                        if (contentFile?.Data == null || contentFile.Data.Length == 0)
                        {
                            progress?.Report(new ExportProgressReport(0.838f, $"[WARNING] MaterialExtract returned empty data for '{fullVmatCPath}'.", ExportLogLevel.Warning));
                            continue;
                        }

                        string vmatText = System.Text.Encoding.UTF8.GetString(contentFile.Data);
                        var inv = System.Globalization.CultureInfo.InvariantCulture;

                        static string UpsertKvParam(string text, string key, string value)
                        {
                            string pattern = $@"""{System.Text.RegularExpressions.Regex.Escape(key)}""\s+""[^""]*""";
                            string replacement = $"\"{key}\"\t\"{value}\"";
                            if (System.Text.RegularExpressions.Regex.IsMatch(text, pattern))
                                return System.Text.RegularExpressions.Regex.Replace(text, pattern, replacement);
                            int insertIdx = text.IndexOf("\"shader\"", StringComparison.OrdinalIgnoreCase);
                            if (insertIdx >= 0)
                            {
                                int lineEnd = text.IndexOf('\n', insertIdx);
                                if (lineEnd >= 0)
                                    return text.Insert(lineEnd + 1, $"\t{replacement}\n");
                            }
                            return text;
                        }

                        if (ov.ColorTintRgb is { Length: >= 3 })
                        {
                            string vec = $"[{ov.ColorTintRgb[0].ToString("F6", inv)} {ov.ColorTintRgb[1].ToString("F6", inv)} {ov.ColorTintRgb[2].ToString("F6", inv)} 0.000000]";
                            vmatText = UpsertKvParam(vmatText, "g_vColorTint1", vec);
                            // Permitir que el Color Tint multiplique también los vertex colors (clave para vindicta_head)
                            vmatText = UpsertKvParam(vmatText, "g_bApplyTintToVertexColors", "1");

                            // Si TextureNprTramsissiveColor1 es un vector de color (ej. celeste en las piernas de Vindicta), actualizarlo al nuevo tint
                            if (System.Text.RegularExpressions.Regex.IsMatch(vmatText, @"""TextureNprTramsissiveColor1""\s+""\[[^\]]*\]"""))
                            {
                                vmatText = UpsertKvParam(vmatText, "TextureNprTramsissiveColor1", vec);
                            }
                        }
                        if (ov.VertexColorStrength.HasValue)
                        {
                            float vcs = Mathf.Clamp(ov.VertexColorStrength.Value, 0f, 1f);
                            vmatText = UpsertKvParam(vmatText, "g_fVertexColorStrength1", vcs.ToString("F4", inv));
                            if (vcs <= 0.001f)
                                vmatText = UpsertKvParam(vmatText, "F_VERTEX_COLOR", "0");
                        }
                        if (ov.SelfIllumTintRgb is { Length: >= 3 })
                        {
                            string vec = $"[{ov.SelfIllumTintRgb[0].ToString("F6", inv)} {ov.SelfIllumTintRgb[1].ToString("F6", inv)} {ov.SelfIllumTintRgb[2].ToString("F6", inv)} 0.000000]";
                            vmatText = UpsertKvParam(vmatText, "g_vSelfIllumTint1", vec);
                        }
                        if (ov.SelfIllumScale.HasValue)
                        {
                            float sis = MathF.Max(0f, ov.SelfIllumScale.Value);
                            vmatText = UpsertKvParam(vmatText, "g_flSelfIllumScale1", sis.ToString("F4", inv));
                            vmatText = UpsertKvParam(vmatText, "F_SELF_ILLUM", sis > 0.001f ? "1" : "0");
                        }
                        if (ov.SelfIllumAlbedoFactor.HasValue)
                        {
                            float sia = Mathf.Clamp(ov.SelfIllumAlbedoFactor.Value, 0f, 1f);
                            vmatText = UpsertKvParam(vmatText, "g_flSelfIllumAlbedoFactor1", sia.ToString("F4", inv));
                        }
                        if (ov.AlbedoContrast.HasValue || ov.AlbedoSaturation.HasValue || ov.AlbedoBrightness.HasValue)
                        {
                            float c = ov.AlbedoContrast ?? 1.0f;
                            float s = ov.AlbedoSaturation ?? 1.0f;
                            float b = ov.AlbedoBrightness ?? 1.0f;
                            string vec = $"[{c.ToString("F6", inv)} {s.ToString("F6", inv)} {b.ToString("F6", inv)} 0.000000]";
                            vmatText = UpsertKvParam(vmatText, "g_vAlbedoContrastSaturationBrightness1", vec);
                        }

                        // Map Compiled Textures (both full hashed stem AND stripped unhashed stem -> .vtex_c)
                        var compiledTexMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        string compiledColorStem = null;
                        var ctBlockMatch = System.Text.RegularExpressions.Regex.Match(vmatText, @"""Compiled Textures""\s*\{([\s\S]*?)\}");
                        if (ctBlockMatch.Success)
                        {
                            foreach (System.Text.RegularExpressions.Match tm in System.Text.RegularExpressions.Regex.Matches(ctBlockMatch.Groups[1].Value, @"""([^""]+)""\s+""([^""]+)"""))
                            {
                                string slotName = tm.Groups[1].Value;
                                string vtexRel = tm.Groups[2].Value.Replace('\\', '/').TrimStart('/');
                                string vtexC = vtexRel.EndsWith("_c", StringComparison.OrdinalIgnoreCase) ? vtexRel : vtexRel + "_c";
                                string vtexStem = Path.GetFileNameWithoutExtension(vtexRel);
                                compiledTexMap[vtexStem] = vtexC;
                                string stripped = StripVtexHashSuffix(vtexStem);
                                if (!string.IsNullOrEmpty(stripped)) compiledTexMap[stripped] = vtexC;
                                if (slotName.Equals("g_tColor", StringComparison.OrdinalIgnoreCase))
                                {
                                    compiledColorStem = vtexStem;
                                }
                            }
                            vmatText = vmatText.Remove(ctBlockMatch.Index, ctBlockMatch.Length);
                        }

                        // Si TextureColor1 era un vector ("[1.0 1.0 1.0 0.0]") pero el usuario pintó una textura para este material,
                        // reemplazar el vector por la textura PNG pintada en el staging.
                        if (System.Text.RegularExpressions.Regex.IsMatch(vmatText, @"""TextureColor1""\s+""\[[^\]]*\]"""))
                        {
                            string paintedColorPng = null;
                            if (!string.IsNullOrEmpty(compiledColorStem) && stagedPngByStem.TryGetValue(compiledColorStem, out var p1) && File.Exists(p1))
                                paintedColorPng = p1;
                            else if (!string.IsNullOrEmpty(matNameOnly) && stagedPngByStem.TryGetValue(matNameOnly + "_color", out var p2) && File.Exists(p2))
                                paintedColorPng = p2;

                            if (!string.IsNullOrEmpty(paintedColorPng))
                            {
                                string vmatRelDir = Path.GetDirectoryName((fullVmatCPath.EndsWith("_c", StringComparison.OrdinalIgnoreCase) ? fullVmatCPath[..^2] : fullVmatCPath))?.Replace('\\', '/') ?? "materials";
                                string pngFileName = Path.GetFileName(paintedColorPng);
                                string relColorPng = $"{vmatRelDir}/{pngFileName}".Replace('\\', '/').TrimStart('/');
                                string destColorPng = Path.Combine(contentStagingRoot, relColorPng.Replace('/', Path.DirectorySeparatorChar));
                                Directory.CreateDirectory(Path.GetDirectoryName(destColorPng));
                                if (!string.Equals(Path.GetFullPath(paintedColorPng), Path.GetFullPath(destColorPng), StringComparison.OrdinalIgnoreCase))
                                {
                                    File.Copy(paintedColorPng, destColorPng, overwrite: true);
                                }
                                vmatText = UpsertKvParam(vmatText, "TextureColor1", relColorPng);
                            }
                        }

                        // Remap MaterialExtract's un-numbered Texture* keys to Deadlock pbr.vfx's numbered Texture*1 keys
                        // so resourcecompiler.exe binds the actual PNG textures instead of falling back to default_color_tga (white)!
                        var pbrTexKeyRemap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["TextureColor"] = "TextureColor1",
                            ["TextureAmbientOcclusion"] = "TextureAmbientOcclusion1",
                            ["TextureNormalRoughness"] = "TextureNormal1",
                            ["TextureSelfIllumMask"] = "TextureSelfIllumMask1",
                            ["TextureNprTransmissiveColor"] = "TextureNprTramsissiveColor1",
                            ["TextureNprOutlineMask"] = "TextureNprOutlineMask1",
                            ["TextureTintMaskRimLightMask"] = "TextureTintMask1"
                        };

                        // Stage every Texture* .png/.tga referenced in the .vmat into contentStagingRoot
                        foreach (System.Text.RegularExpressions.Match texMatch in System.Text.RegularExpressions.Regex.Matches(vmatText, @"""(Texture[^""]+)""\s+""([^""]+\.(?:png|tga))""", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                        {
                            string keyName = texMatch.Groups[1].Value;
                            string relTexPath = texMatch.Groups[2].Value.Replace('\\', '/').TrimStart('/');
                            if (relTexPath.StartsWith("materials/default/", StringComparison.OrdinalIgnoreCase)) continue;

                            string destPng = Path.Combine(contentStagingRoot, relTexPath.Replace('/', Path.DirectorySeparatorChar));
                            string texStem = Path.GetFileNameWithoutExtension(relTexPath);
                            Directory.CreateDirectory(Path.GetDirectoryName(destPng));

                                if (stagedPngByStem.TryGetValue(texStem, out string paintedPng) && File.Exists(paintedPng))
                                {
                                    if (!string.Equals(Path.GetFullPath(paintedPng), Path.GetFullPath(destPng), StringComparison.OrdinalIgnoreCase))
                                    {
                                        File.Copy(paintedPng, destPng, overwrite: true);
                                }
                            }
                            else if (!File.Exists(destPng) && compiledTexMap.TryGetValue(texStem, out string origVtexC))
                            {
                                var meta = (sourceModPackage != null ? LoadOriginalVtexMetadata(origVtexC, sourceModPackage, deadlockPath) : null)
                                        ?? LoadOriginalVtexMetadata(origVtexC, baseVpkPackage, deadlockPath);
                                try
                                {
                                    if (meta?.BaseRgbaImage != null && !meta.BaseRgbaImage.IsEmpty())
                                    {
                                        meta.BaseRgbaImage.SavePng(destPng);
                                    }
                                }
                                finally
                                {
                                    meta?.BaseRgbaImage?.Dispose();
                                }
                            }

                            // If pbr.vfx expects the numbered Texture*1 key, replace any vector fallback with the staged PNG path
                            if (pbrTexKeyRemap.TryGetValue(keyName, out string numberedKey) && File.Exists(destPng))
                            {
                                vmatText = UpsertKvParam(vmatText, numberedKey, relTexPath);
                            }
                        }

                        string vmatRelSource = fullVmatCPath.EndsWith("_c", StringComparison.OrdinalIgnoreCase)
                            ? fullVmatCPath[..^2]
                            : fullVmatCPath;
                        string vmatFolder = Path.GetDirectoryName(vmatRelSource)?.Replace('\\', '/') ?? "materials";
                        string vmatContentDir = Path.Combine(contentStagingRoot, vmatFolder.Replace('/', Path.DirectorySeparatorChar));
                        string vmatGameDir = Path.Combine(gameStagingRoot, vmatFolder.Replace('/', Path.DirectorySeparatorChar));
                        Directory.CreateDirectory(vmatContentDir);

                        string vmatFileName = Path.GetFileName(vmatRelSource);
                        string fullVmatContentPath = Path.Combine(vmatContentDir, vmatFileName);
                        File.WriteAllText(fullVmatContentPath, vmatText);

                        var preExistingVtexC = new HashSet<string>(
                            Directory.Exists(gameStagingRoot)
                                ? Directory.GetFiles(gameStagingRoot, "*.vtex_c", SearchOption.AllDirectories)
                                : Array.Empty<string>(),
                            StringComparer.OrdinalIgnoreCase);

                        progress?.Report(new ExportProgressReport(0.84f, $"[INFO] Compiling patched material: {vmatFileName}..."));
                        string vmatArgs = $"-i \"{fullVmatContentPath.Replace('\\', '/')}\" -nop4";
                        int exitCode = RunCompilerProcess(compilerExe, compilerWorkingDir, vmatArgs, progress, ct, 0.845f);

                        string expectedVmatCName = vmatFileName + "_c";
                        string emittedVmatC = LocateEmittedFile(vmatGameDir, vmatContentDir, expectedVmatCName, gameStagingRoot, contentStagingRoot);
                        if (exitCode == 0 && !string.IsNullOrEmpty(emittedVmatC) && File.Exists(emittedVmatC))
                        {
                            compiledOutputFiles.Add((emittedVmatC, fullVmatCPath));
                            progress?.Report(new ExportProgressReport(0.848f, $"[SUCCESS] Compiled patched material: {fullVmatCPath}", ExportLogLevel.Success));

                            if (Directory.Exists(gameStagingRoot))
                            {
                                foreach (string newVtex in Directory.GetFiles(gameStagingRoot, "*.vtex_c", SearchOption.AllDirectories))
                                {
                                    if (preExistingVtexC.Contains(newVtex)) continue;
                                    string relVpk = Path.GetRelativePath(gameStagingRoot, newVtex).Replace('\\', '/').TrimStart('/');
                                    // Never pack engine default placeholder textures into the mod VPK
                                    if (relVpk.StartsWith("materials/default/", StringComparison.OrdinalIgnoreCase)) continue;
                                    if (!compiledOutputFiles.Any(c => string.Equals(c.originalVtexCPath, relVpk, StringComparison.OrdinalIgnoreCase)))
                                    {
                                        compiledOutputFiles.Add((newVtex, relVpk));
                                    }
                                }
                            }
                        }
                        else
                        {
                            progress?.Report(new ExportProgressReport(0.848f, $"[WARNING] resourcecompiler.exe failed (exit {exitCode}) compiling patched material '{vmatFileName}'. Continuing with texture-only export.", ExportLogLevel.Warning));
                        }
                    }
                    catch (Exception ex)
                    {
                        progress?.Report(new ExportProgressReport(0.848f, $"[ERROR] Failed patching material '{fullVmatCPath}': {ex.Message}", ExportLogLevel.Error));
                        GD.PrintErr($"[SkinExporter] Non-fatal error compiling patched material '{fullVmatCPath}': {ex}");
                    }
                }
            }
        }

        private static void TryStageCompanionRecolorMaps(
            ModExportConfig config,
            SubmeshNodeInfo submesh,
            Image paintedColorImg,
            OriginalVtexMetadata origColorMeta,
            string heroFolder,
            string contentMaterialsDir,
            Package sourceModPackage,
            Package baseVpkPackage,
            string deadlockPath,
            List<(string materialName, string pngPath, string vtexPath, string originalVtexCPath)> stagedTextures,
            HashSet<string> processedVtexCPaths,
            IProgress<ExportProgressReport> progress)
        {
            if (config == null || submesh == null || paintedColorImg == null || paintedColorImg.IsEmpty()) return;

            Package ownerPkg = sourceModPackage;
            PackageEntry vmatEntry = FindVmatEntryInPackage(sourceModPackage, submesh);
            if (vmatEntry == null && baseVpkPackage != null)
            {
                vmatEntry = FindVmatEntryInPackage(baseVpkPackage, submesh);
                ownerPkg = baseVpkPackage;
            }
            if (vmatEntry == null || ownerPkg == null) return;

            try
            {
                using var vmatStream = ownerPkg.GetMemoryMappedStreamIfPossible(vmatEntry);
                using var res = new ValveResourceFormat.Resource();
                res.Read(vmatStream, leaveOpen: true, mode: ReadMode.Deferred);
                if (res.ResourceType != ResourceType.Material || res.DataBlock is not VrfMaterial vmat) return;

                string subKey = ResolveSubmeshKey(submesh);

                // Compute average RGB of painted color image (sampled on 32x32 grid)
                static (float r, float g, float b, float lum) SampleAverageRgb(Image img)
                {
                    if (img == null || img.IsEmpty()) return (0.5f, 0.5f, 0.5f, 0.5f);
                    int w = img.GetWidth(), h = img.GetHeight();
                    int sx = Math.Max(1, w / 32), sy = Math.Max(1, h / 32);
                    double sr = 0, sg = 0, sb = 0; int n = 0;
                    for (int y = 0; y < h; y += sy)
                    {
                        for (int x = 0; x < w; x += sx)
                        {
                            Color c = img.GetPixel(x, y);
                            sr += c.R; sg += c.G; sb += c.B; n++;
                        }
                    }
                    if (n == 0) return (0.5f, 0.5f, 0.5f, 0.5f);
                    float r = (float)(sr / n), g = (float)(sg / n), b = (float)(sb / n);
                    return (r, g, b, 0.2126f * r + 0.7152f * g + 0.0722f * b);
                }

                var paintedAvg = SampleAverageRgb(paintedColorImg);
                (float r, float g, float b, float lum) origAvg = origColorMeta?.BaseRgbaImage != null
                    ? SampleAverageRgb(origColorMeta.BaseRgbaImage)
                    : (0.5f, 0.5f, 0.5f, 0.5f);

                // 1. If F_SELF_ILLUM == 1 and g_flSelfIllumAlbedoFactor1 < 0.05 (e.g. vindicta_props),
                //    self-illum ignores g_tColor and keeps glowing vanilla color unless g_tSelfIllumMask is modulated.
                bool hasSelfIllum = vmat.IntParams != null && vmat.IntParams.TryGetValue("F_SELF_ILLUM", out long siFlag) && siFlag != 0;
                float siAlbedoFactor = 1.0f;
                if (vmat.FloatParams != null && vmat.FloatParams.TryGetValue("g_flSelfIllumAlbedoFactor1", out float af))
                    siAlbedoFactor = af;

                bool userEditedSelfIllum = config.PreBakedChannelsBySubmesh.ContainsKey((subKey, MaterialTextureChannel.SelfIllum));
                if (hasSelfIllum && siAlbedoFactor < 0.05f && !userEditedSelfIllum && origAvg.lum > 0.03f)
                {
                    float lumRatio = Mathf.Clamp(paintedAvg.lum / origAvg.lum, 0.0f, 1.0f);
                    if (lumRatio < 0.65f)
                    {
                        string maskRaw = DeadlockPlayground.Materials.Source2TextureLoader.GetTextureParam(vmat, "g_tSelfIllumMask");
                        if (!string.IsNullOrEmpty(maskRaw) && !Source2MaterialHelper.IsEngineDummyOrPlaceholderTexture(maskRaw))
                        {
                            string maskVtexC = maskRaw.Replace('\\', '/').Trim().TrimStart('/');
                            if (maskVtexC.EndsWith(".vtex", StringComparison.OrdinalIgnoreCase)) maskVtexC += "_c";
                            else if (!maskVtexC.EndsWith(".vtex_c", StringComparison.OrdinalIgnoreCase)) maskVtexC += ".vtex_c";

                            if (processedVtexCPaths.Add(maskVtexC))
                            {
                                var maskMeta = (sourceModPackage != null ? LoadOriginalVtexMetadata(maskVtexC, sourceModPackage, deadlockPath) : null)
                                            ?? LoadOriginalVtexMetadata(maskVtexC, baseVpkPackage, deadlockPath);
                                try
                                {
                                    if (maskMeta?.BaseRgbaImage != null && !maskMeta.BaseRgbaImage.IsEmpty())
                                    {
                                        using var modMask = (Image)maskMeta.BaseRgbaImage.Duplicate();
                                        if (modMask.IsCompressed()) modMask.Decompress();
                                        if (modMask.HasMipmaps()) modMask.ClearMipmaps();
                                        if (modMask.GetFormat() != Image.Format.Rgba8) modMask.Convert(Image.Format.Rgba8);

                                        byte[] mBytes = modMask.GetData();
                                        int pxCount = modMask.GetWidth() * modMask.GetHeight();
                                        for (int i = 0; i < pxCount; i++)
                                        {
                                            int o = i * 4;
                                            mBytes[o] = (byte)Mathf.Clamp(Mathf.RoundToInt(mBytes[o] * lumRatio), 0, 255);
                                            mBytes[o + 1] = (byte)Mathf.Clamp(Mathf.RoundToInt(mBytes[o + 1] * lumRatio), 0, 255);
                                            mBytes[o + 2] = (byte)Mathf.Clamp(Mathf.RoundToInt(mBytes[o + 2] * lumRatio), 0, 255);
                                        }
                                        using var finalMask = Image.CreateFromData(modMask.GetWidth(), modMask.GetHeight(), false, Image.Format.Rgba8, mBytes);
                                        string baseName = Path.GetFileNameWithoutExtension(maskVtexC);
                                        if (baseName.EndsWith("_c", StringComparison.OrdinalIgnoreCase)) baseName = baseName[..^2];
                                        string pngName = $"{baseName}.png";
                                        string fullPng = Path.Combine(contentMaterialsDir, pngName);
                                        finalMask.SavePng(fullPng);
                                        string fullVtex = Path.Combine(contentMaterialsDir, $"{baseName}.vtex");
                                        string relPng = $"{heroFolder}/materials/{pngName}".Replace('\\', '/').TrimStart('/');
                                        File.WriteAllText(fullVtex, GenerateSource2DmxVtex(relPng, !string.IsNullOrEmpty(maskMeta.Format) ? maskMeta.Format : "BC7", maskMeta.Flags));
                                        stagedTextures.Add((baseName, fullPng, fullVtex, maskVtexC));
                                        progress?.Report(new ExportProgressReport(0.29f, $"[INFO] Auto-scaled g_tSelfIllumMask ({lumRatio:F2}x) for '{submesh.DisplayName}' so darkened recolor is not overridden by vanilla self-illum."));
                                    }
                                }
                                finally
                                {
                                    maskMeta?.BaseRgbaImage?.Dispose();
                                }
                            }
                        }
                    }
                }

                // 2. If g_tNprTransmissiveColor is a hero-specific small swatch (e.g. vindicta_head cyan shadow swatch),
                //    tint it to match the painted color so vanilla cyan shadow fill doesn't dominate the recolor.
                bool userEditedShadow = config.PreBakedChannelsBySubmesh.ContainsKey((subKey, MaterialTextureChannel.Shadow));
                if (!userEditedShadow)
                {
                    string transRaw = DeadlockPlayground.Materials.Source2TextureLoader.GetTextureParam(vmat, "g_tNprTransmissiveColor");
                    if (!string.IsNullOrEmpty(transRaw) && !Source2MaterialHelper.IsEngineDummyOrPlaceholderTexture(transRaw))
                    {
                        string transVtexC = transRaw.Replace('\\', '/').Trim().TrimStart('/');
                        if (transVtexC.EndsWith(".vtex", StringComparison.OrdinalIgnoreCase)) transVtexC += "_c";
                        else if (!transVtexC.EndsWith(".vtex_c", StringComparison.OrdinalIgnoreCase)) transVtexC += ".vtex_c";

                        var transMeta = (sourceModPackage != null ? LoadOriginalVtexMetadata(transVtexC, sourceModPackage, deadlockPath) : null)
                                     ?? LoadOriginalVtexMetadata(transVtexC, baseVpkPackage, deadlockPath);
                        try
                        {
                            if (transMeta != null && transMeta.Width > 0 && transMeta.Width <= 16 && transMeta.Height > 0 && transMeta.Height <= 16 &&
                                processedVtexCPaths.Add(transVtexC))
                            {
                                byte sr = (byte)Mathf.Clamp(Mathf.RoundToInt(paintedAvg.r * 0.72f * 255f), 0, 255);
                                byte sg = (byte)Mathf.Clamp(Mathf.RoundToInt(paintedAvg.g * 0.72f * 255f), 0, 255);
                                byte sb = (byte)Mathf.Clamp(Mathf.RoundToInt(paintedAvg.b * 0.72f * 255f), 0, 255);
                                byte[] swBytes = new byte[transMeta.Width * transMeta.Height * 4];
                                for (int i = 0; i < transMeta.Width * transMeta.Height; i++)
                                {
                                    int o = i * 4;
                                    swBytes[o] = sr; swBytes[o + 1] = sg; swBytes[o + 2] = sb; swBytes[o + 3] = 255;
                                }
                                using var swImg = Image.CreateFromData(transMeta.Width, transMeta.Height, false, Image.Format.Rgba8, swBytes);
                                string baseName = Path.GetFileNameWithoutExtension(transVtexC);
                                if (baseName.EndsWith("_c", StringComparison.OrdinalIgnoreCase)) baseName = baseName[..^2];
                                string pngName = $"{baseName}.png";
                                string fullPng = Path.Combine(contentMaterialsDir, pngName);
                                swImg.SavePng(fullPng);
                                string fullVtex = Path.Combine(contentMaterialsDir, $"{baseName}.vtex");
                                string relPng = $"{heroFolder}/materials/{pngName}".Replace('\\', '/').TrimStart('/');
                                File.WriteAllText(fullVtex, GenerateSource2DmxVtex(relPng, !string.IsNullOrEmpty(transMeta.Format) ? transMeta.Format : "BC7", transMeta.Flags));
                                stagedTextures.Add((baseName, fullPng, fullVtex, transVtexC));
                                progress?.Report(new ExportProgressReport(0.29f, $"[INFO] Auto-tinted g_tNprTransmissiveColor swatch ({sr},{sg},{sb}) for '{submesh.DisplayName}' to match painted recolor."));
                            }
                        }
                        finally
                        {
                            transMeta?.BaseRgbaImage?.Dispose();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                GD.Print($"[SkinExporter] Notice: Skipped companion recolor sync for '{submesh.DisplayName}': {ex.Message}");
            }
        }

        private static bool HasAnySubmeshPreBakedChannels(ModExportConfig config, SubmeshNodeInfo submesh)
        {
            if (config?.PreBakedChannelsBySubmesh == null || submesh == null) return false;
            string subKey = ResolveSubmeshKey(submesh);
            foreach (var kvp in config.PreBakedChannelsBySubmesh)
            {
                if (kvp.Key.subKey.Equals(subKey, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static void StageEditedMultiChannelMaps(
            ModExportConfig config,
            SubmeshNodeInfo submesh,
            string heroFolder,
            string contentMaterialsDir,
            Package sourceModPackage,
            Package baseVpkPackage,
            string deadlockPath,
            List<(string materialName, string pngPath, string vtexPath, string originalVtexCPath)> stagedTextures,
            HashSet<string> processedHeroVtexCPaths,
            IProgress<ExportProgressReport> progress)
        {
            if (config?.PreBakedChannelsBySubmesh == null || submesh == null) return;
            string subKey = ResolveSubmeshKey(submesh);
            var chSet = submesh.Channels;

            string NormalizeVtexC(string p)
            {
                if (string.IsNullOrWhiteSpace(p)) return null;
                string norm = p.Replace('\\', '/').TrimStart('/');
                if (!norm.EndsWith("_c", StringComparison.OrdinalIgnoreCase)) norm += "_c";
                return norm;
            }

            void StageSingleVtex(string vtexCPath, Image imgToStage, string label, bool preserveOrigAlpha = false)
            {
                string normVtex = NormalizeVtexC(vtexCPath);
                if (string.IsNullOrEmpty(normVtex) || imgToStage == null || imgToStage.IsEmpty()) return;
                if (!processedHeroVtexCPaths.Add(normVtex)) return;

                string baseName = Path.GetFileNameWithoutExtension(normVtex);
                if (baseName.EndsWith("_c", StringComparison.OrdinalIgnoreCase))
                    baseName = baseName[..^2];

                var origMeta = (sourceModPackage != null ? LoadOriginalVtexMetadata(normVtex, sourceModPackage, deadlockPath) : null)
                            ?? LoadOriginalVtexMetadata(normVtex, baseVpkPackage, deadlockPath);
                try
                {
                    using Image workImg = (Image)imgToStage.Duplicate();
                    if (workImg.IsCompressed()) workImg.Decompress();
                    if (workImg.HasMipmaps()) workImg.ClearMipmaps();
                    if (workImg.GetFormat() != Image.Format.Rgba8) workImg.Convert(Image.Format.Rgba8);

                    int targetW = origMeta != null && origMeta.Width >= 64 ? origMeta.Width : (config.Resolution > 0 ? config.Resolution : workImg.GetWidth());
                    int targetH = origMeta != null && origMeta.Height >= 64 ? origMeta.Height : (config.Resolution > 0 ? config.Resolution : workImg.GetHeight());
                    if (workImg.GetWidth() != targetW || workImg.GetHeight() != targetH)
                    {
                        workImg.Resize(targetW, targetH, Image.Interpolation.Bilinear);
                    }

                    Image finalImg = preserveOrigAlpha && origMeta != null
                        ? ApplyOriginalAlphaChannel(workImg, origMeta)
                        : workImg;

                    string pngFileName = $"{baseName}.png";
                    string fullPngPath = Path.Combine(contentMaterialsDir, pngFileName);
                    if (finalImg.SavePng(fullPngPath) != Error.Ok)
                    {
                        progress?.Report(new ExportProgressReport(0.25f, $"[WARNING] Failed saving multi-channel PNG '{pngFileName}'.", ExportLogLevel.Warning));
                        return;
                    }

                    string vtexFileName = $"{baseName}.vtex";
                    string fullVtexPath = Path.Combine(contentMaterialsDir, vtexFileName);
                    string relPngPath = $"{heroFolder}/materials/{pngFileName}".Replace('\\', '/').TrimStart('/');
                    string outFmt = !string.IsNullOrEmpty(origMeta?.Format) ? origMeta.Format : "BC7";
                    VTexFlags flags = origMeta != null ? origMeta.Flags : (VTexFlags.SUGGEST_CLAMPS | VTexFlags.SUGGEST_CLAMPT);

                    File.WriteAllText(fullVtexPath, GenerateSource2DmxVtex(relPngPath, outFmt, flags));
                    stagedTextures.Add((baseName, fullPngPath, fullVtexPath, normVtex));
                    progress?.Report(new ExportProgressReport(0.29f, $"[INFO] Staged edited {label} map -> {vtexFileName} ({normVtex})"));

                    if (!ReferenceEquals(finalImg, workImg)) finalImg.Dispose();
                }
                finally
                {
                    if (origMeta != null)
                    {
                        origMeta.BaseRgbaImage?.Dispose();
                        origMeta.BaseRgbaImage = null;
                        origMeta.RawAlpha = null;
                    }
                }
            }

            // 1. Packed Normal + Roughness (g_tNormalRoughness) OR separate standalone Normal/Roughness
            bool hasNormEdit = config.PreBakedChannelsBySubmesh.TryGetValue((subKey, MaterialTextureChannel.Normal), out var editedNorm);
            bool hasRoughEdit = config.PreBakedChannelsBySubmesh.TryGetValue((subKey, MaterialTextureChannel.Roughness), out var editedRough);

            if (hasNormEdit || hasRoughEdit)
            {
                var normEntry = chSet?.GetChannel(MaterialTextureChannel.Normal);
                var roughEntry = chSet?.GetChannel(MaterialTextureChannel.Roughness);
                string packedPath = normEntry?.PackedSourceVtexPath
                                 ?? roughEntry?.PackedSourceVtexPath
                                 ?? (submesh.OriginalNormalVtex?.Contains("normalroughness", StringComparison.OrdinalIgnoreCase) == true ? submesh.OriginalNormalVtex : null);

                if (!string.IsNullOrEmpty(packedPath))
                {
                    Image normSrc = editedNorm ?? (config.PreExtractedChannelOriginalsBySubmesh.TryGetValue((subKey, MaterialTextureChannel.Normal), out var origN) ? origN : normEntry?.SourceImage);
                    Image roughSrc = editedRough ?? (config.PreExtractedChannelOriginalsBySubmesh.TryGetValue((subKey, MaterialTextureChannel.Roughness), out var origR) ? origR : roughEntry?.SourceImage);
                    using Image packedImg = MaterialChannelDiscovery.PackNormalRoughnessImage(
                        normSrc,
                        roughSrc,
                        packAsBlueToAlpha: normEntry?.HasBlueToAlphaRoughness ?? true);
                    if (packedImg != null)
                    {
                        StageSingleVtex(packedPath, packedImg, "Normal+Roughness", preserveOrigAlpha: false);
                    }
                }
                else
                {
                    if (hasNormEdit)
                    {
                        StageSingleVtex(normEntry?.SourcePath ?? submesh.OriginalNormalVtex, editedNorm, "Normal");
                    }
                    if (hasRoughEdit)
                    {
                        string rTarget = roughEntry?.SourcePath;
                        if (string.IsNullOrEmpty(rTarget) && submesh.OriginalTextureParams != null)
                        {
                            submesh.OriginalTextureParams.TryGetValue("g_tRoughness", out rTarget);
                        }
                        StageSingleVtex(rTarget, editedRough, "Roughness");
                    }
                }
            }

            // 2. AO, Shadow, SelfIllum, TintMask, Metalness, RimMask, OutlineMask
            var standaloneChannels = new (MaterialTextureChannel ch, string label, Func<string> fallbackPath)[]
            {
                (MaterialTextureChannel.AmbientOcclusion, "AO", () => submesh.OriginalAoVtex),
                (MaterialTextureChannel.Shadow, "Shadow/NPR", () =>
                {
                    if (submesh.OriginalTextureParams != null)
                    {
                        foreach (var k in new[] { "g_tNprTransmissiveColor", "TextureNprTramsissiveColor1", "TextureNprTransmissiveColor", "g_tShadowTint" })
                        {
                            if (submesh.OriginalTextureParams.TryGetValue(k, out var vp) && !string.IsNullOrEmpty(vp)) return vp;
                        }
                    }
                    return null;
                }),
                (MaterialTextureChannel.SelfIllum, "Self-Illum", () =>
                {
                    string val = null;
                    submesh.OriginalTextureParams?.TryGetValue("g_tSelfIllumMask", out val);
                    return val;
                }),
                (MaterialTextureChannel.TintMask, "Tint Mask", () => submesh.OriginalMaskVtex),
                (MaterialTextureChannel.Metalness, "Metalness", () =>
                {
                    string val = null;
                    submesh.OriginalTextureParams?.TryGetValue("g_tMetalness", out val);
                    return val;
                }),
                (MaterialTextureChannel.RimMask, "Rim Mask", () =>
                {
                    string val = null;
                    submesh.OriginalTextureParams?.TryGetValue("g_tRimLightMask", out val);
                    return val;
                }),
                (MaterialTextureChannel.OutlineMask, "Outline Mask", () => submesh.OriginalNprOutlineVtex)
            };

            foreach (var (ch, label, fallbackPath) in standaloneChannels)
            {
                if (!config.PreBakedChannelsBySubmesh.TryGetValue((subKey, ch), out var bakedImg) || bakedImg == null || bakedImg.IsEmpty())
                    continue;

                var entry = chSet?.GetChannel(ch);
                string targetVtexC = entry?.SourcePath;
                if (string.IsNullOrEmpty(targetVtexC) || !targetVtexC.Contains(".vtex", StringComparison.OrdinalIgnoreCase))
                {
                    targetVtexC = fallbackPath();
                }

                if (!string.IsNullOrEmpty(targetVtexC))
                {
                    StageSingleVtex(targetVtexC, bakedImg, label);
                }
            }
        }

        public static List<string> ResolveAllColorVtexCPaths(SubmeshNodeInfo submesh, string heroCodename, Package primaryVpkPackage, Package fallbackVpkPackage = null)
        {
            var list = new List<string>();
            if (submesh?.AllColorVtexCPaths != null && submesh.AllColorVtexCPaths.Count > 0)
            {
                foreach (var p in submesh.AllColorVtexCPaths)
                {
                    if (!string.IsNullOrWhiteSpace(p))
                    {
                        string norm = p.Replace('\\', '/').TrimStart('/');
                        if (!Source2MaterialHelper.IsEngineDummyOrPlaceholderTexture(norm) && !list.Contains(norm, StringComparer.OrdinalIgnoreCase))
                            list.Add(norm);
                    }
                }
            }

            if (list.Count <= 1 && primaryVpkPackage != null && submesh != null)
            {
                string primary = ResolveOriginalColorVtexCPath(submesh, heroCodename, primaryVpkPackage, fallbackVpkPackage);
                if (!string.IsNullOrEmpty(primary) && !Source2MaterialHelper.IsEngineDummyOrPlaceholderTexture(primary) && !list.Contains(primary, StringComparer.OrdinalIgnoreCase))
                {
                    list.Insert(0, primary);
                }
                if (!string.IsNullOrEmpty(submesh?.OriginalSecondaryColorVtexCPath) && !Source2MaterialHelper.IsEngineDummyOrPlaceholderTexture(submesh.OriginalSecondaryColorVtexCPath))
                {
                    string sec = submesh.OriginalSecondaryColorVtexCPath.Replace('\\', '/').TrimStart('/');
                    if (!list.Contains(sec, StringComparer.OrdinalIgnoreCase))
                        list.Add(sec);
                }
                if (submesh?.AllColorVtexCPaths != null)
                {
                    foreach (var p in submesh.AllColorVtexCPaths)
                    {
                        string norm = p.Replace('\\', '/').TrimStart('/');
                        if (!Source2MaterialHelper.IsEngineDummyOrPlaceholderTexture(norm) && !list.Contains(norm, StringComparer.OrdinalIgnoreCase))
                            list.Add(norm);
                    }
                }
            }

            if (list.Count == 0)
            {
                string primary = ResolveOriginalColorVtexCPath(submesh, heroCodename, primaryVpkPackage, fallbackVpkPackage);
                if (!string.IsNullOrEmpty(primary) && !Source2MaterialHelper.IsEngineDummyOrPlaceholderTexture(primary) && !list.Contains(primary, StringComparer.OrdinalIgnoreCase))
                {
                    list.Add(primary);
                }
                if (!string.IsNullOrEmpty(submesh?.OriginalSecondaryColorVtexCPath) && !Source2MaterialHelper.IsEngineDummyOrPlaceholderTexture(submesh.OriginalSecondaryColorVtexCPath))
                {
                    string sec = submesh.OriginalSecondaryColorVtexCPath.Replace('\\', '/').TrimStart('/');
                    if (!list.Contains(sec, StringComparer.OrdinalIgnoreCase))
                        list.Add(sec);
                }
            }

            return list;
        }

        private static PackageEntry FindVmatEntryInPackage(Package pkg, SubmeshNodeInfo submesh)
        {
            if (pkg == null || submesh == null) return null;

            var candidates = new List<string>();
            if (!string.IsNullOrEmpty(submesh.OriginalVmatPath)) candidates.Add(submesh.OriginalVmatPath);
            if (!string.IsNullOrEmpty(submesh.MaterialName))
            {
                candidates.Add($"materials/{submesh.MaterialName}.vmat_c");
                candidates.Add($"models/{submesh.MaterialName}.vmat_c");
            }

            foreach (var c in candidates)
            {
                string norm = c.Replace('\\', '/').TrimStart('/');
                if (!norm.EndsWith("_c", StringComparison.OrdinalIgnoreCase)) norm += "_c";
                var found = pkg.FindEntry(norm);
                if (found != null) return found;
            }

            string targetMatName = !string.IsNullOrEmpty(submesh.MaterialName)
                ? submesh.MaterialName
                : (!string.IsNullOrEmpty(submesh.OriginalVmatPath) ? Path.GetFileNameWithoutExtension(submesh.OriginalVmatPath) : "");

            if (!string.IsNullOrEmpty(targetMatName))
            {
                if (targetMatName.EndsWith(".vmat", StringComparison.OrdinalIgnoreCase))
                    targetMatName = Path.GetFileNameWithoutExtension(targetMatName);

                string targetFile = $"{targetMatName}.vmat_c";
               if (pkg.Entries.TryGetValue("vmat_c", out var vmatEntries))
                {
                    string preferredDir = !string.IsNullOrEmpty(submesh.OriginalVmatPath) && submesh.OriginalVmatPath.Contains('/')
                        ? Path.GetDirectoryName(submesh.OriginalVmatPath.Replace('\\', '/'))?.Replace('\\', '/')?.TrimStart('/')
                        : null;

                    PackageEntry found = null;
                    if (!string.IsNullOrEmpty(preferredDir))
                    {
                        found = vmatEntries.FirstOrDefault(e =>
                            e.FileName.Equals(targetMatName, StringComparison.OrdinalIgnoreCase) &&
                            e.GetFullPath().Replace('\\', '/').StartsWith(preferredDir + "/", StringComparison.OrdinalIgnoreCase));
                    }
                    found ??= vmatEntries.FirstOrDefault(e => string.Equals(e.FileName + "." + e.TypeName, targetFile, StringComparison.OrdinalIgnoreCase))
                           ?? vmatEntries.FirstOrDefault(e => e.FileName.Equals(targetMatName, StringComparison.OrdinalIgnoreCase));
                    if (found != null) return found;
                }
            }

            return null;
        }

        public static string ResolveOriginalColorVtexCPath(SubmeshNodeInfo submesh, string heroCodename, Package primaryVpkPackage, Package fallbackVpkPackage = null)
        {
            if (!string.IsNullOrEmpty(submesh?.OriginalColorVtexCPath) && !Source2MaterialHelper.IsEngineDummyOrPlaceholderTexture(submesh.OriginalColorVtexCPath) && submesh?.AllColorVtexCPaths != null && submesh.AllColorVtexCPaths.Count > 1)
            {
                return submesh.OriginalColorVtexCPath.Replace('\\', '/').TrimStart('/');
            }

            if (submesh != null)
            {
                Package ownerPkg = primaryVpkPackage;
                PackageEntry foundEntry = FindVmatEntryInPackage(primaryVpkPackage, submesh);
                if (foundEntry == null && fallbackVpkPackage != null && fallbackVpkPackage != primaryVpkPackage)
                {
                    foundEntry = FindVmatEntryInPackage(fallbackVpkPackage, submesh);
                    if (foundEntry != null) ownerPkg = fallbackVpkPackage;
                }

                if (foundEntry != null && ownerPkg != null)
                {
                    try
                    {
                        using var vmatStream = ownerPkg.GetMemoryMappedStreamIfPossible(foundEntry);
                        using var resource = new ValveResourceFormat.Resource();
                        resource.Read(vmatStream, leaveOpen: true, mode: ReadMode.Deferred);

                        if (resource.ResourceType == ResourceType.Material && resource.DataBlock is VrfMaterial vmat)
                        {
                            string colorTex = DeadlockPlayground.Materials.Source2TextureLoader.GetTextureParam(vmat, "g_tColor")
                                           ?? DeadlockPlayground.Materials.Source2TextureLoader.GetTextureParam(vmat, "TextureColor")
                                           ?? DeadlockPlayground.Materials.Source2TextureLoader.GetTextureParam(vmat, "g_tColor1")
                                           ?? DeadlockPlayground.Materials.Source2TextureLoader.GetTextureParam(vmat, "g_tColorA");

                            string secColorTex = DeadlockPlayground.Materials.Source2TextureLoader.GetTextureParam(vmat, "TextureColor2")
                                              ?? DeadlockPlayground.Materials.Source2TextureLoader.GetTextureParam(vmat, "g_tColor2")
                                              ?? DeadlockPlayground.Materials.Source2TextureLoader.GetTextureParam(vmat, "TextureColorB")
                                              ?? DeadlockPlayground.Materials.Source2TextureLoader.GetTextureParam(vmat, "g_tAltColor_Reveal")
                                              ?? DeadlockPlayground.Materials.Source2TextureLoader.GetTextureParam(vmat, "g_tAltColor")
                                              ?? DeadlockPlayground.Materials.Source2TextureLoader.GetTextureParam(vmat, "TextureAltColor");

                            if (!string.IsNullOrEmpty(secColorTex) && !Source2MaterialHelper.IsEngineDummyOrPlaceholderTexture(secColorTex))
                            {
                                string normSec = secColorTex.Replace('\\', '/').Trim().TrimStart('/');
                                if (normSec.EndsWith(".vtex", StringComparison.OrdinalIgnoreCase)) normSec += "_c";
                                else if (!normSec.EndsWith(".vtex_c", StringComparison.OrdinalIgnoreCase)) normSec += ".vtex_c";
                                submesh.OriginalSecondaryColorVtexCPath = normSec;
                                if (!submesh.AllColorVtexCPaths.Contains(normSec, StringComparer.OrdinalIgnoreCase))
                                    submesh.AllColorVtexCPaths.Add(normSec);
                            }

                            if (vmat.TextureParams != null)
                            {
                                foreach (var (pKey, pVal) in vmat.TextureParams)
                                {
                                    if (string.IsNullOrWhiteSpace(pVal) || Source2MaterialHelper.IsEngineDummyOrPlaceholderTexture(pVal)) continue;
                                    if (pKey.Contains("Normal", StringComparison.OrdinalIgnoreCase) ||
                                        pKey.Contains("Roughness", StringComparison.OrdinalIgnoreCase) ||
                                        pKey.Contains("AmbientOcclusion", StringComparison.OrdinalIgnoreCase) ||
                                        pKey.Contains("AO", StringComparison.OrdinalIgnoreCase) ||
                                        pKey.Contains("Mask", StringComparison.OrdinalIgnoreCase) ||
                                        pKey.Contains("Transmissive", StringComparison.OrdinalIgnoreCase) ||
                                        pKey.Contains("Shadow", StringComparison.OrdinalIgnoreCase))
                                    {
                                        continue;
                                    }

                                    if (pKey.Contains("Color", StringComparison.OrdinalIgnoreCase) ||
                                        pKey.Contains("Albedo", StringComparison.OrdinalIgnoreCase) ||
                                        pKey.Contains("Diffuse", StringComparison.OrdinalIgnoreCase))
                                    {
                                        string normExtra = pVal.Replace('\\', '/').Trim().TrimStart('/');
                                        if (normExtra.EndsWith(".vtex", StringComparison.OrdinalIgnoreCase)) normExtra += "_c";
                                        else if (!normExtra.EndsWith(".vtex_c", StringComparison.OrdinalIgnoreCase)) normExtra += ".vtex_c";
                                        if (!Source2MaterialHelper.IsEngineDummyOrPlaceholderTexture(normExtra) && !submesh.AllColorVtexCPaths.Contains(normExtra, StringComparer.OrdinalIgnoreCase))
                                        {
                                            submesh.AllColorVtexCPaths.Add(normExtra);
                                        }
                                        if (string.IsNullOrEmpty(colorTex))
                                        {
                                            colorTex = pVal;
                                        }
                                    }
                                }
                            }

                            if (!string.IsNullOrEmpty(colorTex) && !Source2MaterialHelper.IsEngineDummyOrPlaceholderTexture(colorTex))
                            {
                                string norm = colorTex.Replace('\\', '/').Trim().TrimStart('/');
                                if (norm.EndsWith(".vtex", StringComparison.OrdinalIgnoreCase)) norm += "_c";
                                else if (!norm.EndsWith(".vtex_c", StringComparison.OrdinalIgnoreCase)) norm += ".vtex_c";
                                submesh.OriginalColorVtexCPath = norm;
                                if (!submesh.AllColorVtexCPaths.Contains(norm, StringComparer.OrdinalIgnoreCase))
                                    submesh.AllColorVtexCPaths.Insert(0, norm);
                                return norm;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        GD.Print($"[SkinExporter] Notice: Could not extract OriginalColorVtexCPath from VPK: {ex.Message}");
                    }
                }

                // Fallback for texture-only or custom-named mod textures in primaryVpkPackage: match vtex_c by material name prefix
                if (primaryVpkPackage != null && primaryVpkPackage.Entries.TryGetValue("vtex_c", out var modVtexEntries))
                {
                    string matShort = ResolveSubmeshMaterialName(submesh, heroCodename);
                    if (!string.IsNullOrEmpty(matShort))
                    {
                        var matchedVtex = modVtexEntries.FirstOrDefault(e =>
                            (e.FileName.Equals(matShort, StringComparison.OrdinalIgnoreCase) ||
                             e.FileName.StartsWith(matShort + "_", StringComparison.OrdinalIgnoreCase) ||
                             e.FileName.StartsWith(matShort + ".", StringComparison.OrdinalIgnoreCase)) &&
                            !e.FileName.Contains("normal", StringComparison.OrdinalIgnoreCase) &&
                            !e.FileName.Contains("rough", StringComparison.OrdinalIgnoreCase) &&
                            !e.FileName.Contains("mask", StringComparison.OrdinalIgnoreCase) &&
                            !e.FileName.Contains("_ao", StringComparison.OrdinalIgnoreCase));

                        if (matchedVtex != null)
                        {
                            string matchedFull = matchedVtex.GetFullPath().Replace('\\', '/').TrimStart('/');
                            submesh.OriginalColorVtexCPath = matchedFull;
                            if (!submesh.AllColorVtexCPaths.Contains(matchedFull, StringComparer.OrdinalIgnoreCase))
                                submesh.AllColorVtexCPaths.Insert(0, matchedFull);
                            return matchedFull;
                        }
                    }
                }
            }

            if (!string.IsNullOrEmpty(submesh?.OriginalColorVtexCPath) && !Source2MaterialHelper.IsEngineDummyOrPlaceholderTexture(submesh.OriginalColorVtexCPath))
            {
                return submesh.OriginalColorVtexCPath.Replace('\\', '/').TrimStart('/');
            }

            string fallbackMatName = ResolveSubmeshMaterialName(submesh, heroCodename);
            string fallbackFolder = ResolveHeroInternalFolder(heroCodename, null);
            string defaultPath = $"{fallbackFolder}/materials/{fallbackMatName}_color.vtex_c".Replace('\\', '/').TrimStart('/');
            if (submesh != null)
            {
                submesh.OriginalColorVtexCPath = defaultPath;
                if (!submesh.AllColorVtexCPaths.Contains(defaultPath, StringComparer.OrdinalIgnoreCase))
                    submesh.AllColorVtexCPaths.Add(defaultPath);
            }
            return defaultPath;
        }

        public static OriginalVtexMetadata LoadOriginalVtexMetadata(
            string originalVtexCPath,
            Package baseVpkPackage,
            string deadlockGamePath)
        {
            if (string.IsNullOrWhiteSpace(originalVtexCPath) || Source2MaterialHelper.IsEngineDummyOrPlaceholderTexture(originalVtexCPath)) return null;

            string normPath = originalVtexCPath.Replace('\\', '/').TrimStart('/');
            if (!normPath.EndsWith("_c", StringComparison.OrdinalIgnoreCase))
            {
                if (normPath.EndsWith(".vtex", StringComparison.OrdinalIgnoreCase)) normPath += "_c";
                else normPath += ".vtex_c";
            }

            Stream vtexStream = null;

            if (File.Exists(originalVtexCPath))
            {
                vtexStream = OpenSharedFileReadStream(originalVtexCPath);
            }
            else if (File.Exists(normPath))
            {
                vtexStream = OpenSharedFileReadStream(normPath);
            }

            if (vtexStream == null && baseVpkPackage != null)
            {
                var entry = baseVpkPackage.FindEntry(normPath);
                if (entry == null)
                {
                    string fileNameOnly = Path.GetFileName(normPath);
                    if (baseVpkPackage.Entries.TryGetValue("vtex_c", out var vtexEntries))
                    {
                        entry = vtexEntries.FirstOrDefault(e =>
                            string.Equals(e.FileName + "." + e.TypeName, fileNameOnly, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(e.FileName, Path.GetFileNameWithoutExtension(fileNameOnly), StringComparison.OrdinalIgnoreCase));
                    }
                }

                if (entry != null)
                {
                    try
                    {
                        normPath = entry.GetFullPath().Replace('\\', '/').TrimStart('/');
                        vtexStream = baseVpkPackage.GetMemoryMappedStreamIfPossible(entry);
                    }
                    catch (Exception ex)
                    {
                        GD.Print($"[SkinExporter] Notice: Could not read entry '{normPath}' from VPK: {ex.Message}");
                    }
                }
            }

            if (vtexStream == null && !string.IsNullOrEmpty(deadlockGamePath))
            {
                string gameCitadelFile = Path.Combine(deadlockGamePath, "game", "citadel", normPath);
                if (File.Exists(gameCitadelFile))
                {
                    vtexStream = OpenSharedFileReadStream(gameCitadelFile);
                }
            }

            if (vtexStream == null)
            {
                string projectDir = ProjectSettings.GlobalizePath("res://");
                string projectRoot = Path.GetFullPath(Path.Combine(projectDir, ".."));
                string[] searchRoots = new[]
                {
                    Path.Combine(projectRoot, "scratch"),
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "scratch"),
                    Path.Combine(OS.GetUserDataDir(), "staging")
                };

                string targetFileName = Path.GetFileName(normPath);

                foreach (var root in searchRoots)
                {
                    if (vtexStream != null) break;
                    if (!Directory.Exists(root)) continue;

                    string candidate = Path.Combine(root, normPath);
                    if (File.Exists(candidate))
                    {
                        vtexStream = OpenSharedFileReadStream(candidate);
                        if (vtexStream != null) break;
                    }

                    try
                    {
                        var matchingFiles = Directory.GetFiles(root, targetFileName, SearchOption.AllDirectories);
                        if (matchingFiles.Length > 0 && File.Exists(matchingFiles[0]))
                        {
                            vtexStream = OpenSharedFileReadStream(matchingFiles[0]);
                            if (vtexStream != null) break;
                        }
                    }
                    catch { }
                }
            }

            if (vtexStream == null)
            {
                return null;
            }

            using (vtexStream)
            {
                try
                {
                    using var resource = new ValveResourceFormat.Resource { FileName = normPath };
                    resource.Read(vtexStream, leaveOpen: true, mode: ReadMode.Deferred);

                    if (resource.ResourceType != ResourceType.Texture || resource.DataBlock is not ValveResourceFormat.ResourceTypes.Texture vrfTex)
                    {
                        return null;
                    }

                    var meta = new OriginalVtexMetadata
                    {
                        VtexCPath = normPath,
                        Width = (int)vrfTex.Width,
                        Height = (int)vrfTex.Height,
                        Format = vrfTex.Format.ToString(),
                        Flags = vrfTex.Flags,
                        NumMipLevels = (int)vrfTex.NumMipLevels
                    };

                    using var skBitmap = vrfTex.GenerateBitmap(depth: 0, mipLevel: 0);
                    if (skBitmap != null)
                    {
                        int w = skBitmap.Width;
                        int h = skBitmap.Height;
                        meta.Width = w;
                        meta.Height = h;
                        int count = w * h;
                        int rowBytes = skBitmap.RowBytes;
                        IntPtr ptr = skBitmap.GetPixels();
                        if (ptr != IntPtr.Zero && rowBytes >= w * 4 && skBitmap.ByteCount >= rowBytes * h)
                        {
                            byte[] rawAlpha = new byte[count];
                            byte[] rawRgba = new byte[count * 4];
                            bool isBgra = skBitmap.ColorType == SKColorType.Bgra8888;
                            bool hasNonOpaqueAlpha = false;

                            unsafe
                            {
                                byte* srcBase = (byte*)ptr;
                                fixed (byte* dstPtr = rawRgba)
                                fixed (byte* alphaPtr = rawAlpha)
                                {
                                    uint* d32 = (uint*)dstPtr;
                                    for (int y = 0; y < h; y++)
                                    {
                                        uint* s32 = (uint*)(srcBase + y * rowBytes);
                                        int rowOffset = y * w;
                                        for (int x = 0; x < w; x++)
                                        {
                                            uint c = s32[x];
                                            byte a = (byte)((c >> 24) & 0xFF);
                                            int dstIdx = rowOffset + x;
                                            alphaPtr[dstIdx] = a;
                                            if (a < 250) hasNonOpaqueAlpha = true;

                                            if (isBgra)
                                            {
                                                byte b = (byte)(c & 0xFF);
                                                byte g = (byte)((c >> 8) & 0xFF);
                                                byte r = (byte)((c >> 16) & 0xFF);
                                                d32[dstIdx] = (uint)(r | (g << 8) | (b << 16) | ((uint)a << 24));
                                            }
                                            else
                                            {
                                                d32[dstIdx] = c;
                                            }
                                        }
                                    }
                                }
                            }

                            meta.RawAlpha = rawAlpha;
                            meta.HasNonUniformAlpha = hasNonOpaqueAlpha;
                            meta.BaseRgbaImage = Image.CreateFromData(w, h, false, Image.Format.Rgba8, rawRgba);
                            rawRgba = null;
                        }
                    }

                    return meta;
                }
                catch (Exception ex)
                {
                    GD.PrintErr($"[SkinExporter] Failed to decode original .vtex_c metadata for '{normPath}': {ex.Message}");
                    return null;
                }
            }
        }

        public static string GenerateSource2Kv3Material(string relativeColorTexturePath, string materialName)
        {
            return GenerateSource2Kv3Material(relativeColorTexturePath, materialName, null, null, null, null);
        }

        public static string GenerateSource2Kv3Material(
            string relativeColorTexturePath,
            string materialName,
            SubmeshNodeInfo submesh,
            string deadlockGamePath,
            string gameStagingRoot = null,
            Package baseVpkPackage = null)
        {
            string texPath = FormatVtexCResource(relativeColorTexturePath);

            if (submesh != null)
            {
                TryExtractOriginalVmatTexturesFromVpk(deadlockGamePath, submesh, baseVpkPackage);
            }

            string normalVtex = !string.IsNullOrEmpty(submesh?.OriginalNormalVtex)
                ? FormatVtexCResource(submesh.OriginalNormalVtex)
                : null;

            string aoVtex = !string.IsNullOrEmpty(submesh?.OriginalAoVtex)
                ? FormatVtexCResource(submesh.OriginalAoVtex)
                : null;

            string nprOutlineVtex = !string.IsNullOrEmpty(submesh?.OriginalNprOutlineVtex)
                ? FormatVtexCResource(submesh.OriginalNprOutlineVtex)
                : null;

            string maskVtex = !string.IsNullOrEmpty(submesh?.OriginalMaskVtex)
                ? FormatVtexCResource(submesh.OriginalMaskVtex)
                : null;

            if (!string.IsNullOrEmpty(gameStagingRoot))
            {
                if (!string.IsNullOrEmpty(normalVtex)) EnsureTextureStagedInGameTree(gameStagingRoot, normalVtex, baseVpkPackage);
                if (!string.IsNullOrEmpty(aoVtex)) EnsureTextureStagedInGameTree(gameStagingRoot, aoVtex, baseVpkPackage);
                if (!string.IsNullOrEmpty(nprOutlineVtex)) EnsureTextureStagedInGameTree(gameStagingRoot, nprOutlineVtex, baseVpkPackage);
                if (!string.IsNullOrEmpty(maskVtex)) EnsureTextureStagedInGameTree(gameStagingRoot, maskVtex, baseVpkPackage);
            }

            var sb = new StringBuilder();
            sb.AppendLine("<!-- kv3 encoding:text:version{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d} format:generic:version{7412167c-06e9-4698-aff2-e63eb59037e7} -->");
            sb.AppendLine("{");
            sb.AppendLine("\tshader = \"pbr.vfx\"");
            sb.AppendLine("\tF_SPECULAR = 1");
            bool needsAlphaTest = false;
            if (submesh?.Mesh != null && GodotObject.IsInstanceValid(submesh.Mesh))
            {
                var matCheck = submesh.Mesh.GetSurfaceOverrideMaterial(submesh.SurfaceIndex) ?? submesh.Mesh.Mesh?.SurfaceGetMaterial(submesh.SurfaceIndex);
                if (matCheck is StandardMaterial3D stdM && stdM.Transparency != BaseMaterial3D.TransparencyEnum.Disabled)
                {
                    needsAlphaTest = true;
                }
                else if (matCheck is ShaderMaterial shM)
                {
                    var enCutout = shM.GetShaderParameter("enable_alpha_cutout");
                    if (enCutout.VariantType == Variant.Type.Bool && enCutout.AsBool()) needsAlphaTest = true;
                }
            }
            if (needsAlphaTest)
            {
                sb.AppendLine("\tF_ALPHA_TEST = 1");
                sb.AppendLine("\tg_flAlphaTestReference = 0.500000");
            }
            sb.AppendLine($"\tg_tColor = resource:\"{texPath}\"");

            if (!string.IsNullOrEmpty(normalVtex))
            {
                sb.AppendLine($"\tg_tNormalRoughness = resource:\"{normalVtex}\"");
            }
            if (!string.IsNullOrEmpty(aoVtex))
            {
                sb.AppendLine($"\tg_tAmbientOcclusion = resource:\"{aoVtex}\"");
            }
            if (!string.IsNullOrEmpty(nprOutlineVtex))
            {
                sb.AppendLine($"\tg_tNPROutlineMask = resource:\"{nprOutlineVtex}\"");
            }
            if (!string.IsNullOrEmpty(maskVtex))
            {
                sb.AppendLine($"\tg_tSelfIllumMask = resource:\"{maskVtex}\"");
            }

            sb.AppendLine("}");
            return sb.ToString();
        }

        public static string FormatVtexCResource(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            string clean = path.Replace('\\', '/').Trim().TrimStart('/');
            if (clean.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            {
                clean = clean.Substring(0, clean.Length - 4) + ".vtex_c";
            }
            else if (clean.EndsWith(".vtex", StringComparison.OrdinalIgnoreCase))
            {
                clean += "_c";
            }
            else if (!clean.EndsWith(".vtex_c", StringComparison.OrdinalIgnoreCase))
            {
                clean += ".vtex_c";
            }
            return clean;
        }

        private static Package OpenSharedReadPackage(string vpkPath)
        {
            var pkg = new Package();
            FileStream fs = null;
            try
            {
                fs = new FileStream(vpkPath, FileMode.Open, System.IO.FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                pkg.SetFileName(vpkPath);
                pkg.Read(fs);
                fs = null; // Owned and disposed by pkg.Dispose()
                return pkg;
            }
            catch
            {
                fs?.Dispose();
                pkg.Dispose();
                throw;
            }
        }

        private static Stream OpenSharedFileReadStream(string filePath)
        {
            try
            {
                return new FileStream(filePath, FileMode.Open, System.IO.FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            }
            catch
            {
                return null;
            }
        }

        private static void EnsureTextureStagedInGameTree(string gameStagingRoot, string vtexCInternalPath, Package baseVpkPackage)
        {
            if (string.IsNullOrEmpty(gameStagingRoot) || string.IsNullOrEmpty(vtexCInternalPath)) return;
            try
            {
                string normPath = vtexCInternalPath.Replace('\\', '/').TrimStart('/');
                string fullPath = Path.Combine(gameStagingRoot, normPath);
                if (File.Exists(fullPath)) return;

                string dir = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                if (baseVpkPackage != null)
                {
                    var entry = baseVpkPackage.FindEntry(normPath);
                    if (entry != null)
                    {
                        using var src = baseVpkPackage.GetMemoryMappedStreamIfPossible(entry);
                        using var dst = File.Create(fullPath);
                        src.CopyTo(dst);
                        return;
                    }
                }

                File.WriteAllBytes(fullPath, Array.Empty<byte>());
            }
            catch (Exception ex)
            {
                GD.Print($"[SkinExporter] Notice: Could not stage texture '{vtexCInternalPath}' into game tree: {ex.Message}");
            }
        }

        private static void TryExtractOriginalVmatTexturesFromVpk(
            string deadlockGamePath,
            SubmeshNodeInfo submesh,
            Package baseVpkPackage)
        {
            if (submesh == null) return;
            if (!string.IsNullOrEmpty(submesh.OriginalNormalVtex) && !string.IsNullOrEmpty(submesh.OriginalAoVtex))
            {
                return;
            }

            Package pkg = baseVpkPackage;
            bool disposePkg = false;

            try
            {
                if (pkg == null && !string.IsNullOrEmpty(deadlockGamePath))
                {
                    string vpkPath = Path.Combine(deadlockGamePath, "game", "citadel", "pak01_dir.vpk");
                    if (File.Exists(vpkPath))
                    {
                        pkg = OpenSharedReadPackage(vpkPath);
                        disposePkg = true;
                    }
                }

                if (pkg == null) return;

                PackageEntry foundEntry = FindVmatEntryInPackage(pkg, submesh);

                if (foundEntry != null)
                {
                    using var vmatStream = pkg.GetMemoryMappedStreamIfPossible(foundEntry);
                    using var resource = new ValveResourceFormat.Resource();
                    resource.Read(vmatStream, leaveOpen: true, mode: ReadMode.Deferred);

                    if (resource.ResourceType == ResourceType.Material && resource.DataBlock is VrfMaterial vmat)
                    {
                        if (string.IsNullOrEmpty(submesh.OriginalShader) && !string.IsNullOrEmpty(vmat.ShaderName))
                        {
                            submesh.OriginalShader = vmat.ShaderName;
                        }

                        if (vmat.TextureParams != null)
                        {
                            foreach (var kvp in vmat.TextureParams)
                            {
                                submesh.OriginalTextureParams[kvp.Key] = kvp.Value;
                                if (string.IsNullOrEmpty(submesh.OriginalNormalVtex) &&
                                    (kvp.Key.Equals("g_tNormalRoughness", StringComparison.OrdinalIgnoreCase) ||
                                     kvp.Key.Equals("g_tNormal", StringComparison.OrdinalIgnoreCase)))
                                {
                                    submesh.OriginalNormalVtex = kvp.Value;
                                }
                                else if (string.IsNullOrEmpty(submesh.OriginalAoVtex) &&
                                         (kvp.Key.Equals("g_tAmbientOcclusion", StringComparison.OrdinalIgnoreCase) ||
                                          kvp.Key.Equals("g_tAO", StringComparison.OrdinalIgnoreCase)))
                                {
                                    submesh.OriginalAoVtex = kvp.Value;
                                }
                                else if (string.IsNullOrEmpty(submesh.OriginalNprOutlineVtex) &&
                                         kvp.Key.Equals("g_tNPROutlineMask", StringComparison.OrdinalIgnoreCase))
                                {
                                    submesh.OriginalNprOutlineVtex = kvp.Value;
                                }
                                else if (string.IsNullOrEmpty(submesh.OriginalMaskVtex) &&
                                         (kvp.Key.Equals("g_tSelfIllumMask", StringComparison.OrdinalIgnoreCase) ||
                                          kvp.Key.Equals("g_tMask", StringComparison.OrdinalIgnoreCase)))
                                {
                                    submesh.OriginalMaskVtex = kvp.Value;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                GD.Print($"[SkinExporter] Notice: Could not extract original VMAT from VPK: {ex.Message}");
            }
            finally
            {
                if (disposePkg)
                {
                    pkg?.Dispose();
                }
            }
        }

        public static string GenerateSource2DmxVtex(
            string relativePathToPng,
            string outputFormat = "BC7",
            VTexFlags flags = VTexFlags.SUGGEST_CLAMPS | VTexFlags.SUGGEST_CLAMPT,
            bool isLinear = false)
        {
            string cleanPngPath = (relativePathToPng ?? "").Replace('\\', '/').TrimStart('/');
            string fmt = string.IsNullOrWhiteSpace(outputFormat) ? "BC7" : outputFormat.Trim();

            bool clampS = flags.HasFlag(VTexFlags.SUGGEST_CLAMPS);
            bool clampT = flags.HasFlag(VTexFlags.SUGGEST_CLAMPT);
            bool clampU = flags.HasFlag(VTexFlags.SUGGEST_CLAMPU);
            string clampStr = $"{(clampS ? "1" : "0")} {(clampT ? "1" : "0")} {(clampU ? "1" : "0")}";

            bool isBc1 = fmt.Equals("BC1", StringComparison.OrdinalIgnoreCase);
            string colorSpace = isLinear ? "linear" : "srgb";
            bool noLod = flags.HasFlag(VTexFlags.NO_LOD);
            string noLodStr = noLod ? "1" : "0";

            string channelOutputBlock;
            if (isBc1)
            {
                channelOutputBlock = 
$@"		""CDmeTextureOutputChannel""
		{{
			""m_inputTextureArray"" ""string_array"" [ ""InputTexture0"" ]
			""m_srcChannels"" ""string"" ""rgb""
			""m_dstChannels"" ""string"" ""rgb""
			""m_mipAlgorithm"" ""CDmeImageProcessor""
			{{
				""m_algorithm"" ""string"" ""Box""
				""m_stringArg"" ""string"" """"
				""m_vFloat4Arg"" ""vector4"" ""0 0 0 0""
			}}
			""m_outputColorSpace"" ""string"" ""{colorSpace}""
		}}";
            }
            else
            {
                channelOutputBlock = 
$@"		""CDmeTextureOutputChannel""
		{{
			""m_inputTextureArray"" ""string_array"" [ ""InputTexture0"" ]
			""m_srcChannels"" ""string"" ""rgb""
			""m_dstChannels"" ""string"" ""rgb""
			""m_mipAlgorithm"" ""CDmeImageProcessor""
			{{
				""m_algorithm"" ""string"" ""Box""
				""m_stringArg"" ""string"" """"
				""m_vFloat4Arg"" ""vector4"" ""0 0 0 0""
			}}
			""m_outputColorSpace"" ""string"" ""{colorSpace}""
		}},
		""CDmeTextureOutputChannel""
		{{
			""m_inputTextureArray"" ""string_array"" [ ""InputTexture0"" ]
			""m_srcChannels"" ""string"" ""a""
			""m_dstChannels"" ""string"" ""a""
			""m_mipAlgorithm"" ""CDmeImageProcessor""
			{{
				""m_algorithm"" ""string"" ""Box""
				""m_stringArg"" ""string"" """"
				""m_vFloat4Arg"" ""vector4"" ""0 0 0 0""
			}}
			""m_outputColorSpace"" ""string"" ""linear""
		}}";
            }

            return 
$@"<!-- dmx encoding keyvalues2_noids 1 format vtex 1 -->
""CDmeVtex""
{{
	""m_inputTextureArray"" ""element_array""
	[
		""CDmeInputTexture""
		{{
			""m_name"" ""string"" ""InputTexture0""
			""m_fileName"" ""string"" ""{cleanPngPath}""
			""m_colorSpace"" ""string"" ""{colorSpace}""
			""m_typeString"" ""string"" ""2D""
			""m_imageProcessorArray"" ""element_array""
			[
				""CDmeImageProcessor""
				{{
					""m_algorithm"" ""string"" ""None""
					""m_stringArg"" ""string"" """"
					""m_vFloat4Arg"" ""vector4"" ""0 0 0 0""
				}}
			]
		}}
	]
	""m_outputTypeString"" ""string"" ""2D""
	""m_outputFormat"" ""string"" ""{fmt}""
	""m_outputClearColor"" ""vector4"" ""0 0 0 0""
	""m_nOutputMinDimension"" ""int"" ""4""
	""m_nOutputMaxDimension"" ""int"" ""0""
	""m_textureOutputChannelArray"" ""element_array""
	[
{channelOutputBlock}
	]
	""m_vClamp"" ""vector3"" ""{clampStr}""
	""m_bNoLod"" ""bool"" ""{noLodStr}""
}}
";
        }

        private static int FindRootOpeningBrace(string text)
        {
            if (string.IsNullOrEmpty(text)) return -1;
            int headerEnd = text.IndexOf("-->", StringComparison.Ordinal);
            return headerEnd >= 0 ? text.IndexOf('{', headerEnd) : text.IndexOf('{');
        }

        private static bool HasMeaningfulVpcfOverrides(string kv3Text, EmitterOverrideData ov)
        {
            if (string.IsNullOrEmpty(kv3Text) || ov == null) return false;
            if (!ov.Visible) return true;
            if (Math.Abs(ov.SizeScale - 1.0f) > 0.01f) return true;
            if (Math.Abs(ov.AlphaScale - 1.0f) > 0.01f) return true;

            if (ov.TintColorRgba != null && ov.TintColorRgba.Length >= 3)
            {
                int r = Math.Clamp((int)MathF.Round(ov.TintColorRgba[0] * 255f), 0, 255);
                int g = Math.Clamp((int)MathF.Round(ov.TintColorRgba[1] * 255f), 0, 255);
                int b = Math.Clamp((int)MathF.Round(ov.TintColorRgba[2] * 255f), 0, 255);

                var colMatch = System.Text.RegularExpressions.Regex.Match(
                    kv3Text,
                    @"m_ConstantColor\s*=\s*\[\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)(?:\s*,\s*(\d+))?\s*\]");
                if (colMatch.Success)
                {
                    int baseR = int.Parse(colMatch.Groups[1].Value);
                    int baseG = int.Parse(colMatch.Groups[2].Value);
                    int baseB = int.Parse(colMatch.Groups[3].Value);
                    if (Math.Abs(r - baseR) > 1 || Math.Abs(g - baseG) > 1 || Math.Abs(b - baseB) > 1)
                    {
                        return true;
                    }
                }
                else
                {
                    if (r < 250 || g < 250 || b < 250)
                    {
                        return true;
                    }
                }
            }

            if (ov.HasRandomColor && kv3Text.Contains("C_INIT_RandomColor"))
            {
                if (ov.ColorMinRgb != null && ov.ColorMinRgb.Length >= 3)
                {
                    int rMin = Math.Clamp((int)MathF.Round(ov.ColorMinRgb[0] * 255f), 0, 255);
                    int gMin = Math.Clamp((int)MathF.Round(ov.ColorMinRgb[1] * 255f), 0, 255);
                    int bMin = Math.Clamp((int)MathF.Round(ov.ColorMinRgb[2] * 255f), 0, 255);
                    var minMatch = System.Text.RegularExpressions.Regex.Match(
                        kv3Text,
                        @"m_ColorMin\s*=\s*\[\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*\]");
                    if (minMatch.Success)
                    {
                        int baseR = int.Parse(minMatch.Groups[1].Value);
                        int baseG = int.Parse(minMatch.Groups[2].Value);
                        int baseB = int.Parse(minMatch.Groups[3].Value);
                        if (Math.Abs(rMin - baseR) > 1 || Math.Abs(gMin - baseG) > 1 || Math.Abs(bMin - baseB) > 1)
                        {
                            return true;
                        }
                    }
                }

                if (ov.ColorMaxRgb != null && ov.ColorMaxRgb.Length >= 3)
                {
                    int rMax = Math.Clamp((int)MathF.Round(ov.ColorMaxRgb[0] * 255f), 0, 255);
                    int gMax = Math.Clamp((int)MathF.Round(ov.ColorMaxRgb[1] * 255f), 0, 255);
                    int bMax = Math.Clamp((int)MathF.Round(ov.ColorMaxRgb[2] * 255f), 0, 255);
                    var maxMatch = System.Text.RegularExpressions.Regex.Match(
                        kv3Text,
                        @"m_ColorMax\s*=\s*\[\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*\]");
                    if (maxMatch.Success)
                    {
                        int baseR = int.Parse(maxMatch.Groups[1].Value);
                        int baseG = int.Parse(maxMatch.Groups[2].Value);
                        int baseB = int.Parse(maxMatch.Groups[3].Value);
                        if (Math.Abs(rMax - baseR) > 1 || Math.Abs(gMax - baseG) > 1 || Math.Abs(bMax - baseB) > 1)
                        {
                            return true;
                        }
                    }
                }
            }

            if (ov.HasColorFade && ov.ColorFadeRgb != null && ov.ColorFadeRgb.Length >= 3 && kv3Text.Contains("C_OP_ColorInterpolate"))
            {
                int rFade = Math.Clamp((int)MathF.Round(ov.ColorFadeRgb[0] * 255f), 0, 255);
                int gFade = Math.Clamp((int)MathF.Round(ov.ColorFadeRgb[1] * 255f), 0, 255);
                int bFade = Math.Clamp((int)MathF.Round(ov.ColorFadeRgb[2] * 255f), 0, 255);
                var fadeMatch = System.Text.RegularExpressions.Regex.Match(
                    kv3Text,
                    @"m_ColorFade\s*=\s*\[\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*\]");
                if (fadeMatch.Success)
                {
                    int baseR = int.Parse(fadeMatch.Groups[1].Value);
                    int baseG = int.Parse(fadeMatch.Groups[2].Value);
                    int baseB = int.Parse(fadeMatch.Groups[3].Value);
                    if (Math.Abs(rFade - baseR) > 1 || Math.Abs(gFade - baseG) > 1 || Math.Abs(bFade - baseB) > 1)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static string ApplyOverridesToVpcfKv3Text(string kv3Text, EmitterOverrideData ov)
        {
            if (string.IsNullOrEmpty(kv3Text) || ov == null) return kv3Text;

            kv3Text = System.Text.RegularExpressions.Regex.Replace(
                kv3Text,
                @"format:vpcf(6[4-9]|[7-9]\d+|\d{3,}):version\{[a-f0-9\-]+\}",
                "format:vpcf63:version{a6e6a69e-52d3-4527-8b9c-ff3bb91aca3e}");

            string[] incompatibleCs2Keys = new[]
            {
                @"^\s*m_NamedValue\s*=\s*""""\s*\r?\n",
                @"^\s*m_nRandomSeed\s*=\s*-?\d+\s*\r?\n",
                @"^\s*m_nNoiseInputVectorAttribute\s*=\s*\d+\s*\r?\n",
                @"^\s*m_flNoCameraFallback\s*=\s*[-0-9\.]+\s*\r?\n",
                @"^\s*m_bUseBoundsCenter\s*=\s*(?:true|false)\s*\r?\n",
                @"^\s*m_flNotchedRangeMin\s*=\s*[-0-9\.]+\s*\r?\n",
                @"^\s*m_flNotchedRangeMax\s*=\s*[-0-9\.]+\s*\r?\n",
                @"^\s*m_flNotchedOutputOutside\s*=\s*[-0-9\.]+\s*\r?\n",
                @"^\s*m_flNotchedOutputInside\s*=\s*[-0-9\.]+\s*\r?\n",
                @"^\s*m_nRoundType\s*=\s*""[^""]*""\s*\r?\n",
                @"^\s*m_nRefractBlurType\s*=\s*""[^""]*""\s*\r?\n",
                @"^\s*m_bReverseOrder\s*=\s*(?:true|false)\s*\r?\n",
                @"^\s*m_strSnapshotSubset\s*=\s*""[^""]*""\s*\r?\n",
                @"^\s*m_flLOD[0-3]\s*=\s*[-0-9\.]+\s*\r?\n"
            };

            foreach (var pattern in incompatibleCs2Keys)
            {
                kv3Text = System.Text.RegularExpressions.Regex.Replace(
                    kv3Text,
                    pattern,
                    "",
                    System.Text.RegularExpressions.RegexOptions.Multiline);
            }

            if (string.IsNullOrEmpty(kv3Text) || ov == null) return kv3Text;

            if (!kv3Text.Contains("m_ForceGenerators"))
            {
                int renderersIdx = kv3Text.IndexOf("m_Renderers", StringComparison.Ordinal);
                if (renderersIdx > 0)
                {
                    kv3Text = kv3Text.Insert(renderersIdx, "m_ForceGenerators = [  ]\n\t");
                }
            }

            if (!ov.Visible)
            {
                if (System.Text.RegularExpressions.Regex.IsMatch(kv3Text, @"m_nMaxParticles\s*=\s*\d+"))
                {
                    kv3Text = System.Text.RegularExpressions.Regex.Replace(kv3Text, @"m_nMaxParticles\s*=\s*\d+", "m_nMaxParticles = 0");
                }
                else
                {
                    int braceIdx = FindRootOpeningBrace(kv3Text);
                    if (braceIdx >= 0)
                        kv3Text = kv3Text.Insert(braceIdx + 1, "\n\tm_nMaxParticles = 0");
                }
            }

            if (ov.TintColorRgba != null && ov.TintColorRgba.Length >= 3)
            {
                int r = Math.Clamp((int)MathF.Round(ov.TintColorRgba[0] * 255f), 0, 255);
                int g = Math.Clamp((int)MathF.Round(ov.TintColorRgba[1] * 255f), 0, 255);
                int b = Math.Clamp((int)MathF.Round(ov.TintColorRgba[2] * 255f), 0, 255);
                int a = Math.Clamp((int)MathF.Round(ov.AlphaScale * 255f), 0, 255);
                string rgbaBlock = $"[ {r}, {g}, {b}, {a} ]";

                if (System.Text.RegularExpressions.Regex.IsMatch(kv3Text, @"m_ConstantColor\s*=\s*\[[^\]]*\]"))
                {
                    kv3Text = System.Text.RegularExpressions.Regex.Replace(kv3Text, @"m_ConstantColor\s*=\s*\[[^\]]*\]", $"m_ConstantColor = {rgbaBlock}");
                }
                else
                {
                    if (r < 250 || g < 250 || b < 250 || a < 250)
                    {
                        int braceIdx = FindRootOpeningBrace(kv3Text);
                        if (braceIdx >= 0)
                            kv3Text = kv3Text.Insert(braceIdx + 1, $"\n\tm_ConstantColor = {rgbaBlock}");
                    }
                }
            }

            if (ov.HasRandomColor && kv3Text.Contains("C_INIT_RandomColor"))
            {
                if (ov.ColorMinRgb != null && ov.ColorMinRgb.Length >= 3)
                {
                    int rMin = Math.Clamp((int)MathF.Round(ov.ColorMinRgb[0] * 255f), 0, 255);
                    int gMin = Math.Clamp((int)MathF.Round(ov.ColorMinRgb[1] * 255f), 0, 255);
                    int bMin = Math.Clamp((int)MathF.Round(ov.ColorMinRgb[2] * 255f), 0, 255);
                    kv3Text = System.Text.RegularExpressions.Regex.Replace(kv3Text, @"m_ColorMin\s*=\s*\[[^\]]*\]", $"m_ColorMin = [ {rMin}, {gMin}, {bMin} ]");
                }
                if (ov.ColorMaxRgb != null && ov.ColorMaxRgb.Length >= 3)
                {
                    int rMax = Math.Clamp((int)MathF.Round(ov.ColorMaxRgb[0] * 255f), 0, 255);
                    int gMax = Math.Clamp((int)MathF.Round(ov.ColorMaxRgb[1] * 255f), 0, 255);
                    int bMax = Math.Clamp((int)MathF.Round(ov.ColorMaxRgb[2] * 255f), 0, 255);
                    kv3Text = System.Text.RegularExpressions.Regex.Replace(kv3Text, @"m_ColorMax\s*=\s*\[[^\]]*\]", $"m_ColorMax = [ {rMax}, {gMax}, {bMax} ]");
                }
            }

            if (ov.HasColorFade && ov.ColorFadeRgb != null && ov.ColorFadeRgb.Length >= 3 && kv3Text.Contains("C_OP_ColorInterpolate"))
            {
                int rFade = Math.Clamp((int)MathF.Round(ov.ColorFadeRgb[0] * 255f), 0, 255);
                int gFade = Math.Clamp((int)MathF.Round(ov.ColorFadeRgb[1] * 255f), 0, 255);
                int bFade = Math.Clamp((int)MathF.Round(ov.ColorFadeRgb[2] * 255f), 0, 255);
                kv3Text = System.Text.RegularExpressions.Regex.Replace(kv3Text, @"m_ColorFade\s*=\s*\[[^\]]*\]", $"m_ColorFade = [ {rFade}, {gFade}, {bFade} ]");
            }

            if (Math.Abs(ov.SizeScale - 1.0f) > 0.01f)
            {
                string scaleStr = ov.SizeScale.ToString("F4", System.Globalization.CultureInfo.InvariantCulture);

                var match = System.Text.RegularExpressions.Regex.Match(kv3Text, @"m_flConstantRadius\s*=\s*([0-9\.]+)");
                if (match.Success && float.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float baseRad))
                {
                    float newRad = baseRad * ov.SizeScale;
                    kv3Text = kv3Text.Substring(0, match.Index) +
                              $"m_flConstantRadius = {newRad.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)}" +
                              kv3Text.Substring(match.Index + match.Length);
                }
                else
                {
                    int braceIdx = FindRootOpeningBrace(kv3Text);
                    if (braceIdx >= 0)
                        kv3Text = kv3Text.Insert(braceIdx + 1, $"\n\tm_flConstantRadius = {(5.0f * ov.SizeScale).ToString("F4", System.Globalization.CultureInfo.InvariantCulture)}");
                }

                if (!kv3Text.Contains("m_flRadiusScale"))
                {
                    kv3Text = System.Text.RegularExpressions.Regex.Replace(
                        kv3Text,
                        @"(_class\s*=\s*""C_OP_Render(?:Sprites|Trails)"")",
                        $"$1\n\t\t\tm_flRadiusScale = {scaleStr}");
                }
            }

            return kv3Text;
        }

        private static int RunCompilerProcess(
            string compilerExe,
            string workingDir,
            string arguments,
            IProgress<ExportProgressReport> progress,
            CancellationToken ct,
            float activeProgress = 0.65f)
        {
            var psi = new ProcessStartInfo
            {
                FileName = compilerExe,
                Arguments = arguments,
                WorkingDirectory = workingDir,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            int exitCode = 0;
            using (var process = new Process { StartInfo = psi })
            {
                process.OutputDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                    {
                        progress?.Report(new ExportProgressReport(activeProgress, $"[COMPILER] {e.Data.Trim()}"));
                    }
                };
                process.ErrorDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                    {
                        progress?.Report(new ExportProgressReport(activeProgress, $"[COMPILER ERR] {e.Data.Trim()}", ExportLogLevel.Warning));
                    }
                };

                try
                {
                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    using (ct.Register(() =>
                    {
                        try { if (!process.HasExited) process.Kill(true); } catch { }
                    }))
                    {
                        process.WaitForExit();
                    }

                    exitCode = process.ExitCode;
                }
                catch (OperationCanceledException)
                {
                    progress?.Report(new ExportProgressReport(activeProgress, "[WARNING] Compilation canceled by user.", ExportLogLevel.Warning));
                    throw;
                }
            }

            return exitCode;
        }

        private static string LocateEmittedFile(string primaryDir, string fallbackDir, string pattern, string rootGameDir = null, string rootContentDir = null)
        {
            if (Directory.Exists(primaryDir))
            {
                var files = Directory.GetFiles(primaryDir, pattern, SearchOption.TopDirectoryOnly);
                if (files.Length > 0) return files[0];
                var subFiles = Directory.GetFiles(primaryDir, pattern, SearchOption.AllDirectories);
                if (subFiles.Length > 0) return subFiles[0];
            }
            if (Directory.Exists(fallbackDir))
            {
                var files = Directory.GetFiles(fallbackDir, pattern, SearchOption.TopDirectoryOnly);
                if (files.Length > 0) return files[0];
                var subFiles = Directory.GetFiles(fallbackDir, pattern, SearchOption.AllDirectories);
                if (subFiles.Length > 0) return subFiles[0];
            }
            if (!string.IsNullOrEmpty(rootGameDir) && Directory.Exists(rootGameDir))
            {
                var files = Directory.GetFiles(rootGameDir, pattern, SearchOption.AllDirectories);
                if (files.Length > 0) return files[0];
            }
            if (!string.IsNullOrEmpty(rootContentDir) && Directory.Exists(rootContentDir))
            {
                var files = Directory.GetFiles(rootContentDir, pattern, SearchOption.AllDirectories);
                if (files.Length > 0) return files[0];
            }
            return null;
        }

        public SkinExportResult ExportFlattenedPng(string filePath)
        {
            var result = new SkinExportResult();
            if (_layerManager == null)
            {
                result.Success = false;
                result.ErrorMessage = "SkinLayerManager reference is null.";
                return result;
            }

            var image = _layerManager.BakeCompositeImage();
            if (image == null)
            {
                result.Success = false;
                result.ErrorMessage = "Failed to capture composite image from viewport.";
                return result;
            }

            try
            {
                string dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                Error err = image.SavePng(filePath);
                if (err != Error.Ok)
                {
                    result.Success = false;
                    result.ErrorMessage = $"Godot SavePng failed with error code: {err}";
                    return result;
                }

                result.Success = true;
                result.PngPath = filePath;
                return result;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
                return result;
            }
        }

        public SkinExportResult ExportVmat(string vmatPath, string relativeTexturePath)
        {
            var result = new SkinExportResult();
            try
            {
                string dir = Path.GetDirectoryName(vmatPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                string vmatContent = GenerateSource2Kv3Material(relativeTexturePath, Path.GetFileNameWithoutExtension(vmatPath));
                File.WriteAllText(vmatPath, vmatContent);

                result.Success = true;
                result.VmatPath = vmatPath;
                return result;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
                return result;
            }
        }

        public SkinExportResult InstallModToCitadel(string deadlockGamePath, string heroCodename, string submeshName)
        {
            var config = new ModExportConfig
            {
                DeadlockGamePath = deadlockGamePath,
                HeroCodename = heroCodename,
                PreBakedAtlas = _layerManager?.BakeCompositeImage(),
                InstallDirectlyToGame = true
            };
            return ExportModAsync(config, null, CancellationToken.None).GetAwaiter().GetResult();
        }
    }
}