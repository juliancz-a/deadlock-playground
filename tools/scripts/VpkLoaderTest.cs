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

public partial class VpkLoaderTest : Node3D
{
    [Signal]
    public delegate void LoadStartedEventHandler();
    [Signal]
    public delegate void LoadFinishedEventHandler();

	[Export]
	public PackedScene PoseEditorScene;
	
	[Export]
	public string VpkPath = @"E:\SteamLibrary\steamapps\common\Deadlock\game\citadel\pak01_dir.vpk";


	[Export]
	public string HeroName = "viper";
	public string HeroModelName = "viper";

    private Node3D _currentHeroNode;
    private PoseEditorUI _currentPoseEditor;

	public override void _Ready()
	{
		// Don't auto-load, wait for UI to trigger
	}

	public async Task LoadHeroAsync(string hero, string model)
	{
        EmitSignal(SignalName.LoadStarted);

        // 1. Cleanup old hero completely
        CleanupCurrentHero();

		if (!File.Exists(VpkPath))
		{
			GD.PrintErr($"No se encontró el archivo VPK en: {VpkPath}");
            EmitSignal(SignalName.LoadFinished);
			return;
		}

		GD.Print($"Abriendo VPK e indexando archivos...");
		using var package = new Package();
		package.Read(VpkPath);

		// Ruta interna dentro del VPK según la estructura de Deadlock
		// Nota: en el índice de VRF las extensiones no llevan el punto en TypeName
		string internal_route = $"models/heroes_staging/{hero}/{model}.vmdl_c";
		
		var entry = package.FindEntry(internal_route);

		// Fallback por si la convención de nombres difiere levemente
		if (entry == null)
		{
			GD.Print($"Ruta exacta no encontrada ({internal_route}), buscando coincidencia parcial...");
			if (package.Entries.TryGetValue("vmdl_c", out var modelEntries))
			{
				entry = modelEntries.Find(e => 
					e.DirectoryName.Contains($"heroes_staging/{hero}") && 
					e.FileName.Contains(hero));
			}
		}

		if (entry == null)
		{
			GD.PrintErr($"No se encontró el modelo para '{hero}' dentro del VPK.");
            EmitSignal(SignalName.LoadFinished);
			return;
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

		    // 3. Exportamos a glTF 2.0 (GLB binario)
		    GD.Print("Generando GLB con VRF...");

		    var fileLoader = new GameFileLoader(package, internal_route);
		    var exporter = new GltfModelExporter(fileLoader)
		    {
			    ExportAnimations = true,
			    AdaptTextures = true,
			    ExportMaterials = false,
			    ExportExtras = false,
			    ProgressReporter = new Progress<string>(msg => GD.Print($"[VRF] {msg}"))
		    };

		    exporter.AnimationFilter.Clear();
            if (resource.DataBlock is Model vrfModel)
            {
                // Retrieve all animations using VRF API
                var allAnims = vrfModel.GetAllAnimations(fileLoader);
                foreach (var anim in allAnims)
                {
                    string lowerName = anim.Name.ToLower();
                    
                    bool isImportant = lowerName.EndsWith("stand_idle") || 
                                       lowerName.EndsWith("run_center") || 
                                       lowerName.EndsWith("walk_center") || 
                                       lowerName == "shoot_idle" || 
                                       lowerName == "idle_loadout" || 
                                       lowerName == "primary_shoot" ||
                                       lowerName == "out_of_combat_stand_idle";
                    
                    // Exclude crouch poses to keep it small, or keep them if needed. 
                    // By checking EndsWith "stand_idle", we automatically avoid crouch_idle!
                    
                    if (isImportant && !lowerName.Contains("zoomed") && !lowerName.Contains("aim"))
                    {
                        exporter.AnimationFilter.Add(anim.Name);
                    }
                }
            }

			GD.Print($"Cantidad de animaciones que cumplen el filtro: {exporter.AnimationFilter.Count}");
			foreach(var animation in exporter.AnimationFilter)
			{
				GD.Print($"Exportando animación: {animation}");
			}
			
		    string tempGlbPath = Path.Combine(Path.GetTempPath(), $"{hero}_{Guid.NewGuid():N}.glb");
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
		    
            // Marshal back to Main Thread safely for node instantiation
            Callable.From(() => {
                InstantiateInScene(glbBytes, hero, package, meshMaterialMap);
                EmitSignal(SignalName.LoadFinished);
            }).CallDeferred();
        }
        else
        {
            EmitSignal(SignalName.LoadFinished);
        }
	}

    private void CleanupCurrentHero()
    {
        if (_currentPoseEditor != null)
        {
            _currentPoseEditor.QueueFree();
            _currentPoseEditor = null;
        }

        if (_currentHeroNode != null)
        {
            DisposeNodeRecursively(_currentHeroNode);
            _currentHeroNode.GetParent()?.RemoveChild(_currentHeroNode);
            _currentHeroNode.QueueFree();
            _currentHeroNode = null;
        }

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
                var mat = mi.Mesh.SurfaceGetMaterial(i);
                if (mat != null)
                {
                    if (mat is StandardMaterial3D stMat && stMat.AlbedoTexture != null)
                    {
                        stMat.AlbedoTexture.Dispose();
                    }
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
		var map = new Dictionary<string, List<string>>();

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

	private void InstantiateInScene(byte[] glbBytes, string hero, Package package, Dictionary<string, List<string>> meshMaterialMap)
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
		ApplyMaterialsRecursively(modelScene, package, meshMaterialMap);

		// Buscamos si generó el Skeleton3D para confirmar que vino riggeado
		var skeleton = SearchSkeleton(modelScene);
		var animPlayer = SearchAnimationPlayer(modelScene);
		if (skeleton != null)
		{
			GD.Print($"Skeleton detectado con éxito: {skeleton.GetBoneCount()} huesos encontrados.");
			
			// Instanciar y configurar la UI del Pose Editor
			PoseEditorUI poseEditor = null;
			if (PoseEditorScene != null)
			{
				var instancedNode = PoseEditorScene.Instantiate();
				poseEditor = instancedNode as PoseEditorUI;
				
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
				}
				else
				{
					GD.PrintErr("ERROR: The PoseEditorScene does not have the 'PoseEditorUI.cs' script attached to its root node!");
					instancedNode.QueueFree();
				}
			}
			else
			{
				GD.PrintErr("PoseEditorScene not set. Cannot instance UI.");
			}
			
			// Setup SkeletonGizmoManager
			var gizmoManager = new SkeletonGizmoManager();
			gizmoManager.Name = "SkeletonGizmoManager";
			gizmoManager.TargetSkeleton = skeleton;
			gizmoManager.UIManager = poseEditor;
			skeleton.AddChild(gizmoManager);
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
	private void ApplyMaterialsRecursively(Node node, Package package, Dictionary<string, List<string>> meshMaterialMap)
	{
		if (node is MeshInstance3D meshInstance && meshInstance.Mesh != null)
		{
			// VRF nombra los nodos como ".body", ".head", etc.
			// Godot convierte el "." a "_" en nombres de nodo, así que quitamos ambos
			string meshName = meshInstance.Name.ToString().TrimStart('.', '_');

			if (meshMaterialMap.TryGetValue(meshName, out var materialPaths))
			{
				var mesh = meshInstance.Mesh;
				int surfaceCount = mesh.GetSurfaceCount();

				for (int i = 0; i < surfaceCount && i < materialPaths.Count; i++)
				{
					// La ruta viene como "models/.../material.vmat", necesitamos buscar "material.vmat_c"
					string vmatPath = materialPaths[i];

					var mat = Source2MaterialHelper.CreateMaterialFromVmat(package, vmatPath);
					if (mat != null)
					{
						meshInstance.SetSurfaceOverrideMaterial(i, mat);
						GD.Print($"  ✓ Material aplicado: '{Path.GetFileNameWithoutExtension(vmatPath)}' → {meshName}[{i}]");
					}
					else
					{
						GD.PrintErr($"  ✗ No se pudo crear material: '{vmatPath}' para {meshName}[{i}]");
					}
				}

				if (surfaceCount != materialPaths.Count)
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
			ApplyMaterialsRecursively(child, package, meshMaterialMap);
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
