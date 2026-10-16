using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DeadlockPlayground.Materials;
using DeadlockPlayground.Particles;
using DeadlockPlayground.Particles.Models;
using SteamDatabase.ValvePak;
using ValveResourceFormat.IO;

namespace DeadlockPlayground.UI;

/// <summary>
/// Modal dialog for browsing, searching, and selecting hero particle effects.
/// Features tree navigation, visual card gallery, and background thumbnail generation.
/// </summary>
public partial class ParticlePickerModalUI : PanelContainer
{
    [Signal] public delegate void ParticleSelectedEventHandler(string vpcfPath, string variantName, string abilityKey);
    [Signal] public delegate void ModalClosedEventHandler();

    // UI Nodes
    private Label _lblTitle;
    private LineEdit _searchBar;
    private Button _btnClose;
    private Tree _abilityTree;
    private HFlowContainer _cardFlowContainer;
    private ScrollContainer _cardScrollContainer;
    private Label _lblSelectedDetails;
    private Button _btnLoadSelected;
    private Button _btnCancel;

    // Data
    private string _currentHeroName = "Dynamo";
    private IReadOnlyList<AbilityParticleGroup> _abilityGroups = Array.Empty<AbilityParticleGroup>();
    private readonly List<ParticleCardUI> _spawnedCards = new();
    private ParticleCardUI _activeSelectedCard = null;

    // Assets
    private Texture2D _iconSparkles;
    private Texture2D _iconFolder;

    // Thumbnail Caching Subsystem
    private static readonly Dictionary<string, Texture2D> _thumbnailCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Texture2D> _thumbTextureCache = new(StringComparer.OrdinalIgnoreCase);

    // Thumbnail Generation Subsystem
    private SubViewport _thumbViewport;
    private Camera3D _thumbCamera;
    private readonly Queue<ParticleCardUI> _thumbnailQueue = new();
    private bool _isGeneratingThumbnail = false;

    public override void _Ready()
    {
        _iconSparkles = GD.Load<Texture2D>("res://assets/icons/sparkles.svg");
        _iconFolder = GD.Load<Texture2D>("res://assets/at-icons/folder.svg");

        BuildModalLayout();
        SetupThumbnailViewport();
        ConnectEvents();
    }

    private void BuildModalLayout()
    {
        CustomMinimumSize = new Vector2(980, 640);
        CenterModal();

        var mainStyle = new StyleBoxFlat
        {
            BgColor = new Color(0.10f, 0.12f, 0.15f, 0.98f),
            CornerRadiusTopLeft = 8,
            CornerRadiusTopRight = 8,
            CornerRadiusBottomLeft = 8,
            CornerRadiusBottomRight = 8,
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            BorderWidthTop = 1,
            BorderWidthBottom = 1,
            BorderColor = new Color(0.24f, 0.30f, 0.38f, 1.0f),
            ContentMarginLeft = 16,
            ContentMarginRight = 16,
            ContentMarginTop = 14,
            ContentMarginBottom = 14
        };
        AddThemeStyleboxOverride("panel", mainStyle);

        var rootVbox = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.Fill,
            SizeFlagsVertical = SizeFlags.Fill
        };
        rootVbox.AddThemeConstantOverride("separation", 10);
        AddChild(rootVbox);

        // --- 1. Header Bar ---
        var header = new HBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.Fill
        };
        header.AddThemeConstantOverride("separation", 12);
        rootVbox.AddChild(header);

        var headerIcon = new TextureRect
        {
            Texture = _iconSparkles,
            CustomMinimumSize = new Vector2(22, 22),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            Modulate = new Color(0.95f, 0.85f, 0.40f, 1.0f)
        };
        header.AddChild(headerIcon);

        _lblTitle = new Label
        {
            Text = "Hero Particles",
            SizeFlagsHorizontal = SizeFlags.ShrinkBegin
        };
        _lblTitle.AddThemeFontSizeOverride("font_size", 16);
        _lblTitle.AddThemeColorOverride("font_color", Colors.White);
        header.AddChild(_lblTitle);

        // Search bar
        _searchBar = new LineEdit
        {
            PlaceholderText = "Search abilities, effects, or .vpcf paths...",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(260, 32),
            ClearButtonEnabled = true
        };
        header.AddChild(_searchBar);

        // Close button
        _btnClose = new Button
        {
            Text = "✕",
            CustomMinimumSize = new Vector2(32, 32)
        };
        header.AddChild(_btnClose);

        // Separator
        rootVbox.AddChild(new HSeparator());

        // --- 2. Content Split (Tree vs Cards) ---
        var split = new HSplitContainer
        {
            SizeFlagsHorizontal = SizeFlags.Fill,
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        split.SplitOffsets = new[] { 260 };
        rootVbox.AddChild(split);

        // Left Panel (Tree)
        var leftVbox = new VBoxContainer
        {
            CustomMinimumSize = new Vector2(240, 0),
            SizeFlagsVertical = SizeFlags.Fill
        };
        leftVbox.AddThemeConstantOverride("separation", 6);
        split.AddChild(leftVbox);

        var colus = PlaygroundThemeHelper.GetFontColus();

        var lblTreeHeader = new Label
        {
            Text = "Abilities & Sets",
            SizeFlagsHorizontal = SizeFlags.Fill
        };
        if (colus != null) lblTreeHeader.AddThemeFontOverride("font", colus);
        lblTreeHeader.AddThemeFontSizeOverride("font_size", 14);
        lblTreeHeader.AddThemeColorOverride("font_color", new Color(0.92f, 0.94f, 0.98f, 1.0f));
        leftVbox.AddChild(lblTreeHeader);

        _abilityTree = new Tree
        {
            SizeFlagsHorizontal = SizeFlags.Fill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HideRoot = true,
            SelectMode = Tree.SelectModeEnum.Row
        };
        leftVbox.AddChild(_abilityTree);

        // Right Panel (Gallery)
        var rightVbox = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.Fill
        };
        rightVbox.AddThemeConstantOverride("separation", 8);
        split.AddChild(rightVbox);

        _cardScrollContainer = new ScrollContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
        };
        rightVbox.AddChild(_cardScrollContainer);

        _cardFlowContainer = new HFlowContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        _cardFlowContainer.AddThemeConstantOverride("h_separation", 12);
        _cardFlowContainer.AddThemeConstantOverride("v_separation", 12);
        _cardScrollContainer.AddChild(_cardFlowContainer);

        // Separator
        rootVbox.AddChild(new HSeparator());

        // --- 3. Bottom Footer ---
        var footer = new HBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.Fill
        };
        footer.AddThemeConstantOverride("separation", 12);
        rootVbox.AddChild(footer);

        _lblSelectedDetails = new Label
        {
            Text = "Select a particle effect to load...",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            MaxLinesVisible = 2
        };
        _lblSelectedDetails.AddThemeColorOverride("font_color", new Color(0.82f, 0.85f, 0.90f, 1.0f));
        _lblSelectedDetails.AddThemeFontSizeOverride("font_size", 13);
        footer.AddChild(_lblSelectedDetails);

        _btnCancel = new Button
        {
            Text = "Cancel",
            CustomMinimumSize = new Vector2(90, 32)
        };
        footer.AddChild(_btnCancel);

        _btnLoadSelected = new Button
        {
            Text = "Load Selected",
            Disabled = true,
            CustomMinimumSize = new Vector2(130, 32)
        };
        footer.AddChild(_btnLoadSelected);
    }

    private Node3D _thumbStageRoot;

    private VpkLoaderTest GetVpkLoader()
    {
        return GetNodeOrNull<VpkLoaderTest>("/root/Main/UIRoot/MainHUD/VBoxContainer/MainSplit/ViewportArea/SubViewportContainer/WorldViewport/VpkLoaderTest")
            ?? GetNodeOrNull<VpkLoaderTest>("/root/Main/VpkLoaderTest")
            ?? GetTree()?.Root?.FindChild("VpkLoaderTest", true, false) as VpkLoaderTest;
    }

    private static string GetDiskCachePath(string vpcfPath)
    {
        if (string.IsNullOrEmpty(vpcfPath)) return null;
        string safe = vpcfPath.Replace('\\', '/').TrimStart('/');
        safe = safe.Replace('/', '_').Replace('.', '_') + ".png";
        return ProjectSettings.GlobalizePath($"user://cache/particle_thumbs/{safe}");
    }

    private static bool TryLoadDiskThumbnail(string vpcfPath, out Texture2D texture)
    {
        texture = null;
        string path = GetDiskCachePath(vpcfPath);
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;

        try
        {
            var img = Image.LoadFromFile(path);
            if (img != null && !img.IsEmpty())
            {
                texture = ImageTexture.CreateFromImage(img);
                return true;
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[ParticlePickerModalUI] Error reading disk thumbnail cache for {vpcfPath}: {ex.Message}");
        }
        return false;
    }

    private static void SaveDiskThumbnail(string vpcfPath, Image img)
    {
        if (img == null || img.IsEmpty()) return;
        string path = GetDiskCachePath(vpcfPath);
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            img.SavePng(path);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[ParticlePickerModalUI] Failed to save disk thumbnail cache for {vpcfPath}: {ex.Message}");
        }
    }

    private static Texture2D LoadOrGetThumbTexture(string texPath, IFileLoader fileLoader, Package basePackage, Package addonPackage)
    {
        if (string.IsNullOrEmpty(texPath)) return null;

        if (_thumbTextureCache.TryGetValue(texPath, out var cached))
        {
            return cached;
        }

        ImageTexture tex = null;
        if (fileLoader != null)
        {
            tex = Source2TextureLoader.ExtractVtexToGodot(fileLoader, texPath, maxDimension: 256, forceOpaque: false);
        }
        if (tex == null && (basePackage != null || addonPackage != null))
        {
            tex = Source2TextureLoader.ExtractVtexToGodot(basePackage, texPath, maxDimension: 256, forceOpaque: false, addonPackage: addonPackage);
        }

        if (tex != null)
        {
            _thumbTextureCache[texPath] = tex;
            return tex;
        }

        return null;
    }

    public void CenterModal()
    {
        SetAnchorsPreset(LayoutPreset.Center);
        GrowHorizontal = GrowDirection.Both;
        GrowVertical = GrowDirection.Both;
        float halfW = CustomMinimumSize.X > 0 ? CustomMinimumSize.X / 2f : 490f;
        float halfH = CustomMinimumSize.Y > 0 ? CustomMinimumSize.Y / 2f : 320f;
        OffsetLeft = -halfW;
        OffsetTop = -halfH;
        OffsetRight = halfW;
        OffsetBottom = halfH;
    }

    private void SetupThumbnailViewport()
    {
        string cacheDir = ProjectSettings.GlobalizePath("user://cache/particle_thumbs");
        if (!Directory.Exists(cacheDir))
        {
            Directory.CreateDirectory(cacheDir);
        }

        _thumbViewport = new SubViewport
        {
            Size = new Vector2I(256, 180),
            TransparentBg = false,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
            OwnWorld3D = true
        };

        var world = new World3D();
        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = new Color(0.06f, 0.07f, 0.09f, 1.0f)
        };
        world.Environment = env;
        _thumbViewport.World3D = world;

        _thumbStageRoot = new Node3D { Name = "ThumbStageRoot" };
        _thumbViewport.AddChild(_thumbStageRoot);

        var light = new DirectionalLight3D
        {
            Position = new Vector3(1, 2, 2),
            RotationDegrees = new Vector3(-35, 45, 0),
            LightEnergy = 1.2f
        };
        _thumbStageRoot.AddChild(light);

        _thumbCamera = new Camera3D
        {
            Position = new Vector3(0, 0.5f, 2.0f),
            Fov = 45f,
            Current = true
        };
        _thumbStageRoot.AddChild(_thumbCamera);
        _thumbCamera.LookAtFromPosition(_thumbCamera.Position, Vector3.Zero);

        // Add to tree off-screen
        AddChild(_thumbViewport);
    }

    private void ConnectEvents()
    {
        _btnClose.Pressed += OnCloseRequested;
        _btnCancel.Pressed += OnCloseRequested;
        _btnLoadSelected.Pressed += OnLoadConfirmed;

        _searchBar.TextChanged += OnSearchChanged;
        _abilityTree.ItemSelected += OnTreeSelectionChanged;
    }

    public void OpenForHero(string heroName, IReadOnlyList<AbilityParticleGroup> abilityGroups)
    {
        _currentHeroName = HeroParticleService.ResolveDisplayName(heroName);
        _abilityGroups = abilityGroups ?? Array.Empty<AbilityParticleGroup>();

        _lblTitle.Text = $"Hero Particles — {_currentHeroName}";
        _searchBar.Text = "";
        _activeSelectedCard = null;
        _btnLoadSelected.Disabled = true;
        _lblSelectedDetails.Text = "Select a particle effect to load...";

        CenterModal();
        PopulateTree();
        PopulateCards(null); // Show all initially or first folder

        Visible = true;
        _searchBar.GrabFocus();
    }

    private void PopulateTree()
    {
        _abilityTree.Clear();
        var root = _abilityTree.CreateItem();

        string query = _searchBar?.Text?.Trim() ?? "";

        foreach (var group in _abilityGroups)
        {
            string cleanAbility = CleanAbilityTitle(group.AbilityKey);

            // Check if group or any child matches query
            bool groupMatches = string.IsNullOrEmpty(query) ||
                                cleanAbility.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                group.AbilityKey.Contains(query, StringComparison.OrdinalIgnoreCase);

            var matchingParticles = group.Particles.Where(p =>
                groupMatches ||
                p.VariantName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                p.VpcfPath.Contains(query, StringComparison.OrdinalIgnoreCase)
            ).ToList();

            if (matchingParticles.Count == 0) continue;

            var groupItem = _abilityTree.CreateItem(root);
            groupItem.SetText(0, cleanAbility);
            groupItem.SetIcon(0, _iconFolder);
            groupItem.SetMetadata(0, group.AbilityKey);
            groupItem.Collapsed = !string.IsNullOrEmpty(query) ? false : true;

            foreach (var p in matchingParticles)
            {
                var variantItem = _abilityTree.CreateItem(groupItem);
                variantItem.SetText(0, p.VariantName);
                variantItem.SetIcon(0, _iconSparkles);
                variantItem.SetIconMaxWidth(0, 14);
                variantItem.SetMetadata(0, p.VpcfPath);
            }
        }
    }

    private void PopulateCards(string filterAbilityKey)
    {
        // Cleanup old cards
        foreach (var card in _spawnedCards)
        {
            if (IsInstanceValid(card))
            {
                if (card.GetParent() != null) card.GetParent().RemoveChild(card);
                card.QueueFree();
            }
        }
        _spawnedCards.Clear();
        _thumbnailQueue.Clear();

        string query = _searchBar?.Text?.Trim() ?? "";

        foreach (var group in _abilityGroups)
        {
            if (!string.IsNullOrEmpty(filterAbilityKey) && !group.AbilityKey.Equals(filterAbilityKey, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string cleanAbility = CleanAbilityTitle(group.AbilityKey);

            foreach (var p in group.Particles)
            {
                if (!string.IsNullOrEmpty(query))
                {
                    bool match = cleanAbility.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                 p.VariantName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                 p.VpcfPath.Contains(query, StringComparison.OrdinalIgnoreCase);
                    if (!match) continue;
                }

                var card = new ParticleCardUI();
                card.Setup(p, cleanAbility);
                card.CardSelected += OnCardSelected;
                card.CardDoubleClicked += OnCardDoubleClicked;

                _cardFlowContainer.AddChild(card);
                _spawnedCards.Add(card);

                // Immediate cache check (0ms display for cached thumbnails)
                if (_thumbnailCache.TryGetValue(p.VpcfPath, out var memTex))
                {
                    card.SetThumbnail(memTex);
                }
                else if (TryLoadDiskThumbnail(p.VpcfPath, out var diskTex))
                {
                    _thumbnailCache[p.VpcfPath] = diskTex;
                    card.SetThumbnail(diskTex);
                }
                else
                {
                    // Queue for background thumbnail generation
                    _thumbnailQueue.Enqueue(card);
                }
            }
        }

        // Process next thumbnail
        TriggerNextThumbnail();
    }

    private void TriggerNextThumbnail()
    {
        if (!Visible || !IsInsideTree() || _isGeneratingThumbnail || _thumbnailQueue.Count == 0 || _thumbViewport == null) return;

        _isGeneratingThumbnail = true;
        var card = _thumbnailQueue.Dequeue();

        _ = ProcessThumbnailStepAsync(card);
    }

    private async System.Threading.Tasks.Task ProcessThumbnailStepAsync(ParticleCardUI card)
    {
        if (!Visible || !IsInsideTree() || !IsInstanceValid(card) || card.IsQueuedForDeletion())
        {
            _isGeneratingThumbnail = false;
            if (Visible && IsInsideTree()) TriggerNextThumbnail();
            return;
        }

        string vpcfPath = card.Entry?.VpcfPath;
        if (string.IsNullOrEmpty(vpcfPath))
        {
            _isGeneratingThumbnail = false;
            TriggerNextThumbnail();
            return;
        }

        // Check caches again in case another card or previous pass resolved it
        if (_thumbnailCache.TryGetValue(vpcfPath, out var cachedTex))
        {
            card.SetThumbnail(cachedTex);
            _isGeneratingThumbnail = false;
            TriggerNextThumbnail();
            return;
        }

        if (TryLoadDiskThumbnail(vpcfPath, out var diskTex))
        {
            _thumbnailCache[vpcfPath] = diskTex;
            card.SetThumbnail(diskTex);
            _isGeneratingThumbnail = false;
            TriggerNextThumbnail();
            return;
        }

        var vpkLoader = GetVpkLoader();
        var fileLoader = vpkLoader?.GetOrCreateFileLoader();
        var basePkg = vpkLoader?.GetOrCreateBasePackage();
        Package addonPkg = null;
        if (!string.IsNullOrEmpty(vpkLoader?.AddonVpkPath) && File.Exists(vpkLoader.AddonVpkPath))
        {
            try
            {
                addonPkg = new Package();
                addonPkg.Read(vpkLoader.AddonVpkPath);
            }
            catch { }
        }

        // 1. Load strongly typed particle definition hierarchy (shallow depth 2 is fast & sufficient for thumbnails)
        ParticleSystemDefinition rootDef = null;
        try
        {
            rootDef = ParticleHierarchyDefinitionLoader.LoadHierarchy(
                vpcfPath,
                fileLoader,
                basePkg,
                addonPkg,
                maxDepth: 2);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[ParticlePickerModalUI] Error loading hierarchy for {vpcfPath}: {ex.Message}");
        }

        if (rootDef == null)
        {
            _isGeneratingThumbnail = false;
            TriggerNextThumbnail();
            return;
        }

        var renderers = new List<ParticleMultiMeshRenderer>();
        try
        {
            // 2. Setup deterministic simulator
            var simulator = new CpuParticleSimulator();
            simulator.ControlPoints.SetValue(1, 1.0f); // Fully charged so models & charge-scaled operators are visible
            simulator.ControlPoints.SetPosition(10, new Vector3(0, 1.288f, 0)); // Aim point

            var flattened = ParticleHierarchyDefinitionLoader.FlattenHierarchy(rootDef);
            uint seed = 1337;

            foreach (var item in flattened)
            {
                var inst = new ParticleSystemInstance(item.Def, seed++)
                {
                    Delay = item.Delay,
                    IsEndCap = item.IsEndCap
                };
                inst.ControlPoints = new ControlPointRegistry();
                inst.ControlPoints.CopyFrom(simulator.ControlPoints);
                simulator.AddInstance(inst);

                if (item.Def.Renderers.Count == 0) continue;

                for (int rIdx = 0; rIdx < item.Def.Renderers.Count; rIdx++)
                {
                    var rDef = item.Def.Renderers[rIdx];
                    Texture2D tex = null;
                    Vector4[] seqRects = null;

                    string texPath = rDef.PrimaryTexturePath;
                    if (!string.IsNullOrEmpty(texPath))
                    {
                        tex = LoadOrGetThumbTexture(texPath, fileLoader, basePkg, addonPkg);
                        seqRects = Source2TextureLoader.ExtractSpriteSheetSequences(texPath, fileLoader, basePkg, addonPkg);
                    }
                    tex ??= ParticleStage3D.GetFallbackParticleTexture();

                    Texture2D noiseTex = (rDef is RenderModelsDef) ? tex : null;
                    var renderer = new ParticleMultiMeshRenderer(inst, rDef, tex, seqRects, noiseTex);
                    _thumbStageRoot.AddChild(renderer);
                    renderers.Add(renderer);
                }
            }

            // 3. Step forward to a frozen representative frame (Source2Viewer style)
            simulator.Seek(0.18f);

            // Check if alive particles exist
            int aliveCount = 0;
            foreach (var inst in simulator.Instances)
            {
                aliveCount += inst.ActiveParticleCount;
            }

            // Fallback for EndCap or delayed burst particles (e.g. explosion streaks, shells, warps)
            if (aliveCount == 0)
            {
                simulator.TriggerRelease();
                simulator.Seek(0.32f);

                aliveCount = 0;
                foreach (var inst in simulator.Instances)
                {
                    aliveCount += inst.ActiveParticleCount;
                }

                if (aliveCount == 0)
                {
                    simulator.Seek(0.5f);
                }
            }

            // 4. Compute bounding box of active particles
            Aabb bbox = default;
            bool hasAabb = false;

            foreach (var inst in simulator.Instances)
            {
                var particles = inst.Particles;
                for (int i = 0; i < particles.Length; i++)
                {
                    ref readonly var p = ref particles[i];
                    if (!p.IsAlive) continue;

                    float r = MathF.Max(p.Radius, 0.04f);
                    var pAabb = new Aabb(p.Position - new Vector3(r, r, r), new Vector3(r * 2, r * 2, r * 2));
                    if (!hasAabb)
                    {
                        bbox = pAabb;
                        hasAabb = true;
                    }
                    else
                    {
                        bbox = bbox.Merge(pAabb);
                    }
                }
            }

            if (!hasAabb || bbox.Size.LengthSquared() < 1e-4f)
            {
                bbox = new Aabb(new Vector3(-0.4f, -0.4f, -0.4f), new Vector3(0.8f, 0.8f, 0.8f));
            }

            // 5. Frame camera on bounding box with padding (VRF isometric perspective)
            Vector3 center = bbox.GetCenter();
            float maxDim = MathF.Max(bbox.Size.X, MathF.Max(bbox.Size.Y, bbox.Size.Z));
            maxDim = MathF.Max(maxDim, 0.2f);
            float radius = (maxDim * 0.5f) * 1.4f;

            float fovRad = Mathf.DegToRad(_thumbCamera.Fov);
            float dist = radius / MathF.Sin(fovRad * 0.5f);
            dist = Mathf.Clamp(dist, 0.4f, 40.0f);

            // Elevated 3/4 isometric angle (pitch 20 deg, yaw 45 deg)
            Vector3 camDir = new Vector3(0.55f, 0.35f, 0.75f).Normalized();
            Vector3 camPos = center + camDir * dist;

            _thumbCamera.Position = camPos;
            _thumbCamera.LookAtFromPosition(camPos, center, Vector3.Up);

            // 6. Dispatch MultiMesh renderer buffers
            foreach (var renderer in renderers)
            {
                if (IsInstanceValid(renderer))
                {
                    renderer.Render(_thumbCamera, renderer.Instance.ControlPoints ?? simulator.ControlPoints);
                }
            }

            // 7. Render SubViewport and capture frame
            if (!Visible || !IsInsideTree()) return;

            _thumbViewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (!Visible || !IsInsideTree()) return;

            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            if (!Visible || !IsInsideTree()) return;

            var img = _thumbViewport.GetTexture()?.GetImage();
            _thumbViewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;

            if (img != null && !img.IsEmpty() && IsInstanceValid(card) && !card.IsQueuedForDeletion())
            {
                var tex = ImageTexture.CreateFromImage(img);
                _thumbnailCache[vpcfPath] = tex;
                SaveDiskThumbnail(vpcfPath, img);
                card.SetThumbnail(tex);
            }
        }
        finally
        {
            // Safe lifecycle cleanup: guarantee no leaked MultiMesh nodes
            foreach (var r in renderers)
            {
                if (IsInstanceValid(r))
                {
                    if (r.GetParent() != null) r.GetParent().RemoveChild(r);
                    r.QueueFree();
                }
            }
            addonPkg?.Dispose();
        }

        _isGeneratingThumbnail = false;
        if (Visible && IsInsideTree())
        {
            TriggerNextThumbnail();
        }
    }

    private void OnTreeSelectionChanged()
    {
        var selectedItem = _abilityTree.GetSelected();
        if (selectedItem == null) return;

        string meta = selectedItem.GetMetadata(0).AsString();
        if (meta.EndsWith(".vpcf", StringComparison.OrdinalIgnoreCase))
        {
            // Selected a variant directly
            var card = _spawnedCards.FirstOrDefault(c => c.Entry.VpcfPath == meta);
            if (card != null)
            {
                SelectCard(card);
            }
        }
        else
        {
            // Selected an ability folder
            PopulateCards(meta);
        }
    }

    private void OnSearchChanged(string newText)
    {
        PopulateTree();
        PopulateCards(null);
    }

    private void OnCardSelected(ParticleCardUI card)
    {
        SelectCard(card);
    }

    private void OnCardDoubleClicked(ParticleCardUI card)
    {
        SelectCard(card);
        OnLoadConfirmed();
    }

    private void SelectCard(ParticleCardUI card)
    {
        if (_activeSelectedCard != null && IsInstanceValid(_activeSelectedCard))
        {
            _activeSelectedCard.IsSelected = false;
        }

        _activeSelectedCard = card;
        if (_activeSelectedCard != null)
        {
            _activeSelectedCard.IsSelected = true;
            _btnLoadSelected.Disabled = false;
            _lblSelectedDetails.Text = $"Selected: {_activeSelectedCard.AbilityName} - {_activeSelectedCard.Entry.VariantName}\n({_activeSelectedCard.Entry.VpcfPath})";
        }
        else
        {
            _btnLoadSelected.Disabled = true;
            _lblSelectedDetails.Text = "Select a particle effect to load...";
        }
    }

    private void OnLoadConfirmed()
    {
        if (_activeSelectedCard != null && _activeSelectedCard.Entry != null)
        {
            string path = _activeSelectedCard.Entry.VpcfPath;
            string variant = _activeSelectedCard.Entry.VariantName;
            string ability = _activeSelectedCard.AbilityName;

            EmitSignal(SignalName.ParticleSelected, path, variant, ability);
            OnCloseRequested();
        }
    }

    private void OnCloseRequested()
    {
        Visible = false;
        _thumbnailQueue.Clear();
        _isGeneratingThumbnail = false;
        EmitSignal(SignalName.ModalClosed);
    }

    public static string CleanAbilityTitle(string abilityKey)
    {
        if (string.IsNullOrEmpty(abilityKey)) return "Ability";
        ReadOnlySpan<char> span = abilityKey.AsSpan();

        if (span.StartsWith("citadel_ability_", StringComparison.OrdinalIgnoreCase)) span = span[16..];
        else if (span.StartsWith("citadel_weapon_", StringComparison.OrdinalIgnoreCase)) span = span[15..];
        else if (span.StartsWith("ability_melee_", StringComparison.OrdinalIgnoreCase)) span = span[14..];
        else if (span.StartsWith("ability_", StringComparison.OrdinalIgnoreCase)) span = span[8..];

        if (span.EndsWith("_set", StringComparison.OrdinalIgnoreCase)) span = span[..^4];

        string raw = span.ToString().Replace('_', ' ');
        string title = System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(raw);

        // Replace internal dev codenames with localized hero names in ability headers
        title = title.Replace("Chrono ", "Paradox ")
                     .Replace("Sumo ", "Dynamo ")
                     .Replace("Archer ", "Grey Talon ")
                     .Replace("Orion ", "Grey Talon ")
                     .Replace("Geist ", "Lady Geist ")
                     .Replace("Ghost ", "Lady Geist ")
                     .Replace("Hornet ", "Vindicta ")
                     .Replace("Forge ", "McGinnis ")
                     .Replace("Tengu ", "Ivy ")
                     .Replace("Gigawatt ", "Seven ")
                     .Replace("Synth ", "Pocket ")
                     .Replace("Digger ", "Mo & Krill ")
                     .Replace("Astro ", "Holliday ")
                     .Replace("Nano ", "Calico ")
                     .Replace("Artist ", "Violet ")
                     .Replace("Airheart ", "Deadman Danny ")
                     .Replace("Deadpack ", "Deadman Danny ");

        return title;
    }
}
