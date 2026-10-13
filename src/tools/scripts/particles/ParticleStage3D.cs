using Godot;
using System;
using System.Collections.Generic;
using DeadlockPlayground.Materials;
using SteamDatabase.ValvePak;
using ValveResourceFormat.IO;

namespace DeadlockPlayground.Particles;

/// <summary>
/// 3D Stage manager for loading, instantiating, controlling, and rendering
/// Source 2 particle systems via deterministic CPU simulation and MultiMesh instances.
/// </summary>
public partial class ParticleStage3D : Node3D
{
    [Signal] public delegate void ParticleLoadedEventHandler(string vpcfPath, string variantName, string abilityName);
    [Signal] public delegate void ParticleUnloadedEventHandler();

    public string ActiveVpcfPath { get; private set; }
    public string ActiveVariantName { get; private set; }
    public string ActiveAbilityName { get; private set; }
    public ParticleSystemDefinition ActiveRootDefinition { get; private set; }

    private CpuParticleSimulator _simulator;
    private readonly List<ParticleMultiMeshRenderer> _renderers = new();
    private readonly List<Node> _spawnedMountNodes = new();
    private readonly Dictionary<string, ImageTexture> _loadedTextures = new(StringComparer.OrdinalIgnoreCase);

    private IFileLoader _currentFileLoader;
    private Package _currentBasePackage;
    private Package _currentAddonPackage;
    private Node3D _currentHeroNode;

    private static Texture2D _fallbackParticleTexture;

    private readonly List<MeshInstance3D> _hiddenHeroMeshes = new();

    public bool HasActiveParticle => !string.IsNullOrEmpty(ActiveVpcfPath) && _simulator != null;
    public float Duration => _simulator?.Duration ?? 3.0f;
    public float CurrentTime => _simulator?.CurrentTime ?? 0.0f;
    public bool IsPlaying => _simulator?.IsPlaying ?? false;

    public bool AutoChargeLoop
    {
        get => _simulator?.AutoChargeLoop ?? false;
        set { if (_simulator != null) _simulator.AutoChargeLoop = value; }
    }

    public float Charge
    {
        get => _simulator?.GetCharge() ?? 0.0f;
        set { if (_simulator != null) _simulator.SetCharge(value); }
    }

    public override void _Ready()
    {
        Name = "ParticleStage3D";
        _simulator = new CpuParticleSimulator();
    }

    public override void _Process(double delta)
    {
        if (!HasActiveParticle) return;

        // 1. Synchronize bone sockets and Control Points with active skeleton animation
        SyncControlPoints();

        // 2. Step deterministic CPU simulator
        _simulator.Update((float)delta);

        // 3. Render all active particle systems via MultiMesh
        Camera3D camera = GetViewport()?.GetCamera3D();
        var cpRegistry = _simulator.ControlPoints;

        foreach (var renderer in _renderers)
        {
            if (IsInstanceValid(renderer))
            {
                renderer.Render(camera, renderer.Instance.ControlPoints ?? cpRegistry);
            }
        }
    }

    /// <summary>
    /// Loads and mounts a particle system hierarchy in the 3D scene using Plan v2 CPU simulator.
    /// </summary>
    public void LoadParticle(
        string vpcfPath,
        string variantName,
        string abilityName,
        Node3D heroNode,
        IFileLoader fileLoader,
        Package basePackage = null,
        Package addonPackage = null)
    {
        // 1. Clean up existing active particle
        UnloadActiveParticle();

        if (string.IsNullOrEmpty(vpcfPath)) return;

        ActiveVpcfPath = vpcfPath;
        ActiveVariantName = variantName;
        ActiveAbilityName = abilityName;
        _currentFileLoader = fileLoader;
        _currentBasePackage = basePackage;
        _currentAddonPackage = addonPackage;
        _currentHeroNode = heroNode;
        HideHeroMeshes(heroNode);

        GD.Print($"[ParticleStage3D] Loading deterministic CPU particle hierarchy for '{abilityName} - {variantName}' ({vpcfPath})...");

        // 2. Load strongly typed ParticleSystemDefinition hierarchy
        ActiveRootDefinition = ParticleHierarchyDefinitionLoader.LoadHierarchy(
            vpcfPath,
            fileLoader,
            basePackage,
            addonPackage,
            maxDepth: 4);

        if (ActiveRootDefinition == null)
        {
            GD.PrintErr($"[ParticleStage3D] Failed to load particle definition hierarchy for {vpcfPath}");
            return;
        }

        // 3. Build simulator instance hierarchy
        _simulator = new CpuParticleSimulator();
        var flattened = ParticleHierarchyDefinitionLoader.FlattenHierarchy(ActiveRootDefinition);

        uint seedCounter = 1337;
        foreach (var item in flattened)
        {
            var inst = new ParticleSystemInstance(item.Def, seedCounter++)
            {
                Delay = item.Delay,
                IsEndCap = item.IsEndCap
            };
            inst.ControlPoints = new ControlPointRegistry();
            inst.ControlPoints.CopyFrom(_simulator.ControlPoints);
            _simulator.AddInstance(inst);

            // 4. Instantiate a MultiMesh renderer for each renderer definition in the system
            if (item.Def.Renderers.Count == 0)
            {
                // Orchestrator / child-only system (e.g. root time_wall_charge, light): do not instantiate fallback renderers
                continue;
            }

            for (int rIdx = 0; rIdx < item.Def.Renderers.Count; rIdx++)
                {
                    var rDef = item.Def.Renderers[rIdx];
                    Texture2D tex = null;
                    Vector4[] seqRects = null;

                    string texPath = rDef.PrimaryTexturePath;
                    if (!string.IsNullOrEmpty(texPath))
                    {
                        tex = LoadOrGetTexture(texPath, fileLoader, basePackage, addonPackage);
                        seqRects = Source2TextureLoader.ExtractSpriteSheetSequences(texPath, fileLoader, basePackage, addonPackage);
                    }

                    if (tex == null)
                    {
                        tex = GetFallbackParticleTexture();
                    }

                    Texture2D noiseTex = null;
                    if (rDef is RenderModelsDef)
                    {
                        noiseTex = tex;
                    }

                    var renderer = new ParticleMultiMeshRenderer(inst, rDef, tex, seqRects, noiseTex);
                    renderer.Name = $"Renderer_{inst.Definition.VpcfPath.GetFile().GetBaseName()}_{rIdx}_{rDef.ClassName}";
                    AddChild(renderer);
                    _renderers.Add(renderer);
                }
        }

        float calculatedDuration = ParticleHierarchyDefinitionLoader.CalculateDuration(ActiveRootDefinition);
        _simulator.SetDuration(calculatedDuration);
        GD.Print($"[ParticleStage3D] Dynamic system duration calculated: {calculatedDuration:F2}s");

        _simulator.Reset();
        _simulator.Play();

        // 6. Emit loaded signal
        EmitSignal(SignalName.ParticleLoaded, ActiveVpcfPath, ActiveVariantName, ActiveAbilityName);
    }

    private void SyncControlPoints()
    {
        if (_simulator == null) return;

        // CP0: Socket attachment from hero skeleton or origin
        Vector3 cp0Pos = Vector3.Zero;
        Quaternion cp0Rot = Quaternion.Identity;
        Skeleton3D skeleton = null;

        if (_currentHeroNode != null)
        {
            skeleton = SearchSkeleton(_currentHeroNode);
            if (skeleton != null)
            {
                string attName = "hourglass_fx";
                if (ActiveRootDefinition != null && ActiveRootDefinition.ControlPointDrivers.Count > 0)
                {
                    var cp0Driver = ActiveRootDefinition.ControlPointDrivers.Find(d => d.ControlPointNumber == 0);
                    if (cp0Driver != null && !string.IsNullOrEmpty(cp0Driver.AttachmentName))
                    {
                        attName = cp0Driver.AttachmentName;
                    }
                }

                int boneIdx = ResolveBoneIndex(skeleton, attName);
                if (boneIdx != -1)
                {
                    var bonePose = skeleton.GetBoneGlobalPose(boneIdx);
                    cp0Pos = skeleton.ToGlobal(bonePose.Origin);
                    cp0Rot = (skeleton.GlobalTransform.Basis * bonePose.Basis).Orthonormalized().GetRotationQuaternion();
                }
                else
                {
                    cp0Pos = _currentHeroNode.GlobalPosition + new Vector3(0, 1.288f, 0);
                    cp0Rot = _currentHeroNode.GlobalTransform.Basis.Orthonormalized().GetRotationQuaternion();
                }
            }
            else
            {
                cp0Pos = _currentHeroNode.GlobalPosition + new Vector3(0, 1.288f, 0);
                cp0Rot = _currentHeroNode.GlobalTransform.Basis.Orthonormalized().GetRotationQuaternion();
            }
        }
        else
        {
            // P1-3: Fall back to preview torso height (1.288 m) when no hero is loaded
            cp0Pos = new Vector3(0, 1.288f, 0);
        }

        _simulator.ControlPoints.SetPosition(0, cp0Pos);
        _simulator.ControlPoints.SetOrientation(0, cp0Rot);

        // CP10: Target / torso height (1.288 m relative to hero origin)
        Vector3 cp10Pos;
        if (_currentHeroNode != null)
        {
            cp10Pos = _currentHeroNode.GlobalPosition + new Vector3(0, 1.288f, 0);
        }
        else
        {
            cp10Pos = new Vector3(0, 1.288f, 0);
        }
        _simulator.ControlPoints.SetPosition(10, cp10Pos);
        _simulator.ControlPoints.SetOrientation(10, cp0Rot);

        // Section A4: Sync each child system's own ControlPointRegistry
        foreach (var inst in _simulator.Instances)
        {
            inst.ControlPoints.SetValue(1, _simulator.ControlPoints.GetValue(1));
            inst.ControlPoints.SetPosition(10, cp10Pos);
            inst.ControlPoints.SetOrientation(10, cp0Rot);

            // Check if child definition specifies its own CP0 driver (e.g. streaks -> muzzle_fx)
            string childAttName = null;
            var cp0Drv = inst.Definition.ControlPointDrivers.Find(d => d.ControlPointNumber == 0);
            if (cp0Drv != null && !string.IsNullOrEmpty(cp0Drv.AttachmentName))
            {
                childAttName = cp0Drv.AttachmentName;
            }

            if (!string.IsNullOrEmpty(childAttName) && skeleton != null)
            {
                int boneIdx = ResolveBoneIndex(skeleton, childAttName);
                if (boneIdx != -1)
                {
                    var bonePose = skeleton.GetBoneGlobalPose(boneIdx);
                    Vector3 childPos = skeleton.ToGlobal(bonePose.Origin);
                    Quaternion childRot = (skeleton.GlobalTransform.Basis * bonePose.Basis).Orthonormalized().GetRotationQuaternion();
                    inst.ControlPoints.SetPosition(0, childPos);
                    inst.ControlPoints.SetOrientation(0, childRot);
                    continue;
                }
            }

            // Fall back to inherited CP0
            inst.ControlPoints.SetPosition(0, cp0Pos);
            inst.ControlPoints.SetOrientation(0, cp0Rot);
        }
    }

    private static int ResolveBoneIndex(Skeleton3D skeleton, string attachmentOrBoneName)
    {
        if (skeleton == null || string.IsNullOrWhiteSpace(attachmentOrBoneName)) return -1;

        int idx = skeleton.FindBone(attachmentOrBoneName);
        if (idx != -1) return idx;

        string clean = attachmentOrBoneName.Trim().ToLowerInvariant();
        string[] candidates = clean switch
        {
            "attach_hitloc" or "hitloc" => new[] { "spine_2", "spine_1", "chest", "pelvis" },
            "attach_attack" or "attach_hand_r" or "hand_r" or "muzzle_fx" => new[] { "weapon_hand_R", "weapon_hand_r", "hand_R", "hand_r", "hold_R" },
            "attach_hand_l" or "hand_l" => new[] { "weapon_hand_L", "weapon_hand_l", "hand_L", "hand_l", "hold_L" },
            "attach_head" or "head" => new[] { "head", "neck_0" },
            "attach_root" or "root" => new[] { "pelvis", "root" },
            "hourglass_fx" or "hourglass" => new[] { "backHourGlass_0", "backHourGlass_1", "hourglass" },
            _ => Array.Empty<string>()
        };

        foreach (var c in candidates)
        {
            int found = skeleton.FindBone(c);
            if (found != -1) return found;

            int total = skeleton.GetBoneCount();
            for (int i = 0; i < total; i++)
            {
                if (string.Equals(skeleton.GetBoneName(i), c, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
        }

        string stripped = clean.Replace("_fx", "").Replace("attach_", "").Trim();
        if (stripped.Length >= 4)
        {
            int total = skeleton.GetBoneCount();
            for (int i = 0; i < total; i++)
            {
                string bName = skeleton.GetBoneName(i);
                if (bName.Contains(stripped, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
        }

        return -1;
    }

    private static Skeleton3D SearchSkeleton(Node node)
    {
        if (node is Skeleton3D sk) return sk;
        foreach (Node child in node.GetChildren())
        {
            var res = SearchSkeleton(child);
            if (res != null) return res;
        }
        return null;
    }

    public static Texture2D GetFallbackParticleTexture()
    {
        if (_fallbackParticleTexture != null) return _fallbackParticleTexture;

        var grad = new Gradient();
        grad.Colors = new[] { Colors.White, new Color(1, 1, 1, 0) };
        grad.Offsets = new[] { 0.0f, 1.0f };

        var tex = new GradientTexture2D
        {
            Gradient = grad,
            Fill = GradientTexture2D.FillEnum.Radial,
            FillFrom = new Vector2(0.5f, 0.5f),
            FillTo = new Vector2(0.5f, 0.0f),
            Width = 64,
            Height = 64
        };
        _fallbackParticleTexture = tex;
        return tex;
    }

    private ImageTexture LoadOrGetTexture(string vtexPath, IFileLoader fileLoader, Package basePackage, Package addonPackage)
    {
        if (string.IsNullOrEmpty(vtexPath)) return null;

        if (_loadedTextures.TryGetValue(vtexPath, out var cachedTex))
        {
            return cachedTex;
        }

        ImageTexture tex = null;
        if (fileLoader != null)
        {
            tex = Source2TextureLoader.ExtractVtexToGodot(fileLoader, vtexPath, maxDimension: 512, forceOpaque: false);
        }

        if (tex == null && (basePackage != null || addonPackage != null))
        {
            tex = Source2TextureLoader.ExtractVtexToGodot(basePackage, vtexPath, maxDimension: 512, forceOpaque: false, addonPackage: addonPackage);
        }

        if (tex != null)
        {
            _loadedTextures[vtexPath] = tex;
            return tex;
        }

        return null;
    }

    public void UpdateParticleTexture(string vtexPath, Image newImage)
    {
        if (string.IsNullOrEmpty(vtexPath) || newImage == null) return;

        if (_loadedTextures.TryGetValue(vtexPath, out var tex) && tex != null)
        {
            tex.Update(newImage);
        }
        else
        {
            var newTex = ImageTexture.CreateFromImage(newImage);
            _loadedTextures[vtexPath] = newTex;
            foreach (var renderer in _renderers)
            {
                if (IsInstanceValid(renderer))
                {
                    renderer.SetTexture(newTex);
                }
            }
        }
    }

    public IReadOnlyDictionary<string, ImageTexture> GetActiveTextures()
    {
        return _loadedTextures;
    }

    public IReadOnlyList<ParticleMultiMeshRenderer> Renderers => _renderers;

    // --- Deterministic Playback API ---

    public void Play()
    {
        _simulator?.Play();
    }

    public void Pause()
    {
        _simulator?.Pause();
    }

    public void Stop()
    {
        _simulator?.Stop();
        RenderImmediate();
    }

    public void Reset()
    {
        _simulator?.Reset();
        RenderImmediate();
    }

    public void Seek(float targetTime)
    {
        _simulator?.Seek(targetTime);
        RenderImmediate();
    }

    public void SetLooping(bool looping)
    {
        if (_simulator != null)
        {
            _simulator.IsLooping = looping;
        }
    }

    private void RenderImmediate()
    {
        Camera3D camera = GetViewport()?.GetCamera3D();
        var cpRegistry = _simulator?.ControlPoints;
        foreach (var renderer in _renderers)
        {
            if (IsInstanceValid(renderer))
            {
                renderer.Render(camera, renderer.Instance.ControlPoints ?? cpRegistry);
            }
        }
    }

    public void SetSpeed(float speed)
    {
        if (_simulator != null)
        {
            _simulator.SpeedScale = speed;
        }
    }

    public void TriggerRelease()
    {
        _simulator?.TriggerRelease();
    }

    public void SetCharge(float charge01)
    {
        if (_simulator != null)
        {
            _simulator.SetCharge(charge01);
        }
    }

    public void SetAutoChargeLoop(bool enabled)
    {
        if (_simulator != null)
        {
            _simulator.AutoChargeLoop = enabled;
        }
    }

    public void UnloadActiveParticle()
    {
        if (!HasActiveParticle) return;

        GD.Print($"[ParticleStage3D] Unloading deterministic particle: {ActiveVpcfPath}");

        foreach (var renderer in _renderers)
        {
            if (IsInstanceValid(renderer))
            {
                renderer.QueueFree();
            }
        }
        _renderers.Clear();

        foreach (var mount in _spawnedMountNodes)
        {
            if (IsInstanceValid(mount))
            {
                mount.QueueFree();
            }
        }
        _spawnedMountNodes.Clear();

        _simulator?.ClearInstances();
        _loadedTextures.Clear();

        ActiveVpcfPath = null;
        ActiveVariantName = null;
        ActiveAbilityName = null;
        ActiveRootDefinition = null;

        var badge = GetTree()?.Root?.FindChild("ActiveParticleBadge", true, false) as DeadlockPlayground.UI.ActiveParticleBadgeUI;
        badge?.HideBadge();

        RestoreHeroMeshes();

        EmitSignal(SignalName.ParticleUnloaded);
    }

    private void HideHeroMeshes(Node heroNode)
    {
        _hiddenHeroMeshes.Clear();
        if (heroNode == null) return;

        void CollectMeshes(Node parent)
        {
            if (parent is MeshInstance3D mesh && mesh.Visible)
            {
                mesh.Visible = false;
                _hiddenHeroMeshes.Add(mesh);
            }
            foreach (Node child in parent.GetChildren())
            {
                CollectMeshes(child);
            }
        }

        CollectMeshes(heroNode);
    }

    private void RestoreHeroMeshes()
    {
        foreach (var mesh in _hiddenHeroMeshes)
        {
            if (IsInstanceValid(mesh))
            {
                mesh.Visible = true;
            }
        }
        _hiddenHeroMeshes.Clear();
    }
}
