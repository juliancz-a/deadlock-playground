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
	public string HeroName = "viper";
	public string HeroModelName = "viper";

    private Node3D _currentHeroNode;
    public Node3D CurrentHeroNode => _currentHeroNode;
    private PoseEditorUI _currentPoseEditor;

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

		GD.Print($"[VpkLoader] Abriendo VPK para cargar '{displayName}' ({internalRoute})...");
		using var package = new Package();
		package.Read(VpkPath);

		string searchRoute = internalRoute.Replace('\\', '/');
		if (!searchRoute.EndsWith("_c")) searchRoute += "_c";

		var entry = package.FindEntry(searchRoute);

		// Fallback por si la convención de nombres difiere levemente
		if (entry == null)
		{
			string fileNameOnly = Path.GetFileNameWithoutExtension(internalRoute);
			GD.Print($"Ruta exacta no encontrada ({searchRoute}), buscando archivo '{fileNameOnly}'...");
			if (package.Entries.TryGetValue("vmdl_c", out var modelEntries))
			{
				entry = modelEntries.Find(e => e.FileName.Equals(fileNameOnly, StringComparison.OrdinalIgnoreCase));
			}
		}

		if (entry == null)
		{
			GD.PrintErr($"No se encontró el modelo para '{internalRoute}' dentro del VPK.");
			EmitSignal(SignalName.LoadFinished);
			return null;
		}

		GD.Print($"Modelo encontrado: {entry.DirectoryName}/{entry.FileName}.{entry.TypeName}");

		byte[] glbBytes = null;
		Dictionary<string, List<string>> meshMaterialMap = null;

		// Run heavy extraction and GLTF export in background thread
		await Task.Run(() => 
		{
			// 1. Extraemos y parseamos el recurso de Source 2
			package.ReadEntry(entry, out byte[] resourceData);
			using var resource = new ValveResourceFormat.Resource();
			using var stream = new MemoryStream(resourceData);
			resource.Read(stream);

			// 2. Extraemos el mapa mesh → materiales desde los draw calls del VMDL
			meshMaterialMap = BuildMeshMaterialMap(resource);

			// 3. Exportamos a glTF 2.0 (GLB binario) sin pistas continuas pesadas (Zero-RAM Pose Mode)
			GD.Print("Generando GLB con VRF (Zero-RAM Pose Mode)...");

			var fileLoader = new GameFileLoader(package, entry.DirectoryName);
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
			package.ReadEntry(entry, out byte[] modelData);
			using var modelRes = new ValveResourceFormat.Resource();
			using var ms = new MemoryStream(modelData);
			modelRes.Read(ms);
			var vrfModel = modelRes.DataBlock as Model;

			InstantiateInScene(glbBytes, heroKey, package, meshMaterialMap, vrfModel, entry.DirectoryName);
		}

		EmitSignal(SignalName.LoadFinished);
		return _currentHeroNode;
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

	private void InstantiateInScene(byte[] glbBytes, string hero, Package package, Dictionary<string, List<string>> meshMaterialMap, Model vrfModel, string vmdlDirectory)
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
			ApplyMaterialsRecursively(modelScene, hero, package, meshMaterialMap);

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
	/// VRF prefija los nombres de malla con "." al generar el GLB,
	/// por lo que quitamos ese punto para buscar en el mapa.
	/// </summary>
	private void ApplyMaterialsRecursively(Node node, string heroName, Package package, Dictionary<string, List<string>> meshMaterialMap)
	{
		if (node is MeshInstance3D meshInstance && meshInstance.Mesh != null)
		{
			// VRF nombra los nodos como ".body", ".head", etc.
			// Godot convierte el "." a "_" en nombres de nodo, así que quitamos ambos
			string meshName = meshInstance.Name.ToString().TrimStart('.', '_');

			// Viscous's bodyoutline or generic outline submeshes: hide by default so they do not occlude the hero body
			if (meshName.Equals("bodyoutline", StringComparison.OrdinalIgnoreCase) ||
				meshName.Equals("outline", StringComparison.OrdinalIgnoreCase))
			{
				meshInstance.Visible = false;
			}

			// Billy / punkgoat: Ensure main model and primary jitter meshes are visible
			if (heroName.Contains("punkgoat", StringComparison.OrdinalIgnoreCase) || heroName.Contains("billy", StringComparison.OrdinalIgnoreCase))
			{
				if (meshName.Contains("jitter02", StringComparison.OrdinalIgnoreCase))
				{
					meshInstance.Visible = false;
				}
				else if (meshName.Contains("jitter", StringComparison.OrdinalIgnoreCase) || meshName.Equals("punkgoat_model", StringComparison.OrdinalIgnoreCase))
				{
					meshInstance.Visible = true;
				}
			}
			else if (meshName.Contains("jitter", StringComparison.OrdinalIgnoreCase))
			{
				meshInstance.Visible = true;
			}

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

			if (materialPaths != null)
			{
				var mesh = meshInstance.Mesh;
				int surfaceCount = mesh.GetSurfaceCount();

				// If this submesh is a single-surface shell/fin (e.g. ghost_shawl_fur01 to fur05)
				// matched against a multi-draw-call parent mesh (e.g. ghost_shawl with 6 draw calls),
				// resolve the specific shell index so fur01 receives materialPaths[1], fur02 receives materialPaths[2], etc.
				int targetMaterialIndex = 0;
				if (surfaceCount == 1 && materialPaths.Count > 1)
				{
					var match = System.Text.RegularExpressions.Regex.Match(meshName, @"(?:fur|layer|shell|sub|part)?0*(\d+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
					if (match.Success && int.TryParse(match.Groups[1].Value, out int shellNum))
					{
						if (shellNum < materialPaths.Count)
						{
							targetMaterialIndex = shellNum;
						}
						else if (meshName.Contains("fur", StringComparison.OrdinalIgnoreCase))
						{
							targetMaterialIndex = Math.Min(1, materialPaths.Count - 1);
						}
					}
					else if (meshName.Contains("fur", StringComparison.OrdinalIgnoreCase))
					{
						targetMaterialIndex = Math.Min(1, materialPaths.Count - 1);
					}
				}

				for (int i = 0; i < surfaceCount; i++)
				{
					int matIdx = targetMaterialIndex + i;
					if (matIdx >= materialPaths.Count) matIdx = materialPaths.Count - 1;
					string vmatPath = materialPaths[matIdx];

					Godot.Material mat = null;

					// Fallback: If this is a fur submesh but resolved material is still the base cloth (e.g. ghost_shawl.vmat),
					// automatically check for the corresponding fur material (e.g. ghost_shawl_fur.vmat).
					if (meshName.Contains("fur", StringComparison.OrdinalIgnoreCase) && !vmatPath.Contains("fur", StringComparison.OrdinalIgnoreCase))
					{
						string furCandidate = vmatPath.Replace("ghost_shawl", "ghost_shawl_fur")
						                              .Replace("geist_shawl", "geist_shawl_fur");
						if (!furCandidate.Contains("fur"))
						{
							string dir = Path.GetDirectoryName(vmatPath)?.Replace('\\', '/');
							furCandidate = string.IsNullOrEmpty(dir) ? "ghost_shawl_fur.vmat" : $"{dir}/ghost_shawl_fur.vmat";
						}

						mat = Source2MaterialHelper.CreateMaterialFromVmat(package, furCandidate, meshName);
						if (mat != null)
						{
							vmatPath = furCandidate;
						}
					}

					if (mat == null)
					{
						mat = Source2MaterialHelper.CreateMaterialFromVmat(package, vmatPath, meshName);
					}

					if (mat == null && (meshName.Contains("jitter", StringComparison.OrdinalIgnoreCase) || vmatPath.Contains("jitter", StringComparison.OrdinalIgnoreCase)))
					{
						mat = Source2MaterialHelper.CreateMaterialFromVmat(package, "models/heroes_wip/punkgoat/materials/punkgoat_border_jitter01.vmat", meshName);
					}
					if (mat == null && isCardMesh)
					{
						mat = Source2MaterialHelper.CreateMaterialFromVmat(package, "models/heroes_wip/wraith/materials/wraith_cards.vmat", meshName);
					}

					if (mat != null)
					{
						mat.ResourceName = vmatPath;
						HeroMaterialManager.ConfigureMaterial(heroName, meshName, i, vmatPath, mat, package);
						meshInstance.SetSurfaceOverrideMaterial(i, mat);
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
			ApplyMaterialsRecursively(child, heroName, package, meshMaterialMap);
		}
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
