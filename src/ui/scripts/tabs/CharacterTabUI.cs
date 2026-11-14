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
using DeadlockPlayground.Importer;

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
    [Export] private Button _btnImportCustomModel;
    [Export] private Control _customModelActionsRow;
    [Export] private Button _btnEditCustomModel;
    [Export] private Button _btnResetCustomModel;
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
    private string _lastImportedFilePath;
    private Node3D _lastImportedScene;
    private CustomModelAutoRigger.RiggingOptions _lastImportedOptions;
    private bool _hasCustomModelApplied = false;

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

        _btnImportCustomModel ??= GetNodeOrNull<Button>("HeroSelectGroup/BtnImportCustomModel")
                               ?? FindChild("BtnImportCustomModel", true, false) as Button;

        if (_btnImportCustomModel != null)
        {
            var iconTex = GD.Load<Texture2D>("res://assets/at-icons/file_arrow_down.svg");
            if (iconTex != null)
            {
                _btnImportCustomModel.Icon = iconTex;
                _btnImportCustomModel.ExpandIcon = true;
                _btnImportCustomModel.AddThemeConstantOverride("icon_max_width", 20);
                _btnImportCustomModel.AddThemeConstantOverride("h_separation", 8);
            }
        }

        _customModelActionsRow ??= GetNodeOrNull<Control>("HeroSelectGroup/CustomModelActionsRow")
                                ?? FindChild("CustomModelActionsRow", true, false) as Control;
        _btnEditCustomModel ??= GetNodeOrNull<Button>("HeroSelectGroup/CustomModelActionsRow/BtnEditCustomModel")
                             ?? FindChild("BtnEditCustomModel", true, false) as Button;
        _btnResetCustomModel ??= GetNodeOrNull<Button>("HeroSelectGroup/CustomModelActionsRow/BtnResetCustomModel")
                              ?? FindChild("BtnResetCustomModel", true, false) as Button;

        if (_btnEditCustomModel != null)
        {
            var iconEdit = GD.Load<Texture2D>("res://assets/at-icons/sliders.svg");
            if (iconEdit != null)
            {
                _btnEditCustomModel.Icon = iconEdit;
                _btnEditCustomModel.ExpandIcon = true;
                _btnEditCustomModel.AddThemeConstantOverride("icon_max_width", 20);
                _btnEditCustomModel.AddThemeConstantOverride("h_separation", 8);
            }
        }

        if (_btnResetCustomModel != null)
        {
            _btnResetCustomModel.ThemeTypeVariation = "DangerButton";
            var iconReset = GD.Load<Texture2D>("res://assets/at-icons/rotate.svg");
            if (iconReset != null)
            {
                _btnResetCustomModel.Icon = iconReset;
                _btnResetCustomModel.ExpandIcon = true;
                _btnResetCustomModel.AddThemeConstantOverride("icon_max_width", 20);
                _btnResetCustomModel.AddThemeConstantOverride("h_separation", 8);
            }
        }

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
        if (_btnImportCustomModel != null) _btnImportCustomModel.Disabled = !hasHeroOrModel;
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

        if (_btnImportCustomModel != null)
        {
            _btnImportCustomModel.Pressed += OnImportCustomModelPressed;
        }

        if (_btnEditCustomModel != null)
        {
            _btnEditCustomModel.Pressed += OnEditCustomModelPressed;
        }

        if (_btnResetCustomModel != null)
        {
            _btnResetCustomModel.Pressed += OnResetCustomModelPressed;
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

        string baseVmdl = _vpkLoader.BaseHeroEntry?.VmdlRelativePath?.Replace('\\', '/');
        string normSelected = vmdlPath.Replace('\\', '/');
        bool isBaseCharacter = (!string.IsNullOrEmpty(baseVmdl) && normSelected.Equals(baseVmdl, StringComparison.OrdinalIgnoreCase))
                            || (displayName != null && displayName.Contains("(Base Character)", StringComparison.OrdinalIgnoreCase));

        if (isBaseCharacter && _vpkLoader.BaseHeroEntry != null)
        {
            await _vpkLoader.LoadHeroModelAsync(_vpkLoader.BaseHeroEntry);
        }
        else
        {
            string heroKey = _vpkLoader.HeroName ?? "Hero";
            await _vpkLoader.LoadExtraModelAsync(vmdlPath, heroKey, displayName);
        }
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

    private FileDialog _customModelFileDialog;

    public void OnImportCustomModelPressed()
    {
        var hero = _currentHeroNode ?? _vpkLoader?.CurrentHeroNode;
        if (hero == null)
        {
            GD.PrintErr("[CharacterTabUI] Cannot import custom model: no base hero is loaded.");
            return;
        }

        if (_customModelFileDialog == null || !GodotObject.IsInstanceValid(_customModelFileDialog))
        {
            _customModelFileDialog = new FileDialog
            {
                Name = "CustomModelFileDialog",
                Title = "Select Custom 3D Character or Accessory (.glb / .gltf / .fbx)",
                FileMode = FileDialog.FileModeEnum.OpenFile,
                Access = FileDialog.AccessEnum.Filesystem,
                Filters = new[]
                {
                    "*.glb, *.gltf, *.fbx ; 3D Model Files (*.glb, *.gltf, *.fbx)",
                    "*.glb ; GL Transmission Format Binary (*.glb)",
                    "*.gltf ; GL Transmission Format (*.gltf)",
                    "*.fbx ; Filmbox 3D Model (*.fbx)",
                    "*.* ; All Files"
                },
                UseNativeDialog = true
            };
            AddChild(_customModelFileDialog);
            _customModelFileDialog.FileSelected += OnCustomModelFileSelected;
        }

        _customModelFileDialog.PopupCentered(new Vector2I(850, 550));
    }

    private void OnCustomModelFileSelected(string filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
        {
            GD.PrintErr($"[CharacterTabUI] Invalid custom model path: '{filePath}'");
            return;
        }

        var hero = _currentHeroNode ?? _vpkLoader?.CurrentHeroNode;
        if (hero == null)
        {
            GD.PrintErr("[CharacterTabUI] No hero loaded to attach custom model to.");
            return;
        }

        var sceneRoot = CustomModelAutoRigger.LoadAndSanitize3DModelScene(filePath);
        if (sceneRoot == null)
        {
            GD.PrintErr($"[CharacterTabUI] Could not parse 3D scene from '{filePath}'.");
            return;
        }

        // Open Self-Contained Custom Model Importer Modal (CanvasLayer 110 with isolated SubViewport)
        var oldModal = GetTree()?.Root?.FindChild("CustomModelImporterModal", true, false) as CustomModelImporterModalUI;
        oldModal?.QueueFree();

        var modal = new CustomModelImporterModalUI { Name = "CustomModelImporterModal" };
        GetTree()?.Root?.AddChild(modal);

        modal.Open(hero, sceneRoot, filePath);
        modal.RiggingConfirmed += () =>
        {
            _lastImportedOptions = modal.LastRiggingOptions;
            _lastImportedFilePath = filePath;
            _lastImportedScene = sceneRoot;
            _hasCustomModelApplied = true;
            UpdateContextualButtonsVisibility();

            var activeHero = _currentHeroNode ?? _vpkLoader?.CurrentHeroNode;
            if (activeHero != null)
            {
                ClearSubmeshes(clearHeroNode: false);
                CollectSubmeshesRecursive(activeHero);
                PopulateSubmeshList();

                var paintTab = GetTree()?.Root?.FindChild("PaintTab", true, false) as PaintTabUI;
                // Invalidate cached hero so switching to Paint tab reloads it smoothly without freezing CharacterTabUI
                paintTab?.ClearHero();

                var hierarchy = GetTree()?.Root?.FindChild("HeroMeshHierarchy", true, false) as HeroMeshHierarchy;
                hierarchy?.ScanHero(activeHero);

                var shadingTab = GetTree()?.Root?.FindChild("ShadingTab", true, false) as ShadingTabUI
                              ?? GetTree()?.Root?.FindChild("ShadingTabUI", true, false) as ShadingTabUI;
                shadingTab?.SetHero(activeHero);

                var skeleton = CustomModelAutoRigger.FindSkeleton(activeHero);
                if (skeleton != null)
                {
                    var playerTab = GetTree()?.Root?.FindChild("PoseTab", true, false) as PlayerTabUI
                                 ?? GetTree()?.Root?.FindChild("PlayerTabUI", true, false) as PlayerTabUI;
                    playerTab?.SetSkeleton(skeleton);

                    var animDock = GetTree()?.Root?.FindChild("AnimationStudioDock", true, false) as DeadlockPlayground.UI.AnimationStudioDock;
                    animDock?.SetSkeleton(skeleton);
                }

                GD.Print("[CharacterTabUI] Custom model rigging confirmed and bound successfully!");
            }
        };
    }

    private void OnEditCustomModelPressed()
    {
        var hero = _currentHeroNode ?? _vpkLoader?.CurrentHeroNode;
        if (hero == null)
        {
            GD.PrintErr("[CharacterTabUI] No hero loaded to edit custom model.");
            return;
        }

        if (string.IsNullOrEmpty(_lastImportedFilePath) || !File.Exists(_lastImportedFilePath))
        {
            GD.PrintErr("[CharacterTabUI] Source file path for imported model is missing or deleted.");
            return;
        }

        // Re-generate pristine scene root from file to avoid any mutations from previous riggings
        var sceneRoot = CustomModelAutoRigger.LoadAndSanitize3DModelScene(_lastImportedFilePath);
        if (sceneRoot == null)
        {
            GD.PrintErr($"[CharacterTabUI] Could not reload 3D scene from '{_lastImportedFilePath}'.");
            return;
        }

        // Temporarily hide current custom model container so preview in modal is isolated
        var existingContainer = hero.FindChild("Custom_Rigged_Character", true, false) as Node3D;
        if (existingContainer != null)
        {
            existingContainer.Visible = false;
        }

        // Open modal with cached options
        var oldModal = GetTree()?.Root?.FindChild("CustomModelImporterModal", true, false) as CustomModelImporterModalUI;
        oldModal?.QueueFree();

        var modal = new CustomModelImporterModalUI { Name = "CustomModelImporterModal" };
        GetTree()?.Root?.AddChild(modal);

        modal.Open(hero, sceneRoot, _lastImportedFilePath, _lastImportedOptions);

        modal.ModalCancelled += () =>
        {
            // Restore visibility if cancelled
            if (existingContainer != null && GodotObject.IsInstanceValid(existingContainer))
            {
                existingContainer.Visible = true;
            }
        };

        modal.RiggingConfirmed += () =>
        {
            _lastImportedOptions = modal.LastRiggingOptions;
            _lastImportedScene = sceneRoot;
            _hasCustomModelApplied = true;
            UpdateContextualButtonsVisibility();

            var activeHero = _currentHeroNode ?? _vpkLoader?.CurrentHeroNode;
            if (activeHero != null)
            {
                ClearSubmeshes(clearHeroNode: false);
                CollectSubmeshesRecursive(activeHero);
                PopulateSubmeshList();

                var paintTab = GetTree()?.Root?.FindChild("PaintTab", true, false) as PaintTabUI;
                paintTab?.ClearHero();

                var hierarchy = GetTree()?.Root?.FindChild("HeroMeshHierarchy", true, false) as HeroMeshHierarchy;
                hierarchy?.ScanHero(activeHero);

                var shadingTab = GetTree()?.Root?.FindChild("ShadingTab", true, false) as ShadingTabUI
                              ?? GetTree()?.Root?.FindChild("ShadingTabUI", true, false) as ShadingTabUI;
                shadingTab?.SetHero(activeHero);

                var skeleton = CustomModelAutoRigger.FindSkeleton(activeHero);
                if (skeleton != null)
                {
                    var playerTab = GetTree()?.Root?.FindChild("PoseTab", true, false) as PlayerTabUI
                                 ?? GetTree()?.Root?.FindChild("PlayerTabUI", true, false) as PlayerTabUI;
                    playerTab?.SetSkeleton(skeleton);

                    var animDock = GetTree()?.Root?.FindChild("AnimationStudioDock", true, false) as DeadlockPlayground.UI.AnimationStudioDock;
                    animDock?.SetSkeleton(skeleton);
                }

                GD.Print("[CharacterTabUI] Custom model re-rigged and updated successfully!");
            }
        };
    }

    private void OnResetCustomModelPressed()
    {
        var activeHero = _currentHeroNode ?? _vpkLoader?.CurrentHeroNode;
        if (activeHero != null)
        {
            CustomModelAutoRigger.RemoveCustomModelAndRestoreHero(activeHero);
            _hasCustomModelApplied = false;
            _lastImportedOptions = null;
            _lastImportedScene = null;
            _lastImportedFilePath = null;

            ClearSubmeshes(clearHeroNode: false);
            CollectSubmeshesRecursive(activeHero);
            PopulateSubmeshList();

            var paintTab = GetTree()?.Root?.FindChild("PaintTab", true, false) as PaintTabUI;
            paintTab?.ClearHero();

            var skeleton = CustomModelAutoRigger.FindSkeleton(activeHero);
            if (skeleton != null)
            {
                skeleton.ResetBonePoses();
                var playerTab = GetTree()?.Root?.FindChild("PoseTab", true, false) as PlayerTabUI
                             ?? GetTree()?.Root?.FindChild("PlayerTabUI", true, false) as PlayerTabUI;
                playerTab?.SetSkeleton(skeleton);

                var animDock = GetTree()?.Root?.FindChild("AnimationStudioDock", true, false) as DeadlockPlayground.UI.AnimationStudioDock;
                animDock?.SetSkeleton(skeleton);
            }

            UpdateContextualButtonsVisibility();
            GD.Print("[CharacterTabUI] Reset hero to original Deadlock state and removed custom model.");
        }
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
            _btnHideAccessories.Visible = false;
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
            nameBtn.ThemeTypeVariation = "ToolbarButton";
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
        LoadParticleInternal(vpcfPath, variantName, abilityKey, null);
    }

    public void LoadParticleDirectly(string vpcfPath, string variantName = null, string abilityKey = null, string ownerHeroId = null)
    {
        variantName ??= System.IO.Path.GetFileNameWithoutExtension(vpcfPath);
        abilityKey ??= "Custom";
        LoadParticleInternal(vpcfPath, variantName, abilityKey, ownerHeroId);
    }

    private void LoadParticleInternal(string vpcfPath, string variantName, string abilityKey, string ownerHeroId)
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
            stage.LoadParticle(vpcfPath, variantName, abilityKey, _vpkLoader.CurrentHeroNode, fileLoader, basePkg, null, ownerHeroId);
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

        if (_btnHideAccessories != null && GodotObject.IsInstanceValid(_btnHideAccessories))
        {
            _btnHideAccessories.Visible = !hasParticle;
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

        var heroNode = _currentHeroNode ?? _vpkLoader?.CurrentHeroNode;
        bool hasCustom = _hasCustomModelApplied || (heroNode != null && heroNode.FindChild("Custom_Rigged_Character", true, false) != null);
        if (_customModelActionsRow != null && GodotObject.IsInstanceValid(_customModelActionsRow))
        {
            _customModelActionsRow.Visible = hasCustom;
        }
        if (_btnEditCustomModel != null && GodotObject.IsInstanceValid(_btnEditCustomModel))
        {
            _btnEditCustomModel.Disabled = !hasCustom;
        }
        if (_btnResetCustomModel != null && GodotObject.IsInstanceValid(_btnResetCustomModel))
        {
            _btnResetCustomModel.Disabled = !hasCustom;
        }
    }

    public void SetHero(Node3D heroNode)
    {
        _currentHeroNode = heroNode;
        bool hasCustom = heroNode != null && heroNode.FindChild("Custom_Rigged_Character", true, false) != null;
        if (!hasCustom)
        {
            _hasCustomModelApplied = false;
        }
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

            // Filter out original hero meshes replaced by custom model
            if (mi.HasMeta("UserHiddenByCustomModel") && mi.GetMeta("UserHiddenByCustomModel").AsBool())
            {
                return;
            }

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
        return HeroMeshHierarchy.FormatCleanMaterialDisplayName(raw);
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
            nameBtn.ThemeTypeVariation = "ToolbarButton";
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
        var stage = GetOrCreateParticleStage();
        if (_isParticleHierarchyMode || (stage != null && stage.HasActiveParticle))
        {
            if (stage != null)
            {
                for (int i = 0; i < stage.Renderers.Count; i++)
                {
                    stage.SetRendererVisibility(i, true);
                }
            }
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
        var stage = GetOrCreateParticleStage();
        if (_isParticleHierarchyMode || (stage != null && stage.HasActiveParticle))
        {
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
