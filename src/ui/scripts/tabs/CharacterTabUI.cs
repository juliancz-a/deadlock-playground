using Godot;
using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using DeadlockPlayground.Catalog;
using DeadlockPlayground.Addons;
using DeadlockPlayground.UI;

using DeadlockPlayground.Particles;
using DeadlockPlayground.Painter;
using DeadlockPlayground.Materials;

public partial class CharacterTabUI : VBoxContainer
{
    [Signal] public delegate void CharacterRequestedEventHandler(string internalId);

    [Export] private Container _submeshContainer;
    [Export] private Label _partsLabel;
    [Export] private Button _btnShowAll;
    [Export] private Button _btnHideAccessories;
    [Export] private Button _btnBrowseParticles;
    [Export] private Button _btnBrowseHeroGallery;
    [Export] private Button _btnExtraModels;
    [Export] private Button _btnBrowseModGallery;
    [Export] private Button _btnCloseParticle;
    [Export] private Button _btnRestoreCharacter;
    [Export] private Control _secondaryActionButtons;
    [Export] private TextureRect _texHeroGallery;
    [Export] private TextureRect _texModGallery;
    [Export] private TextureRect _texExtraModels;
    [Export] private TextureRect _texParticles;
    [Export] private VpkLoaderTest _vpkLoader;

    public class SubmeshItem
    {
        public MeshInstance3D Mesh { get; set; }
        public string RawName { get; set; }
        public string DisplayName { get; set; }
        public bool IsAccessory { get; set; }
        public CheckBox CheckBoxWidget { get; set; }
    }

    public class ParticleHierarchyItem
    {
        public ParticleMultiMeshRenderer Renderer { get; set; }
        public string DisplayName { get; set; }
        public CheckBox CheckBoxWidget { get; set; }
    }

    private readonly List<SubmeshItem> _submeshes = new();
    private readonly List<ParticleHierarchyItem> _particleHierarchyItems = new();
    private readonly List<AddonModInfo> _addonMods = new();
    private Node3D _currentHeroNode;
    private bool _isParticleHierarchyMode = false;
    private bool _hasConnectedParticleStage = false;
    private bool _artworkLoaded = false;

    public override void _Ready()
    {
        _partsLabel ??= GetNodeOrNull<Label>("SubmeshHeader/PartsLabel")
                     ?? FindChild("PartsLabel", true, false) as Label;

        _btnCloseParticle ??= GetNodeOrNull<Button>("HeroSelectGroup/ContextualActionButtons/BtnCloseParticle")
                           ?? FindChild("BtnCloseParticle", true, false) as Button;

        _btnRestoreCharacter ??= GetNodeOrNull<Button>("HeroSelectGroup/ContextualActionButtons/BtnRestoreCharacter")
                              ?? FindChild("BtnRestoreCharacter", true, false) as Button;

        _btnBrowseHeroGallery ??= GetNodeOrNull<Button>("HeroSelectGroup/PrimaryActionButtons/BtnBrowseHeroGallery")
                               ?? FindChild("BtnBrowseHeroGallery", true, false) as Button;

        _btnBrowseModGallery ??= GetNodeOrNull<Button>("HeroSelectGroup/PrimaryActionButtons/BtnBrowseModGallery")
                              ?? FindChild("BtnBrowseModGallery", true, false) as Button;

        _secondaryActionButtons ??= GetNodeOrNull<Control>("HeroSelectGroup/SecondaryActionButtons")
                                 ?? FindChild("SecondaryActionButtons", true, false) as Control;

        _btnExtraModels ??= GetNodeOrNull<Button>("HeroSelectGroup/SecondaryActionButtons/BtnExtraModels")
                         ?? FindChild("BtnExtraModels", true, false) as Button;

        _btnBrowseParticles ??= GetNodeOrNull<Button>("HeroSelectGroup/SecondaryActionButtons/BtnBrowseParticles")
                             ?? GetNodeOrNull<Button>("SubmeshHeader/HBoxButtons/BtnBrowseParticles")
                             ?? FindChild("BtnBrowseParticles", true, false) as Button;

        _texHeroGallery ??= FindChild("TexHeroGallery", true, false) as TextureRect;
        _texModGallery ??= FindChild("TexModGallery", true, false) as TextureRect;
        _texExtraModels ??= FindChild("TexExtraModels", true, false) as TextureRect;
        _texParticles ??= FindChild("TexParticles", true, false) as TextureRect;

        CallDeferred(nameof(LoadActionButtonsArtwork));

        ConnectEvents();

        if (_vpkLoader == null)
        {
            _vpkLoader = GetNodeOrNull<VpkLoaderTest>("/root/Main/UIRoot/MainHUD/VBoxContainer/MainSplit/ViewportArea/SubViewportContainer/WorldViewport/VpkLoaderTest")
                      ?? GetNodeOrNull<VpkLoaderTest>("/root/Main/VpkLoaderTest")
                      ?? GetTree().Root.FindChild("VpkLoaderTest", true, false) as VpkLoaderTest;
        }

        UpdateContextualButtonsVisibility();

        var stage = GetOrCreateParticleStage();
        if (stage != null && stage.HasActiveParticle)
        {
            PopulateParticleHierarchy(stage);
        }

        if (_vpkLoader != null)
        {
            _vpkLoader.HeroLoaded += SetHero;
            _vpkLoader.HeroUnloaded += ClearSubmeshes;
            if (_vpkLoader.CurrentHeroNode != null)
            {
                SetHero(_vpkLoader.CurrentHeroNode);
            }
            else
            {
                SetControlsEnabled(false);
            }
        }
        else
        {
            SetControlsEnabled(false);
        }
    }

    public void SetControlsEnabled(bool enabled)
    {
        if (_btnShowAll != null) _btnShowAll.Disabled = !enabled;
        if (_btnHideAccessories != null) _btnHideAccessories.Disabled = !enabled;
        bool hasHeroOrModel = enabled || _currentHeroNode != null || _vpkLoader?.CurrentHeroNode != null;
        if (_btnBrowseParticles != null) _btnBrowseParticles.Disabled = !hasHeroOrModel;
        if (_btnExtraModels != null) _btnExtraModels.Disabled = !hasHeroOrModel;
        if (_submeshContainer != null)
        {
            _submeshContainer.Modulate = enabled ? Colors.White : new Color(1, 1, 1, 0.4f);
        }
    }

    private void ConnectEvents()
    {
        if (_btnCloseParticle != null)
        {
            _btnCloseParticle.Pressed += () =>
            {
                var stage = GetOrCreateParticleStage();
                stage?.UnloadActiveParticle();
                UpdateContextualButtonsVisibility();
            };
        }

        if (_btnRestoreCharacter != null)
        {
            _btnRestoreCharacter.Pressed += async () =>
            {
                if (_vpkLoader != null && _vpkLoader.BaseHeroEntry != null)
                {
                    await _vpkLoader.LoadHeroModelAsync(_vpkLoader.BaseHeroEntry);
                    UpdateContextualButtonsVisibility();
                }
            };
        }

        if (_btnShowAll != null)
        {
            _btnShowAll.Pressed += ShowAll;
        }

        if (_btnHideAccessories != null)
        {
            _btnHideAccessories.Pressed += HideAccessories;
        }

        if (_btnBrowseHeroGallery != null)
        {
            _btnBrowseHeroGallery.Pressed += OpenHeroPickerModal;
        }

        if (_btnExtraModels != null)
        {
            _btnExtraModels.Pressed += OpenExtraModelModal;
        }

        if (_btnBrowseModGallery != null)
        {
            _btnBrowseModGallery.Pressed += OpenModPickerModal;
        }

        if (_btnBrowseParticles != null)
        {
            _btnBrowseParticles.Pressed += OnBrowseParticlesPressed;
        }
    }

    public void OpenHeroPickerModal()
    {
        var modalsLayer = GetTree()?.Root?.FindChild("ModalsLayer", true, false) as Control;
        var modal = modalsLayer?.FindChild("HeroPickerModal", true, false) as HeroPickerModalUI;
        if (modal == null && modalsLayer != null)
        {
            modal = new HeroPickerModalUI { Name = "HeroPickerModal" };
            modalsLayer.AddChild(modal);
            modal.HeroSelected += OnHeroSelectedFromModal;
            modal.ModalClosed += () =>
            {
                var studioUI = GetTree()?.Root?.FindChild("UIRoot", true, false) as StudioUIManager
                            ?? GetTree()?.Root?.FindChild("StudioUIManager", true, false) as StudioUIManager;
                studioUI?.UpdateModalsState();
            };
        }

        if (modal != null)
        {
            var basePkg = _vpkLoader?.GetOrCreateBasePackage();
            modal.Open(basePkg, _vpkLoader?.LastLoadedHeroEntry?.InternalCodename);
            var studioUI = GetTree()?.Root?.FindChild("UIRoot", true, false) as StudioUIManager
                        ?? GetTree()?.Root?.FindChild("StudioUIManager", true, false) as StudioUIManager;
            studioUI?.UpdateModalsState();
        }
    }

    private async void OnHeroSelectedFromModal(string heroCodename, string vmdlPath, string displayName)
    {
        if (_vpkLoader == null) return;
        var entry = DeadlockHeroCatalog.GetByCodename(heroCodename) ?? DeadlockHeroCatalog.ResolveHero(heroCodename);
        if (entry != null)
        {
            await _vpkLoader.LoadHeroModelAsync(entry);
        }
        else
        {
            await _vpkLoader.LoadExtraModelAsync(vmdlPath, heroCodename, displayName);
        }
        UpdateContextualButtonsVisibility();
    }

    public void OpenExtraModelModal()
    {
        var modalsLayer = GetTree()?.Root?.FindChild("ModalsLayer", true, false) as Control;
        var modal = modalsLayer?.FindChild("ExtraModelPickerModal", true, false) as ExtraModelPickerModalUI;
        if (modal == null && modalsLayer != null)
        {
            modal = new ExtraModelPickerModalUI { Name = "ExtraModelPickerModal" };
            modalsLayer.AddChild(modal);
            modal.ExtraModelSelected += OnExtraModelSelectedFromModal;
            modal.ModalClosed += () =>
            {
                var studioUI = GetTree()?.Root?.FindChild("UIRoot", true, false) as StudioUIManager
                            ?? GetTree()?.Root?.FindChild("StudioUIManager", true, false) as StudioUIManager;
                studioUI?.UpdateModalsState();
            };
        }

        if (modal != null)
        {
            var basePkg = _vpkLoader?.GetOrCreateBasePackage();
            var heroEntry = _vpkLoader?.LastLoadedHeroEntry 
                         ?? DeadlockHeroCatalog.ResolveHero(_vpkLoader?.HeroName ?? "")
                         ?? DeadlockHeroCatalog.ResolveHero("Dynamo");
            modal.OpenForHero(basePkg, heroEntry, _vpkLoader?.LastLoadedVmdlPath);
            var studioUI = GetTree()?.Root?.FindChild("UIRoot", true, false) as StudioUIManager
                        ?? GetTree()?.Root?.FindChild("StudioUIManager", true, false) as StudioUIManager;
            studioUI?.UpdateModalsState();
        }
    }

    private async void OnExtraModelSelectedFromModal(string vmdlPath, string displayName)
    {
        if (_vpkLoader == null || string.IsNullOrEmpty(vmdlPath)) return;
        string heroKey = _vpkLoader.HeroName ?? "Hero";
        await _vpkLoader.LoadExtraModelAsync(vmdlPath, heroKey, displayName);
        UpdateContextualButtonsVisibility();
    }

    public void OpenModPickerModal()
    {
        var modalsLayer = GetTree()?.Root?.FindChild("ModalsLayer", true, false) as Control;
        var modal = modalsLayer?.FindChild("ModPickerModal", true, false) as ModPickerModalUI;
        if (modal == null && modalsLayer != null)
        {
            modal = new ModPickerModalUI { Name = "ModPickerModal" };
            modalsLayer.AddChild(modal);
            modal.ModSelected += OnModSelectedFromModal;
            modal.ModalClosed += () =>
            {
                var studioUI = GetTree()?.Root?.FindChild("UIRoot", true, false) as StudioUIManager
                            ?? GetTree()?.Root?.FindChild("StudioUIManager", true, false) as StudioUIManager;
                studioUI?.UpdateModalsState();
            };
        }

        if (modal != null)
        {
            string addonsDir = GetResolvedAddonsDir();
            var detected = !string.IsNullOrEmpty(addonsDir) && Directory.Exists(addonsDir)
                ? DeadlockAddonScanner.ScanAddons(addonsDir)
                : new List<AddonModInfo>();

            _addonMods.Clear();
            _addonMods.AddRange(detected);

            modal.Open(detected, _vpkLoader?.AddonVpkPath);
            var studioUI = GetTree()?.Root?.FindChild("UIRoot", true, false) as StudioUIManager
                        ?? GetTree()?.Root?.FindChild("StudioUIManager", true, false) as StudioUIManager;
            studioUI?.UpdateModalsState();
        }
    }

    private async void OnModSelectedFromModal(string modFilePath, string modDisplayTitle, bool isVanilla)
    {
        if (_vpkLoader == null) return;

        if (isVanilla || string.IsNullOrEmpty(modFilePath))
        {
            // Vanilla
            _vpkLoader.SetActiveAddon(null, null);
            await _vpkLoader.ReloadCurrentHeroAsync();
            UpdateContextualButtonsVisibility();
            return;
        }

        var mod = _addonMods.FirstOrDefault(m => m.FilePath.Equals(modFilePath, StringComparison.OrdinalIgnoreCase))
               ?? (File.Exists(modFilePath) ? DeadlockAddonScanner.InspectAddonPackage(modFilePath) : null)
               ?? new AddonModInfo
               {
                   FilePath = modFilePath,
                   DisplayTitle = modDisplayTitle,
                   FileName = System.IO.Path.GetFileName(modFilePath)
               };

        _vpkLoader.SetActiveAddon(mod.FilePath, mod);
        await _vpkLoader.LoadAddonModelAsync(mod);
        UpdateContextualButtonsVisibility();
    }

    private ParticleStage3D GetOrCreateParticleStage()
    {
        var worldViewport = GetTree()?.Root?.FindChild("WorldViewport", true, false) as SubViewport;
        var stage = worldViewport?.FindChild("ParticleStage3D", true, false) as ParticleStage3D;
        if (stage == null && worldViewport != null)
        {
            stage = new ParticleStage3D();
            worldViewport.AddChild(stage);
        }

        if (stage != null && !_hasConnectedParticleStage)
        {
            _hasConnectedParticleStage = true;
            stage.ParticleLoaded += OnParticleLoaded;
            stage.ParticleUnloaded += OnParticleUnloaded;
        }

        return stage;
    }

    private void OnParticleLoaded(string vpcfPath, string variantName, string abilityName)
    {
        UpdateContextualButtonsVisibility();
        var stage = GetOrCreateParticleStage();
        if (stage != null)
        {
            PopulateParticleHierarchy(stage);
        }
    }

    private void OnParticleUnloaded()
    {
        UpdateContextualButtonsVisibility();
        _isParticleHierarchyMode = false;
        _particleHierarchyItems.Clear();

        if (_partsLabel != null)
        {
            _partsLabel.Text = "Submesh Visibility";
        }
        if (_btnShowAll != null)
        {
            _btnShowAll.Text = "Show All";
        }
        if (_btnHideAccessories != null)
        {
            _btnHideAccessories.Text = "Hide Accessories";
        }

        var hero = _currentHeroNode ?? _vpkLoader?.CurrentHeroNode;
        if (hero != null && GodotObject.IsInstanceValid(hero))
        {
            _currentHeroNode = hero;
            ClearSubmeshes(clearHeroNode: false);
            CollectSubmeshesRecursive(_currentHeroNode);
            PopulateSubmeshList();
            SetControlsEnabled(true);
        }
        else
        {
            ClearSubmeshes(clearHeroNode: true);
            SetControlsEnabled(false);
        }

        if (_btnBrowseParticles != null)
        {
            _btnBrowseParticles.Disabled = false;
        }
    }

    public void PopulateParticleHierarchy(ParticleStage3D stage)
    {
        if (stage == null || !stage.HasActiveParticle) return;

        _isParticleHierarchyMode = true;
        _particleHierarchyItems.Clear();

        if (_partsLabel != null)
        {
            _partsLabel.Text = "Particle Hierarchy";
        }
        if (_btnShowAll != null)
        {
            _btnShowAll.Text = "Show All";
        }
        if (_btnHideAccessories != null)
        {
            _btnHideAccessories.Text = "Hide All";
        }

        if (_submeshContainer != null)
        {
            foreach (Node child in _submeshContainer.GetChildren())
            {
                child.QueueFree();
            }
        }

        string rootBase = stage.ActiveRootDefinition?.VpcfPath?.GetFile()?.GetBaseName() ?? "";

        foreach (var renderer in stage.Renderers)
        {
            if (renderer == null || !GodotObject.IsInstanceValid(renderer)) continue;

            string baseName = renderer.Instance?.Definition?.VpcfPath?.GetFile()?.GetBaseName() ?? renderer.Name.ToString();
            string displayName;
            if (!string.IsNullOrEmpty(rootBase) && baseName.StartsWith(rootBase, StringComparison.OrdinalIgnoreCase) && baseName.Length > rootBase.Length)
            {
                displayName = baseName.Substring(rootBase.Length);
            }
            else
            {
                displayName = baseName;
            }

            string rType = renderer.RendererDef?.ClassName?.Replace("C_OP_", "")?.Replace("Render", "") ?? "";
            if (!string.IsNullOrEmpty(rType))
            {
                displayName += $" ({rType})";
            }

            var item = new ParticleHierarchyItem
            {
                Renderer = renderer,
                DisplayName = displayName
            };

            var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            row.AddThemeConstantOverride("separation", 6);

            var cb = new CheckBox
            {
                ButtonPressed = renderer.VisibleAndEmitting,
                TooltipText = "Toggle child particle system visibility & emission",
                SizeFlagsVertical = SizeFlags.ShrinkCenter
            };

            var localRenderer = renderer;
            cb.Toggled += (pressed) =>
            {
                if (localRenderer != null && GodotObject.IsInstanceValid(localRenderer))
                {
                    localRenderer.VisibleAndEmitting = pressed;
                }
            };

            var btnWrapper = new Control
            {
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                SizeFlagsVertical = SizeFlags.Fill,
                CustomMinimumSize = new Vector2(0, 28),
                ClipContents = true
            };

            var nameBtn = new Button
            {
                Text = displayName,
                Alignment = HorizontalAlignment.Left,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                ClipText = true,
                TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
                TooltipText = displayName
            };
            nameBtn.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            nameBtn.AddThemeFontSizeOverride("font_size", 11);
            nameBtn.Pressed += () =>
            {
                cb.ButtonPressed = !cb.ButtonPressed;
            };

            btnWrapper.AddChild(nameBtn);
            row.AddChild(cb);
            row.AddChild(btnWrapper);

            item.CheckBoxWidget = cb;
            _particleHierarchyItems.Add(item);

            _submeshContainer?.AddChild(row);
        }

        SetControlsEnabled(true);
    }

    private async void OnBrowseParticlesPressed()
    {
        string heroName = _vpkLoader?.LastLoadedHeroEntry?.DisplayName 
                       ?? _vpkLoader?.HeroName 
                       ?? "Dynamo";

        var service = HeroParticleService.Instance;
        await service.EnsureLoadedAsync();
        var groups = service.GetHeroAbilities(heroName);

        var modalsLayer = GetTree()?.Root?.FindChild("ModalsLayer", true, false) as Control;
        var modal = modalsLayer?.FindChild("ParticlePickerModal", true, false) as ParticlePickerModalUI;
        if (modal == null && modalsLayer != null)
        {
            modal = new ParticlePickerModalUI { Name = "ParticlePickerModal" };
            modalsLayer.AddChild(modal);
            modal.ParticleSelected += OnParticleSelectedFromModal;
            modal.ModalClosed += () =>
            {
                var studioUI = GetTree()?.Root?.FindChild("UIRoot", true, false) as StudioUIManager
                            ?? GetTree()?.Root?.FindChild("StudioUIManager", true, false) as StudioUIManager;
                studioUI?.UpdateModalsState();
            };
        }

        if (modal != null)
        {
            modal.OpenForHero(heroName, groups);
            var studioUI = GetTree()?.Root?.FindChild("UIRoot", true, false) as StudioUIManager
                        ?? GetTree()?.Root?.FindChild("StudioUIManager", true, false) as StudioUIManager;
            studioUI?.UpdateModalsState();
        }
    }

    private void OnParticleSelectedFromModal(string vpcfPath, string variantName, string abilityKey)
    {
        if (_vpkLoader == null) return;

        // 1. Resolve or create ParticleStage3D in world viewport
        var stage = GetOrCreateParticleStage();

        // 2. Connect to Player Tab timeline & Paint Tab before loading
        var playerTab = GetTree()?.Root?.FindChild("PoseTab", true, false) as PlayerTabUI;
        if (playerTab != null && stage != null)
        {
            playerTab.SetParticleStage(stage);
        }

        var paintTab = GetTree()?.Root?.FindChild("PaintTab", true, false) as PaintTabUI;
        if (paintTab != null && stage != null)
        {
            paintTab.SetParticleStage(stage);
        }

        if (stage != null)
        {
            var fileLoader = _vpkLoader.GetOrCreateFileLoader();
            var basePkg = _vpkLoader.GetOrCreateBasePackage();
            stage.LoadParticle(vpcfPath, variantName, abilityKey, _vpkLoader.CurrentHeroNode, fileLoader, basePkg);
        }

        // 3. Resolve or create ActiveParticleBadgeUI inside 3D ViewportArea
        var viewportArea = GetTree()?.Root?.FindChild("ViewportArea", true, false) as Control;
        var badge = viewportArea?.FindChild("ActiveParticleBadge", true, false) as ActiveParticleBadgeUI;
        if (badge == null && viewportArea != null)
        {
            badge = new ActiveParticleBadgeUI { Name = "ActiveParticleBadge" };
            viewportArea.AddChild(badge);
            badge.UnloadRequested += () => stage?.UnloadActiveParticle();
            badge.BrowseRequested += OnBrowseParticlesPressed;
        }
        if (badge != null)
        {
            badge.ShowBadge(_vpkLoader?.LastLoadedHeroEntry?.DisplayName ?? "Hero", abilityKey, variantName);
        }
    }

    public string GetResolvedAddonsDir()
    {
        string addonsDir = null;
        if (_vpkLoader != null && !string.IsNullOrEmpty(_vpkLoader.VpkPath))
        {
            string citadelDir = Path.GetDirectoryName(_vpkLoader.VpkPath);
            if (!string.IsNullOrEmpty(citadelDir))
            {
                addonsDir = Path.Combine(citadelDir, "addons");
            }
        }

        if (string.IsNullOrEmpty(addonsDir) || !Directory.Exists(addonsDir))
        {
            var pathManager = new GamePathManager();
            pathManager.LoadConfig();
            if (!string.IsNullOrEmpty(pathManager.CurrentGamePath))
            {
                addonsDir = Path.Combine(pathManager.CurrentGamePath, "game", "citadel", "addons");
            }
        }

        return addonsDir;
    }

    public void PopulateAddonDropdown()
    {
        // Redundant dropdown removed, kept for backward compatibility with StudioUIManager
    }

    public void LoadActionButtonsArtwork()
    {
        if (_artworkLoaded) return;
        var basePkg = _vpkLoader?.GetOrCreateBasePackage();
        if (basePkg == null) return;

        try
        {
            if (_texHeroGallery != null)
            {
                var tex = Source2TextureLoader.GetOrLoadTexture(basePkg, "panorama/images/main_menu/play/card_play_subject_psd.vtex_c", forceOpaque: false);
                if (tex != null) _texHeroGallery.Texture = tex;
            }

            if (_texModGallery != null)
            {
                var tex = Source2TextureLoader.GetOrLoadTexture(basePkg, "panorama/images/main_menu/play/card_custom_subject_psd.vtex_c", forceOpaque: false);
                if (tex != null) _texModGallery.Texture = tex;
            }

            if (_texParticles != null)
            {
                var tex = Source2TextureLoader.GetOrLoadTexture(basePkg, "panorama/images/main_menu/play/card_roster_subject_psd.vtex_c", forceOpaque: false);
                if (tex != null) _texParticles.Texture = tex;
            }

            if (_texExtraModels != null)
            {
                var tex = Source2TextureLoader.GetOrLoadTexture(basePkg, "panorama/images/heroes/guns/bookworm_gun_psd.vtex_c", forceOpaque: false);
                if (tex != null) _texExtraModels.Texture = tex;
            }

            if (_texHeroGallery?.Texture != null || _texModGallery?.Texture != null)
            {
                _artworkLoaded = true;
                GD.Print("[CharacterTabUI] Action buttons artwork loaded successfully.");
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[CharacterTabUI] Failed to load action buttons artwork: {ex.Message}");
        }
    }

    private void UpdateContextualButtonsVisibility()
    {
        var stage = GetOrCreateParticleStage();
        bool hasParticle = stage != null && stage.HasActiveParticle;
        if (_btnCloseParticle != null && GodotObject.IsInstanceValid(_btnCloseParticle))
        {
            _btnCloseParticle.Visible = hasParticle;
        }

        bool hasExtraModel = _vpkLoader != null && _vpkLoader.IsExtraModelLoaded;
        if (_btnRestoreCharacter != null && GodotObject.IsInstanceValid(_btnRestoreCharacter))
        {
            _btnRestoreCharacter.Visible = hasExtraModel;
        }

        bool hasHero = _currentHeroNode != null || (_vpkLoader != null && _vpkLoader.CurrentHeroNode != null);
        if (_secondaryActionButtons != null && GodotObject.IsInstanceValid(_secondaryActionButtons))
        {
            _secondaryActionButtons.Visible = hasHero;
        }
        else
        {
            if (_btnExtraModels != null && GodotObject.IsInstanceValid(_btnExtraModels))
            {
                _btnExtraModels.Visible = hasHero;
            }
            if (_btnBrowseParticles != null && GodotObject.IsInstanceValid(_btnBrowseParticles))
            {
                _btnBrowseParticles.Visible = hasHero;
            }
        }
    }

    public void SetHero(Node3D heroNode)
    {
        _currentHeroNode = heroNode;
        UpdateContextualButtonsVisibility();
        LoadActionButtonsArtwork();

        var stage = GetOrCreateParticleStage();
        if (stage != null && stage.HasActiveParticle)
        {
            PopulateParticleHierarchy(stage);
            return;
        }

        ClearSubmeshes();
        if (heroNode == null)
        {
            SetControlsEnabled(false);
            return;
        }

        CollectSubmeshesRecursive(heroNode);
        PopulateSubmeshList();
        SetControlsEnabled(true);
    }

    public void ClearSubmeshes()
    {
        ClearSubmeshes(false);
    }

    public void ClearSubmeshes(bool clearHeroNode)
    {
        if (clearHeroNode)
        {
            _currentHeroNode = null;
        }
        UpdateContextualButtonsVisibility();
        if (_submeshContainer != null)
        {
            foreach (Node child in _submeshContainer.GetChildren())
            {
                child.QueueFree();
            }
        }
        _submeshes.Clear();
        _particleHierarchyItems.Clear();
        SetControlsEnabled(_currentHeroNode != null || _vpkLoader?.CurrentHeroNode != null);
    }

    private void CollectSubmeshesRecursive(Node node)
    {
        if (node == null) return;
        if (node is BoneAttachment3D) return;

        if (node is MeshInstance3D mi && mi.Mesh != null)
        {
            string rawName = mi.Name.ToString();
            if (rawName.StartsWith("@") || rawName.StartsWith(".")) return;

            if (!HeroMeshHierarchy.IsAuthenticHeroMesh(mi))
            {
                return;
            }

            string lowerName = rawName.ToLowerInvariant();
            if (lowerName.Contains("tipvisual") || lowerName.Contains("polevisual") ||
                lowerName.Contains("visual") || lowerName.Contains("wireframe") ||
                lowerName.Contains("marker") || lowerName.Contains("gizmo") ||
                lowerName.Contains("preview") || lowerName.Contains("picker") ||
                lowerName.Contains("handle"))
            {
                return;
            }

            // Skip internal split surfaces created by HeroMeshHierarchy so only high-level submeshes appear
            if (mi.HasMeta("ParentCompositeMesh") || lowerName.Contains("_surf_"))
            {
                return;
            }

            {
                bool isAcc = lowerName.Contains("acc") ||
                             lowerName.Contains("prop") ||
                             lowerName.Contains("weapon") ||
                             lowerName.Contains("gun") ||
                             lowerName.Contains("hat") ||
                             lowerName.Contains("gear") ||
                             lowerName.Contains("glass") ||
                             lowerName.Contains("mask") ||
                             lowerName.Contains("cape") ||
                             lowerName.Contains("coat");

                string displayName = CleanMeshName(rawName);

                _submeshes.Add(new SubmeshItem
                {
                    Mesh = mi,
                    RawName = rawName,
                    DisplayName = displayName,
                    IsAccessory = isAcc
                });
            }
        }

        foreach (Node child in node.GetChildren())
        {
            CollectSubmeshesRecursive(child);
        }
    }

    private string CleanMeshName(string raw)
    {
        string clean = raw.TrimStart('.', '_');
        if (string.IsNullOrEmpty(clean)) return "Mesh Part";
        return char.ToUpperInvariant(clean[0]) + clean.Substring(1).Replace('_', ' ');
    }

    private void PopulateSubmeshList()
    {
        if (_submeshContainer == null) return;

        foreach (var item in _submeshes)
        {
            bool isInitiallyVisible = item.Mesh.Visible;
            if (item.Mesh.HasMeta("UserVisibility"))
            {
                isInitiallyVisible = item.Mesh.GetMeta("UserVisibility").AsBool();
            }
            else if (item.Mesh.HasMeta("GeneratedSubmeshes"))
            {
                var gen = item.Mesh.GetMeta("GeneratedSubmeshes").AsGodotArray<MeshInstance3D>();
                if (gen.Count > 0 && gen[0] != null && GodotObject.IsInstanceValid(gen[0]))
                {
                    isInitiallyVisible = gen[0].Visible;
                }
            }

            item.Mesh.SetMeta("UserVisibility", isInitiallyVisible);

            var row = new HBoxContainer
            {
                SizeFlagsHorizontal = SizeFlags.ExpandFill
            };
            row.AddThemeConstantOverride("separation", 6);

            var cb = new CheckBox
            {
                ButtonPressed = isInitiallyVisible,
                TooltipText = "Toggle submesh visibility",
                SizeFlagsVertical = SizeFlags.ShrinkCenter
            };

            var localItem = item;
            cb.Toggled += (pressed) =>
            {
                if (localItem.Mesh != null && GodotObject.IsInstanceValid(localItem.Mesh))
                {
                    if (!localItem.Mesh.HasMeta("IsHiddenComposite"))
                    {
                        localItem.Mesh.Visible = pressed;
                    }
                    else
                    {
                        localItem.Mesh.Visible = false;
                    }
                    localItem.Mesh.SetMeta("UserVisibility", pressed);

                    if (localItem.Mesh.HasMeta("GeneratedSubmeshes"))
                    {
                        var genList = localItem.Mesh.GetMeta("GeneratedSubmeshes").AsGodotArray<MeshInstance3D>();
                        foreach (var sub in genList)
                        {
                            if (sub != null && GodotObject.IsInstanceValid(sub))
                            {
                                sub.Visible = pressed;
                            }
                        }
                    }
                }
            };

            int charLen = item.DisplayName?.Length ?? 0;
            float minHeight = charLen > 40 ? 52f : (charLen > 22 ? 38f : 28f);

            var btnWrapper = new Control
            {
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                SizeFlagsVertical = SizeFlags.Fill,
                CustomMinimumSize = new Vector2(0, minHeight),
                ClipContents = true
            };

            var nameBtn = new Button
            {
                Text = item.DisplayName,
                Alignment = HorizontalAlignment.Left,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                ClipText = true,
                TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
                TooltipText = item.DisplayName
            };
            nameBtn.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            nameBtn.OffsetLeft = 0;
            nameBtn.OffsetTop = 0;
            nameBtn.OffsetRight = 0;
            nameBtn.OffsetBottom = 0;
            nameBtn.AddThemeFontSizeOverride("font_size", 11);
            nameBtn.Pressed += () =>
            {
                cb.ButtonPressed = !cb.ButtonPressed;
            };

            btnWrapper.AddChild(nameBtn);
            row.AddChild(cb);
            row.AddChild(btnWrapper);

            item.CheckBoxWidget = cb;
            _submeshContainer.AddChild(row);
        }
    }

    public void ShowAll()
    {
        if (_isParticleHierarchyMode)
        {
            foreach (var item in _particleHierarchyItems)
            {
                if (item.Renderer != null && GodotObject.IsInstanceValid(item.Renderer))
                {
                    item.Renderer.VisibleAndEmitting = true;
                }
                if (item.CheckBoxWidget != null) item.CheckBoxWidget.ButtonPressed = true;
            }
            return;
        }

        foreach (var item in _submeshes)
        {
            if (item.Mesh != null && GodotObject.IsInstanceValid(item.Mesh))
            {
                if (!item.Mesh.HasMeta("IsHiddenComposite"))
                {
                    item.Mesh.Visible = true;
                }
                else
                {
                    item.Mesh.Visible = false;
                }
                item.Mesh.SetMeta("UserVisibility", true);

                if (item.Mesh.HasMeta("GeneratedSubmeshes"))
                {
                    var genList = item.Mesh.GetMeta("GeneratedSubmeshes").AsGodotArray<MeshInstance3D>();
                    foreach (var sub in genList)
                    {
                        if (sub != null && GodotObject.IsInstanceValid(sub))
                        {
                            sub.Visible = true;
                        }
                    }
                }

                if (item.CheckBoxWidget != null) item.CheckBoxWidget.ButtonPressed = true;
            }
        }
    }

    public void HideAccessories()
    {
        if (_isParticleHierarchyMode)
        {
            foreach (var item in _particleHierarchyItems)
            {
                if (item.Renderer != null && GodotObject.IsInstanceValid(item.Renderer))
                {
                    item.Renderer.VisibleAndEmitting = false;
                }
                if (item.CheckBoxWidget != null) item.CheckBoxWidget.ButtonPressed = false;
            }
            return;
        }

        foreach (var item in _submeshes)
        {
            if (item.Mesh != null && GodotObject.IsInstanceValid(item.Mesh))
            {
                bool shouldBeVisible = !item.IsAccessory;
                if (!item.Mesh.HasMeta("IsHiddenComposite"))
                {
                    item.Mesh.Visible = shouldBeVisible;
                }
                else
                {
                    item.Mesh.Visible = false;
                }
                item.Mesh.SetMeta("UserVisibility", shouldBeVisible);

                if (item.Mesh.HasMeta("GeneratedSubmeshes"))
                {
                    var genList = item.Mesh.GetMeta("GeneratedSubmeshes").AsGodotArray<MeshInstance3D>();
                    foreach (var sub in genList)
                    {
                        if (sub != null && GodotObject.IsInstanceValid(sub))
                        {
                            sub.Visible = shouldBeVisible;
                        }
                    }
                }

                if (item.CheckBoxWidget != null) item.CheckBoxWidget.ButtonPressed = shouldBeVisible;
            }
        }
    }
}
