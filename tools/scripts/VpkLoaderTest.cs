using Godot;
using SteamDatabase.ValvePak;
using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using ValveResourceFormat;
using ValveResourceFormat.IO;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;
using System.Threading.Tasks;
using DeadlockPlayground.Tools;
using DeadlockPlayground.Materials;
using DeadlockPlayground.Catalog;
using DeadlockPlayground.Addons;

public partial class VpkLoaderTest : Node3D
{
    [Signal]
    public delegate void LoadStartedEventHandler();
    [Signal]
    public delegate void LoadFinishedEventHandler();
    [Signal]
    public delegate void HeroLoadedEventHandler(Node3D heroNode);
    [Signal]
    public delegate void HeroUnloadedEventHandler();

	[Export]
	public PackedScene PoseEditorScene;
	
	[Export]
	public string VpkPath = @"E:\SteamLibrary\steamapps\common\Deadlock\game\citadel\pak01_dir.vpk";

	[Export]
	public string AddonVpkPath { get; set; } = "";

	public string ActiveAddonVpkPath => AddonVpkPath;
	public DeadlockHeroEntry LastLoadedHeroEntry { get; private set; }
	public string LastLoadedVmdlPath { get; private set; }
	public string LastLoadedHeroKey { get; private set; }
	public string LastLoadedDisplayName { get; private set; }
	public AddonModInfo ActiveAddonInfo { get; set; }

	[Export]
	public string HeroName = "viper";
	public string HeroModelName = "viper";

    private Node3D _currentHeroNode;
    public Node3D CurrentHeroNode => _currentHeroNode;
    private PoseEditorUI _currentPoseEditor;

	public void SetActiveAddon(string addonVpkPath, AddonModInfo modInfo = null)
	{
		AddonVpkPath = addonVpkPath ?? "";
		ActiveAddonInfo = modInfo;
		GD.Print($"[VpkLoader] Active Addon set to: {(string.IsNullOrEmpty(AddonVpkPath) ? "None (Vanilla)" : Path.GetFileName(AddonVpkPath))}");
	}

	public async Task ReloadCurrentHeroAsync()
	{
		if (LastLoadedHeroEntry != null)
		{
			await LoadHeroModelAsync(LastLoadedHeroEntry);
		}
		else if (!string.IsNullOrEmpty(LastLoadedVmdlPath))
		{
			await LoadModelInternalAsync(LastLoadedVmdlPath, LastLoadedHeroKey ?? HeroName, LastLoadedDisplayName ?? HeroName);
		}
	}

	/// <summary>
	/// Directly loads an addon model or texture-only mod without requiring prior vanilla hero selection.
	/// </summary>
	public async Task<Node3D> LoadAddonModelAsync(AddonModInfo modInfo, string explicitVmdlPath = null)
	{
		if (modInfo == null) return null;

		SetActiveAddon(modInfo.FilePath, modInfo);

		// If texture-only mod with detected hero, load the corresponding hero from catalog
		if (modInfo.ModType == AddonModType.TextureOnly && !string.IsNullOrEmpty(modInfo.DetectedHeroCodename))
		{
			var catalogEntry = DeadlockHeroCatalog.GetByCodename(modInfo.DetectedHeroCodename);
			if (catalogEntry != null)
			{
				GD.Print($"[VpkLoader] Loading base hero '{catalogEntry.DisplayName}' with addon textures from '{modInfo.FileName}'...");
				return await LoadHeroModelAsync(catalogEntry);
			}
		}

		// Full model mod or explicit model route
		string targetRoute = explicitVmdlPath;
		if (string.IsNullOrEmpty(targetRoute))
		{
			targetRoute = modInfo.ModelEntries.FirstOrDefault();
		}

		if (string.IsNullOrEmpty(targetRoute))
		{
			GD.PrintErr($"[VpkLoader] No .vmdl_c found in addon '{modInfo.FileName}' to load.");
			return null;
		}

		string heroKey = !string.IsNullOrEmpty(modInfo.DetectedHeroCodename)
			? modInfo.DetectedHeroCodename
			: Path.GetFileNameWithoutExtension(targetRoute);

		string displayName = !string.IsNullOrEmpty(modInfo.DetectedHeroDisplayName)
			? $"{modInfo.DetectedHeroDisplayName} (Addon)"
			: (modInfo.DisplayTitle ?? Path.GetFileNameWithoutExtension(targetRoute));

		if (!string.IsNullOrEmpty(modInfo.DetectedHeroCodename))
		{
			LastLoadedHeroEntry = DeadlockHeroCatalog.GetByCodename(modInfo.DetectedHeroCodename);
		}
		else
		{
			LastLoadedHeroEntry = null;
		}

		LastLoadedVmdlPath = targetRoute;
		LastLoadedHeroKey = heroKey;
		LastLoadedDisplayName = displayName;

		return await LoadModelInternalAsync(targetRoute, heroKey, displayName);
	}

	public override void _Ready()
	{
		// Don't auto-load, wait for UI to trigger
	}

	/// <summary>
	/// Loads a hero model using its explicit catalog entry.
	/// Handles arbitrary directory depths, folder name mismatches, and staging vs WIP models.
	/// </summary>
	public async Task<Node3D> LoadHeroModelAsync(DeadlockHeroEntry entry)
	{
		if (entry == null) return null;
		LastLoadedHeroEntry = entry;
		LastLoadedVmdlPath = entry.VmdlRelativePath;
		LastLoadedHeroKey = entry.InternalCodename;
		LastLoadedDisplayName = entry.DisplayName;
		HeroName = entry.InternalCodename;
		HeroModelName = Path.GetFileNameWithoutExtension(entry.VmdlRelativePath);
		return await LoadModelInternalAsync(entry.VmdlRelativePath, entry.InternalCodename, entry.DisplayName);
	}

	public async Task LoadHeroAsync(string hero, string model)
	{
		var catalogEntry = DeadlockHeroCatalog.GetByCodename(hero) ??
		                   DeadlockHeroCatalog.GetByCodename(model);
		if (catalogEntry != null)
		{
			await LoadHeroModelAsync(catalogEntry);
			return;
		}

		string internalRoute = $"models/heroes_staging/{hero}/{model}.vmdl_c";
		await LoadModelInternalAsync(internalRoute, hero, hero);
	}

	private async Task<Node3D> LoadModelInternalAsync(string internalRoute, string heroKey, string displayName)
	{
		HeroName = heroKey;
		HeroModelName = Path.GetFileNameWithoutExtension(internalRoute);
		EmitSignal(SignalName.LoadStarted);

		// 1. Cleanup old hero completely
		CleanupCurrentHero();

		if (!File.Exists(VpkPath))
		{
			GD.PrintErr($"No se encontró el archivo VPK en: {VpkPath}");
			EmitSignal(SignalName.LoadFinished);
			return null;
		}

		Package addonPackage = null;
		if (!string.IsNullOrWhiteSpace(AddonVpkPath) && File.Exists(AddonVpkPath))
		{
			try
			{
				addonPackage = new Package();
				addonPackage.Read(AddonVpkPath);
				GD.Print($"[VpkLoader] Active Addon VPK opened: {Path.GetFileName(AddonVpkPath)}");
			}
			catch (Exception ex)
			{
				GD.PrintErr($"[VpkLoader] Failed to open Addon VPK '{AddonVpkPath}': {ex.Message}");
				addonPackage?.Dispose();
				addonPackage = null;
			}
		}

		try
		{
			GD.Print($"[VpkLoader] Abriendo VPK para cargar '{displayName}' ({internalRoute})...");
			using var basePackage = new Package();
			basePackage.Read(VpkPath);

			string searchRoute = internalRoute.Replace('\\', '/');
			if (!searchRoute.EndsWith("_c")) searchRoute += "_c";

			PackageEntry entry = null;
			Package modelOwnerPackage = null;

			// Priority 1: Active Addon VPK
			if (addonPackage != null)
			{
				entry = FindModelEntry(addonPackage, searchRoute, heroKey, isAddon: true);
				if (entry != null)
				{
					modelOwnerPackage = addonPackage;
					GD.Print($"[VpkLoader] Injected model override from Addon VPK: {entry.DirectoryName}/{entry.FileName}.{entry.TypeName}");
				}
			}

			// Priority 2: Base Game VPK
			if (entry == null)
			{
				entry = FindModelEntry(basePackage, searchRoute, heroKey, isAddon: false);
				if (entry != null)
				{
					modelOwnerPackage = basePackage;
				}
			}

			if (entry == null)
			{
				GD.PrintErr($"No se encontró el modelo para '{internalRoute}' dentro del VPK.");
				EmitSignal(SignalName.LoadFinished);
				return null;
			}

			GD.Print($"Modelo encontrado: {entry.DirectoryName}/{entry.FileName}.{entry.TypeName} (Origen: {(modelOwnerPackage == addonPackage ? "Addon" : "Base")})");

			byte[] glbBytes = null;
			Dictionary<string, List<string>> meshMaterialMap = null;

			// Run heavy extraction and GLTF export in background thread
			await Task.Run(() => 
			{
				// 1. Extraemos y parseamos el recurso de Source 2
				modelOwnerPackage.ReadEntry(entry, out byte[] resourceData);
				using var resource = new ValveResourceFormat.Resource();
				using var stream = new MemoryStream(resourceData);
				resource.Read(stream);

				// 2. Extraemos el mapa mesh → materiales desde los draw calls del VMDL
				meshMaterialMap = BuildMeshMaterialMap(resource);

				// 3. Exportamos a glTF 2.0 (GLB binario) sin pistas continuas pesadas (Zero-RAM Pose Mode)
				GD.Print("Generando GLB con VRF (Zero-RAM Pose Mode)...");

				IFileLoader fileLoader;
				if (addonPackage != null)
				{
					var primaryLoader = new GameFileLoader(addonPackage, entry.DirectoryName);
					var secondaryLoader = new GameFileLoader(basePackage, entry.DirectoryName);
					fileLoader = new DualLayerGameFileLoader(primaryLoader, secondaryLoader);
				}
				else
				{
					fileLoader = new GameFileLoader(basePackage, entry.DirectoryName);
				}

				var exporter = new GltfModelExporter(fileLoader)
				{
					ExportAnimations = true,
					AdaptTextures = true,
					ExportMaterials = false,
					ExportExtras = false,
					ProgressReporter = new Progress<string>(msg => GD.Print($"[VRF] {msg}"))
				};

			// Curated high-value posing animation filter to keep GLB lightweight while exporting undeformed, retargeted animations
			if (resource.DataBlock is Model vModel)
			{
				try
				{
					var allAnims = vModel.GetAllAnimations(fileLoader).ToList();
					string[] targetKeywords = new[]
					{
						"parry", "reload", "stand_idle", "primary_idle", "crouch_idle", "primary_crouch_idle",
						"shoot_idle", "primary_shoot", "ui_hero_select", "ui_pose", "hero_pose",
						"melee", "cast", "attack", "idle"
					};

					int GetPriority(string name)
					{
						if (name.Contains("parry")) return 1;
						if (name.Contains("reload")) return 2;
						if (name.Contains("stand_idle") || name.Contains("primary_idle") || name.Contains("ui_hero_select")) return 3;
						return 4;
					}

					var matchedAnims = allAnims.Where(a =>
					{
						string n = a.Name.ToLowerInvariant();
						bool matchesKeyword = targetKeywords.Any(k => n.Contains(k));
						if (!matchesKeyword) return false;

						if (n.Contains("_nw") || n.Contains("_ne") || n.Contains("_sw") || n.Contains("_se") ||
						    n.EndsWith("_w") || n.EndsWith("_e") || n.EndsWith("_s")) return false;

						if (n.Contains("ragdoll") || n.Contains("death") || n.Contains("knockdown") ||
						    n.Contains("flinch") || n.Contains("drown") || n.Contains("hit_") || n.Contains("pain_")) return false;

						return true;
					})
					.OrderBy(a => GetPriority(a.Name.ToLowerInvariant()))
					.Take(40)
					.ToList();

					if (matchedAnims.Count == 0 && allAnims.Count > 0)
					{
						matchedAnims = allAnims.Take(10).ToList();
					}

					foreach (var a in matchedAnims)
					{
						string raw = a.Name;
						string clean = raw.Replace('\\', '/');
						string withoutExt = Path.ChangeExtension(clean, null);
						exporter.AnimationFilter.Add(raw);
						exporter.AnimationFilter.Add(clean);
						exporter.AnimationFilter.Add("@" + clean);
						exporter.AnimationFilter.Add(withoutExt);
						exporter.AnimationFilter.Add(Path.GetFileName(clean));
						exporter.AnimationFilter.Add(Path.GetFileName(withoutExt));
					}
					GD.Print($"[VRF] Exporting {matchedAnims.Count} curated posing animations into GLTF.");
				}
				catch (Exception ex)
				{
					GD.PrintErr($"[VRF] Error populating animation filter: {ex.Message}");
				}
			}

			string tempGlbPath = Path.Combine(Path.GetTempPath(), $"{heroKey}_{Guid.NewGuid():N}.glb");
			try
			{
				exporter.Export(resource, tempGlbPath);
				glbBytes = File.ReadAllBytes(tempGlbPath);
			}
			finally
			{
				if (File.Exists(tempGlbPath))
				{
					File.Delete(tempGlbPath);
				}
			}
		});

		if (glbBytes != null)
		{
			GD.Print($"GLB generado con éxito ({glbBytes.Length / (1024 * 1024)} MB). Importando a Godot...");
			modelOwnerPackage.ReadEntry(entry, out byte[] modelData);
			using var modelRes = new ValveResourceFormat.Resource();
			using var ms = new MemoryStream(modelData);
			modelRes.Read(ms);
			var vrfModel = modelRes.DataBlock as Model;

			InstantiateInScene(glbBytes, heroKey, basePackage, meshMaterialMap, vrfModel, entry.DirectoryName, addonPackage);
		}

		EmitSignal(SignalName.LoadFinished);
		return _currentHeroNode;
		}
		finally
		{
			addonPackage?.Dispose();
		}
	}

    private void CleanupCurrentHero()
    {
        EmitSignal(SignalName.HeroUnloaded);

        if (_currentPoseEditor != null)
        {
            _currentPoseEditor.QueueFree();
            _currentPoseEditor = null;
        }

        if (_currentHeroNode != null)
        {
            DeadlockAnimLoader.ClearHeroPoses(_currentHeroNode);
            DisposeNodeRecursively(_currentHeroNode);
            _currentHeroNode.GetParent()?.RemoveChild(_currentHeroNode);
            _currentHeroNode.QueueFree();
            _currentHeroNode = null;
        }

        // Clean texture cache so reloading a hero or switching heroes never accesses disposed texture objects
        Source2MaterialHelper.cleanCache();

        // Force Garbage Collection to clean up VRF memory dumps
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }

    private void DisposeNodeRecursively(Node node)
    {
        if (node is MeshInstance3D mi && mi.Mesh != null)
        {
            for (int i = 0; i < mi.Mesh.GetSurfaceCount(); i++)
            {
                var mat = mi.GetSurfaceOverrideMaterial(i) ?? mi.Mesh.SurfaceGetMaterial(i);
                if (mat != null)
                {
                    mat.Dispose();
                }
            }
            mi.Mesh.Dispose();
        }

        foreach (Node child in node.GetChildren())
        {
            DisposeNodeRecursively(child);
        }
    }
	/// Lee los draw calls de cada malla embebida en el VMDL para construir
	/// un mapa: nombre_de_malla → lista ordenada de rutas .vmat (una por superficie).
	/// </summary>
	private Dictionary<string, List<string>> BuildMeshMaterialMap(ValveResourceFormat.Resource modelResource)
	{
		var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

		if (modelResource.DataBlock is not Model model)
		{
			GD.PrintErr("[MaterialMap] El recurso no es un modelo VMDL.");
			return map;
		}

		foreach (var (mesh, meshIndex, meshName) in model.GetEmbeddedMeshes())
		{
			var materials = new List<string>();

			var sceneObjects = mesh.Data.GetArray("m_sceneObjects");
			if (sceneObjects != null)
			{
				foreach (var sceneObj in sceneObjects)
				{
					var drawCalls = sceneObj.GetArray("m_drawCalls");
					if (drawCalls != null)
					{
						for (int d = 0; d < drawCalls.Count; d++)
						{
							string matPath = drawCalls[d].GetStringProperty("m_material");
							materials.Add(matPath);
						}
					}
				}
			}

			map[meshName] = materials;
			GD.Print($"[MaterialMap] Mesh '{meshName}': {materials.Count} superficie(s) → [{string.Join(", ", materials.Select(Path.GetFileNameWithoutExtension))}]");
		}

		return map;
	}

	private void InstantiateInScene(byte[] glbBytes, string hero, Package package, Dictionary<string, List<string>> meshMaterialMap, Model vrfModel, string vmdlDirectory, Package addonPackage = null)
	{
		try
		{
			var gltfDoc = new GltfDocument();
			var gltfState = new GltfState();

			// Parseamos el buffer GLB
			Error err = gltfDoc.AppendFromBuffer(glbBytes, "", gltfState);
			if (err != Error.Ok)
			{
				GD.PrintErr($"Error de Godot al parsear el buffer GLTF: {err}");
				return;
			}

			// Generamos los nodos 3D (MeshInstance3D, Skeleton3D, etc.)
			Node3D modelScene = (Node3D)gltfDoc.GenerateScene(gltfState);
			modelScene.Name = $"Hero_{hero}";

			_currentHeroNode = modelScene;

			AddChild(modelScene);
			GD.Print($"¡Héroe '{hero}' instanciado en el Viewport con éxito!");

			GD.Print("Malla instanciada. Enlazando materiales por submalla...");
			ApplyMaterialsRecursively(modelScene, hero, package, meshMaterialMap, addonPackage);

			// Apply initial submesh visibility rules (e.g. Viscous core/body, hair, weapons)
			DeadlockMaterialResolver.ApplyInitialSubmeshVisibility(modelScene, hero);

			// Note: Retargeted, clean poses were exported directly into glTF AnimationPlayer by VRF.
			// DeadlockAnimLoader.LoadHeroPoses is bypassed here so it does not clear AnimationPlayer or push unretargeted raw bone frames.

			EmitSignal(SignalName.HeroLoaded, modelScene);

			// Buscamos si generó el Skeleton3D para confirmar que vino riggeado
			var skeleton = SearchSkeleton(modelScene);
			var animPlayer = SearchAnimationPlayer(modelScene);
		if (skeleton != null)
		{
			GD.Print($"Skeleton detectado con éxito: {skeleton.GetBoneCount()} huesos encontrados.");
			
			// Instanciar y configurar la UI del Pose Editor legacy si está explícitamente asignada
			if (PoseEditorScene != null)
			{
				var instancedNode = PoseEditorScene.Instantiate();
				var poseEditor = instancedNode as PoseEditorUI;
				
				if (poseEditor != null)
				{
					poseEditor.Name = "PoseEditorUI";
					_currentPoseEditor = poseEditor;
					AddChild(poseEditor);
					poseEditor.SetSkeleton(skeleton);
					if (animPlayer != null)
					{
						poseEditor.SetAnimationPlayer(animPlayer);
					}

					var gizmoManager = new SkeletonGizmoManager();
					gizmoManager.Name = "SkeletonGizmoManager";
					gizmoManager.TargetSkeleton = skeleton;
					gizmoManager.UIManager = poseEditor;
					skeleton.AddChild(gizmoManager);
				}
				else
				{
					GD.PrintErr("ERROR: The PoseEditorScene does not have the 'PoseEditorUI.cs' script attached to its root node!");
					instancedNode.QueueFree();
				}
			}
		}
		else
		{
			GD.Print("Aviso: No se detectó un Skeleton3D en la raíz de la malla generada.");
		}
		}
		catch (Exception ex)
		{
			GD.PrintErr($"[InstantiateInScene Crash]: {ex.Message}\n{ex.StackTrace}");
		}
	}

	/// <summary>
	/// Recorre recursivamente los nodos buscando MeshInstance3D y les aplica
	/// los materiales según el mapa extraído del VMDL (draw calls).
	/// Prioriza addonPackage (Priority 1) sobre package base (Priority 2).
	/// </summary>
	private void ApplyMaterialsRecursively(Node node, string heroName, Package package, Dictionary<string, List<string>> meshMaterialMap, Package addonPackage = null)
	{
		if (node is MeshInstance3D meshInstance && meshInstance.Mesh != null)
		{
			// VRF nombra los nodos como ".body", ".head", etc.
			// Godot convierte el "." a "_" en nombres de nodo, así que quitamos ambos
			string meshName = meshInstance.Name.ToString().TrimStart('.', '_');

			// Match mesh name against embedded draw call material map
			if (!meshMaterialMap.TryGetValue(meshName, out var materialPaths))
			{
				foreach (var kvp in meshMaterialMap.OrderByDescending(k => k.Key.Length))
				{
					if (meshName.Equals(kvp.Key, StringComparison.OrdinalIgnoreCase) ||
						meshName.EndsWith(kvp.Key, StringComparison.OrdinalIgnoreCase) ||
						kvp.Key.EndsWith(meshName, StringComparison.OrdinalIgnoreCase) ||
						meshName.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase) ||
						kvp.Key.Contains(meshName, StringComparison.OrdinalIgnoreCase))
					{
						materialPaths = kvp.Value;
						break;
					}
				}
			}

			if (materialPaths == null && meshName.Contains("jitter", StringComparison.OrdinalIgnoreCase))
			{
				foreach (var kvp in meshMaterialMap)
				{
					if (kvp.Key.Contains("jitter", StringComparison.OrdinalIgnoreCase) && kvp.Value != null && kvp.Value.Count > 0)
					{
						materialPaths = kvp.Value;
						break;
					}
				}
				if (materialPaths == null)
				{
					materialPaths = new List<string> { "models/heroes_wip/punkgoat/materials/punkgoat_border_jitter01.vmat" };
				}
			}

			// Wraith hand card: match mesh name, node parent hierarchy, or surface material names
			bool isCardMesh = meshName.Contains("card", StringComparison.OrdinalIgnoreCase) ||
			                  meshName.Contains("deck", StringComparison.OrdinalIgnoreCase) ||
			                  node.Name.ToString().Contains("card", StringComparison.OrdinalIgnoreCase) ||
			                  (node.GetParent() != null && (
			                      node.GetParent().Name.ToString().Contains("card", StringComparison.OrdinalIgnoreCase) ||
			                      node.GetParent().Name.ToString().Contains("wraith_card", StringComparison.OrdinalIgnoreCase)
			                  )) ||
			                  (node.GetParent()?.GetParent() != null && (
			                      node.GetParent().GetParent().Name.ToString().Contains("card", StringComparison.OrdinalIgnoreCase) ||
			                      node.GetParent().GetParent().Name.ToString().Contains("wraith_card", StringComparison.OrdinalIgnoreCase)
			                  ));

			if (!isCardMesh && meshInstance.Mesh != null)
			{
				for (int s = 0; s < meshInstance.Mesh.GetSurfaceCount(); s++)
				{
					var sm = meshInstance.Mesh.SurfaceGetMaterial(s) ?? meshInstance.GetSurfaceOverrideMaterial(s);
					if (sm != null)
					{
						string sName = (sm.ResourceName + " " + sm.ResourcePath).ToLowerInvariant();
						if (sName.Contains("card") || sName.Contains("deck"))
						{
							isCardMesh = true;
							break;
						}
					}
				}
			}

			if (materialPaths == null && isCardMesh)
			{
				materialPaths = new List<string> { "models/heroes_wip/wraith/materials/wraith_cards.vmat" };
				GD.Print($"  [MaterialMap] Mapped card mesh '{meshName}' (parent: '{node.GetParent()?.Name}') to 'models/heroes_wip/wraith/materials/wraith_cards.vmat'");
			}

			// Lady Geist shawl fur shells (ghost_shawl_fur01-05, geist_fur, etc.): match mesh name, node parent hierarchy, or surface materials
			bool isFurOrShawlMesh = meshName.Contains("fur", StringComparison.OrdinalIgnoreCase) ||
			                        meshName.Contains("shawl", StringComparison.OrdinalIgnoreCase) ||
			                        node.Name.ToString().Contains("fur", StringComparison.OrdinalIgnoreCase) ||
			                        node.Name.ToString().Contains("shawl", StringComparison.OrdinalIgnoreCase) ||
			                        (node.GetParent() != null && (
			                            node.GetParent().Name.ToString().Contains("fur", StringComparison.OrdinalIgnoreCase) ||
			                            node.GetParent().Name.ToString().Contains("shawl", StringComparison.OrdinalIgnoreCase)
			                        )) ||
			                        (node.GetParent()?.GetParent() != null && (
			                            node.GetParent().GetParent().Name.ToString().Contains("fur", StringComparison.OrdinalIgnoreCase) ||
			                            node.GetParent().GetParent().Name.ToString().Contains("shawl", StringComparison.OrdinalIgnoreCase)
			                        ));

			bool isFurShellSubmesh = isFurOrShawlMesh && meshName.Contains("fur", StringComparison.OrdinalIgnoreCase);

			bool hasAuthenticFurMaterial = materialPaths != null && materialPaths.Any(p => p.Contains("fur", StringComparison.OrdinalIgnoreCase) && !IsDummyMaterialPath(p));

			// If materialPaths is missing, empty, or consists exclusively of dev dummy materials (e.g. primary_white.vmat),
			// or if this is a fur shell submesh that matched a non-fur base cloth (e.g. ghost_shawl_fur01 matched ghost_shawl.vmat):
			if (isFurOrShawlMesh && (materialPaths == null || materialPaths.Count == 0 || (isFurShellSubmesh && !hasAuthenticFurMaterial)))
			{
				materialPaths = null;
				// First check for dedicated non-dummy fur materials in meshMaterialMap
				foreach (var kvp in meshMaterialMap)
				{
					if (kvp.Key.Contains("fur", StringComparison.OrdinalIgnoreCase) &&
					    kvp.Value != null && kvp.Value.Any(p => !IsDummyMaterialPath(p)))
					{
						materialPaths = kvp.Value.Where(p => !IsDummyMaterialPath(p)).ToList();
						GD.Print($"  [MaterialMap] Mapped fur mesh '{meshName}' to '{kvp.Key}' ({string.Join(", ", materialPaths.Select(Path.GetFileNameWithoutExtension))})");
						break;
					}
				}
				// Next check for authentic shawl materials in meshMaterialMap ONLY IF this is a base shawl mesh (NOT a fur shell)
				if (materialPaths == null && !isFurShellSubmesh)
				{
					foreach (var kvp in meshMaterialMap)
					{
						if (kvp.Key.Contains("shawl", StringComparison.OrdinalIgnoreCase) &&
						    kvp.Value != null && kvp.Value.Any(p => !IsDummyMaterialPath(p)))
						{
							materialPaths = kvp.Value.Where(p => !IsDummyMaterialPath(p)).ToList();
							GD.Print($"  [MaterialMap] Mapped fur/shawl mesh '{meshName}' to '{kvp.Key}' ({string.Join(", ", materialPaths.Select(Path.GetFileNameWithoutExtension))})");
							break;
						}
					}
				}
				// Cross-model authentic fallback: fur shells prioritize ghost_shawl_fur.vmat / geist_fur.vmat; base shawl prioritizes ghost_shawl.vmat
				if (materialPaths == null || materialPaths.Count == 0)
				{
					string fallbackVmat = isFurShellSubmesh
						? (heroName.Contains("ghost", StringComparison.OrdinalIgnoreCase)
							? "models/heroes_staging/ghost/materials/ghost_shawl_fur.vmat"
							: "models/heroes_wip/geist/materials/geist_fur.vmat")
						: (heroName.Contains("ghost", StringComparison.OrdinalIgnoreCase)
							? "models/heroes_staging/ghost/materials/ghost_shawl.vmat"
							: "models/heroes_wip/geist/materials/geist_fur.vmat");
					materialPaths = new List<string> { fallbackVmat };
					GD.Print($"  [MaterialMap] Fallback fur/shawl mesh '{meshName}' to '{fallbackVmat}'");
				}
			}

			if (materialPaths != null)
			{
				var mesh = meshInstance.Mesh;
				int surfaceCount = mesh.GetSurfaceCount();

				// Resolve numeric shell index for single-surface layered shells (e.g. ghost_shawl_fur01 to fur05)
				int targetMaterialIndex = DeadlockMaterialResolver.ResolveFurShellIndex(meshName, surfaceCount, materialPaths.Count);

				for (int i = 0; i < surfaceCount; i++)
				{
					int matIdx = targetMaterialIndex + i;
					if (matIdx >= materialPaths.Count) matIdx = materialPaths.Count - 1;
					string vmatPath = materialPaths[matIdx];

					// If fur shell draw call points to a dummy, replace with non-dummy candidate in materialPaths
					if (isFurShellSubmesh && IsDummyMaterialPath(vmatPath))
					{
						var nonDummy = materialPaths.FirstOrDefault(p => !IsDummyMaterialPath(p));
						if (!string.IsNullOrEmpty(nonDummy))
						{
							vmatPath = nonDummy;
						}
					}

					// Resolve submesh material fallbacks (e.g. fur shells pointing to base cloth, weapon staging placeholders like tengu_gun.vmat)
					string submeshFb = DeadlockMaterialResolver.ResolveSubmeshMaterialFallback(meshName, vmatPath);
					if (!string.IsNullOrEmpty(submeshFb))
					{
						vmatPath = submeshFb;
					}

					Godot.Material mat = Source2MaterialHelper.CreateMaterialFromVmat(package, vmatPath, meshName, addonPackage);

					// If material is a dummy, null, or untextured on a fur/shawl mesh, cascade through authentic hero fur materials
					if (isFurOrShawlMesh && (IsDummyMaterial(mat, vmatPath) || (isFurShellSubmesh && !vmatPath.Contains("fur", StringComparison.OrdinalIgnoreCase))))
					{
						// Cascade 1: Any non-dummy candidate from meshMaterialMap matching fur
						foreach (var kvp in meshMaterialMap)
						{
							if (kvp.Key.Contains("fur", StringComparison.OrdinalIgnoreCase) && kvp.Value != null)
							{
								foreach (var cand in kvp.Value)
								{
									if (!IsDummyMaterialPath(cand))
									{
										var fbMat = Source2MaterialHelper.CreateMaterialFromVmat(package, cand, meshName, addonPackage);
										if (!IsDummyMaterial(fbMat, cand))
										{
											mat = fbMat;
											vmatPath = cand;
											break;
										}
									}
								}
								if (mat != null && !IsDummyMaterial(mat, vmatPath)) break;
							}
						}

						// Cascade 2: ghost_shawl_fur.vmat
						if (mat == null || IsDummyMaterial(mat, vmatPath) || (isFurShellSubmesh && !vmatPath.Contains("fur", StringComparison.OrdinalIgnoreCase)))
						{
							string ghostFurFallback = "models/heroes_staging/ghost/materials/ghost_shawl_fur.vmat";
							var fbMat = Source2MaterialHelper.CreateMaterialFromVmat(package, ghostFurFallback, meshName, addonPackage);
							if (fbMat != null && !IsDummyMaterial(fbMat, ghostFurFallback))
							{
								mat = fbMat;
								vmatPath = ghostFurFallback;
							}
						}

						// Cascade 3: geist_fur.vmat (authentic Lady Geist fur card material)
						if (mat == null || IsDummyMaterial(mat, vmatPath) || (isFurShellSubmesh && !vmatPath.Contains("fur", StringComparison.OrdinalIgnoreCase)))
						{
							string geistFallback = "models/heroes_wip/geist/materials/geist_fur.vmat";
							var fbMat = Source2MaterialHelper.CreateMaterialFromVmat(package, geistFallback, meshName, addonPackage);
							if (fbMat != null && !IsDummyMaterial(fbMat, geistFallback))
							{
								mat = fbMat;
								vmatPath = geistFallback;
							}
						}

						// Cascade 4: ghost_shawl.vmat (strictly for base cloth shawl, NOT for fur shell submeshes!)
						if (!isFurShellSubmesh && (mat == null || IsDummyMaterial(mat, vmatPath)))
						{
							string ghostFallback = "models/heroes_staging/ghost/materials/ghost_shawl.vmat";
							var fbMat = Source2MaterialHelper.CreateMaterialFromVmat(package, ghostFallback, meshName, addonPackage);
							if (fbMat != null && !IsDummyMaterial(fbMat, ghostFallback))
							{
								mat = fbMat;
								vmatPath = ghostFallback;
							}
						}
					}

					if (isFurShellSubmesh && !IsDummyMaterial(mat, vmatPath))
					{
						GD.Print($"  [FurShell] Resolved authentic fur material '{vmatPath}' for mesh '{meshName}'");
					}

					if (mat == null && (meshName.Contains("jitter", StringComparison.OrdinalIgnoreCase) || vmatPath.Contains("jitter", StringComparison.OrdinalIgnoreCase)))
					{
						mat = Source2MaterialHelper.CreateMaterialFromVmat(package, "models/heroes_wip/punkgoat/materials/punkgoat_border_jitter01.vmat", meshName, addonPackage);
					}
					if (mat == null && isCardMesh)
					{
						mat = Source2MaterialHelper.CreateMaterialFromVmat(package, "models/heroes_wip/wraith/materials/wraith_cards.vmat", meshName, addonPackage);
					}

					if (mat != null)
					{
						mat.ResourceName = vmatPath;
						HeroMaterialManager.ConfigureMaterial(heroName, meshName, i, vmatPath, mat, package, addonPackage);
						meshInstance.SetSurfaceOverrideMaterial(i, mat);
						if (meshInstance.Mesh is ArrayMesh arrMesh)
						{
							arrMesh.SurfaceSetMaterial(i, mat);
						}
						meshInstance.SetMeta($"OriginalMaterial_{i}", mat);
						GD.Print($"  ✓ Material aplicado: '{Path.GetFileNameWithoutExtension(vmatPath)}' → {meshName}[{i}]");
					}
					else
					{
						GD.PrintErr($"  ✗ No se pudo crear material: '{vmatPath}' para {meshName}[{i}]");
					}
				}

				if (surfaceCount > materialPaths.Count)
				{
					var fallbackMat = meshInstance.GetSurfaceOverrideMaterial(0);
					if (fallbackMat != null)
					{
						for (int extra = materialPaths.Count; extra < surfaceCount; extra++)
						{
							meshInstance.SetSurfaceOverrideMaterial(extra, fallbackMat);
							if (meshInstance.Mesh is ArrayMesh arrMesh)
							{
								arrMesh.SurfaceSetMaterial(extra, fallbackMat);
							}
							meshInstance.SetMeta($"OriginalMaterial_{extra}", fallbackMat);
						}
					}
				}

				if (surfaceCount != materialPaths.Count && targetMaterialIndex == 0)
				{
					GD.Print($"  ⚠ Desajuste: mesh '{meshName}' tiene {surfaceCount} superficies pero el VMDL declara {materialPaths.Count} draw calls.");
				}
			}
			else
			{
				GD.Print($"  ? Mesh '{meshName}' no encontrada en el mapa de materiales del VMDL.");
			}
		}

		foreach (Node child in node.GetChildren())
		{
			ApplyMaterialsRecursively(child, heroName, package, meshMaterialMap, addonPackage);
		}
	}

	private static PackageEntry FindModelEntry(Package package, string searchRoute, string heroKey, bool isAddon = false)
	{
		if (package == null) return null;

		string normRoute = searchRoute.Replace('\\', '/').TrimStart('/');
		var entry = package.FindEntry(normRoute);
		if (entry != null) return entry;

		if (normRoute.EndsWith("_c", StringComparison.OrdinalIgnoreCase))
		{
			entry = package.FindEntry(normRoute.Substring(0, normRoute.Length - 2));
			if (entry != null) return entry;
		}
		else
		{
			entry = package.FindEntry(normRoute + "_c");
			if (entry != null) return entry;
		}

		string fileNameOnly = Path.GetFileNameWithoutExtension(searchRoute);
		if (fileNameOnly.EndsWith(".vmdl", StringComparison.OrdinalIgnoreCase))
		{
			fileNameOnly = Path.GetFileNameWithoutExtension(fileNameOnly);
		}

		foreach (var ext in new[] { "vmdl_c", "vmdl" })
		{
			if (package.Entries.TryGetValue(ext, out var modelEntries) && modelEntries.Count > 0)
			{
				// 1. Match exact filename
				entry = modelEntries.Find(e => e.FileName.Equals(fileNameOnly, StringComparison.OrdinalIgnoreCase));
				if (entry != null) return entry;

				// 2. Match hero key if hero-specific
				if (!string.IsNullOrEmpty(heroKey))
				{
					entry = modelEntries.Find(e =>
						!e.FileName.Contains("_physics", StringComparison.OrdinalIgnoreCase) &&
						!e.FileName.Contains("_agdoll", StringComparison.OrdinalIgnoreCase) &&
						(e.FileName.Equals(heroKey, StringComparison.OrdinalIgnoreCase)
						 || e.DirectoryName.Contains($"/{heroKey}", StringComparison.OrdinalIgnoreCase)
						 || e.DirectoryName.EndsWith($"/{heroKey}", StringComparison.OrdinalIgnoreCase)));
					if (entry != null) return entry;
				}

				// 3. For addon packages: fallback to primary character model (exclude physics/ragdoll/hitbox/weapons)
				if (isAddon)
				{
					var candidates = modelEntries.Where(e =>
						!e.FileName.Contains("_physics", StringComparison.OrdinalIgnoreCase) &&
						!e.FileName.Contains("_agdoll", StringComparison.OrdinalIgnoreCase) &&
						!e.FileName.Contains("_hitbox", StringComparison.OrdinalIgnoreCase)).ToList();

					if (candidates.Count > 0)
					{
						var nonWeapon = candidates.Find(e => !e.FileName.Contains("_weapon", StringComparison.OrdinalIgnoreCase));
						return nonWeapon ?? candidates[0];
					}
				}
			}
		}

		return null;
	}

	private static bool IsDummyMaterialPath(string path)
	{
		if (string.IsNullOrWhiteSpace(path)) return true;
		string p = path.ToLowerInvariant();
		return p.Contains("dev/") ||
		       p.Contains("default") ||
		       p.Contains("primary_white") ||
		       p.Contains("vertcolor_pbr_basic") ||
		       p.Contains("498635a");
	}

	private static bool IsDummyMaterial(Godot.Material mat, string vmatPath)
	{
		if (mat == null) return true;
		if (IsDummyMaterialPath(vmatPath)) return true;
		if (mat is StandardMaterial3D sm)
		{
			if (sm.AlbedoTexture == null) return true;
			string texName = (sm.AlbedoTexture.ResourceName + " " + sm.AlbedoTexture.ResourcePath).ToLowerInvariant();
			if (texName.Contains("primary_white") || texName.Contains("498635a") || texName.Contains("dev/"))
			{
				return true;
			}
		}
		return false;
	}

	private Skeleton3D SearchSkeleton(Node node)
	{
		if (node is Skeleton3D sk) return sk;

		foreach (Node child in node.GetChildren())
		{
			var res = SearchSkeleton(child);
			if (res != null) return res;
		}
		return null;
	}

	private AnimationPlayer SearchAnimationPlayer(Node node)
	{
		if (node is AnimationPlayer ap) return ap;

		foreach (Node child in node.GetChildren())
		{
			var res = SearchAnimationPlayer(child);
			if (res != null) return res;
		}
		return null;
	}
}
