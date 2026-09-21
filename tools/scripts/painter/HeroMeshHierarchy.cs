using Godot;
using System;
using System.IO;
using System.Collections.Generic;
using DeadlockPlayground.Materials;

namespace DeadlockPlayground.Painter
{
    public class SubmeshNodeInfo
    {
        public MeshInstance3D Mesh { get; set; }
        public string RawName { get; set; }
        public string DisplayName { get; set; }
        public int SurfaceIndex { get; set; } = 0;
        public bool IsAccessory { get; set; }
        public bool IsSoloed { get; set; }
        public MeshRaycaster Raycaster { get; set; }
        public string OriginalVmatPath { get; set; }
        public string MaterialName { get; set; }

        // Dirty tracking & export selection
        public bool IsDirty { get; set; } = false;
        public bool IsSelected { get; set; } = false;

        // Exact compiled color texture path for direct in-place VPK replacement
        public string OriginalColorVtexCPath { get; set; }

        // Original Source 2 material texture parameters
        public string OriginalNormalVtex { get; set; }
        public string OriginalAoVtex { get; set; }
        public string OriginalNprOutlineVtex { get; set; }
        public string OriginalMaskVtex { get; set; }
        public string OriginalShader { get; set; }
        public Dictionary<string, string> OriginalTextureParams { get; set; } = new();

        // Shading materials
        public Material OriginalMaterial { get; set; }
        public ShaderMaterial ToonMaterial { get; set; }
        public ShaderMaterial OutlineMaterial { get; set; }
        public bool PreserveOriginal { get; set; } = false;
    }

    public partial class HeroMeshHierarchy : Node
    {
        [Signal] public delegate void HierarchyChangedEventHandler();
        [Signal] public delegate void TargetMeshChangedEventHandler(MeshInstance3D mesh, int surfaceIndex);

        private readonly List<SubmeshNodeInfo> _submeshes = new();
        public IReadOnlyList<SubmeshNodeInfo> Submeshes => _submeshes;

        private SubmeshNodeInfo _activeTarget;
        public SubmeshNodeInfo ActiveTarget => _activeTarget;

        public bool IsPaintingModeActive { get; set; } = false;

        private bool _isAnySoloed = false;
        private readonly Dictionary<MeshInstance3D, bool> _preSoloVisibility = new();

        // Track dynamically created submesh nodes and hidden multi-surface nodes for clean teardown
        private readonly List<MeshInstance3D> _generatedSubmeshNodes = new();
        private readonly List<MeshInstance3D> _hiddenOriginalNodes = new();
        private static string _currentHeroName = string.Empty;

        public void ScanHero(Node3D heroNode)
        {
            CleanupGeneratedSubmeshes();

            _currentHeroName = heroNode?.Name.ToString().ToLowerInvariant() ?? string.Empty;
            _submeshes.Clear();
            _preSoloVisibility.Clear();
            _isAnySoloed = false;
            _activeTarget = null;

            if (heroNode == null)
            {
                NotifyHierarchyChanged();
                return;
            }

            // 1. Gather all candidate MeshInstance3D nodes first so modifying the tree doesn't affect recursion
            var candidateMeshes = new List<MeshInstance3D>();
            CollectCandidateMeshesRecursive(heroNode, candidateMeshes);

            // 2. Process each mesh: register candidate meshes directly preserving original skinning and materials
            foreach (var mi in candidateMeshes)
            {
                string rawName = mi.Name.ToString();
                int surfaceCount = mi.Mesh?.GetSurfaceCount() ?? 0;
                if (surfaceCount == 0) continue;

                bool isAcc = IsAccessoryKeyword(rawName);

                if (surfaceCount == 1)
                {
                    string matPath = ResolveSurfaceMaterialName(mi, 0);
                    string cleanMat = !string.IsNullOrEmpty(matPath) ? Path.GetFileNameWithoutExtension(matPath) : "";
                    mi.SetMeta("OriginalVmatPath", matPath ?? "");

                    Material origMat = GetAuthenticMaterial(mi, 0);
                    if (origMat != null)
                    {
                        mi.SetMeta("OriginalMaterial_0", origMat);
                    }
                    var (toonMat, outlineMat, preserve) = CreateToonMaterials(origMat, rawName, matPath);

                    string singleDisplay = CleanName(rawName);
                    if (!string.IsNullOrEmpty(matPath))
                    {
                        if (matPath.Contains("vindicta_headv2", StringComparison.OrdinalIgnoreCase))
                            singleDisplay = "Head (vindicta_headv2)";
                        else if (matPath.Contains("vindicta_head", StringComparison.OrdinalIgnoreCase))
                            singleDisplay = "Limbs / Hands & Legs (vindicta_head)";
                    }

                    var info = new SubmeshNodeInfo
                    {
                        Mesh = mi,
                        RawName = rawName,
                        DisplayName = singleDisplay,
                        SurfaceIndex = 0,
                        IsAccessory = isAcc,
                        IsSoloed = false,
                        OriginalVmatPath = matPath,
                        MaterialName = cleanMat,
                        IsDirty = false,
                        IsSelected = false,
                        OriginalMaterial = origMat,
                        ToonMaterial = toonMat,
                        OutlineMaterial = outlineMat,
                        PreserveOriginal = preserve
                    };
                    ExtractMaterialMetadata(info, mi, 0);
                    _submeshes.Add(info);
                    _preSoloVisibility[mi] = mi.Visible;
                }
                else
                {
                    // Hide original composite multi-surface mesh while painter operates on separated surface meshes
                    bool isCompositeVisible = mi.Visible;
                    if (mi.HasMeta("UserVisibility"))
                    {
                        isCompositeVisible = mi.GetMeta("UserVisibility").AsBool();
                    }

                    mi.Visible = false;
                    mi.Layers &= ~(uint)(1 << 20);
                    mi.SetMeta("IsHiddenComposite", true);
                    _hiddenOriginalNodes.Add(mi);

                    var genArray = new Godot.Collections.Array<MeshInstance3D>();
                    mi.SetMeta("GeneratedSubmeshes", genArray);

                    for (int s = 0; s < surfaceCount; s++)
                    {
                        string surfaceRawName = (s == 0) ? rawName : $"{rawName}[{s}]";
                        string clean = CleanName(rawName);

                        string matName = ResolveSurfaceMaterialName(mi, s);
                        string cleanMat = !string.IsNullOrEmpty(matName) ? Path.GetFileNameWithoutExtension(matName) : "";
                        string display;
                        if (!string.IsNullOrEmpty(matName))
                        {
                            string formattedMat = CleanName(matName);
                            if (matName.Contains("vindicta_headv2", StringComparison.OrdinalIgnoreCase))
                            {
                                display = "Head (vindicta_headv2)";
                            }
                            else if (matName.Contains("vindicta_head", StringComparison.OrdinalIgnoreCase))
                            {
                                display = "Limbs / Hands & Legs (vindicta_head)";
                            }
                            else if (string.Equals(clean, formattedMat, StringComparison.OrdinalIgnoreCase))
                            {
                                display = (s == 0) ? clean : $"{clean} [{s}]";
                            }
                            else
                            {
                                display = $"{clean} ({formattedMat})";
                            }
                        }
                        else
                        {
                            display = (s == 0) ? clean : $"{clean} [{s}]";
                        }

                        // Extract surface geometry into an isolated MeshInstance3D sharing skin & skeleton
                        var origMat = GetAuthenticMaterial(mi, s);
                        var newMesh = new ArrayMesh();
                        var arrays = mi.Mesh.SurfaceGetArrays(s);
                        newMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
                        if (origMat != null)
                        {
                            newMesh.SurfaceSetMaterial(0, origMat);
                        }
                        newMesh.LightmapSizeHint = mi.Mesh.LightmapSizeHint != Vector2I.Zero ? mi.Mesh.LightmapSizeHint : new Vector2I(256, 256);

                        var newMi = new MeshInstance3D
                        {
                            Name = $"{mi.Name}_surf_{s}_{display.Replace(" ", "_").Replace("(", "").Replace(")", "")}",
                            Mesh = newMesh,
                            Skin = mi.Skin,
                            Skeleton = mi.Skeleton,
                            Transform = mi.Transform,
                            Layers = mi.Layers,
                            Visible = isCompositeVisible
                        };
                        newMi.SetMeta("OriginalVmatPath", matName ?? "");
                        newMi.SetMeta("ParentCompositeMesh", mi);
                        if (origMat != null)
                        {
                            newMi.SetMeta("OriginalMaterial_0", origMat);
                        }

                        mi.GetParent()?.AddChild(newMi);
                        _generatedSubmeshNodes.Add(newMi);
                        genArray.Add(newMi);

                        var (toonMat, outlineMat, preserve) = CreateToonMaterials(origMat, surfaceRawName, matName);

                        var info = new SubmeshNodeInfo
                        {
                            Mesh = newMi,
                            RawName = surfaceRawName,
                            DisplayName = display,
                            SurfaceIndex = 0,
                            IsAccessory = isAcc,
                            IsSoloed = false,
                            OriginalVmatPath = matName,
                            MaterialName = cleanMat,
                            IsDirty = false,
                            IsSelected = false,
                            OriginalMaterial = origMat,
                            ToonMaterial = toonMat,
                            OutlineMaterial = outlineMat,
                            PreserveOriginal = preserve
                        };
                        ExtractMaterialMetadata(info, mi, s);
                        _submeshes.Add(info);
                        _preSoloVisibility[newMi] = isCompositeVisible;
                    }
                }
            }

            // 3. Preprocess meshes: Ensure non-zero lightmap hints for GPU Texture Painter atlas packing
            EnsureLightmapUv2Coordinates();

            // 4. Select body or first submesh as default target
            if (_submeshes.Count > 0)
            {
                var bodySubmesh = _submeshes.Find(s => s.RawName.ToLowerInvariant().Contains("body")) ?? _submeshes[0];
                SelectTarget(bodySubmesh, bodySubmesh.SurfaceIndex);
            }

            // 5. Automatically inherit global UserSettings.ToonEnabled (unless painting mode is active)
            if (UserSettings.ToonEnabled && !IsPaintingModeActive)
            {
                ApplyToonShading(true);
            }
            else
            {
                ApplyToonShading(false);
            }

            NotifyHierarchyChanged();
        }

        public static bool IsToonMaterial(Material mat)
        {
            if (mat is ShaderMaterial sm)
            {
                string path = sm.Shader?.ResourcePath ?? "";
                return path.Contains("toon_shader") || path.Contains("toon_outline");
            }
            return false;
        }

        public static Material GetAuthenticMaterial(MeshInstance3D mi, int surfaceIndex)
        {
            if (mi == null) return null;

            if (mi.HasMeta($"OriginalMaterial_{surfaceIndex}"))
            {
                var metaMat = mi.GetMeta($"OriginalMaterial_{surfaceIndex}").As<Material>();
                if (metaMat != null && !IsToonMaterial(metaMat))
                {
                    return metaMat;
                }
            }

            var baseMat = mi.Mesh?.SurfaceGetMaterial(surfaceIndex);
            if (baseMat != null && !IsToonMaterial(baseMat))
            {
                return baseMat;
            }

            var overrideMat = mi.GetSurfaceOverrideMaterial(surfaceIndex);
            if (overrideMat != null && !IsToonMaterial(overrideMat))
            {
                return overrideMat;
            }

            return baseMat ?? overrideMat;
        }

        private static Shader _toonShader;
        private static Shader _toonOutlineShader;

        private static Shader GetToonShader()
        {
            _toonShader ??= GD.Load<Shader>("res://assets/shaders/toon_shader.gdshader");
            return _toonShader;
        }

        private static Shader GetToonOutlineShader()
        {
            _toonOutlineShader ??= GD.Load<Shader>("res://assets/shaders/toon_outline.gdshader");
            return _toonOutlineShader;
        }

        private static (ShaderMaterial ToonMat, ShaderMaterial OutlineMat, bool Preserve) CreateToonMaterials(Material origMat, string meshName, string vmatPath)
        {
            var toonShader = GetToonShader();
            var outlineShader = GetToonOutlineShader();

            bool isWeapon = meshName.Contains("weapon", StringComparison.OrdinalIgnoreCase) ||
                            meshName.Contains("gun", StringComparison.OrdinalIgnoreCase) ||
                            meshName.Contains("blaster", StringComparison.OrdinalIgnoreCase) ||
                            (!string.IsNullOrEmpty(vmatPath) && (vmatPath.Contains("weapon", StringComparison.OrdinalIgnoreCase) ||
                                                                  vmatPath.Contains("gun", StringComparison.OrdinalIgnoreCase) ||
                                                                  vmatPath.Contains("blaster", StringComparison.OrdinalIgnoreCase)));

            bool isGlass = meshName.Contains("glass", StringComparison.OrdinalIgnoreCase) ||
                           meshName.Contains("lens", StringComparison.OrdinalIgnoreCase) ||
                           meshName.Contains("specs", StringComparison.OrdinalIgnoreCase) ||
                           meshName.Contains("spectacle", StringComparison.OrdinalIgnoreCase) ||
                           (!string.IsNullOrEmpty(vmatPath) && (vmatPath.Contains("glass", StringComparison.OrdinalIgnoreCase) ||
                                                                vmatPath.Contains("lens", StringComparison.OrdinalIgnoreCase) ||
                                                                vmatPath.Contains("specs", StringComparison.OrdinalIgnoreCase) ||
                                                                vmatPath.Contains("spectacle", StringComparison.OrdinalIgnoreCase))) ||
                           (origMat is ShaderMaterial smGl && smGl.Shader?.ResourcePath?.Contains("source2_glass") == true);

            bool shouldPreserve = isGlass || DeadlockMaterialResolver.ShouldPreserveOriginalMaterial(meshName, vmatPath, origMat);
            if (shouldPreserve)
            {
                if (!isGlass && isWeapon && outlineShader != null)
                {
                    Color sigColor = HeroMaterialManager.GetSignatureOutlineColor(_currentHeroName, vmatPath) ?? new Color(0.08f, 0.08f, 0.08f, 1.0f);
                    var weaponOutline = new ShaderMaterial { Shader = outlineShader };
                    weaponOutline.SetShaderParameter("outline_width", 1.0f);
                    weaponOutline.SetShaderParameter("outline_color", sigColor);
                    return (null, weaponOutline, true);
                }
                return (null, null, true);
            }

            bool isAdditive = false;
            bool isTranslucent = isGlass;
            if (origMat is StandardMaterial3D sm)
            {
                isAdditive = sm.BlendMode == BaseMaterial3D.BlendModeEnum.Add;
                isTranslucent = isTranslucent || sm.Transparency == BaseMaterial3D.TransparencyEnum.Alpha;
            }
            else if (origMat is ShaderMaterial smPbr &&
                     (smPbr.Shader?.ResourcePath?.Contains("source2_vertcolor_pbr") == true ||
                      smPbr.Shader?.ResourcePath?.Contains("source2_pbr") == true ||
                      smPbr.Shader?.ResourcePath?.Contains("unicorn_hair") == true))
            {
                if (outlineShader != null)
                {
                    Color sigColor = HeroMaterialManager.GetSignatureOutlineColor(_currentHeroName, vmatPath) ?? new Color(0.08f, 0.08f, 0.08f, 1.0f);
                    var outline = new ShaderMaterial { Shader = outlineShader };
                    outline.SetShaderParameter("outline_width", 1.0f);
                    outline.SetShaderParameter("outline_color", sigColor);
                    return (null, outline, true);
                }
                return (null, null, true);
            }
            else if (origMat is ShaderMaterial)
            {
                isAdditive = true;
            }

            if (isGlass || isAdditive || isTranslucent)
            {
                // NEVER create outline for glass or translucent surfaces (even on weapons)
                if (!isGlass && !isTranslucent && isWeapon && outlineShader != null)
                {
                    Color sigColor = HeroMaterialManager.GetSignatureOutlineColor(_currentHeroName, vmatPath) ?? new Color(0.08f, 0.08f, 0.08f, 1.0f);
                    var weaponOutline = new ShaderMaterial { Shader = outlineShader };
                    weaponOutline.SetShaderParameter("outline_width", 1.0f);
                    weaponOutline.SetShaderParameter("outline_color", sigColor);
                    return (null, weaponOutline, true);
                }
                return (null, null, true);
            }

            if (toonShader == null || outlineShader == null)
            {
                return (null, null, true);
            }

            Color outlineCol = HeroMaterialManager.GetSignatureOutlineColor(_currentHeroName, vmatPath) ?? new Color(0.08f, 0.08f, 0.08f, 1.0f);
            var toonMat = new ShaderMaterial { Shader = toonShader };
            var outlineMat = new ShaderMaterial { Shader = outlineShader };

            outlineMat.SetShaderParameter("outline_width", 1.0f);
            outlineMat.SetShaderParameter("outline_color", outlineCol);

            if (origMat is StandardMaterial3D stdMat)
            {
                toonMat.SetShaderParameter("albedo_color", stdMat.AlbedoColor);
                if (stdMat.AlbedoTexture != null)
                {
                    toonMat.SetShaderParameter("albedo_texture", stdMat.AlbedoTexture);
                }
                if (stdMat.NormalTexture != null)
                {
                    toonMat.SetShaderParameter("normal_texture", stdMat.NormalTexture);
                    toonMat.SetShaderParameter("normal_strength", stdMat.NormalEnabled ? 1.0f : 0.0f);
                }
                toonMat.SetShaderParameter("metallic", stdMat.Metallic);
                toonMat.SetShaderParameter("roughness", stdMat.Roughness > 0.01f ? stdMat.Roughness : 0.65f);
                toonMat.SetShaderParameter("specular", stdMat.Metallic > 0.5f ? 0.45f : 0.30f);
                toonMat.SetShaderParameter("uv1_scale", stdMat.Uv1Scale);
                toonMat.SetShaderParameter("uv1_offset", stdMat.Uv1Offset);

                if (stdMat.Transparency == BaseMaterial3D.TransparencyEnum.AlphaScissor)
                {
                    toonMat.SetShaderParameter("alpha_scissor_threshold", stdMat.AlphaScissorThreshold > 0.01f ? stdMat.AlphaScissorThreshold : 0.5f);
                }

                if (stdMat.EmissionEnabled)
                {
                    toonMat.SetShaderParameter("emission_color", stdMat.Emission);
                    toonMat.SetShaderParameter("emission_energy", stdMat.EmissionEnergyMultiplier);
                    if (stdMat.EmissionTexture != null)
                    {
                        toonMat.SetShaderParameter("emission_texture", stdMat.EmissionTexture);
                    }
                }
            }

            toonMat.SetShaderParameter("toon_intensity", 1.0f);
            toonMat.SetShaderParameter("use_stepped", true);
            toonMat.SetShaderParameter("steps", 3.0f);
            toonMat.SetShaderParameter("step_smoothness", 0.30f);
            toonMat.SetShaderParameter("shadow_tint", new Color(0.18f, 0.16f, 0.26f, 1.0f));
            toonMat.SetShaderParameter("shadow_tint_amount", 0.40f);
            toonMat.SetShaderParameter("use_rim", true);
            toonMat.SetShaderParameter("rim_color", new Color(1.0f, 1.0f, 1.0f, 1.0f));
            toonMat.SetShaderParameter("rim_amount", 2.0f);
            toonMat.SetShaderParameter("rim_smoothness", 0.2f);
            toonMat.SetShaderParameter("rim_blend", 1.0f);
            toonMat.SetShaderParameter("rim_mask_shadow", 1.0f);

            return (toonMat, outlineMat, false);
        }

        public void ApplyToonShading(bool enabled, bool enableOutline = true)
        {
            if (enabled && IsPaintingModeActive)
            {
                enabled = false;
            }

            foreach (var sm in _submeshes)
            {
                if (sm.Mesh == null || !GodotObject.IsInstanceValid(sm.Mesh)) continue;

                bool isWeapon = sm.RawName.Contains("weapon", StringComparison.OrdinalIgnoreCase) ||
                                sm.RawName.Contains("gun", StringComparison.OrdinalIgnoreCase) ||
                                sm.RawName.Contains("blaster", StringComparison.OrdinalIgnoreCase) ||
                                (!string.IsNullOrEmpty(sm.OriginalVmatPath) && (sm.OriginalVmatPath.Contains("weapon", StringComparison.OrdinalIgnoreCase) ||
                                                                               sm.OriginalVmatPath.Contains("gun", StringComparison.OrdinalIgnoreCase) ||
                                                                               sm.OriginalVmatPath.Contains("blaster", StringComparison.OrdinalIgnoreCase)));

                bool isGlass = sm.RawName.Contains("glass", StringComparison.OrdinalIgnoreCase) ||
                               sm.RawName.Contains("lens", StringComparison.OrdinalIgnoreCase) ||
                               sm.RawName.Contains("specs", StringComparison.OrdinalIgnoreCase) ||
                               sm.RawName.Contains("spectacle", StringComparison.OrdinalIgnoreCase) ||
                               (!string.IsNullOrEmpty(sm.OriginalVmatPath) && (sm.OriginalVmatPath.Contains("glass", StringComparison.OrdinalIgnoreCase) ||
                                                                               sm.OriginalVmatPath.Contains("lens", StringComparison.OrdinalIgnoreCase) ||
                                                                               sm.OriginalVmatPath.Contains("specs", StringComparison.OrdinalIgnoreCase) ||
                                                                               sm.OriginalVmatPath.Contains("spectacle", StringComparison.OrdinalIgnoreCase))) ||
                               (sm.OriginalMaterial is ShaderMaterial smGl && smGl.Shader?.ResourcePath?.Contains("source2_glass") == true);

                if (isGlass)
                {
                    if (sm.OriginalMaterial != null) sm.OriginalMaterial.NextPass = null;
                    sm.Mesh.SetSurfaceOverrideMaterial(sm.SurfaceIndex, sm.OriginalMaterial);
                    continue;
                }

                if (enabled && !sm.PreserveOriginal && sm.ToonMaterial != null)
                {
                    if (enableOutline && sm.OutlineMaterial != null)
                    {
                        sm.ToonMaterial.NextPass = sm.OutlineMaterial;
                    }
                    else
                    {
                        sm.ToonMaterial.NextPass = null;
                    }
                    sm.Mesh.SetSurfaceOverrideMaterial(sm.SurfaceIndex, sm.ToonMaterial);
                }
                else
                {
                    var activeMat = sm.OriginalMaterial;
                    if (enabled && enableOutline && (isWeapon || (sm.PreserveOriginal && sm.OutlineMaterial != null)) && activeMat != null)
                    {
                        activeMat.NextPass = sm.OutlineMaterial;
                    }
                    else if (activeMat != null)
                    {
                        activeMat.NextPass = null;
                    }
                    sm.Mesh.SetSurfaceOverrideMaterial(sm.SurfaceIndex, activeMat);
                }
            }
        }

        public void UpdateToonUniforms(float intensity, float steps, float smoothness, float shadowAmt, Color shadowColor, float outlineWidth, Color outlineColor)
        {
            foreach (var sm in _submeshes)
            {
                if (sm.ToonMaterial != null)
                {
                    sm.ToonMaterial.SetShaderParameter("toon_intensity", intensity);
                    sm.ToonMaterial.SetShaderParameter("steps", steps);
                    sm.ToonMaterial.SetShaderParameter("step_smoothness", smoothness);
                    sm.ToonMaterial.SetShaderParameter("shadow_tint", shadowColor);
                    sm.ToonMaterial.SetShaderParameter("shadow_tint_amount", shadowAmt);
                }
                if (sm.OutlineMaterial != null)
                {
                    sm.OutlineMaterial.SetShaderParameter("outline_width", outlineWidth);
                    sm.OutlineMaterial.SetShaderParameter("outline_color", outlineColor);
                }
            }
        }

        public void SetOutlineParam(string paramName, Variant value)
        {
            foreach (var sm in _submeshes)
            {
                sm.OutlineMaterial?.SetShaderParameter(paramName, value);
            }
        }

        private void EnsureLightmapUv2Coordinates()
        {
            foreach (var submesh in _submeshes)
            {
                var mi = submesh.Mesh;
                if (mi == null || mi.Mesh == null) continue;

                string name = (submesh.DisplayName ?? submesh.RawName ?? "").ToLowerInvariant();
                bool isMinor = name.Contains("teeth") || name.Contains("tooth") || name.Contains("tongue") ||
                               name.Contains("eye") || name.Contains("pupil") || name.Contains("cornea") || name.Contains("eyelash");
                bool isMajor = !isMinor && (name.Contains("head") || name.Contains("face") || name.Contains("body") ||
                                           name.Contains("torso") || name.Contains("skin") || name.Contains("lower") ||
                                           name.Contains("upper") || name.Contains("cloth") || name.Contains("coat") ||
                                           name.Contains("jacket") || name.Contains("pants") || name.Contains("dress"));

                int dim = isMajor ? 512 : (isMinor ? 128 : 256);
                mi.Mesh.LightmapSizeHint = new Vector2I(dim, dim);
            }
        }

        private static void CollectCandidateMeshesRecursive(Node node, List<MeshInstance3D> results)
        {
            if (node is MeshInstance3D mi && mi.Mesh != null)
            {
                string lowerName = mi.Name.ToString().ToLowerInvariant();
                if (!lowerName.Contains("marker") && !lowerName.Contains("gizmo") && !lowerName.Contains("preview"))
                {
                    results.Add(mi);
                }
            }

            foreach (Node child in node.GetChildren())
            {
                CollectCandidateMeshesRecursive(child, results);
            }
        }

        private static bool IsAccessoryKeyword(string name)
        {
            string lower = name.ToLowerInvariant();
            return lower.Contains("acc") ||
                   lower.Contains("prop") ||
                   lower.Contains("weapon") ||
                   lower.Contains("gun") ||
                   lower.Contains("blaster") ||
                   lower.Contains("hat") ||
                   lower.Contains("gear") ||
                   lower.Contains("glass") ||
                   lower.Contains("mask") ||
                   lower.Contains("cape") ||
                   lower.Contains("coat") ||
                   lower.Contains("card") ||
                   lower.Contains("bag") ||
                   lower.Contains("strap") ||
                   lower.Contains("holster") ||
                   lower.Contains("boot");
        }

        private static string CleanName(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "Mesh Part";

            string leaf = raw.Trim();
            int lastSlash = Math.Max(leaf.LastIndexOf('/'), leaf.LastIndexOf('\\'));
            if (lastSlash >= 0 && lastSlash < leaf.Length - 1)
            {
                leaf = leaf.Substring(lastSlash + 1);
            }

            int dotIndex = leaf.LastIndexOf('.');
            if (dotIndex > 0)
            {
                leaf = leaf.Substring(0, dotIndex);
            }

            string[] prefixes = new[] { "model_", "mesh_", "hero_", "mat_" };
            bool stripped;
            do
            {
                stripped = false;
                foreach (var p in prefixes)
                {
                    if (leaf.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                    {
                        leaf = leaf.Substring(p.Length);
                        stripped = true;
                    }
                }
            } while (stripped && leaf.Length > 0);

            string clean = leaf.TrimStart('.', '_', '-').Trim();
            if (string.IsNullOrEmpty(clean)) return "Mesh Part";

            string formatted = clean.Replace('_', ' ').Replace('-', ' ').Trim();
            if (string.IsNullOrEmpty(formatted)) return "Mesh Part";

            return char.ToUpperInvariant(formatted[0]) + formatted.Substring(1);
        }

        private static string ResolveSurfaceMaterialName(MeshInstance3D mi, int s)
        {
            if (mi == null) return null;
            var mat = GetAuthenticMaterial(mi, s);
            if (mat == null) return null;

            string name = mat.ResourceName;
            if (string.IsNullOrEmpty(name))
            {
                name = mat.ResourcePath;
            }

            if (!string.IsNullOrEmpty(name))
            {
                int lastSlash = Math.Max(name.LastIndexOf('/'), name.LastIndexOf('\\'));
                if (lastSlash >= 0 && lastSlash < name.Length - 1)
                {
                    name = name.Substring(lastSlash + 1);
                }
                int dot = name.IndexOf('.');
                if (dot > 0)
                {
                    name = name.Substring(0, dot);
                }
                return name;
            }
            return null;
        }

        private static void ExtractMaterialMetadata(SubmeshNodeInfo info, MeshInstance3D mi, int surfaceIndex)
        {
            if (info == null || mi == null) return;
            var mat = mi.GetSurfaceOverrideMaterial(surfaceIndex) ?? mi.Mesh?.SurfaceGetMaterial(surfaceIndex);
            if (mat == null) return;

            if (mat.HasMeta("OriginalShader")) info.OriginalShader = mat.GetMeta("OriginalShader").AsString();
            if (mat.HasMeta("OriginalColorVtexCPath"))
            {
                info.OriginalColorVtexCPath = mat.GetMeta("OriginalColorVtexCPath").AsString();
            }
            else if (mat.HasMeta("TextureParam_g_tColor"))
            {
                string raw = mat.GetMeta("TextureParam_g_tColor").AsString().Replace('\\', '/').Trim().TrimStart('/');
                if (raw.EndsWith(".vtex", StringComparison.OrdinalIgnoreCase)) raw += "_c";
                else if (!raw.EndsWith(".vtex_c", StringComparison.OrdinalIgnoreCase)) raw += ".vtex_c";
                info.OriginalColorVtexCPath = raw;
            }
            else if (mat.HasMeta("TextureParam_TextureColor"))
            {
                string raw = mat.GetMeta("TextureParam_TextureColor").AsString().Replace('\\', '/').Trim().TrimStart('/');
                if (raw.EndsWith(".vtex", StringComparison.OrdinalIgnoreCase)) raw += "_c";
                else if (!raw.EndsWith(".vtex_c", StringComparison.OrdinalIgnoreCase)) raw += ".vtex_c";
                info.OriginalColorVtexCPath = raw;
            }

            if (mat.HasMeta("TextureParam_g_tNormalRoughness")) info.OriginalNormalVtex = mat.GetMeta("TextureParam_g_tNormalRoughness").AsString();
            else if (mat.HasMeta("TextureParam_g_tNormal")) info.OriginalNormalVtex = mat.GetMeta("TextureParam_g_tNormal").AsString();

            if (mat.HasMeta("TextureParam_g_tAmbientOcclusion")) info.OriginalAoVtex = mat.GetMeta("TextureParam_g_tAmbientOcclusion").AsString();
            else if (mat.HasMeta("TextureParam_g_tAO")) info.OriginalAoVtex = mat.GetMeta("TextureParam_g_tAO").AsString();

            if (mat.HasMeta("TextureParam_g_tNPROutlineMask")) info.OriginalNprOutlineVtex = mat.GetMeta("TextureParam_g_tNPROutlineMask").AsString();
            if (mat.HasMeta("TextureParam_g_tSelfIllumMask")) info.OriginalMaskVtex = mat.GetMeta("TextureParam_g_tSelfIllumMask").AsString();
        }

        public void MarkSubmeshDirty(MeshInstance3D mesh)
        {
            if (mesh == null) return;
            foreach (var sm in _submeshes)
            {
                if (sm.Mesh == mesh)
                {
                    sm.IsDirty = true;
                    sm.IsSelected = true;
                }
            }
        }

        public void MarkSubmeshDirty(string rawOrMaterialName)
        {
            if (string.IsNullOrEmpty(rawOrMaterialName)) return;
            foreach (var sm in _submeshes)
            {
                if (string.Equals(sm.RawName, rawOrMaterialName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(sm.MaterialName, rawOrMaterialName, StringComparison.OrdinalIgnoreCase))
                {
                    sm.IsDirty = true;
                    sm.IsSelected = true;
                }
            }
        }

        public List<SubmeshNodeInfo> GetDirtySubmeshes()
        {
            return _submeshes.FindAll(s => s.IsDirty);
        }

        public void SelectTarget(SubmeshNodeInfo info, int surfaceIndex = -1)
        {
            if (info == null || info.Mesh == null) return;
            _activeTarget = info;
            int targetSurface = (surfaceIndex >= 0) ? surfaceIndex : info.SurfaceIndex;
            NotifyTargetMeshChanged(info.Mesh, targetSurface);
        }

        public void SetMeshVisibility(SubmeshNodeInfo info, bool visible)
        {
            if (info == null || info.Mesh == null || !GodotObject.IsInstanceValid(info.Mesh)) return;
            info.Mesh.Visible = visible;
            _preSoloVisibility[info.Mesh] = visible;
            NotifyHierarchyChanged();
        }

        public void ToggleSolo(SubmeshNodeInfo info)
        {
            if (info == null || info.Mesh == null || !GodotObject.IsInstanceValid(info.Mesh)) return;

            if (info.IsSoloed)
            {
                // Unsolo: Restore previous visibility states
                foreach (var s in _submeshes)
                {
                    s.IsSoloed = false;
                    if (s.Mesh != null && GodotObject.IsInstanceValid(s.Mesh))
                    {
                        if (_preSoloVisibility.TryGetValue(s.Mesh, out bool prevVis))
                        {
                            s.Mesh.Visible = prevVis;
                        }
                        else
                        {
                            s.Mesh.Visible = true;
                        }
                    }
                }
                _isAnySoloed = false;
                _preSoloVisibility.Clear();
            }
            else
            {
                // Solo: Save current states if not already soloing
                if (!_isAnySoloed)
                {
                    _preSoloVisibility.Clear();
                    foreach (var s in _submeshes)
                    {
                        if (s.Mesh != null && GodotObject.IsInstanceValid(s.Mesh))
                        {
                            _preSoloVisibility[s.Mesh] = s.Mesh.Visible;
                        }
                    }
                }

                foreach (var s in _submeshes)
                {
                    s.IsSoloed = (s == info);
                    if (s.Mesh != null && GodotObject.IsInstanceValid(s.Mesh))
                    {
                        s.Mesh.Visible = (s == info);
                    }
                }
                _isAnySoloed = true;
                SelectTarget(info, info.SurfaceIndex);
            }

            NotifyHierarchyChanged();
        }

        public void ShowAll()
        {
            _isAnySoloed = false;
            _preSoloVisibility.Clear();

            foreach (var s in _submeshes)
            {
                s.IsSoloed = false;
                if (s.Mesh != null && GodotObject.IsInstanceValid(s.Mesh))
                {
                    s.Mesh.Visible = true;
                    _preSoloVisibility[s.Mesh] = true;
                }
            }
            NotifyHierarchyChanged();
        }

        public void HideAccessories()
        {
            _isAnySoloed = false;
            _preSoloVisibility.Clear();

            foreach (var s in _submeshes)
            {
                s.IsSoloed = false;
                if (s.Mesh != null && GodotObject.IsInstanceValid(s.Mesh))
                {
                    s.Mesh.Visible = !s.IsAccessory;
                    _preSoloVisibility[s.Mesh] = !s.IsAccessory;
                }
            }
            NotifyHierarchyChanged();
        }

        public void CacheVisibilityState(out Dictionary<string, bool> visMap, out Dictionary<string, bool> preSoloMap, out string soloedRawName)
        {
            visMap = new Dictionary<string, bool>();
            preSoloMap = new Dictionary<string, bool>();
            soloedRawName = null;

            foreach (var s in _submeshes)
            {
                if (s.Mesh != null && GodotObject.IsInstanceValid(s.Mesh))
                {
                    visMap[s.RawName] = s.Mesh.Visible;
                    if (s.IsSoloed)
                    {
                        soloedRawName = s.RawName;
                    }
                    if (_preSoloVisibility.TryGetValue(s.Mesh, out bool preVis))
                    {
                        preSoloMap[s.RawName] = preVis;
                    }
                }
            }
        }

        public void RestoreVisibilityState(Dictionary<string, bool> visMap, Dictionary<string, bool> preSoloMap, string soloedRawName)
        {
            if (visMap == null) return;

            _isAnySoloed = !string.IsNullOrEmpty(soloedRawName);
            _preSoloVisibility.Clear();

            foreach (var s in _submeshes)
            {
                if (s.Mesh != null && GodotObject.IsInstanceValid(s.Mesh))
                {
                    if (visMap.TryGetValue(s.RawName, out bool vis))
                    {
                        s.Mesh.Visible = vis;
                    }
                    s.IsSoloed = (_isAnySoloed && s.RawName == soloedRawName);

                    if (preSoloMap != null && preSoloMap.TryGetValue(s.RawName, out bool preVis))
                    {
                        _preSoloVisibility[s.Mesh] = preVis;
                    }
                    else if (!_isAnySoloed)
                    {
                        _preSoloVisibility[s.Mesh] = s.Mesh.Visible;
                    }
                }
            }

            NotifyHierarchyChanged();
        }

        public void CleanupGeneratedSubmeshes()
        {
            foreach (var node in _generatedSubmeshNodes)
            {
                if (node != null && GodotObject.IsInstanceValid(node))
                {
                    node.QueueFree();
                }
            }
            _generatedSubmeshNodes.Clear();

            foreach (var orig in _hiddenOriginalNodes)
            {
                if (orig != null && GodotObject.IsInstanceValid(orig))
                {
                    orig.Visible = true;
                    if (orig.HasMeta("IsHiddenComposite"))
                    {
                        orig.RemoveMeta("IsHiddenComposite");
                    }
                    if (orig.HasMeta("GeneratedSubmeshes"))
                    {
                        orig.RemoveMeta("GeneratedSubmeshes");
                    }
                }
            }
            _hiddenOriginalNodes.Clear();
        }

        public override void _ExitTree()
        {
            CleanupGeneratedSubmeshes();
            base._ExitTree();
        }

        private void NotifyHierarchyChanged()
        {
            EmitSignal(SignalName.HierarchyChanged);
        }

        private void NotifyTargetMeshChanged(MeshInstance3D mesh, int surfaceIndex)
        {
            EmitSignal(SignalName.TargetMeshChanged, mesh, surfaceIndex);
        }
    }
}
