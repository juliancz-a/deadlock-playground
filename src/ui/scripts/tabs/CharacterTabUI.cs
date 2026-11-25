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
    [Export] private Button _btnImportAccessory;
    [Export] private Control _accessorySlotsSection;
    [Export] private VBoxContainer _accessorySlotsContainer;
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
    private CustomModelImporterModalUI.ImportWorkflowIntent _pendingFileDialogIntent = CustomModelImporterModalUI.ImportWorkflowIntent.FullCharacter;
    private string _pendingAccessorySlotId;
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

        _btnImportAccessory ??= GetNodeOrNull<Button>("HeroSelectGroup/BtnImportAccessory")
                             ?? FindChild("BtnImportAccessory", true, false) as Button;
        _accessorySlotsSection ??= GetNodeOrNull<Control>("HeroSelectGroup/AccessorySlotsSection")
                                ?? FindChild("AccessorySlotsSection", true, false) as Control;
        _accessorySlotsContainer ??= GetNodeOrNull<VBoxContainer>("HeroSelectGroup/AccessorySlotsSection/AccessorySlotsContainer")
                                  ?? FindChild("AccessorySlotsContainer", true, false) as VBoxContainer;

        if (_btnImportAccessory != null)
        {
            var iconAcc = GD.Load<Texture2D>("res://assets/at-icons/layers.svg");
            if (iconAcc != null)
            {
                _btnImportAccessory.Icon = iconAcc;
                _btnImportAccessory.ExpandIcon = true;
                _btnImportAccessory.AddThemeConstantOverride("icon_max_width", 20);
                _btnImportAccessory.AddThemeConstantOverride("h_separation", 8);
            }
        }

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
        UpdateImportAccessoryButtonState(hasHeroOrModel);
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

        if (_btnImportAccessory != null)
        {
            _btnImportAccessory.Pressed += OnImportAccessoryPressed;
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
        await DeadlockPlayground.Core.SessionCrashGuard.SafeRunAsync(async () =>
        {
            DeadlockPlayground.Core.SessionCrashGuard.RecordBreadcrumb(
                $"Load hero: {displayName ?? heroCodename}",
                activeHeroId: heroCodename,
                activeHeroDisplayName: displayName);
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
        }, $"Load Hero '{displayName ?? heroCodename}'");
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
        await DeadlockPlayground.Core.SessionCrashGuard.SafeRunAsync(async () =>
        {
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
        }, $"Load Extra Model '{displayName ?? vmdlPath}'");
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
        await DeadlockPlayground.Core.SessionCrashGuard.SafeRunAsync(async () =>
        {
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
        }, $"Load Mod '{modDisplayTitle ?? modFilePath}'");
    }

    private string GetActiveHeroId(Node3D heroNode = null)
    {
        heroNode ??= _currentHeroNode ?? _vpkLoader?.CurrentHeroNode;
        string heroId = _vpkLoader?.LastLoadedHeroEntry?.InternalCodename;
        if (string.IsNullOrEmpty(heroId) && heroNode != null)
            heroId = heroNode.Name.ToString().Replace("Hero_", "").ToLowerInvariant();
        return heroId;
    }

    private int GetActiveAccessorySlotCount(Node3D heroNode = null)
    {
        heroNode ??= _currentHeroNode ?? _vpkLoader?.CurrentHeroNode;
        if (heroNode == null) return 0;

        var containers = CustomModelAutoRigger.CollectCustomContainers(heroNode);
        int count = 0;
        foreach (var c in containers)
        {
            if (c.Name != "Custom_Rigged_Character")
                count++;
        }
        return count;
    }

    private void UpdateImportAccessoryButtonState(bool? hasHeroOverride = null)
    {
        if (_btnImportAccessory == null || !GodotObject.IsInstanceValid(_btnImportAccessory))
            return;

        var hero = _currentHeroNode ?? _vpkLoader?.CurrentHeroNode;
        bool hasHero = hasHeroOverride ?? (hero != null);
        int accCount = GetActiveAccessorySlotCount(hero);
        bool atLimit = accCount >= CustomModelAutoRigger.MaxAccessorySlots;

        _btnImportAccessory.Disabled = !hasHero || atLimit;
        _btnImportAccessory.TooltipText = atLimit
            ? $"Accessory limit reached ({CustomModelAutoRigger.MaxAccessorySlots}/{CustomModelAutoRigger.MaxAccessorySlots}). Remove an active slot to add another."
            : $"Add a new accessory (hat, glasses, cape, weapon) or replace specific hero outfit parts (.glb / .fbx) [{accCount}/{CustomModelAutoRigger.MaxAccessorySlots}]";
    }

    private FileDialog _customModelFileDialog;

    private void EnsureCustomModelFileDialog()
    {
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
    }

    public void OnImportCustomModelPressed()
    {
        var hero = _currentHeroNode ?? _vpkLoader?.CurrentHeroNode;
        if (hero == null)
        {
            GD.PrintErr("[CharacterTabUI] Cannot import custom model: no base hero is loaded.");
            return;
        }

        _pendingFileDialogIntent = CustomModelImporterModalUI.ImportWorkflowIntent.FullCharacter;
        _pendingAccessorySlotId = null;
        EnsureCustomModelFileDialog();
        _customModelFileDialog.Title = "Select Custom 3D Character (.glb / .gltf / .fbx)";
        _customModelFileDialog.PopupCentered(new Vector2I(850, 550));
    }

    public void OnImportAccessoryPressed()
    {
        var hero = _currentHeroNode ?? _vpkLoader?.CurrentHeroNode;
        if (hero == null)
        {
            GD.PrintErr("[CharacterTabUI] Cannot import accessory: no base hero is loaded.");
            return;
        }

        int count = GetActiveAccessorySlotCount(hero);
        if (count >= CustomModelAutoRigger.MaxAccessorySlots)
        {
            GD.PrintErr($"[CharacterTabUI] Accessory slot limit ({CustomModelAutoRigger.MaxAccessorySlots}) reached.");
            UpdateImportAccessoryButtonState();
            return;
        }

        _pendingFileDialogIntent = CustomModelImporterModalUI.ImportWorkflowIntent.AccessoryAddOrReplace;
        _pendingAccessorySlotId = $"acc_{Guid.NewGuid().ToString("N")[..6]}";
        EnsureCustomModelFileDialog();
        _customModelFileDialog.Title = "Select Accessory or Outfit Part (.glb / .gltf / .fbx)";
        _customModelFileDialog.PopupCentered(new Vector2I(850, 550));
    }

    private void RefreshAfterModelOrAccessoryChange(Node3D activeHero, bool clearPaintCache = false, bool skipSkeletonReset = false)
    {
        if (activeHero == null) return;

        var hierarchy = GetTree()?.Root?.FindChild("HeroMeshHierarchy", true, false) as HeroMeshHierarchy;
        hierarchy?.ScanHero(activeHero);

        ClearSubmeshes(clearHeroNode: false);
        CollectSubmeshesRecursive(activeHero);
        PopulateSubmeshList();

        var paintTab = GetTree()?.Root?.FindChild("PaintTab", true, false) as PaintTabUI;
        if (clearPaintCache)
            paintTab?.ClearHero();
        else
            paintTab?.InvalidateHeroCache();

        var shadingTab = GetTree()?.Root?.FindChild("ShadingTab", true, false) as ShadingTabUI
                      ?? GetTree()?.Root?.FindChild("ShadingTabUI", true, false) as ShadingTabUI;
        shadingTab?.SetHero(activeHero);

        if (!skipSkeletonReset)
        {
            var skeleton = CustomModelAutoRigger.FindSkeleton(activeHero);
            if (skeleton != null)
            {
                var playerTab = GetTree()?.Root?.FindChild("PoseTab", true, false) as PlayerTabUI
                             ?? GetTree()?.Root?.FindChild("PlayerTabUI", true, false) as PlayerTabUI;
                playerTab?.SetSkeleton(skeleton);

                var animDock = GetTree()?.Root?.FindChild("AnimationStudioDock", true, false) as DeadlockPlayground.UI.AnimationStudioDock;
                animDock?.SetSkeleton(skeleton);
            }
        }

        UpdateContextualButtonsVisibility();
        RefreshAccessorySlotsList();
    }

    /// <summary>
    /// Runs a free-RAM pre-check (ImporterMemoryGuard). Only prompts on Critical (< ~0.9 GB) free RAM
    /// with Light Mode / Full Quality / Cancel + "Don't warn again this session" options, and parses
    /// the 3D scene asynchronously with a progress bar overlay.
    /// </summary>
    private async void LoadSceneForImporterModalAsync(string path, Node3D hero, Action<Node3D> onSceneReady)
    {
        var report = ImporterMemoryGuard.Evaluate(path, hero);
        bool allowCriticalLight = false;
        bool forceFullQuality = false;

        if (report.Verdict == ImporterMemoryGuard.Verdict.Critical && !ImporterMemoryGuard.SuppressPromptForSession)
        {
            // 0 = Cancel, 1 = Light Mode, 2 = Full Quality
            var tcs = new System.Threading.Tasks.TaskCompletionSource<int>();
            var dlg = new AcceptDialog
            {
                Title = "Low Memory Warning",
                OkButtonText = "Continue in Light Mode",
                Exclusive = false
            };

            var body = new VBoxContainer();
            body.AddThemeConstantOverride("separation", 10);
            var lbl = new Label
            {
                Text = report.Message,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                CustomMinimumSize = new Vector2(460, 0)
            };
            body.AddChild(lbl);

            var chkSuppress = new CheckBox
            {
                Text = "Don't warn again this session",
                ButtonPressed = false
            };
            body.AddChild(chkSuppress);
            dlg.AddChild(body);

            dlg.AddButton("Continue Full Quality", false, "full_quality");
            dlg.AddCancelButton("Cancel");

            dlg.Confirmed += () =>
            {
                if (chkSuppress.ButtonPressed) ImporterMemoryGuard.SuppressPromptForSession = true;
                tcs.TrySetResult(1);
                dlg.QueueFree();
            };
            dlg.CustomAction += (action) =>
            {
                if (action == "full_quality")
                {
                    if (chkSuppress.ButtonPressed) ImporterMemoryGuard.SuppressPromptForSession = true;
                    tcs.TrySetResult(2);
                    dlg.QueueFree();
                }
            };
            dlg.Canceled += () => { tcs.TrySetResult(0); dlg.QueueFree(); };

            GetTree()?.Root?.AddChild(dlg);
            dlg.PopupCentered(new Vector2I(520, 220));

            int choice = await tcs.Task;
            if (choice == 0) return;
            if (choice == 1)
            {
                allowCriticalLight = true;
                ImporterMemoryGuard.LightMode = true;
            }
            else
            {
                forceFullQuality = true;
                ImporterMemoryGuard.LightMode = false;
            }
        }
        else if (report.Verdict == ImporterMemoryGuard.Verdict.Critical && ImporterMemoryGuard.SuppressPromptForSession)
        {
            forceFullQuality = !ImporterMemoryGuard.LightMode;
            allowCriticalLight = ImporterMemoryGuard.LightMode;
        }
        else
        {
            // Ok or LowMemory (1.0–1.5 GB): enter directly without nagging popup; user can toggle Light Mode from footer
            ImporterMemoryGuard.LightMode = false;
        }

        // Progress overlay
        var overlay = new CanvasLayer { Layer = 115 };
        var backdrop = new ColorRect { Color = new Color(0, 0, 0, 0.45f) };
        backdrop.SetAnchorsPreset(LayoutPreset.FullRect);
        overlay.AddChild(backdrop);

        var center = new CenterContainer();
        center.SetAnchorsPreset(LayoutPreset.FullRect);
        overlay.AddChild(center);

        var panel = new PanelContainer { CustomMinimumSize = new Vector2(420, 110), ThemeTypeVariation = "ModalPanel" };
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 8);
        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 16);
        margin.AddThemeConstantOverride("margin_right", 16);
        margin.AddThemeConstantOverride("margin_top", 14);
        margin.AddThemeConstantOverride("margin_bottom", 14);
        margin.AddChild(box);
        panel.AddChild(margin);
        center.AddChild(panel);

        var lblStage = new Label { Text = "Loading 3D model..." };
        var bar = new ProgressBar { MinValue = 0, MaxValue = 100, Value = 10, CustomMinimumSize = new Vector2(0, 22) };
        box.AddChild(lblStage);
        box.AddChild(bar);
        GetTree()?.Root?.AddChild(overlay);

        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        var (scene, userMessage, _) = await ImporterMemoryGuard.LoadSceneGuardedAsync(
            path,
            hero,
            onProgress: (p, msg) =>
            {
                if (GodotObject.IsInstanceValid(bar)) bar.Value = p * 100.0;
                if (GodotObject.IsInstanceValid(lblStage)) lblStage.Text = msg;
            },
            allowCriticalInLightMode: allowCriticalLight,
            forceFullQuality: forceFullQuality);

        if (GodotObject.IsInstanceValid(overlay)) overlay.QueueFree();

        if (scene == null)
        {
            if (!string.IsNullOrEmpty(userMessage))
                ShowImporterNotice("Model Import Failed", userMessage);
            GD.PrintErr($"[CharacterTabUI] Could not parse 3D scene from '{path}'.");
            return;
        }

        onSceneReady?.Invoke(scene);
    }

    private void ShowImporterNotice(string title, string message)
    {
        var dlg = new AcceptDialog { Title = title, DialogText = message, Exclusive = false };
        dlg.Confirmed += () => dlg.QueueFree();
        dlg.Canceled += () => dlg.QueueFree();
        GetTree()?.Root?.AddChild(dlg);
        dlg.PopupCentered(new Vector2I(520, 220));
    }

    private void OnCustomModelFileSelected(string filePath)
    {
        var intent = _pendingFileDialogIntent;
        var slotId = _pendingAccessorySlotId;
        _pendingFileDialogIntent = CustomModelImporterModalUI.ImportWorkflowIntent.FullCharacter;
        _pendingAccessorySlotId = null;

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

        LoadSceneForImporterModalAsync(filePath, hero, sceneRoot =>
        {
            var oldModal = GetTree()?.Root?.FindChild("CustomModelImporterModal", true, false) as CustomModelImporterModalUI;
            oldModal?.QueueFree();

            var modal = new CustomModelImporterModalUI { Name = "CustomModelImporterModal" };
            GetTree()?.Root?.AddChild(modal);

            modal.Open(hero, sceneRoot, filePath, null, intent, slotId);
            modal.RiggingConfirmed += () =>
            {
                var opts = modal.LastRiggingOptions;
                var activeHero = _currentHeroNode ?? _vpkLoader?.CurrentHeroNode;
                if (activeHero == null) return;

                if (opts != null && !string.IsNullOrEmpty(opts.SlotId))
                {
                    string heroId = GetActiveHeroId(activeHero);
                    string displayName = _vpkLoader?.LastLoadedHeroEntry?.DisplayName ?? heroId;
                    string accName = !string.IsNullOrWhiteSpace(opts.AccessoryName)
                        ? opts.AccessoryName
                        : Path.GetFileNameWithoutExtension(filePath);

                    DeadlockPlayground.Workspace.ModWorkspaceSession.Instance.StageAccessory(
                        heroId, displayName, opts.SlotId, accName, filePath, opts);
                    RefreshAfterModelOrAccessoryChange(activeHero);
                    GD.Print($"[CharacterTabUI] Accessory/outfit slot '{accName}' ({opts.SlotId}) bound and staged!");
                }
                else
                {
                    _lastImportedOptions = opts;
                    _lastImportedFilePath = filePath;
                    _lastImportedScene = sceneRoot;
                    _hasCustomModelApplied = true;
                    RegisterCustomModelInWorkspace(filePath, opts);
                    RefreshAfterModelOrAccessoryChange(activeHero);
                    GD.Print("[CharacterTabUI] Custom model rigging confirmed and bound successfully!");
                }
            };
        });
    }

    public void OpenImporterFromRecoveryDraft(DeadlockPlayground.Core.SessionCrashGuard.ImporterDraftData draft)
    {
        var hero = _currentHeroNode ?? _vpkLoader?.CurrentHeroNode;
        if (hero == null || draft == null || string.IsNullOrEmpty(draft.ModelFilePath) || !File.Exists(draft.ModelFilePath))
            return;

        LoadSceneForImporterModalAsync(draft.ModelFilePath, hero, sceneRoot =>
        {
            var oldModal = GetTree()?.Root?.FindChild("CustomModelImporterModal", true, false) as CustomModelImporterModalUI;
            oldModal?.QueueFree();

            var modal = new CustomModelImporterModalUI { Name = "CustomModelImporterModal" };
            GetTree()?.Root?.AddChild(modal);

            var initialOpts = draft.ModelConfig?.ToRiggingOptions();
            var intent = (CustomModelImporterModalUI.ImportWorkflowIntent)draft.WorkflowIntent;
            modal.Open(hero, sceneRoot, draft.ModelFilePath, initialOpts, intent, draft.AccessorySlotId);

            modal.RiggingConfirmed += () =>
            {
                var opts = modal.LastRiggingOptions;
                var activeHero = _currentHeroNode ?? _vpkLoader?.CurrentHeroNode;
                if (activeHero == null) return;

                if (opts != null && !string.IsNullOrEmpty(opts.SlotId))
                {
                    string heroId = GetActiveHeroId(activeHero);
                    string displayName = _vpkLoader?.LastLoadedHeroEntry?.DisplayName ?? heroId;
                    string accName = !string.IsNullOrWhiteSpace(opts.AccessoryName)
                        ? opts.AccessoryName
                        : Path.GetFileNameWithoutExtension(draft.ModelFilePath);
                    DeadlockPlayground.Workspace.ModWorkspaceSession.Instance.StageAccessory(
                        heroId, displayName, opts.SlotId, accName, draft.ModelFilePath, opts);
                }
                else
                {
                    _lastImportedOptions = opts;
                    _lastImportedFilePath = draft.ModelFilePath;
                    _lastImportedScene = sceneRoot;
                    _hasCustomModelApplied = true;
                    RegisterCustomModelInWorkspace(draft.ModelFilePath, opts);
                }
                RefreshAfterModelOrAccessoryChange(activeHero);
            };
        });
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

        LoadSceneForImporterModalAsync(_lastImportedFilePath, hero, sceneRoot =>
        {
            var existingContainer = hero.FindChild("Custom_Rigged_Character", true, false) as Node3D;
            if (existingContainer != null)
            {
                existingContainer.Visible = false;
            }

            var oldModal = GetTree()?.Root?.FindChild("CustomModelImporterModal", true, false) as CustomModelImporterModalUI;
            oldModal?.QueueFree();

            var modal = new CustomModelImporterModalUI { Name = "CustomModelImporterModal" };
            GetTree()?.Root?.AddChild(modal);

            modal.Open(hero, sceneRoot, _lastImportedFilePath, _lastImportedOptions,
                CustomModelImporterModalUI.ImportWorkflowIntent.FullCharacter, null);

            modal.ModalCancelled += () =>
            {
                if (existingContainer != null && GodotObject.IsInstanceValid(existingContainer))
                {
                    existingContainer.Visible = true;
                }
                if (hero != null && GodotObject.IsInstanceValid(hero))
                {
                    CustomModelAutoRigger.RecomputeHeroReplacementVisibility(hero);
                }
            };

            modal.RiggingConfirmed += () =>
            {
                _lastImportedOptions = modal.LastRiggingOptions;
                _lastImportedScene = sceneRoot;
                _hasCustomModelApplied = true;

                var activeHero = _currentHeroNode ?? _vpkLoader?.CurrentHeroNode;
                if (activeHero != null)
                {
                    RegisterCustomModelInWorkspace(_lastImportedFilePath, _lastImportedOptions);
                    RefreshAfterModelOrAccessoryChange(activeHero);
                    GD.Print("[CharacterTabUI] Custom model re-rigged and updated successfully!");
                }
            };
        });
    }

    private void OnEditAccessorySlotPressed(string slotId)
    {
        var hero = _currentHeroNode ?? _vpkLoader?.CurrentHeroNode;
        if (hero == null || string.IsNullOrEmpty(slotId)) return;

        string heroId = GetActiveHeroId(hero);
        var session = DeadlockPlayground.Workspace.ModWorkspaceSession.Instance;
        DeadlockPlayground.Workspace.WorkspaceAccessoryEntry entry = null;
        if (!string.IsNullOrEmpty(heroId) && session?.Manifest?.Heroes != null &&
            session.Manifest.Heroes.TryGetValue(heroId, out var heroEntry))
        {
            entry = heroEntry.Accessories?.FirstOrDefault(a =>
                string.Equals(a.SlotId, slotId, StringComparison.OrdinalIgnoreCase));
        }

        string diskPath = entry?.ModelConfig != null
            ? session.ResolveCustomModelDiskPath(entry.ModelConfig)
            : null;
        if (string.IsNullOrEmpty(diskPath) || !File.Exists(diskPath))
        {
            GD.PrintErr($"[CharacterTabUI] Source file for accessory slot '{slotId}' is missing or deleted.");
            return;
        }

        LoadSceneForImporterModalAsync(diskPath, hero, sceneRoot =>
        {
            string containerName = CustomModelAutoRigger.GetContainerNameForSlot(slotId);
            var existingContainer = hero.FindChild(containerName, true, false) as Node3D;
            bool prevVis = existingContainer == null || existingContainer.Visible;
            if (existingContainer != null)
            {
                existingContainer.Visible = false;
            }

            var oldModal = GetTree()?.Root?.FindChild("CustomModelImporterModal", true, false) as CustomModelImporterModalUI;
            oldModal?.QueueFree();

            var modal = new CustomModelImporterModalUI { Name = "CustomModelImporterModal" };
            GetTree()?.Root?.AddChild(modal);

            var initialOpts = entry.ModelConfig.ToRiggingOptions();
            initialOpts.SlotId = slotId;
            initialOpts.AccessoryName = entry.DisplayName;

            modal.Open(hero, sceneRoot, diskPath, initialOpts,
                CustomModelImporterModalUI.ImportWorkflowIntent.AccessoryAddOrReplace, slotId);

            modal.ModalCancelled += () =>
            {
                if (existingContainer != null && GodotObject.IsInstanceValid(existingContainer))
                {
                    existingContainer.Visible = prevVis;
                }
                if (hero != null && GodotObject.IsInstanceValid(hero))
                {
                    CustomModelAutoRigger.RecomputeHeroReplacementVisibility(hero);
                }
            };

            modal.RiggingConfirmed += () =>
            {
                var opts = modal.LastRiggingOptions;
                var activeHero = _currentHeroNode ?? _vpkLoader?.CurrentHeroNode;
                if (activeHero == null || opts == null) return;

                string activeHeroId = GetActiveHeroId(activeHero);
                string displayName = _vpkLoader?.LastLoadedHeroEntry?.DisplayName ?? activeHeroId;
                string accName = !string.IsNullOrWhiteSpace(opts.AccessoryName)
                    ? opts.AccessoryName
                    : entry.DisplayName;

                session?.StageAccessory(activeHeroId, displayName, slotId, accName, diskPath, opts);
                RefreshAfterModelOrAccessoryChange(activeHero);
                GD.Print($"[CharacterTabUI] Accessory slot '{accName}' ({slotId}) updated successfully!");
            };
        });
    }

    private void OnDuplicateMirrorAccessorySlotPressed(string slotId)
    {
        var hero = _currentHeroNode ?? _vpkLoader?.CurrentHeroNode;
        if (hero == null || string.IsNullOrEmpty(slotId)) return;

        if (GetActiveAccessorySlotCount(hero) >= CustomModelAutoRigger.MaxAccessorySlots)
        {
            GD.PrintErr($"[CharacterTabUI] Cannot duplicate/mirror accessory slot '{slotId}': limit ({CustomModelAutoRigger.MaxAccessorySlots}) reached.");
            UpdateImportAccessoryButtonState(true);
            return;
        }

        string heroId = GetActiveHeroId(hero);
        var session = DeadlockPlayground.Workspace.ModWorkspaceSession.Instance;
        DeadlockPlayground.Workspace.WorkspaceAccessoryEntry entry = null;
        if (!string.IsNullOrEmpty(heroId) && session?.Manifest?.Heroes != null &&
            session.Manifest.Heroes.TryGetValue(heroId, out var heroEntry))
        {
            entry = heroEntry.Accessories?.FirstOrDefault(a =>
                string.Equals(a.SlotId, slotId, StringComparison.OrdinalIgnoreCase));
        }

        string diskPath = entry?.ModelConfig != null
            ? session.ResolveCustomModelDiskPath(entry.ModelConfig)
            : null;
        if (string.IsNullOrEmpty(diskPath) || !File.Exists(diskPath))
        {
            GD.PrintErr($"[CharacterTabUI] Cannot duplicate/mirror '{slotId}': staged source file not found.");
            return;
        }

        var dupReport = ImporterMemoryGuard.Evaluate(diskPath, hero);
        if (dupReport.Verdict == ImporterMemoryGuard.Verdict.Critical && !ImporterMemoryGuard.SuppressPromptForSession)
        {
            GD.PushWarning($"[CharacterTabUI] Blocked mirror/duplicate due to critical RAM: {dupReport.Message}");
            ShowImporterNotice("Low Memory Warning", dupReport.Message);
            return;
        }

        var sceneRoot = CustomModelAutoRigger.LoadAndSanitize3DModelScene(diskPath);
        if (sceneRoot == null)
        {
            GD.PrintErr($"[CharacterTabUI] Failed to reload 3D model from '{diskPath}'.");
            return;
        }

        try
        {
            var opts = BuildMirroredAccessoryOptions(entry.DisplayName, entry.ModelConfig, sceneRoot, hero);
            string displayName = _vpkLoader?.LastLoadedHeroEntry?.DisplayName ?? heroId;
            var rigRes = CustomModelAutoRigger.RigAndBind(sceneRoot, hero, opts);
            if (!rigRes.Success)
            {
                GD.PrintErr($"[CharacterTabUI] Mirror & Duplicate failed: {rigRes.ErrorMessage}");
                return;
            }

            session?.StageAccessory(heroId, displayName, opts.SlotId, opts.AccessoryName, diskPath, opts);
            RefreshAfterModelOrAccessoryChange(hero);
            GD.Print($"[CharacterTabUI] Duplicated & mirrored accessory '{opts.AccessoryName}' ({opts.SlotId}, Bone={opts.RigidBoneName}, FlipX={opts.FlipX}).");
        }
        finally
        {
            if (GodotObject.IsInstanceValid(sceneRoot) && sceneRoot.GetParent() == null)
            {
                sceneRoot.QueueFree();
            }
        }
    }

    /// <summary>
    /// Clones an accessory slot's configuration into a new slot with X-mirrored geometry,
    /// swapped L &lt;-&gt; R bone targets, and hero-X-mirrored placement.
    /// </summary>
    public static CustomModelAutoRigger.RiggingOptions BuildMirroredAccessoryOptions(
        string sourceDisplayName,
        DeadlockPlayground.Workspace.WorkspaceCustomModelEntry sourceConfig,
        Node3D loadedScene,
        Node3D heroNode)
    {
        var opts = sourceConfig.ToRiggingOptions();
        opts.SlotId = $"acc_{Guid.NewGuid().ToString("N")[..6]}";
        string baseName = string.IsNullOrWhiteSpace(sourceDisplayName)
            ? (string.IsNullOrWhiteSpace(opts.AccessoryName) ? "Accessory" : opts.AccessoryName)
            : sourceDisplayName;
        if (baseName.EndsWith(" (L)", StringComparison.OrdinalIgnoreCase))
            opts.AccessoryName = baseName[..^4] + " (R)";
        else if (baseName.EndsWith(" (R)", StringComparison.OrdinalIgnoreCase))
            opts.AccessoryName = baseName[..^4] + " (L)";
        else if (baseName.EndsWith(" (Mirrored)", StringComparison.OrdinalIgnoreCase))
            opts.AccessoryName = baseName[..^11];
        else
            opts.AccessoryName = baseName + " (Mirrored)";

        opts.FlipX = !opts.FlipX;

        var skel = CustomModelAutoRigger.FindSkeleton(heroNode);
        string SwapIfValidBone(string bn)
        {
            if (string.IsNullOrEmpty(bn)) return bn;
            string mir = CustomModelAutoRigger.MirrorBoneNameLtoR(bn);
            if (!string.IsNullOrEmpty(mir) && (skel == null || skel.FindBone(mir) >= 0))
                return mir;
            return bn;
        }
        opts.RigidBoneName = SwapIfValidBone(opts.RigidBoneName);
        opts.OriginalMeshDeformBoneName = SwapIfValidBone(opts.OriginalMeshDeformBoneName);
        if (opts.SubmeshAttachmentSockets != null && opts.SubmeshAttachmentSockets.Count > 0)
        {
            var keys = opts.SubmeshAttachmentSockets.Keys.ToList();
            foreach (var k in keys)
                opts.SubmeshAttachmentSockets[k] = SwapIfValidBone(opts.SubmeshAttachmentSockets[k]);
        }

        Transform3D curXf = opts.AlignedTransform;
        if (curXf == Transform3D.Identity && (opts.RootPosition != Vector3.Zero || opts.RootRotationDeg != Vector3.Zero || MathF.Abs(opts.RootScale - 1f) > 1e-4f || opts.RootScaleXYZ != Vector3.One))
        {
            Basis rb = Basis.FromEuler(new Vector3(Mathf.DegToRad(opts.RootRotationDeg.X), Mathf.DegToRad(opts.RootRotationDeg.Y), Mathf.DegToRad(opts.RootRotationDeg.Z)), EulerOrder.Yxz);
            Basis sb = Basis.FromScale(opts.RootScaleXYZ * opts.RootScale);
            curXf = new Transform3D(rb * sb, opts.RootPosition);
        }

        Vector3 localCenter = Vector3.Zero;
        if (loadedScene != null)
        {
            var filter = opts.IncludedImportedSubmeshNames != null && opts.IncludedImportedSubmeshNames.Count > 0
                ? new HashSet<string>(opts.IncludedImportedSubmeshNames, StringComparer.OrdinalIgnoreCase)
                : null;
            var (minV, maxV, ok) = CustomModelAutoRigger.ComputePristineModelBounds(loadedScene, filter);
            if (ok) localCenter = (minV + maxV) * 0.5f;
        }

        Vector3 worldCenter = curXf * localCenter;
        Vector3 mirroredWorldCenter = new Vector3(-worldCenter.X, worldCenter.Y, worldCenter.Z);
        Vector3 mirRot = new Vector3(opts.RootRotationDeg.X, -opts.RootRotationDeg.Y, -opts.RootRotationDeg.Z);
        Basis mirRotBasis = Basis.FromEuler(
            new Vector3(Mathf.DegToRad(mirRot.X), Mathf.DegToRad(mirRot.Y), Mathf.DegToRad(mirRot.Z)),
            EulerOrder.Yxz);
        float sc = opts.RootScale > 0.0001f ? opts.RootScale : 1.0f;
        Basis mirScaleBasis = Basis.FromScale(opts.RootScaleXYZ * sc);
        Basis mirBasis = mirRotBasis * mirScaleBasis;
        Vector3 mirOrigin = mirroredWorldCenter - (mirBasis * localCenter);

        opts.RootRotationDeg = mirRot;
        opts.RootPosition = mirOrigin;
        opts.AlignedTransform = new Transform3D(mirBasis, mirOrigin);
        return opts;
    }

    public void RemoveAccessorySlotById(string slotId)
    {
        var activeHero = _currentHeroNode ?? _vpkLoader?.CurrentHeroNode;
        if (activeHero == null || string.IsNullOrEmpty(slotId)) return;

        string heroId = GetActiveHeroId(activeHero);
        if (!string.IsNullOrEmpty(heroId))
        {
            DeadlockPlayground.Workspace.ModWorkspaceSession.Instance.DiscardAccessory(heroId, slotId);
        }

        CustomModelAutoRigger.RemoveAccessorySlot(activeHero, slotId);
        RefreshAfterModelOrAccessoryChange(activeHero);
        GD.Print($"[CharacterTabUI] Removed accessory slot '{slotId}' and recomputed hero visibility.");
    }

    private void RefreshAccessorySlotsList()
    {
        if (_accessorySlotsContainer == null || !GodotObject.IsInstanceValid(_accessorySlotsContainer))
        {
            UpdateImportAccessoryButtonState();
            return;
        }

        foreach (Node child in _accessorySlotsContainer.GetChildren())
        {
            child.QueueFree();
        }

        var hero = _currentHeroNode ?? _vpkLoader?.CurrentHeroNode;
        if (hero == null)
        {
            if (_accessorySlotsSection != null && GodotObject.IsInstanceValid(_accessorySlotsSection))
                _accessorySlotsSection.Visible = false;
            UpdateImportAccessoryButtonState(false);
            return;
        }

        string heroId = GetActiveHeroId(hero);
        var session = DeadlockPlayground.Workspace.ModWorkspaceSession.Instance;
        DeadlockPlayground.Workspace.WorkspaceHeroEntry heroEntry = null;
        if (!string.IsNullOrEmpty(heroId))
            session?.Manifest?.Heroes?.TryGetValue(heroId, out heroEntry);

        var containers = CustomModelAutoRigger.CollectCustomContainers(hero)
            .Where(c => c.Name != "Custom_Rigged_Character")
            .ToList();

        if (containers.Count == 0)
        {
            if (_accessorySlotsSection != null && GodotObject.IsInstanceValid(_accessorySlotsSection))
                _accessorySlotsSection.Visible = false;
            UpdateImportAccessoryButtonState(true);
            return;
        }

        if (_accessorySlotsSection != null && GodotObject.IsInstanceValid(_accessorySlotsSection))
            _accessorySlotsSection.Visible = true;

        var iconEyeOpen = GD.Load<Texture2D>("res://assets/at-icons/eye.svg");
        var iconEyeClosed = GD.Load<Texture2D>("res://assets/at-icons/eye_closed.svg");
        var iconMirror = GD.Load<Texture2D>("res://assets/at-icons/mirror_horizontally.svg");
        var iconRename = GD.Load<Texture2D>("res://assets/at-icons/pencil.svg");
        var iconEdit = GD.Load<Texture2D>("res://assets/at-icons/sliders.svg");
        var iconTrash = GD.Load<Texture2D>("res://assets/at-icons/trash.svg");

        foreach (var container in containers)
        {
            string cname = container.Name.ToString();
            string slotId = cname.StartsWith("Custom_Accessory_", StringComparison.Ordinal)
                ? cname.Substring("Custom_Accessory_".Length)
                : cname;

            var entry = heroEntry?.Accessories?.FirstOrDefault(a =>
                string.Equals(a.SlotId, slotId, StringComparison.OrdinalIgnoreCase));

            string title = !string.IsNullOrWhiteSpace(entry?.DisplayName)
                ? entry.DisplayName
                : (container.HasMeta("AccessorySlotName")
                    ? container.GetMeta("AccessorySlotName").AsString()
                    : slotId);

            bool isReplace = entry?.ModelConfig?.Intent == (int)CustomModelAutoRigger.AccessoryIntent.ReplacePart
                || (entry?.ModelConfig?.ExplicitReplacedTargets?.Count ?? 0) > 0;
            string modeTag = isReplace ? "[Replace]" : "[Add]";
            string targetSummary = entry?.ModelConfig?.RigidBoneName;
            string tooltip = isReplace && (entry?.ModelConfig?.ExplicitReplacedTargets?.Count ?? 0) > 0
                ? $"Replaces: {string.Join(", ", entry.ModelConfig.ExplicitReplacedTargets)}"
                : (!string.IsNullOrEmpty(targetSummary) ? $"Attached to: {targetSummary}" : "Custom accessory slot");

            var rowPanel = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            var margin = new MarginContainer();
            margin.AddThemeConstantOverride("margin_left", 6);
            margin.AddThemeConstantOverride("margin_right", 6);
            margin.AddThemeConstantOverride("margin_top", 4);
            margin.AddThemeConstantOverride("margin_bottom", 4);
            rowPanel.AddChild(margin);

            var hbox = new HBoxContainer();
            hbox.AddThemeConstantOverride("separation", 6);
            margin.AddChild(hbox);

            bool isVisible = container.Visible;
            var btnVis = new Button
            {
                CustomMinimumSize = new Vector2(28, 28),
                Icon = isVisible ? iconEyeOpen : iconEyeClosed,
                ExpandIcon = true,
                IconAlignment = HorizontalAlignment.Center,
                ThemeTypeVariation = "IconButton",
                TooltipText = isVisible ? "Hide accessory slot" : "Show accessory slot"
            };
            btnVis.AddThemeConstantOverride("icon_max_width", 16);
            btnVis.Pressed += () =>
            {
                if (!GodotObject.IsInstanceValid(container)) return;
                bool nextVis = !container.Visible;
                var hierarchy = GetTree()?.Root?.FindChild("HeroMeshHierarchy", true, false) as HeroMeshHierarchy;
                // Tear down split _surf_* clones first so RecomputeHeroDeformations restores/re-applies cuts
                // cleanly on pristine composite parent meshes before ScanHero re-splits them.
                hierarchy?.CleanupGeneratedSubmeshes();
                container.Visible = nextVis;
                foreach (var accMi in CustomModelAutoRigger.CollectMeshes(container))
                {
                    if (accMi != null && GodotObject.IsInstanceValid(accMi))
                    {
                        accMi.Visible = nextVis;
                        accMi.SetMeta("UserVisibility", nextVis);
                    }
                }
                if (!string.IsNullOrEmpty(heroId))
                    session?.SetAccessoryVisibility(heroId, slotId, nextVis);
                CustomModelAutoRigger.RecomputeHeroReplacementVisibility(hero);
                RefreshAfterModelOrAccessoryChange(hero, clearPaintCache: false, skipSkeletonReset: true);
            };
            hbox.AddChild(btnVis);

            var lblName = new Label
            {
                Text = $"{modeTag} {title}",
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
                TooltipText = tooltip,
                MouseFilter = MouseFilterEnum.Stop
            };
            lblName.AddThemeFontSizeOverride("font_size", 12);
            if (!isVisible)
                lblName.Modulate = new Color(0.65f, 0.65f, 0.65f, 0.8f);
            hbox.AddChild(lblName);

            var btnMirrorSlot = new Button
            {
                CustomMinimumSize = new Vector2(28, 28),
                Icon = iconMirror,
                ExpandIcon = true,
                IconAlignment = HorizontalAlignment.Center,
                ThemeTypeVariation = "IconButton",
                TooltipText = "Duplicate & Mirror X (L <-> R bone + flipped geometry, e.g. copy Left Sneaker to Right Foot)",
                Disabled = entry == null || containers.Count >= CustomModelAutoRigger.MaxAccessorySlots
            };
            btnMirrorSlot.AddThemeConstantOverride("icon_max_width", 16);
            btnMirrorSlot.Pressed += () => OnDuplicateMirrorAccessorySlotPressed(slotId);
            hbox.AddChild(btnMirrorSlot);

            var btnRenameSlot = new Button
            {
                CustomMinimumSize = new Vector2(28, 28),
                Icon = iconRename,
                ExpandIcon = true,
                IconAlignment = HorizontalAlignment.Center,
                ThemeTypeVariation = "IconButton",
                TooltipText = "Rename accessory / mesh slot"
            };
            btnRenameSlot.AddThemeConstantOverride("icon_max_width", 16);
            string capturedTitle = title;
            btnRenameSlot.Pressed += () => PromptRenameAccessorySlot(container, hero, heroId, slotId, capturedTitle);
            hbox.AddChild(btnRenameSlot);

            var btnEditSlot = new Button
            {
                CustomMinimumSize = new Vector2(28, 28),
                Icon = iconEdit,
                ExpandIcon = true,
                IconAlignment = HorizontalAlignment.Center,
                ThemeTypeVariation = "IconButton",
                TooltipText = "Edit accessory transform, bone attachment, or replaced parts",
                Disabled = entry == null
            };
            btnEditSlot.AddThemeConstantOverride("icon_max_width", 16);
            btnEditSlot.Pressed += () => OnEditAccessorySlotPressed(slotId);
            hbox.AddChild(btnEditSlot);

            var btnRemoveSlot = new Button
            {
                CustomMinimumSize = new Vector2(28, 28),
                Icon = iconTrash,
                ExpandIcon = true,
                IconAlignment = HorizontalAlignment.Center,
                ThemeTypeVariation = "IconButton",
                TooltipText = "Remove accessory slot and restore any replaced hero parts"
            };
            btnRemoveSlot.AddThemeConstantOverride("icon_max_width", 16);
            btnRemoveSlot.AddThemeColorOverride("icon_normal_color", new Color(0.88f, 0.42f, 0.42f, 1.0f));
            btnRemoveSlot.AddThemeColorOverride("icon_hover_color", new Color(1.0f, 0.56f, 0.56f, 1.0f));
            btnRemoveSlot.AddThemeColorOverride("icon_pressed_color", new Color(1.0f, 0.35f, 0.35f, 1.0f));
            btnRemoveSlot.Pressed += () => RemoveAccessorySlotById(slotId);
            hbox.AddChild(btnRemoveSlot);

            _accessorySlotsContainer.AddChild(rowPanel);
        }

        UpdateImportAccessoryButtonState(true);
    }

    private void PromptRenameAccessorySlot(Node3D container, Node3D hero, string heroId, string slotId, string currentName)
    {
        if (container == null || !GodotObject.IsInstanceValid(container)) return;

        var dlg = new ConfirmationDialog
        {
            Title = "Rename Accessory / Mesh Slot",
            OkButtonText = "Rename",
            MinSize = new Vector2I(360, 120)
        };
        var vbox = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        vbox.AddThemeConstantOverride("separation", 6);
        vbox.AddChild(new Label { Text = "Enter a new name for this accessory slot / mesh:" });
        var input = new LineEdit
        {
            Text = currentName ?? "",
            PlaceholderText = "Accessory / mesh name...",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SelectAllOnFocus = true
        };
        vbox.AddChild(input);
        dlg.AddChild(vbox);

        void ApplyRename()
        {
            string nextName = input.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(nextName))
            {
                if (GodotObject.IsInstanceValid(container))
                {
                    container.SetMeta("AccessorySlotName", nextName);
                    foreach (var mi in CustomModelAutoRigger.CollectMeshes(container))
                    {
                        mi.SetMeta("AccessorySlotName", nextName);
                    }
                }
                if (!string.IsNullOrEmpty(heroId))
                {
                    DeadlockPlayground.Workspace.ModWorkspaceSession.Instance?.RenameAccessorySlot(heroId, slotId, nextName);
                }
                RefreshAfterModelOrAccessoryChange(hero);
            }
            dlg.QueueFree();
        }

        input.TextSubmitted += (_) => ApplyRename();
        dlg.Confirmed += ApplyRename;
        dlg.Canceled += () => dlg.QueueFree();

        AddChild(dlg);
        dlg.PopupCentered();
        input.GrabFocus();
    }

    private void RegisterCustomModelInWorkspace(string modelFilePath, DeadlockPlayground.Importer.CustomModelAutoRigger.RiggingOptions options)
    {
        if (string.IsNullOrEmpty(modelFilePath) || options == null) return;
        string heroId = GetActiveHeroId();
        if (string.IsNullOrEmpty(heroId)) return;

        string displayName = _vpkLoader?.LastLoadedHeroEntry?.DisplayName ?? heroId;
        DeadlockPlayground.Workspace.ModWorkspaceSession.Instance.StageCustomModel(heroId, displayName, modelFilePath, options);
    }

    /// <summary>
    /// Async wrapper around <see cref="RestoreCustomModelFromWorkspace"/> that runs a pre-flight
    /// <see cref="ImporterMemoryGuard"/> check across all staged custom models/accessories in the .dpp
    /// and prompts the user before loading if free RAM or Windows commit headroom is critically low.
    /// </summary>
    public async System.Threading.Tasks.Task<bool> RestoreCustomModelFromWorkspaceAsync(Node3D heroNode, string heroId)
    {
        if (heroNode == null || string.IsNullOrEmpty(heroId)) return false;
        var session = DeadlockPlayground.Workspace.ModWorkspaceSession.Instance;
        if (session?.Manifest?.Heroes == null ||
            !session.Manifest.Heroes.TryGetValue(heroId, out var heroEntry) ||
            heroEntry == null)
            return false;

        var modelPaths = new List<string>();
        if (heroEntry.CustomModel != null)
        {
            string p = session.ResolveCustomModelDiskPath(heroEntry.CustomModel);
            if (!string.IsNullOrEmpty(p)) modelPaths.Add(p);
        }
        if (heroEntry.Accessories != null)
        {
            foreach (var acc in heroEntry.Accessories)
            {
                if (acc?.ModelConfig == null) continue;
                string p = session.ResolveCustomModelDiskPath(acc.ModelConfig);
                if (!string.IsNullOrEmpty(p)) modelPaths.Add(p);
            }
        }
        if (modelPaths.Count == 0) return false;

        ImporterMemoryGuard.Log("before .dpp custom model restore");
        var report = ImporterMemoryGuard.EvaluateBatch(modelPaths, heroNode);
        if (report.Verdict == ImporterMemoryGuard.Verdict.Critical && !ImporterMemoryGuard.SuppressPromptForSession)
        {
            var tcs = new System.Threading.Tasks.TaskCompletionSource<int>();
            var dlg = new AcceptDialog
            {
                Title = "Low Memory Warning — Loading Project Models",
                OkButtonText = "Continue (Light Mode)",
                Exclusive = false
            };

            var body = new VBoxContainer();
            body.AddThemeConstantOverride("separation", 10);
            body.AddChild(new Label
            {
                Text = report.Message,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                CustomMinimumSize = new Vector2(460, 0)
            });
            var chkSuppress = new CheckBox { Text = "Don't warn again this session" };
            body.AddChild(chkSuppress);
            dlg.AddChild(body);
            dlg.AddCancelButton("Skip Custom Models");

            dlg.Confirmed += () =>
            {
                if (chkSuppress.ButtonPressed) ImporterMemoryGuard.SuppressPromptForSession = true;
                ImporterMemoryGuard.LightMode = true;
                tcs.TrySetResult(1);
                dlg.QueueFree();
            };
            dlg.Canceled += () =>
            {
                tcs.TrySetResult(0);
                dlg.QueueFree();
            };

            GetTree()?.Root?.AddChild(dlg);
            dlg.PopupCentered(new Vector2I(520, 220));

            int choice = await tcs.Task;
            if (choice == 0)
            {
                GD.PushWarning("[CharacterTabUI] User skipped restoring custom models due to low memory.");
                return false;
            }
        }
        else if (report.Verdict == ImporterMemoryGuard.Verdict.LowMemory)
        {
            ImporterMemoryGuard.LightMode = true;
        }

        return RestoreCustomModelFromWorkspace(heroNode, heroId);
    }

    public bool RestoreCustomModelFromWorkspace(Node3D heroNode, string heroId)
    {
        if (heroNode == null || string.IsNullOrEmpty(heroId)) return false;
        var session = DeadlockPlayground.Workspace.ModWorkspaceSession.Instance;
        if (session?.Manifest?.Heroes == null ||
            !session.Manifest.Heroes.TryGetValue(heroId, out var heroEntry) ||
            heroEntry == null)
            return false;

        bool hasCustomModel = heroEntry.CustomModel != null;
        bool hasAccessories = heroEntry.Accessories != null && heroEntry.Accessories.Count > 0;
        if (!hasCustomModel && !hasAccessories)
            return false;

        bool restoredAny = false;
        var failureReasons = new List<string>();

        if (hasCustomModel)
        {
            string modelDiskPath = session.ResolveCustomModelDiskPath(heroEntry.CustomModel);
            if (!DeadlockPlayground.Workspace.ModWorkspaceSession.ValidateModelExternalDependencies(modelDiskPath, out string depErr))
            {
                GD.PrintErr($"[CharacterTabUI] Cannot restore custom model: {depErr}");
                failureReasons.Add(depErr);
            }
            else
            {
                var sceneRoot = CustomModelAutoRigger.LoadAndSanitize3DModelScene(modelDiskPath);
                if (sceneRoot == null)
                {
                    string err = $"Failed to parse saved custom model '{Path.GetFileName(modelDiskPath)}'.";
                    GD.PrintErr($"[CharacterTabUI] {err}");
                    failureReasons.Add(err);
                }
                else
                {
                    var options = heroEntry.CustomModel.ToRiggingOptions();
                    options.IsPreviewOnly = false;
                    options.SlotId = null;

                    var res = CustomModelAutoRigger.RigAndBind(sceneRoot, heroNode, options);
                    if (!res.Success)
                    {
                        sceneRoot.QueueFree();
                        string err = $"Failed to re-bind saved custom model: {res.ErrorMessage}";
                        GD.PrintErr($"[CharacterTabUI] {err}");
                        failureReasons.Add(err);
                    }
                    else
                    {
                        _lastImportedOptions = options;
                        _lastImportedFilePath = modelDiskPath;
                        _lastImportedScene = sceneRoot;
                        _hasCustomModelApplied = true;
                        restoredAny = true;
                    }
                }
            }
        }

        if (hasAccessories)
        {
            int restoredSlots = 0;
            foreach (var acc in heroEntry.Accessories)
            {
                if (acc?.ModelConfig == null || restoredSlots >= CustomModelAutoRigger.MaxAccessorySlots)
                    continue;

                string accDiskPath = session.ResolveCustomModelDiskPath(acc.ModelConfig);
                if (!DeadlockPlayground.Workspace.ModWorkspaceSession.ValidateModelExternalDependencies(accDiskPath, out string accDepErr))
                {
                    GD.PrintErr($"[CharacterTabUI] Cannot restore accessory '{acc.DisplayName}': {accDepErr}");
                    failureReasons.Add($"Accessory '{acc.DisplayName}': {accDepErr}");
                    continue;
                }

                var accScene = CustomModelAutoRigger.LoadAndSanitize3DModelScene(accDiskPath);
                if (accScene == null)
                {
                    string err = $"Failed to parse saved accessory '{acc.DisplayName}'.";
                    GD.PrintErr($"[CharacterTabUI] {err}");
                    failureReasons.Add(err);
                    continue;
                }

                var accOpts = acc.ModelConfig.ToRiggingOptions();
                accOpts.IsPreviewOnly = false;
                accOpts.SlotId = acc.SlotId;
                accOpts.AccessoryName = acc.DisplayName;

                var accRes = CustomModelAutoRigger.RigAndBind(accScene, heroNode, accOpts);
                if (!accRes.Success)
                {
                    accScene.QueueFree();
                    string err = $"Failed to re-bind saved accessory '{acc.DisplayName}': {accRes.ErrorMessage}";
                    GD.PrintErr($"[CharacterTabUI] {err}");
                    failureReasons.Add(err);
                    continue;
                }

                if (!acc.Visible)
                {
                    string cname = CustomModelAutoRigger.GetContainerNameForSlot(acc.SlotId);
                    if (heroNode.FindChild(cname, true, false) is Node3D accContainer)
                        accContainer.Visible = false;
                }

                restoredSlots++;
                restoredAny = true;
            }

            if (restoredSlots > 0)
            {
                CustomModelAutoRigger.RecomputeHeroReplacementVisibility(heroNode);
            }
        }

        if (failureReasons.Count > 0)
        {
            StudioUIManager.Instance?.ShowToast(
                restoredAny ? "Partial Project Restore" : "Could Not Restore Custom Model",
                failureReasons[0]);
        }

        if (!restoredAny)
            return false;

        _currentHeroNode = heroNode;
        RefreshAfterModelOrAccessoryChange(heroNode);
        GD.Print($"[CharacterTabUI] Restored custom model/accessories for '{heroId}' from workspace/project.");
        return true;
    }

    public void ResetCustomModel() => OnResetCustomModelPressed();

    private void OnResetCustomModelPressed()
    {
        var activeHero = _currentHeroNode ?? _vpkLoader?.CurrentHeroNode;
        if (activeHero != null)
        {
            string heroId = GetActiveHeroId(activeHero);
            DeadlockPlayground.Workspace.ModWorkspaceSession.Instance.DiscardCustomModel(heroId, includeAccessories: false);

            CustomModelAutoRigger.RemoveCustomModelAndRestoreHero(activeHero, includeAccessories: false);
            _hasCustomModelApplied = false;
            _lastImportedOptions = null;
            _lastImportedScene = null;
            _lastImportedFilePath = null;

            bool hasAnyAcc = GetActiveAccessorySlotCount(activeHero) > 0;
            RefreshAfterModelOrAccessoryChange(activeHero, clearPaintCache: !hasAnyAcc);

            if (!hasAnyAcc)
            {
                var playerTab = GetTree()?.Root?.FindChild("PoseTab", true, false) as PlayerTabUI
                             ?? GetTree()?.Root?.FindChild("PlayerTabUI", true, false) as PlayerTabUI;
                if (playerTab != null)
                {
                    playerTab.RestoreDefaultIdlePose();
                }
                else
                {
                    var skeleton = CustomModelAutoRigger.FindSkeleton(activeHero);
                    skeleton?.ResetBonePoses();
                }
            }

            GD.Print("[CharacterTabUI] Reset base hero to original Deadlock state and removed full-character custom model.");
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
        UpdateImportAccessoryButtonState(hasHero);
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
        RefreshAccessorySlotsList();
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

            bool isCustomImported = mi.HasMeta("IsCustomImportedMesh") && mi.GetMeta("IsCustomImportedMesh").AsBool();
            string lowerName = rawName.ToLowerInvariant();
            if (HeroMeshHierarchy.IsEditorHelperNode(mi))
            {
                return;
            }
            if (!isCustomImported)
            {
                if (lowerName.Contains("wireframe") || lowerName.Contains("marker") || lowerName.Contains("preview"))
                {
                    return;
                }
            }

            // If this composite mesh has been split into individual per-surface meshes by HeroMeshHierarchy,
            // expose those split surface meshes directly instead of a single monolithic composite toggle.
            if (mi.HasMeta("IsHiddenComposite") && mi.HasMeta("GeneratedSubmeshes"))
            {
                var gen = mi.GetMeta("GeneratedSubmeshes").AsGodotArray<MeshInstance3D>();
                if (gen != null && gen.Count > 0 && gen.Any(g => g != null && GodotObject.IsInstanceValid(g)))
                {
                    foreach (Node child in node.GetChildren())
                    {
                        CollectSubmeshesRecursive(child);
                    }
                    return;
                }
            }

            {
                string semanticSource = rawName;
                if (mi.HasMeta("ParentCompositeMesh"))
                {
                    string matName = DeadlockPlayground.Painter.HeroMeshHierarchy.ResolveSurfaceMaterialName(mi, 0);
                    if (!string.IsNullOrEmpty(matName))
                    {
                        semanticSource = matName;
                    }
                }
                string semanticLower = semanticSource.ToLowerInvariant();
                bool isAcc = lowerName.Contains("acc") ||
                             lowerName.Contains("prop") ||
                             lowerName.Contains("weapon") ||
                             lowerName.Contains("gun") ||
                             lowerName.Contains("hat") ||
                             lowerName.Contains("gear") ||
                             lowerName.Contains("glass") ||
                             lowerName.Contains("mask") ||
                             lowerName.Contains("cape") ||
                             lowerName.Contains("coat") ||
                             semanticLower.Contains("shotgun") ||
                             semanticLower.Contains("weapon") ||
                             semanticLower.Contains("gun") ||
                             semanticLower.Contains("stake") ||
                             semanticLower.Contains("crossbow");

                string displayName = CleanMeshName(semanticSource);

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

    public void SyncSubmeshVisibilityFromScene()
    {
        if (_currentHeroNode == null || !GodotObject.IsInstanceValid(_currentHeroNode)) return;
        var stage = GetOrCreateParticleStage();
        if (_isParticleHierarchyMode || (stage != null && stage.HasActiveParticle)) return;

        ClearSubmeshes();
        CollectSubmeshesRecursive(_currentHeroNode);
        PopulateSubmeshList();
        SetControlsEnabled(true);
    }

    private static void ApplyUserVisibilityToSubmesh(MeshInstance3D mesh, bool pressed)
    {
        if (mesh == null || !GodotObject.IsInstanceValid(mesh)) return;

        bool hiddenByCustom = mesh.HasMeta("UserHiddenByCustomModel") && mesh.GetMeta("UserHiddenByCustomModel").AsBool();
        // If the user explicitly clicks the eye icon ON to recover an original vanilla mesh hidden by an accessory slot,
        // clear UserHiddenByCustomModel and remove it from sibling accessory SlotHiddenTargets so it can ALWAYS be recovered!
        if (pressed && hiddenByCustom)
        {
            mesh.RemoveMeta("UserHiddenByCustomModel");
            hiddenByCustom = false;
            Node3D activeHero = null;
            for (Node cur = mesh.GetParent(); cur is Node3D n3; cur = cur.GetParent())
            {
                activeHero = n3;
                if (CustomModelAutoRigger.CollectCustomContainers(n3).Count > 0) break;
            }
            if (activeHero != null && GodotObject.IsInstanceValid(activeHero))
            {
                string meshKey = mesh.Name.ToString();
                string parentKey = mesh.HasMeta("ParentCompositeMesh") && mesh.GetMeta("ParentCompositeMesh").As<MeshInstance3D>() is MeshInstance3D pMi
                    ? CustomModelAutoRigger.MakeSurfaceKeepKey(pMi.Name.ToString(), mesh.HasMeta("SurfaceIndex") ? mesh.GetMeta("SurfaceIndex").AsInt32() : 0)
                    : CustomModelAutoRigger.MakeSurfaceKeepKey(meshKey, 0);
                CustomModelAutoRigger.ClearTargetFromAccessoryHiddenLists(activeHero, meshKey, parentKey);
            }
        }
        if (!mesh.HasMeta("IsHiddenComposite"))
        {
            mesh.Visible = pressed && !hiddenByCustom;
        }
        else
        {
            mesh.Visible = false;
        }
        mesh.SetMeta("UserVisibility", pressed);

        if (mesh.HasMeta("GeneratedSubmeshes"))
        {
            var genList = mesh.GetMeta("GeneratedSubmeshes").AsGodotArray<MeshInstance3D>();
            foreach (var sub in genList)
            {
                if (sub != null && GodotObject.IsInstanceValid(sub))
                {
                    bool subHidden = sub.HasMeta("UserHiddenByCustomModel") && sub.GetMeta("UserHiddenByCustomModel").AsBool();
                    sub.SetMeta("UserVisibility", pressed);
                    sub.Visible = pressed && !subHidden;
                }
            }
        }

        if (mesh.HasMeta("ParentCompositeMesh"))
        {
            var parent = mesh.GetMeta("ParentCompositeMesh").As<MeshInstance3D>();
            if (parent != null && GodotObject.IsInstanceValid(parent))
            {
                bool anyGenVis = pressed;
                if (parent.HasMeta("GeneratedSubmeshes"))
                {
                    var genList = parent.GetMeta("GeneratedSubmeshes").AsGodotArray<MeshInstance3D>();
                    anyGenVis = genList.Any(g => g != null && GodotObject.IsInstanceValid(g) &&
                        (g.HasMeta("UserVisibility") ? g.GetMeta("UserVisibility").AsBool() : g.Visible));
                }
                parent.SetMeta("UserVisibility", anyGenVis);
            }
        }
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
                if (gen.Count > 0)
                {
                    isInitiallyVisible = gen.Any(g => g != null && GodotObject.IsInstanceValid(g) && g.Visible);
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
                ApplyUserVisibilityToSubmesh(localItem.Mesh, pressed);
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
                ApplyUserVisibilityToSubmesh(item.Mesh, true);
                if (item.CheckBoxWidget != null) item.CheckBoxWidget.SetPressedNoSignal(true);
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
                ApplyUserVisibilityToSubmesh(item.Mesh, shouldBeVisible);
                if (item.CheckBoxWidget != null) item.CheckBoxWidget.SetPressedNoSignal(shouldBeVisible);
            }
        }
    }
}
