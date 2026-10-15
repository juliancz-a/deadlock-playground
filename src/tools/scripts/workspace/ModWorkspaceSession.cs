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

        [JsonPropertyName("assets")]
        public List<WorkspaceAssetEntry> Assets { get; set; } = new();
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
    }

   public class WorkspaceMaterialEntry
    {
        [JsonPropertyName("material_path")]
        public string MaterialPath { get; set; } = "";

        [JsonPropertyName("display_name")]
        public string DisplayName { get; set; } = "";

        [JsonPropertyName("textures")]
        public Dictionary<string, string> Textures { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        [JsonPropertyName("active_layer_index")]
        public int ActiveLayerIndex { get; set; } = 0;

        [JsonPropertyName("width")]
        public int Width { get; set; } = 2048;

        [JsonPropertyName("height")]
        public int Height { get; set; } = 2048;

        [JsonPropertyName("layers")]
        public List<WorkspaceLayerEntry> Layers { get; set; } = new();
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
        public string ManifestPath { get; private set; }

        public WorkspaceManifest Manifest { get; private set; } = new();
        public bool HasUnsavedChanges { get; set; } = false;

        private readonly Dictionary<string, Image> _stagedImagesInMemory = new(StringComparer.OrdinalIgnoreCase);

        public event Action OnWorkspaceChanged;
        public event Action<string, string> OnAssetDiscarded; // (heroId, vpkPath)
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
                ManifestPath = Path.Combine(SessionDir, "session_manifest.json");

                Directory.CreateDirectory(SessionDir);
                Directory.CreateDirectory(TexturesDir);
                Directory.CreateDirectory(ParticlesDir);

                if (File.Exists(ManifestPath))
                {
                    string json = File.ReadAllText(ManifestPath);
                    Manifest = JsonSerializer.Deserialize<WorkspaceManifest>(json, JsonOpts) ?? new WorkspaceManifest();
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
                if (Directory.Exists(SessionDir))
                {
                    Directory.Delete(SessionDir, true);
                }
                Manifest = new WorkspaceManifest();
                _stagedImagesInMemory.Clear();
                HasUnsavedChanges = false;
                Initialize();
                OnWorkspaceChanged?.Invoke();
                OnWorkspaceCleared?.Invoke();
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[ModWorkspaceSession] Error cleaning session: {ex.Message}");
            }
        }

        public void ClearSession() => CleanupTempSession();

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

            var asset = hero.Assets.FirstOrDefault(a => string.Equals(a.VpkPath, ownerAssetPath, StringComparison.OrdinalIgnoreCase) ||
                                                       string.Equals(a.OwnerAssetPath, ownerAssetPath, StringComparison.OrdinalIgnoreCase));
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

            if (hero.Assets.Count == 0)
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
                hero.Assets.Remove(asset);
            }

            if (hero.Assets.Count == 0)
            {
                Manifest.Heroes.Remove(heroId);
            }

            SaveManifest();
            HasUnsavedChanges = true;
            OnWorkspaceChanged?.Invoke();
            OnAssetDiscarded?.Invoke(heroId, vpkPath);
        }

        public int GetTotalModifiedCount()
        {
            int count = 0;
            foreach (var hero in Manifest.Heroes.Values)
            {
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
