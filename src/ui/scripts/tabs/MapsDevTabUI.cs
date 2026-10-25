using System;
using System.IO;
using Godot;
using DeadlockPlayground.Maps;
using DeadlockPlayground.Maps.Editor;
using DeadlockPlayground.Materials;
using DeadlockPlayground.Painter;
using SteamDatabase.ValvePak;

namespace DeadlockPlayground.UI;

/// <summary>
/// Dedicated "Maps (Dev)" UI Tab controller for configuring, loading, and debugging
/// 3D map stages (dl_hideout.vpk) and Deadlock Map Creator / Forge Level Editor.
/// </summary>
public partial class MapsDevTabUI : VBoxContainer
{
    // --- Sub-Tab Switcher ---
    [Export] private Button _btnSubTabStageViewer;
    [Export] private Button _btnSubTabMapCreator;
    [Export] private Control _stageViewerContainer;
    [Export] private Control _forgeContainer;

    // --- Stage Viewer Controls ---
    [Export] private CheckButton _toggleStageActive;
    [Export] private OptionButton _presetOption;
    [Export] private Label _presetDescLabel;
    [Export] private HSlider _radiusSlider;
    [Export] private Label _radiusValueLabel;
    [Export] private Button _btnRadiusRoom;
    [Export] private Button _btnRadiusHall;
    [Export] private Button _btnRadiusWing;
    [Export] private Button _btnRadiusFull;
    [Export] private OptionButton _qualityOption;
    [Export] private CheckBox _checkCenterOnHero;
    [Export] private CheckBox _checkMapNavigation;
    [Export] private Label _statLoadTime;
    [Export] private Label _statMeshes;
    [Export] private Label _statMaterials;
    [Export] private Label _statLights;
    [Export] private Label _statTextures;
    [Export] private Label _statMissingTextures;
    [Export] private Button _btnAuditTextures;
    [Export] private Button _btnInspectSurface;
    [Export] private Button _btnDumpUntinted;
    [Export] private PanelContainer _surfaceInspectPanel;
    [Export] private RichTextLabel _inspectDetailsLabel;
    [Export] private Button _btnUnloadStage;
    [Export] private Button _btnReloadStage;

    // --- Forge Mode: Starting Modes ---
    [Export] private Button _btnNewBlankCanvas;
    [Export] private Button _btnEditHideoutStage;

    // --- Forge Mode: Gizmo Toolbar ---
    [Export] private Button _btnGizmoMove;
    [Export] private Button _btnGizmoRotate;
    [Export] private Button _btnGizmoScale;
    [Export] private CheckBox _checkSnapEnabled;
    [Export] private OptionButton _snapDistOption;
    [Export] private OptionButton _snapAngleOption;

    // --- Forge Mode: Spawners & Actions ---
    [Export] private Button _btnOpenCatalog;
    [Export] private Button _btnSpawnPlayerStart;
    [Export] private Button _btnSpawnOmniLight;
    [Export] private Button _btnSpawnSpotLight;
    [Export] private Button _btnSpawnSunLight;
    [Export] private Button _btnSpawnTargetDummy;
    [Export] private Button _btnSpawnShopZone;

    // --- Forge Mode: Project & Export ---
    [Export] private Button _btnUndo;
    [Export] private Button _btnRedo;
    [Export] private Button _btnSaveMap;
    [Export] private Button _btnLoadMap;
    [Export] private Button _btnExportVpk;
    [Export] private Button _btnConsoleCommands;
    [Export] private Label _forgeStatusLabel;

    // --- Sub-Components ---
    [Export] private MapObjectInspectorUI _objectInspectorUI;
    private MapAssetBrowserUI _assetBrowserUI;
    private MapConsoleCommandsDialog _consoleCommandsDialog;
    private bool _isForgeModeActive = false;

    private Node _worldStageParent;
    private Package _baseGamePackage;
    private Package _hideoutPackage;
    private StagePresetDefinition _activePreset;
    private float _currentRadius = 35.0f;
    private MapTextureQuality _currentQuality = MapTextureQuality.Medium;
    private StudioUIManager _studioUI;
    private OrbitCamera _orbitCam;
    private bool _isInspectModeActive = false;

    public override void _Ready()
    {
        LinkReferences();
        InitializeUiComponents();
    }

    private void LinkReferences()
    {
        // Sub-Tab Switcher
        _btnSubTabStageViewer ??= GetNodeOrNull<Button>("SubTabHeader/BtnSubTabStageViewer")
                               ?? FindChild("BtnSubTabStageViewer", true, false) as Button;
        _btnSubTabMapCreator ??= GetNodeOrNull<Button>("SubTabHeader/BtnSubTabMapCreator")
                              ?? FindChild("BtnSubTabMapCreator", true, false) as Button;
        _stageViewerContainer ??= GetNodeOrNull<Control>("StageViewerContainer")
                               ?? FindChild("StageViewerContainer", true, false) as Control;
        _forgeContainer ??= GetNodeOrNull<Control>("ForgeContainer")
                         ?? FindChild("ForgeContainer", true, false) as Control;

        // Stage Viewer references
        _toggleStageActive ??= FindChild("ToggleStageActive", true, false) as CheckButton;
        _presetOption ??= FindChild("PresetOption", true, false) as OptionButton;
        _presetDescLabel ??= FindChild("PresetDescLabel", true, false) as Label;
        _radiusSlider ??= FindChild("RadiusSlider", true, false) as HSlider;
        _radiusValueLabel ??= FindChild("RadiusValueLabel", true, false) as Label;
        _btnRadiusRoom ??= FindChild("BtnRadiusRoom", true, false) as Button;
        _btnRadiusHall ??= FindChild("BtnRadiusHall", true, false) as Button;
        _btnRadiusWing ??= FindChild("BtnRadiusWing", true, false) as Button;
        _btnRadiusFull ??= FindChild("BtnRadiusFull", true, false) as Button;
        _qualityOption ??= FindChild("QualityOption", true, false) as OptionButton;
        _checkCenterOnHero ??= FindChild("CheckCenterOnHero", true, false) as CheckBox;
        _checkMapNavigation ??= FindChild("CheckMapNavigation", true, false) as CheckBox;
        _statLoadTime ??= FindChild("LoadTimeVal", true, false) as Label;
        _statMeshes ??= FindChild("MeshesVal", true, false) as Label;
        _statMaterials ??= FindChild("MaterialsVal", true, false) as Label;
        _statLights ??= FindChild("LightsVal", true, false) as Label;
        _statTextures ??= FindChild("TexturesVal", true, false) as Label;
        _statMissingTextures ??= FindChild("MissingTexVal", true, false) as Label;
        _btnAuditTextures ??= FindChild("BtnAuditTextures", true, false) as Button;
        _btnInspectSurface ??= FindChild("BtnInspectSurface", true, false) as Button;
        _btnDumpUntinted ??= FindChild("BtnDumpUntinted", true, false) as Button;
        _surfaceInspectPanel ??= FindChild("SurfaceInspectPanel", true, false) as PanelContainer;
        _inspectDetailsLabel ??= FindChild("InspectDetailsLabel", true, false) as RichTextLabel;
        _btnUnloadStage ??= FindChild("BtnUnloadStage", true, false) as Button;
        _btnReloadStage ??= FindChild("BtnReloadStage", true, false) as Button;

        // Forge Mode Starting Modes
        _btnNewBlankCanvas ??= FindChild("BtnNewBlankCanvas", true, false) as Button;
        _btnEditHideoutStage ??= FindChild("BtnEditHideoutStage", true, false) as Button;

        // Forge Mode Gizmo Toolbar
        _btnGizmoMove ??= FindChild("BtnGizmoMove", true, false) as Button;
        _btnGizmoRotate ??= FindChild("BtnGizmoRotate", true, false) as Button;
        _btnGizmoScale ??= FindChild("BtnGizmoScale", true, false) as Button;
        _checkSnapEnabled ??= FindChild("CheckSnapEnabled", true, false) as CheckBox;
        _snapDistOption ??= FindChild("SnapDistOption", true, false) as OptionButton;
        _snapAngleOption ??= FindChild("SnapAngleOption", true, false) as OptionButton;

        // Forge Mode Spawners
        _btnOpenCatalog ??= FindChild("BtnOpenCatalog", true, false) as Button;
        _btnSpawnPlayerStart ??= FindChild("BtnSpawnPlayerStart", true, false) as Button;
        _btnSpawnOmniLight ??= FindChild("BtnSpawnOmniLight", true, false) as Button;
        _btnSpawnSpotLight ??= FindChild("BtnSpawnSpotLight", true, false) as Button;
        _btnSpawnSunLight ??= FindChild("BtnSpawnSunLight", true, false) as Button;
        _btnSpawnTargetDummy ??= FindChild("BtnSpawnTargetDummy", true, false) as Button;
        _btnSpawnShopZone ??= FindChild("BtnSpawnShopZone", true, false) as Button;

        // Forge Mode Project & History
        _btnUndo ??= FindChild("BtnUndo", true, false) as Button;
        _btnRedo ??= FindChild("BtnRedo", true, false) as Button;
        _btnSaveMap ??= FindChild("BtnSaveMap", true, false) as Button;
        _btnLoadMap ??= FindChild("BtnLoadMap", true, false) as Button;
        _btnExportVpk ??= FindChild("BtnExportVpk", true, false) as Button;
        _btnConsoleCommands ??= FindChild("BtnConsoleCommands", true, false) as Button;
        _forgeStatusLabel ??= FindChild("ForgeStatusLabel", true, false) as Label;

        _objectInspectorUI ??= FindChild("MapObjectInspectorUI", true, false) as MapObjectInspectorUI;

        GD.Print($"[MapsDevTabUI] LinkReferences: Toggle={_toggleStageActive != null}, ForgeContainer={_forgeContainer != null}, NewBlankBtn={_btnNewBlankCanvas != null}, CatalogBtn={_btnOpenCatalog != null}");
    }

    private void InitializeUiComponents()
    {
        // 0. Ensure Forge Singletons
        if (MapSelectionManager.Instance == null)
        {
            var selectionMgr = new MapSelectionManager { Name = "MapSelectionManager" };
            AddChild(selectionMgr);
        }
        if (MapEditorManager.Instance == null)
        {
            var editorMgr = new MapEditorManager { Name = "MapEditorManager" };
            AddChild(editorMgr);
        }

        // Sub-Tab Switcher wiring
        if (_btnSubTabStageViewer != null) _btnSubTabStageViewer.Pressed += () => SwitchSubTab(false);
        if (_btnSubTabMapCreator != null) _btnSubTabMapCreator.Pressed += () => SwitchSubTab(true);

        // 1. Populate Stage Presets
        if (_presetOption != null)
        {
            _presetOption.Clear();
            for (int i = 0; i < StagePresets.HideoutPresets.Count; i++)
            {
                var p = StagePresets.HideoutPresets[i];
                _presetOption.AddItem(p.DisplayName, i);
            }
            _presetOption.ItemSelected += OnPresetSelected;
        }

        // 2. Populate Texture Quality Options
        if (_qualityOption != null)
        {
            _qualityOption.Clear();
            _qualityOption.AddItem("Low (512px Cap - Fast)", 0);
            _qualityOption.AddItem("Medium (1024px Cap - Balanced)", 1);
            _qualityOption.AddItem("High (2048px Cap - Crisp)", 2);
            _qualityOption.Select(1); // Default Medium
            _qualityOption.ItemSelected += OnQualitySelected;
        }

        // 3. Connect Radius Slider and Quick Buttons
        if (_radiusSlider != null)
        {
            _radiusSlider.MinValue = 15;
            _radiusSlider.MaxValue = 100;
            _radiusSlider.Value = 35;
            _radiusSlider.ValueChanged += (val) =>
            {
                _currentRadius = (float)val;
                UpdateRadiusLabel();
            };
        }

        if (_btnRadiusRoom != null) _btnRadiusRoom.Pressed += () => { GD.Print("[MapsDevTabUI] 25m Room pressed"); SetRadius(25f); };
        if (_btnRadiusHall != null) _btnRadiusHall.Pressed += () => { GD.Print("[MapsDevTabUI] 35m Hall pressed"); SetRadius(35f); };
        if (_btnRadiusWing != null) _btnRadiusWing.Pressed += () => { GD.Print("[MapsDevTabUI] 50m Wing pressed"); SetRadius(50f); };
        if (_btnRadiusFull != null) _btnRadiusFull.Pressed += () => { GD.Print("[MapsDevTabUI] Full Map pressed"); SetRadius(-1f); };

        // 4. Toggle Stage Active
        if (_toggleStageActive != null)
        {
            _toggleStageActive.Toggled += (active) =>
            {
                GD.Print($"[MapsDevTabUI] ToggleStageActive toggled: {active}");
                if (active) LoadActiveStage();
                else UnloadActiveStage();
            };
        }

        if (_btnUnloadStage != null) _btnUnloadStage.Pressed += UnloadActiveStage;
        if (_btnReloadStage != null) _btnReloadStage.Pressed += LoadActiveStage;

        // 5. FPS / Map WASD Navigation toggle
        if (_checkMapNavigation != null)
        {
            _checkMapNavigation.Toggled += OnMapNavigationToggled;
        }

        // 6. Texture & Material Audit
        if (_btnAuditTextures != null)
        {
            _btnAuditTextures.Pressed += OnAuditTexturesClicked;
        }

        // 7. Click-to-Inspect 3D Surface Material
        var selectIcon = GD.Load<Texture2D>("res://assets/at-icons/mesh_select.svg");
        if (_btnInspectSurface != null)
        {
            if (selectIcon != null) _btnInspectSurface.Icon = selectIcon;
            _btnInspectSurface.Toggled += OnInspectSurfaceToggled;
        }

        var infoIcon = GD.Load<Texture2D>("res://assets/at-icons/info.svg");
        if (_btnDumpUntinted != null)
        {
            if (infoIcon != null) _btnDumpUntinted.Icon = infoIcon;
            _btnDumpUntinted.Pressed += OnDumpUntintedClicked;
        }

        // --- Forge Mode: Starting Modes ---
        if (_btnNewBlankCanvas != null) _btnNewBlankCanvas.Pressed += OnNewBlankCanvasPressed;
        if (_btnEditHideoutStage != null) _btnEditHideoutStage.Pressed += OnEditHideoutStagePressed;

        // --- Forge Mode: Gizmo Toolbar ---
        if (_btnGizmoMove != null) _btnGizmoMove.Pressed += () => MapSelectionManager.Instance?.SetGizmoMode(MapGizmoMode.Move);
        if (_btnGizmoRotate != null) _btnGizmoRotate.Pressed += () => MapSelectionManager.Instance?.SetGizmoMode(MapGizmoMode.Rotate);
        if (_btnGizmoScale != null) _btnGizmoScale.Pressed += () => MapSelectionManager.Instance?.SetGizmoMode(MapGizmoMode.Scale);

        if (_snapDistOption != null)
        {
            _snapDistOption.Clear();
            _snapDistOption.AddItem("Snap: 0.25 m", 0);
            _snapDistOption.AddItem("Snap: 0.50 m", 1);
            _snapDistOption.AddItem("Snap: 1.00 m", 2);
            _snapDistOption.AddItem("Snap: 2.00 m", 3);
            _snapDistOption.Select(1);
            _snapDistOption.ItemSelected += (idx) =>
            {
                float snap = idx switch { 0 => 0.25f, 1 => 0.5f, 2 => 1.0f, 3 => 2.0f, _ => 0.5f };
                if (MapSelectionManager.Instance != null) MapSelectionManager.Instance.TranslateSnap = snap;
            };
        }

        if (_snapAngleOption != null)
        {
            _snapAngleOption.Clear();
            _snapAngleOption.AddItem("Angle: 15°", 0);
            _snapAngleOption.AddItem("Angle: 45°", 1);
            _snapAngleOption.AddItem("Angle: 90°", 2);
            _snapAngleOption.Select(0);
            _snapAngleOption.ItemSelected += (idx) =>
            {
                float angle = idx switch { 0 => 15f, 1 => 45f, 2 => 90f, _ => 15f };
                if (MapSelectionManager.Instance != null) MapSelectionManager.Instance.RotateSnap = angle;
            };
        }

        if (_checkSnapEnabled != null)
        {
            _checkSnapEnabled.Toggled += (enabled) =>
            {
                if (MapSelectionManager.Instance != null) MapSelectionManager.Instance.IsSnappingEnabled = enabled;
            };
        }

        // --- Forge Mode: Spawners ---
        if (_btnOpenCatalog != null) _btnOpenCatalog.Pressed += ToggleAssetCatalog;
        if (_btnSpawnPlayerStart != null) _btnSpawnPlayerStart.Pressed += () => SpawnEntityInFront(MapEntitySpawner.EntityPlayerStart);
        if (_btnSpawnOmniLight != null) _btnSpawnOmniLight.Pressed += () => SpawnEntityInFront(MapEntitySpawner.EntityLightOmni);
        if (_btnSpawnSpotLight != null) _btnSpawnSpotLight.Pressed += () => SpawnEntityInFront(MapEntitySpawner.EntityLightSpot);
        if (_btnSpawnSunLight != null) _btnSpawnSunLight.Pressed += () => SpawnEntityInFront(MapEntitySpawner.EntityLightSun);
        if (_btnSpawnTargetDummy != null) _btnSpawnTargetDummy.Pressed += () => SpawnEntityInFront("npc_trooper");
        if (_btnSpawnShopZone != null) _btnSpawnShopZone.Pressed += () => SpawnEntityInFront("trigger_shop");

        // --- Forge Mode: Project & History ---
        if (_btnUndo != null) _btnUndo.Pressed += () => MapSelectionManager.Instance?.Undo();
        if (_btnRedo != null) _btnRedo.Pressed += () => MapSelectionManager.Instance?.Redo();
        if (_btnSaveMap != null) _btnSaveMap.Pressed += OnSaveMapProject;
        if (_btnLoadMap != null) _btnLoadMap.Pressed += OnLoadMapProject;
        if (_btnExportVpk != null) _btnExportVpk.Pressed += OnExportPlayableMapVpk;
        if (_btnConsoleCommands != null) _btnConsoleCommands.Pressed += OpenConsoleCommandsDialog;

        if (MapSelectionManager.Instance != null)
        {
            MapSelectionManager.Instance.UndoRedoStateChanged += (canUndo, canRedo) =>
            {
                if (_btnUndo != null) _btnUndo.Disabled = !canUndo;
                if (_btnRedo != null) _btnRedo.Disabled = !canRedo;
            };
        }

        if (MapEditorManager.Instance != null)
        {
            MapEditorManager.Instance.ProjectUpdated += () =>
            {
                UpdateForgeStatus($"Project: {MapEditorManager.Instance.CurrentProject.MapName} ({MapEditorManager.Instance.PlacedCount} objects)");
            };
            MapEditorManager.Instance.GhostPlacementStateChanged += (isPlacing, vmdl) =>
            {
                if (isPlacing)
                    UpdateForgeStatus($"Placing '{Path.GetFileName(vmdl)}'. Left-Click: Stamp | Wheel/R: Rotate | Right-Click/Esc: Cancel");
                else
                    UpdateForgeStatus("Ready. Select props from catalog or place entities.");
            };
        }

        // Select initial default preset
        _activePreset = StagePresets.HideoutPresets[0];
        UpdatePresetDescription();
        UpdateRadiusLabel();
        AssignButtonIcons();
        SwitchSubTab(false);

        GD.Print("[MapsDevTabUI] UI Components successfully initialized.");
    }

    private void AssignButtonIcons()
    {
        TrySetIcon(_btnSubTabStageViewer, "res://assets/at-icons/building.svg");
        TrySetIcon(_btnSubTabMapCreator, "res://assets/at-icons/brick_wall.svg");
        TrySetIcon(_btnNewBlankCanvas, "res://assets/at-icons/plus.svg");
        TrySetIcon(_btnEditHideoutStage, "res://assets/at-icons/pencil.svg");
        TrySetIcon(_btnGizmoMove, "res://assets/at-icons/move.svg");
        TrySetIcon(_btnGizmoRotate, "res://assets/at-icons/rotate.svg");
        TrySetIcon(_btnGizmoScale, "res://assets/at-icons/resize.svg");
        TrySetIcon(_btnOpenCatalog, "res://assets/at-icons/cube.svg");
        TrySetIcon(_btnSpawnPlayerStart, "res://assets/at-icons/flag_triangular.svg");
        TrySetIcon(_btnSpawnOmniLight, "res://assets/at-icons/lightbulb.svg");
        TrySetIcon(_btnSpawnSpotLight, "res://assets/at-icons/desk_lamp.svg");
        TrySetIcon(_btnSpawnSunLight, "res://assets/at-icons/sun.svg");
        TrySetIcon(_btnSpawnTargetDummy, "res://assets/at-icons/bullseye.svg");
        TrySetIcon(_btnSpawnShopZone, "res://assets/at-icons/shop.svg");
        TrySetIcon(_btnUndo, "res://assets/at-icons/undo.svg");
        TrySetIcon(_btnRedo, "res://assets/at-icons/redo.svg");
        TrySetIcon(_btnSaveMap, "res://assets/at-icons/floppy_disk.svg");
        TrySetIcon(_btnLoadMap, "res://assets/at-icons/folder_open.svg");
        TrySetIcon(_btnExportVpk, "res://assets/at-icons/file_arrow_down.svg");
        TrySetIcon(_btnConsoleCommands, "res://assets/at-icons/terminal.svg");
    }

    private static void TrySetIcon(Button btn, string iconPath)
    {
        if (btn == null) return;
        try
        {
            var icon = GD.Load<Texture2D>(iconPath);
            if (icon != null)
            {
                btn.Icon = icon;
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[MapsDevTabUI] Failed to load icon '{iconPath}': {ex.Message}");
        }
    }

    private void OnAuditTexturesClicked()
    {
        GD.Print("[MapsDevTabUI] Auditing textures and materials...");
        StageMaterialBuilder.LogDiagnosticReport();
        UpdateDiagnostics();
    }

    private void OnMapNavigationToggled(bool toggled)
    {
        EnsureParentAndPackage();
        if (_orbitCam != null)
        {
            _orbitCam.IsMapNavigationMode = toggled;
            GD.Print($"[MapsDevTabUI] Map Navigation Mode toggled: {toggled}");
        }
    }

    private void SetRadius(float r)
    {
        _currentRadius = r;
        if (_radiusSlider != null)
        {
            _radiusSlider.Value = r > 0 ? r : 100;
        }
        UpdateRadiusLabel();

        if (_toggleStageActive != null && _toggleStageActive.ButtonPressed)
        {
            LoadActiveStage();
        }
    }

    private void UpdateRadiusLabel()
    {
        if (_radiusValueLabel != null)
        {
            _radiusValueLabel.Text = _currentRadius > 0 ? $"{_currentRadius:F0} m" : "Full Map";
        }
    }

    private void OnPresetSelected(long index)
    {
        int idx = (int)index;
        if (idx >= 0 && idx < StagePresets.HideoutPresets.Count)
        {
            _activePreset = StagePresets.HideoutPresets[idx];
            UpdatePresetDescription();

            if (_toggleStageActive != null && _toggleStageActive.ButtonPressed)
            {
                LoadActiveStage();
            }
        }
    }

    private void OnQualitySelected(long index)
    {
        _currentQuality = index switch
        {
            0 => MapTextureQuality.Low,
            1 => MapTextureQuality.Medium,
            2 => MapTextureQuality.High,
            _ => MapTextureQuality.Medium
        };

        if (_toggleStageActive != null && _toggleStageActive.ButtonPressed)
        {
            LoadActiveStage();
        }
    }

    private void UpdatePresetDescription()
    {
        if (_presetDescLabel != null && _activePreset != null)
        {
            _presetDescLabel.Text = _activePreset.Description;
        }
    }

    public void SetContext(Node worldStageParent, Package baseGamePackage)
    {
        _worldStageParent = worldStageParent;
        _baseGamePackage = baseGamePackage;
    }

    private void EnsureParentAndPackage()
    {
        _studioUI ??= GetNodeOrNull<StudioUIManager>("/root/Main/UIRoot")
                   ?? GetTree()?.Root?.FindChild("UIRoot", true, false) as StudioUIManager;

        _orbitCam ??= GetTree()?.Root?.FindChild("CameraRoot", true, false) as OrbitCamera
                   ?? GetTree()?.Root?.FindChild("OrbitCamera", true, false) as OrbitCamera;

        if (_worldStageParent == null || !GodotObject.IsInstanceValid(_worldStageParent))
        {
            var worldVp = GetNodeOrNull<SubViewport>("/root/Main/UIRoot/MainHUD/VBoxContainer/MainSplit/ViewportArea/SubViewportContainer/WorldViewport")
                       ?? GetTree()?.Root?.FindChild("WorldViewport", true, false) as SubViewport;
            if (worldVp != null)
            {
                _worldStageParent = worldVp;
            }
            else
            {
                var vpkLoader = GetTree()?.Root?.FindChild("VpkLoaderTest", true, false) as VpkLoaderTest;
                _worldStageParent = vpkLoader;
            }
        }

        var (resolvedBase, _) = Source2MapLoader.EnsureAllPackagesMounted(_baseGamePackage);
        if (_baseGamePackage == null)
        {
            _baseGamePackage = resolvedBase;
        }
    }

    public async void LoadActiveStage()
    {
        GD.Print($"[MapsDevTabUI] LoadActiveStage requested for preset: '{_activePreset?.DisplayName ?? "Default"}'");

        EnsureParentAndPackage();

        string presetName = _activePreset?.DisplayName ?? "3D Hideout Stage";
        _studioUI?.ShowLoading(true, $"Loading 3D Map: {presetName}...");

        if (_presetDescLabel != null)
        {
            _presetDescLabel.Text = $"Loading '{presetName}' stage...";
        }

        // Wait two visual frames so Godot paints the loading modal before synchronous parsing begins
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        try
        {
            if (_worldStageParent == null)
            {
                GD.PrintErr("[MapsDevTabUI] Unable to locate world stage parent node.");
                if (_presetDescLabel != null) _presetDescLabel.Text = "Error: WorldViewport not found in scene tree.";
                return;
            }

            bool center = _checkCenterOnHero == null || _checkCenterOnHero.ButtonPressed;

            var stageNode = Source2MapLoader.LoadMapStage(
                _worldStageParent,
                _baseGamePackage,
                _activePreset ?? StagePresets.HideoutPresets[0],
                _currentRadius,
                _currentQuality,
                centerOnHero: center
            );

            if (stageNode != null)
            {
                if (_toggleStageActive != null)
                {
                    _toggleStageActive.SetPressedNoSignal(true);
                }

                if (_presetDescLabel != null)
                {
                    _presetDescLabel.Text = $"Stage loaded: {_activePreset?.DisplayName} ({Source2MapLoader.LastMeshesCount} meshes, {Source2MapLoader.LastLightsCount} lights in {Source2MapLoader.LastLoadTimeMs}ms)";
                }

                // Expand camera pan limits for large 3D scene traversal
                if (_orbitCam != null)
                {
                    float radius = _currentRadius > 0f ? Math.Max(120f, _currentRadius * 1.5f) : 250f;
                    _orbitCam.SetStagePanLimits(radius);
                    _orbitCam.IsMapNavigationMode = true;
                }

                // Automatically switch on Map WASD Navigation mode (W/S = Forward/Back)
                if (_checkMapNavigation != null)
                {
                    _checkMapNavigation.SetPressedNoSignal(true);
                }

                GD.Print($"[MapsDevTabUI] Stage successfully loaded and mounted under '{_worldStageParent.Name}'.");
            }
            else
            {
                if (_presetDescLabel != null)
                {
                    _presetDescLabel.Text = "Failed to load stage! Check debugger/output for details.";
                }
                GD.PrintErr("[MapsDevTabUI] Source2MapLoader returned null stage node!");
            }

            UpdateDiagnostics();
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[MapsDevTabUI] Exception during LoadActiveStage: {ex}");
            if (_presetDescLabel != null)
            {
                _presetDescLabel.Text = $"Error: {ex.Message}";
            }
        }
        finally
        {
            _studioUI?.ShowLoading(false);
        }
    }

    public void UnloadActiveStage()
    {
        GD.Print("[MapsDevTabUI] Unloading active stage...");
        Source2MapLoader.UnloadCurrentStage();

        if (_toggleStageActive != null)
        {
            _toggleStageActive.SetPressedNoSignal(false);
        }

        if (_presetDescLabel != null)
        {
            _presetDescLabel.Text = _activePreset?.Description ?? "Stage unloaded. Select a preset and enable stage to load.";
        }

        // Restore default camera limits and Hero Navigation mode (W/S = Up/Down)
        if (_orbitCam != null)
        {
            _orbitCam.ResetDefaultPanLimits();
            _orbitCam.IsMapNavigationMode = false;
        }

        if (_checkMapNavigation != null)
        {
            _checkMapNavigation.SetPressedNoSignal(false);
        }

        UpdateDiagnostics();
        GD.Print("[MapsDevTabUI] Stage unloaded and VRAM freed.");
    }

    private void UpdateDiagnostics()
    {
        if (_statLoadTime != null) _statLoadTime.Text = $"{Source2MapLoader.LastLoadTimeMs} ms";
        if (_statMeshes != null) _statMeshes.Text = $"{Source2MapLoader.LastMeshesCount}";
        if (_statMaterials != null) _statMaterials.Text = $"{Source2MapLoader.LastMaterialsCount}";
        if (_statLights != null) _statLights.Text = $"{Source2MapLoader.LastLightsCount}";
        if (_statTextures != null) _statTextures.Text = $"{StageMaterialBuilder.LoadedTextureCount}";
        if (_statMissingTextures != null)
        {
            int missing = StageMaterialBuilder.MissingTextureCount;
            _statMissingTextures.Text = $"{missing}";
            if (missing > 0)
            {
                _statMissingTextures.AddThemeColorOverride("font_color", new Color(1.0f, 0.65f, 0.25f));
            }
            else
            {
                _statMissingTextures.AddThemeColorOverride("font_color", new Color(0.45f, 0.9f, 0.55f));
            }
        }
    }

    private Camera3D GetWorldCamera()
    {
        // 1. Try finding Camera3D inside _worldStageParent (which is WorldViewport)
        if (_worldStageParent is SubViewport svp)
        {
            var cam = svp.GetCamera3D();
            if (cam != null) return cam;
        }

        // 2. Try _orbitCam
        if (_orbitCam != null)
        {
            var cam = _orbitCam.Camera ?? _orbitCam.GetNodeOrNull<Camera3D>("Camera3D");
            if (cam != null) return cam;
        }

        // 3. Fallback discovery in scene tree
        var root = GetTree()?.Root;
        if (root != null)
        {
            var worldVp = root.FindChild("WorldViewport", true, false) as SubViewport;
            var cam = worldVp?.GetCamera3D();
            if (cam != null) return cam;

            var orbitCam = root.FindChild("OrbitCamera", true, false) as OrbitCamera
                        ?? root.FindChild("CameraPivot", true, false) as OrbitCamera;
            cam = orbitCam?.Camera ?? orbitCam?.GetNodeOrNull<Camera3D>("Camera3D");
            if (cam != null) return cam;
        }

        return null;
    }

    private Vector2 GetWorldViewportMousePosition()
    {
        var root = GetTree()?.Root;
        var container = root?.FindChild("SubViewportContainer", true, false) as SubViewportContainer;
        if (container != null && GodotObject.IsInstanceValid(container))
        {
            return container.GetLocalMousePosition();
        }
        if (_worldStageParent is SubViewport svp && GodotObject.IsInstanceValid(svp))
        {
            return svp.GetMousePosition();
        }
        return GetViewport().GetMousePosition();
    }

    private void SwitchSubTab(bool isForge)
    {
        _isForgeModeActive = isForge;
        if (_stageViewerContainer != null) _stageViewerContainer.Visible = !isForge;
        if (_forgeContainer != null) _forgeContainer.Visible = isForge;

        if (_btnSubTabStageViewer != null) _btnSubTabStageViewer.ButtonPressed = !isForge;
        if (_btnSubTabMapCreator != null) _btnSubTabMapCreator.ButtonPressed = isForge;

        if (isForge)
        {
            EnsureParentAndPackage();
            if (_baseGamePackage != null)
            {
                MapEditorManager.Instance?.Initialize(_baseGamePackage, Source2MapLoader.LoadedHideoutPackage);
            }
            if (_orbitCam != null)
            {
                _orbitCam.SetStagePanLimits(250f);
                _orbitCam.IsMapNavigationMode = true;
            }
            UpdateForgeStatus("Forge Mode active. Choose 'New Blank Map Canvas' or 'Edit Hideout Stage'.");
        }
    }

    private void OnNewBlankCanvasPressed()
    {
        EnsureParentAndPackage();
        if (_worldStageParent == null)
        {
            UpdateForgeStatus("Error: WorldViewport not found.");
            return;
        }

        if (Source2MapLoader.ActiveStageRoot != null)
        {
            UnloadActiveStage();
        }

        MapEditorManager.Instance?.Initialize(_baseGamePackage, Source2MapLoader.LoadedHideoutPackage);
        MapEditorManager.Instance?.CreateBlankCanvas(_worldStageParent, GetWorldCamera());

        if (_orbitCam != null)
        {
            _orbitCam.PanTarget = new Vector3(0, 1.0f, 0);
            _orbitCam.SetStagePanLimits(200f);
            _orbitCam.IsMapNavigationMode = true;
        }

        UpdateForgeStatus("Blank Canvas created! Reference grid, walkable floor, sun, and spawn point ready.");
    }

    private async void OnEditHideoutStagePressed()
    {
        EnsureParentAndPackage();
        if (Source2MapLoader.ActiveStageRoot == null || !GodotObject.IsInstanceValid(Source2MapLoader.ActiveStageRoot))
        {
            LoadActiveStage();
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }

        if (Source2MapLoader.ActiveStageRoot != null)
        {
            MapEditorManager.Instance?.Initialize(_baseGamePackage, Source2MapLoader.LoadedHideoutPackage);
            MapEditorManager.Instance?.ConvertLoadedHideoutToEditable(Source2MapLoader.ActiveStageRoot, GetWorldCamera());
            UpdateForgeStatus("Loaded Hideout stage converted to editable Forge Mode scene!");
        }
    }

    private void ToggleAssetCatalog()
    {
        EnsureParentAndPackage();
        if (_assetBrowserUI == null || !GodotObject.IsInstanceValid(_assetBrowserUI))
        {
            var modalsLayer = GetTree()?.Root?.FindChild("ModalsLayer", true, false) as Control;
            var scene = GD.Load<PackedScene>("res://ui/scenes/components/MapAssetBrowserUI.tscn");
            if (scene != null)
            {
                _assetBrowserUI = scene.Instantiate<MapAssetBrowserUI>();
                _assetBrowserUI.Name = "MapAssetBrowserUI";
                if (modalsLayer != null)
                {
                    modalsLayer.AddChild(_assetBrowserUI);
                }
                else
                {
                    var vpArea = GetTree()?.Root?.FindChild("ViewportArea", true, false) as Control;
                    if (vpArea != null)
                    {
                        vpArea.AddChild(_assetBrowserUI);
                    }
                    else
                    {
                        AddChild(_assetBrowserUI);
                    }
                }
            }
        }

        if (_assetBrowserUI != null)
        {
            var (bPkg, hPkg) = Source2MapLoader.EnsureAllPackagesMounted(_baseGamePackage);
            _baseGamePackage ??= bPkg;
            _hideoutPackage = hPkg ?? _hideoutPackage ?? Source2MapLoader.LoadedHideoutPackage;
            _assetBrowserUI.Initialize(_baseGamePackage, _hideoutPackage);

            if (_assetBrowserUI.Visible)
            {
                _assetBrowserUI.CloseModal();
            }
            else
            {
                _assetBrowserUI.OpenModal();
            }
        }
    }

    private void SpawnEntityInFront(string entityType)
    {
        EnsureParentAndPackage();
        var cam = GetWorldCamera();
        Vector3 spawnPos = Vector3.Zero;
        if (cam != null)
        {
            spawnPos = cam.GlobalPosition - cam.GlobalBasis.Z * 4.0f;
            spawnPos.Y = Mathf.Max(0.1f, spawnPos.Y);
        }

        var data = new MapObjectData
        {
            Name = $"{entityType}_{MapEditorManager.Instance?.PlacedCount + 1 ?? 1}",
            EntityType = entityType,
            Position = spawnPos,
            RotationDegrees = Vector3.Zero,
            Scale = Vector3.One,
            LightColor = Colors.White,
            LightEnergy = 1.0f,
            LightRange = 8.0f,
            SpotAngle = 45.0f
        };

        var node = MapEditorManager.Instance?.SpawnObjectFromData(data, pushUndo: true);
        if (node != null)
        {
            MapSelectionManager.Instance?.SelectObject(node, data);
            UpdateForgeStatus($"Spawned {entityType} at ({spawnPos.X:F1}, {spawnPos.Y:F1}, {spawnPos.Z:F1})");
        }
    }

    private void OnSaveMapProject()
    {
        var proj = MapEditorManager.Instance?.CurrentProject;
        if (proj == null) return;

        string mapsDir = "user://maps";
        DirAccess.MakeDirAbsolute(ProjectSettings.GlobalizePath(mapsDir));
        string path = $"{mapsDir}/{proj.MapName}.dpmap";
        bool ok = proj.SaveToFile(path);
        if (ok)
        {
            UpdateForgeStatus($"Saved map project to {path} ({proj.Objects.Count} objects)");
            GD.Print($"[MapsDevTabUI] Saved map project to: {ProjectSettings.GlobalizePath(path)}");
        }
        else
        {
            UpdateForgeStatus("Failed to save map project.");
        }
    }

    private void OnLoadMapProject()
    {
        string mapsDir = "user://maps";
        string path = $"{mapsDir}/custom_deadlock_arena.dpmap";
        if (!Godot.FileAccess.FileExists(path))
        {
            UpdateForgeStatus($"No saved map found at {path}");
            return;
        }

        var proj = MapProject.LoadFromFile(path);
        if (proj == null)
        {
            UpdateForgeStatus("Failed to load map project.");
            return;
        }

        EnsureParentAndPackage();
        MapEditorManager.Instance?.ClearStage();
        MapEditorManager.Instance?.CreateBlankCanvas(_worldStageParent, GetWorldCamera());

        foreach (var obj in proj.Objects)
        {
            MapEditorManager.Instance?.SpawnObjectFromData(obj, pushUndo: false);
        }
        UpdateForgeStatus($"Loaded map '{proj.MapName}' ({proj.Objects.Count} objects)");
    }

    private async void OnExportPlayableMapVpk()
    {
        EnsureParentAndPackage();
        var proj = MapEditorManager.Instance?.CurrentProject;
        if (proj == null || proj.Objects.Count == 0)
        {
            UpdateForgeStatus("Cannot export: Map has no objects placed!");
            return;
        }

        string gamePath = new GamePathManager().CurrentGamePath;
        if (string.IsNullOrEmpty(gamePath) || !Directory.Exists(gamePath))
        {
            gamePath = DeadlockPlayground.Particles.HeroParticleService.ResolveGameRootPath();
        }

        if (string.IsNullOrEmpty(gamePath) || !Directory.Exists(gamePath))
        {
            UpdateForgeStatus("Error: Deadlock game folder not found. Set path in settings.");
            return;
        }

        _studioUI?.ShowLoading(true, "Compiling and packaging playable Deadlock VPK map...");
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        try
        {
            var options = new MapCompileOptions
            {
                ExportAsAddon = true
            };

            var result = Source2MapCompiler.CompileMap(
                proj,
                gamePath,
                options
            );

            if (result.Success)
            {
                UpdateForgeStatus($"Map compiled! VPK: {Path.GetFileName(result.OutputVpkPath)} - {result.Message}");
                GD.Print($"[MapsDevTabUI] Map exported successfully to: {result.OutputVpkPath}");
                OpenConsoleCommandsDialog();
            }
            else
            {
                UpdateForgeStatus($"Export failed: {result.ErrorDetails}");
                GD.PrintErr($"[MapsDevTabUI] Export failed: {result.ErrorDetails}");
            }
        }
        catch (Exception ex)
        {
            UpdateForgeStatus($"Export error: {ex.Message}");
            GD.PrintErr($"[MapsDevTabUI] Exception exporting map: {ex}");
        }
        finally
        {
            _studioUI?.ShowLoading(false);
        }
    }

    private void OpenConsoleCommandsDialog()
    {
        if (_consoleCommandsDialog == null || !GodotObject.IsInstanceValid(_consoleCommandsDialog))
        {
            var scene = GD.Load<PackedScene>("res://ui/scenes/modals/MapConsoleCommandsDialog.tscn");
            if (scene != null)
            {
                _consoleCommandsDialog = scene.Instantiate<MapConsoleCommandsDialog>();
                var root = GetTree()?.Root?.FindChild("UIRoot", true, false) as Control ?? (Control)this;
                root.AddChild(_consoleCommandsDialog);
            }
        }
        _consoleCommandsDialog?.PopupCentered();
    }

    private void FocusCameraOnSelected()
    {
        var selNode = MapSelectionManager.Instance?.SelectedNode;
        if (selNode == null || !GodotObject.IsInstanceValid(selNode)) return;

        if (_orbitCam != null)
        {
            _orbitCam.PanTarget = selNode.GlobalPosition;
        }
    }

    private void UpdateForgeStatus(string msg)
    {
        if (_forgeStatusLabel != null)
        {
            _forgeStatusLabel.Text = msg;
        }
        GD.Print($"[ForgeMode] {msg}");
    }

    public override void _Input(InputEvent @event)
    {
        // 1. Ghost Placement Mode Input
        if (MapEditorManager.Instance != null && MapEditorManager.Instance.IsPlacingGhost)
        {
            if (@event is InputEventMouseMotion)
            {
                MapEditorManager.Instance.UpdateGhostRaycast(GetWorldViewportMousePosition(), GetWorldCamera());
            }
            else if (@event is InputEventMouseButton mb && mb.Pressed)
            {
                if (mb.ButtonIndex == MouseButton.WheelUp)
                {
                    MapEditorManager.Instance.RotateGhost(15.0f);
                    GetViewport().SetInputAsHandled();
                }
                else if (mb.ButtonIndex == MouseButton.WheelDown)
                {
                    MapEditorManager.Instance.RotateGhost(-15.0f);
                    GetViewport().SetInputAsHandled();
                }
                else if (mb.ButtonIndex == MouseButton.Left)
                {
                    MapEditorManager.Instance.CommitGhostPlacement();
                    GetViewport().SetInputAsHandled();
                }
                else if (mb.ButtonIndex == MouseButton.Right)
                {
                    MapEditorManager.Instance.CancelPlacement();
                    GetViewport().SetInputAsHandled();
                }
            }
            else if (@event is InputEventKey ek && ek.Pressed && !ek.Echo)
            {
                if (ek.Keycode == Key.Escape)
                {
                    MapEditorManager.Instance.CancelPlacement();
                    GetViewport().SetInputAsHandled();
                }
                else if (ek.Keycode == Key.R)
                {
                    MapEditorManager.Instance.RotateGhost(45.0f);
                    GetViewport().SetInputAsHandled();
                }
            }
            return;
        }

        // 2. Surface Inspection Mode Input (Stage Viewer)
        if (_isInspectModeActive)
        {
            if (@event is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
            {
                var hovered = GetViewport()?.GuiGetHoveredControl();
                if (hovered != null && (hovered == this || IsAncestorOf(hovered)))
                {
                    return;
                }

                if (_surfaceInspectPanel != null && _surfaceInspectPanel.Visible)
                {
                    if (_surfaceInspectPanel.GetGlobalRect().HasPoint(mb.GlobalPosition))
                    {
                        return;
                    }
                }

                Vector2 vpMouse = GetWorldViewportMousePosition();
                PerformSurfaceInspection(vpMouse);
            }
            return;
        }

        // 3. Forge Mode 3D Viewport Picking & Hotkeys
        if (_isForgeModeActive)
        {
            // Hotkeys
            if (@event is InputEventKey ek && ek.Pressed && !ek.Echo)
            {
                var focused = GetViewport()?.GuiGetFocusOwner();
                if (focused is LineEdit || focused is TextEdit)
                {
                    return;
                }

                if (ek.Keycode == Key.Z && ek.CtrlPressed && !ek.ShiftPressed)
                {
                    MapSelectionManager.Instance?.Undo();
                    GetViewport().SetInputAsHandled();
                    return;
                }
                if ((ek.Keycode == Key.Y && ek.CtrlPressed) || (ek.Keycode == Key.Z && ek.CtrlPressed && ek.ShiftPressed))
                {
                    MapSelectionManager.Instance?.Redo();
                    GetViewport().SetInputAsHandled();
                    return;
                }
                if (ek.Keycode == Key.D && (ek.CtrlPressed || ek.ShiftPressed))
                {
                    MapEditorManager.Instance?.DuplicateSelected();
                    GetViewport().SetInputAsHandled();
                    return;
                }
                if (ek.Keycode == Key.Delete || ek.Keycode == Key.X)
                {
                    MapEditorManager.Instance?.DeleteSelected();
                    GetViewport().SetInputAsHandled();
                    return;
                }
                if (ek.Keycode == Key.F)
                {
                    FocusCameraOnSelected();
                    GetViewport().SetInputAsHandled();
                    return;
                }
                if (ek.Keycode == Key.W || ek.Keycode == Key.G)
                {
                    MapSelectionManager.Instance?.SetGizmoMode(MapGizmoMode.Move);
                    return;
                }
                if (ek.Keycode == Key.E)
                {
                    MapSelectionManager.Instance?.SetGizmoMode(MapGizmoMode.Rotate);
                    return;
                }
                if (ek.Keycode == Key.R)
                {
                    MapSelectionManager.Instance?.SetGizmoMode(MapGizmoMode.Scale);
                    return;
                }
            }

            // Viewport Left-Click Picking
            if (@event is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
            {
                var hovered = GetViewport()?.GuiGetHoveredControl();
                if (hovered != null && (hovered == this || IsAncestorOf(hovered) || hovered is Button || hovered is SpinBox || hovered is Slider || hovered is LineEdit))
                {
                    return;
                }

                var vpMouse = GetWorldViewportMousePosition();

                // If user clicked or is interacting with the 3D Gizmo handle, do NOT run picking and do NOT clear selection!
                if (MapSelectionManager.Instance != null && (MapSelectionManager.Instance.IsGizmoInteracting || MapSelectionManager.Instance.IsHitGizmo(vpMouse)))
                {
                    return;
                }

                PerformForgeObjectPicking(vpMouse);
            }
        }
    }

    private void PerformForgeObjectPicking(Vector2 mousePos)
    {
        var cam = GetWorldCamera();
        if (cam == null) return;

        var rayOrigin = cam.ProjectRayOrigin(mousePos);
        var rayNormal = cam.ProjectRayNormal(mousePos);
        var spaceState = cam.GetWorld3D()?.DirectSpaceState;
        if (spaceState == null) return;

        var query = PhysicsRayQueryParameters3D.Create(rayOrigin, rayOrigin + rayNormal * 1000.0f);
        var hit = spaceState.IntersectRay(query);

        if (hit.Count > 0 && hit.TryGetValue("collider", out var colVar) && colVar.AsGodotObject() is Node colNode)
        {
            Node curr = colNode;
            string objId = null;
            while (curr != null && curr != GetTree()?.Root)
            {
                if (curr.HasMeta("map_object_id"))
                {
                    objId = curr.GetMeta("map_object_id").AsString();
                    break;
                }
                curr = curr.GetParent();
            }

            if (!string.IsNullOrEmpty(objId))
            {
                var objData = MapEditorManager.Instance?.CurrentProject?.Objects.Find(o => o.Id == objId);
                var targetNode = MapEditorManager.Instance?.GetNodeForObject(objId) ?? curr as Node3D;
                if (objData != null && targetNode != null)
                {
                    MapSelectionManager.Instance?.SelectObject(targetNode, objData);
                    return;
                }
            }
        }

        // Broad-phase Ray-to-Object distance fallback for entities or lights
        var project = MapEditorManager.Instance?.CurrentProject;
        if (project != null && project.Objects.Count > 0)
        {
            float closestDist = float.MaxValue;
            MapObjectData bestObj = null;
            Node3D bestNode = null;

            foreach (var obj in project.Objects)
            {
                var node = MapEditorManager.Instance?.GetNodeForObject(obj.Id);
                if (node == null || !GodotObject.IsInstanceValid(node)) continue;

                Vector3 objPos = node.GlobalPosition;
                Vector3 toObj = objPos - rayOrigin;
                float projDist = toObj.Dot(rayNormal);
                if (projDist > 0.1f)
                {
                    Vector3 closestPt = rayOrigin + rayNormal * projDist;
                    float perpDistSq = closestPt.DistanceSquaredTo(objPos);
                    // Hit radius tolerance: 1.25m radius around entity origin
                    if (perpDistSq < 1.56f && projDist < closestDist)
                    {
                        closestDist = projDist;
                        bestObj = obj;
                        bestNode = node;
                    }
                }
            }

            if (bestObj != null && bestNode != null)
            {
                MapSelectionManager.Instance?.SelectObject(bestNode, bestObj);
                return;
            }
        }

        // Clicked ground or empty space: clear selection
        MapSelectionManager.Instance?.ClearSelection();
    }

    private void OnInspectSurfaceToggled(bool active)
    {
        _isInspectModeActive = active;
        if (_surfaceInspectPanel != null)
        {
            _surfaceInspectPanel.Visible = active;
        }
        if (active)
        {
            if (_inspectDetailsLabel != null)
            {
                _inspectDetailsLabel.Text = "[color=#88bbff][b]Inspect Mode Active[/b][/color]\n[color=#aaaaaa]Click on any 3D surface in the viewport to inspect its model, material, tint, and shader parameters.[/color]";
            }
            GD.Print("[MapsDevTabUI] Surface Inspect Mode ENABLED. Click 3D surfaces in the viewport to inspect.");
        }
        else
        {
            GD.Print("[MapsDevTabUI] Surface Inspect Mode DISABLED.");
        }
    }

    private void PerformSurfaceInspection(Vector2 viewportPos)
    {
        var stageRoot = Source2MapLoader.ActiveStageRoot;
        if (stageRoot == null || !GodotObject.IsInstanceValid(stageRoot))
        {
            GD.Print("[MapsDevTabUI] No active map stage loaded.");
            return;
        }

        var camera = GetWorldCamera();
        if (camera == null)
        {
            GD.PrintErr("[MapsDevTabUI] Could not find active Camera3D in viewport.");
            return;
        }

        Vector3 rayOrigin = camera.ProjectRayOrigin(viewportPos);
        Vector3 rayDir = camera.ProjectRayNormal(viewportPos);

        MeshInstance3D bestMesh = null;
        int bestSurface = -1;
        Vector3 bestHitPos = Vector3.Zero;
        float minDistance = float.MaxValue;

        var meshList = new System.Collections.Generic.List<MeshInstance3D>();
        CollectMeshInstances(stageRoot, meshList);

        foreach (var mi in meshList)
        {
            if (!mi.Visible || mi.Mesh == null) continue;

            Aabb aabb = mi.GetAabb();
            Transform3D invTrans = mi.GlobalTransform.AffineInverse();
            Vector3 localOrigin = invTrans * rayOrigin;
            Vector3 localDir = (invTrans.Basis * rayDir).Normalized();

            // Broad-phase AABB test
            if (!DeadlockPlayground.Painter.MeshRaycaster.RayIntersectsAabb(localOrigin, localDir, aabb.Grow(0.1f)))
            {
                continue;
            }

            // Narrow-phase triangle test
            var arrMesh = mi.Mesh as ArrayMesh;
            if (arrMesh == null) continue;

            for (int s = 0; s < arrMesh.GetSurfaceCount(); s++)
            {
                var arrays = arrMesh.SurfaceGetArrays(s);
                if (arrays == null || arrays.Count == 0) continue;

                var verts = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                var indices = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
                if (verts == null || verts.Length == 0) continue;

                int triCount = (indices != null && indices.Length > 0) ? indices.Length / 3 : verts.Length / 3;
                for (int t = 0; t < triCount; t++)
                {
                    Vector3 v0 = (indices != null && indices.Length > 0) ? verts[indices[t * 3]] : verts[t * 3];
                    Vector3 v1 = (indices != null && indices.Length > 0) ? verts[indices[t * 3 + 1]] : verts[t * 3 + 1];
                    Vector3 v2 = (indices != null && indices.Length > 0) ? verts[indices[t * 3 + 2]] : verts[t * 3 + 2];

                    if (DeadlockPlayground.Painter.MeshRaycaster.RayIntersectsTriangle(localOrigin, localDir, v0, v1, v2, out float tDist, out _, out _))
                    {
                        Vector3 hitLocal = localOrigin + localDir * tDist;
                        Vector3 hitWorld = mi.GlobalTransform * hitLocal;
                        float d = (hitWorld - rayOrigin).Length();
                        if (d < minDistance)
                        {
                            minDistance = d;
                            bestMesh = mi;
                            bestSurface = s;
                            bestHitPos = hitWorld;
                        }
                    }
                }
            }
        }

        if (bestMesh == null)
        {
            GD.Print("[MapsDevTabUI] No stage surface intersected at click location.");
            return;
        }

        DisplayAndDumpSurfaceDetails(bestMesh, bestSurface, bestHitPos);
    }

    private void DisplayAndDumpSurfaceDetails(MeshInstance3D mi, int surfaceIdx, Vector3 hitPos)
    {
        string modelPath = mi.HasMeta("model_path") ? mi.GetMeta("model_path").AsString() : mi.Name;
        string matPath = mi.HasMeta("material_path") ? mi.GetMeta("material_path").AsString() : "";
        string objName = mi.HasMeta("object_name") ? mi.GetMeta("object_name").AsString() : "";
        Color instanceTint = mi.HasMeta("instance_tint") ? mi.GetMeta("instance_tint").AsColor() : Colors.White;
        string shaderName = mi.HasMeta("shader_name") ? mi.GetMeta("shader_name").AsString() : "Unknown";
        Color albedoTint = mi.HasMeta("albedo_tint") ? mi.GetMeta("albedo_tint").AsColor() : Colors.White;

        bool hasVertexColors = false;
        Color avgVertexColor = Colors.White;
        var arrMesh = mi.Mesh as ArrayMesh;
        if (arrMesh != null && surfaceIdx >= 0 && surfaceIdx < arrMesh.GetSurfaceCount())
        {
            var arrays = arrMesh.SurfaceGetArrays(surfaceIdx);
            if (arrays != null && arrays.Count > (int)Mesh.ArrayType.Color)
            {
                var colors = arrays[(int)Mesh.ArrayType.Color].AsColorArray();
                if (colors != null && colors.Length > 0)
                {
                    hasVertexColors = true;
                    float sumR = 0, sumG = 0, sumB = 0;
                    for (int c = 0; c < colors.Length; c++)
                    {
                        sumR += colors[c].R;
                        sumG += colors[c].G;
                        sumB += colors[c].B;
                    }
                    avgVertexColor = new Color(sumR / colors.Length, sumG / colors.Length, sumB / colors.Length);
                }
            }
        }

        var diag = StageMaterialBuilder.GetMaterialDiagnostic(matPath);

        GD.Print("\n=======================================================");
        GD.Print("        [MAPS DEV] 3D SURFACE MATERIAL INSPECTION      ");
        GD.Print("=======================================================");
        GD.Print($"Hit Position:       {hitPos}");
        GD.Print($"Mesh Node:          {mi.GetPath()}");
        GD.Print($"Object/Entity:      {objName}");
        GD.Print($"Model Path:         {modelPath}");
        GD.Print($"Surface Index:      {surfaceIdx}");
        GD.Print($"Material Path:      {matPath}");
        GD.Print($"Shader Name:        {diag?.ShaderName ?? shaderName}");
        GD.Print($"Instance Tint:      {instanceTint} (R:{instanceTint.R * 255:F0} G:{instanceTint.G * 255:F0} B:{instanceTint.B * 255:F0})");
        GD.Print($"Material Tint:      {diag?.AlbedoTintA ?? albedoTint}");
        GD.Print($"Has Tint Mask:      {diag?.HasTintMask ?? false}");
        GD.Print($"Has Vertex Colors:  {hasVertexColors} (Avg RGB: {avgVertexColor.R:F3}, {avgVertexColor.G:F3}, {avgVertexColor.B:F3})");

        if (diag != null)
        {
            GD.Print("--- VectorParams ---");
            foreach (var (k, v) in diag.VectorParams) GD.Print($"  {k} = {v}");
            GD.Print("--- TextureParams ---");
            foreach (var (k, v) in diag.TextureParams) GD.Print($"  {k} = {v}");
            GD.Print("--- FloatParams ---");
            foreach (var (k, v) in diag.FloatParams) GD.Print($"  {k} = {v}");
            GD.Print("--- IntParams ---");
            foreach (var (k, v) in diag.IntParams) GD.Print($"  {k} = {v}");
        }
        GD.Print("=======================================================\n");

        if (_inspectDetailsLabel != null)
        {
            string tintHex = instanceTint.ToHtml(false);
            string matTintHex = (diag?.AlbedoTintA ?? albedoTint).ToHtml(false);
            string vColHex = avgVertexColor.ToHtml(false);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"[b][color=#88ccff]Inspected Surface:[/color] {Path.GetFileName(modelPath)}[/b]");
            sb.AppendLine($"[color=#aaaaaa]Model:[/color] {modelPath}");
            if (!string.IsNullOrEmpty(objName)) sb.AppendLine($"[color=#aaaaaa]Object/Entity:[/color] {objName}");
            sb.AppendLine($"[color=#aaaaaa]Material:[/color] {Path.GetFileName(matPath)} ([color=#88bbff]{diag?.ShaderName ?? shaderName}[/color])");
            sb.AppendLine($"[color=#aaaaaa]Instance Tint:[/color] [color=#{tintHex}]■[/color] ({instanceTint.R * 255:F0}, {instanceTint.G * 255:F0}, {instanceTint.B * 255:F0})");
            sb.AppendLine($"[color=#aaaaaa]Material Tint:[/color] [color=#{matTintHex}]■[/color] ({(diag?.AlbedoTintA ?? albedoTint).R * 255:F0}, {(diag?.AlbedoTintA ?? albedoTint).G * 255:F0}, {(diag?.AlbedoTintA ?? albedoTint).B * 255:F0})");
            sb.AppendLine($"[color=#aaaaaa]Tint Mask:[/color] {(diag?.HasTintMask == true ? "[color=#55ff55]Yes[/color]" : "[color=#888888]No[/color]")} | [color=#aaaaaa]Vertex Colors:[/color] {(hasVertexColors ? $"[color=#{vColHex}]Yes (Avg {avgVertexColor.R:F2}, {avgVertexColor.G:F2}, {avgVertexColor.B:F2})[/color]" : "[color=#888888]No[/color]")}");

            if (diag != null && diag.TextureParams.Count > 0)
            {
                sb.AppendLine("[color=#eebb55][b]Textures:[/b][/color]");
                foreach (var (k, v) in diag.TextureParams)
                {
                    sb.AppendLine($"  [color=#aaaaaa]{k}:[/color] {Path.GetFileName(v)}");
                }
            }

            _inspectDetailsLabel.Text = sb.ToString();
        }
    }

    private void OnDumpUntintedClicked()
    {
        var stageRoot = Source2MapLoader.ActiveStageRoot;
        if (stageRoot == null || !GodotObject.IsInstanceValid(stageRoot))
        {
            GD.Print("[MapsDevTabUI] No active map stage loaded.");
            return;
        }

        var meshList = new System.Collections.Generic.List<MeshInstance3D>();
        CollectMeshInstances(stageRoot, meshList);

        var reportedMaterials = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var untintedList = new System.Collections.Generic.List<(string Model, string Obj, string Mat, MaterialDiagnosticInfo Diag)>();

        var fullDumpSb = new System.Text.StringBuilder();
        fullDumpSb.AppendLine("=======================================================");
        fullDumpSb.AppendLine("    [MAPS DEV] FULL DUMP OF WHITE / UN-TINTED SURFACES  ");
        fullDumpSb.AppendLine($"    Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        fullDumpSb.AppendLine("=======================================================");

        int totalUntinted = 0;
        foreach (var mi in meshList)
        {
            Color instTint = mi.HasMeta("instance_tint") ? mi.GetMeta("instance_tint").AsColor() : Colors.White;
            string matPath = mi.HasMeta("material_path") ? mi.GetMeta("material_path").AsString() : "";
            string modelPath = mi.HasMeta("model_path") ? mi.GetMeta("model_path").AsString() : mi.Name;
            string objName = mi.HasMeta("object_name") ? mi.GetMeta("object_name").AsString() : "";

            if (string.IsNullOrEmpty(matPath) || !reportedMaterials.Add(matPath)) continue;

            var diag = StageMaterialBuilder.GetMaterialDiagnostic(matPath);
            Color matTint = diag?.AlbedoTintA ?? Colors.White;

            bool instIsNearWhite = instTint.R > 0.98f && instTint.G > 0.98f && instTint.B > 0.98f;
            bool matIsNearWhite = matTint.R > 0.98f && matTint.G > 0.98f && matTint.B > 0.98f;

            // Only report surfaces where BOTH instance tint AND material tint are white/untinted
            if (instIsNearWhite && matIsNearWhite)
            {
                totalUntinted++;
                untintedList.Add((modelPath, objName, matPath, diag));

                fullDumpSb.AppendLine($"Untinted #{totalUntinted}: Model='{modelPath}', Obj='{objName}', Mat='{matPath}'");
                fullDumpSb.AppendLine($"   Shader='{diag?.ShaderName ?? "Unknown"}', MatTint={matTint}, HasTintMask={diag?.HasTintMask ?? false}");
                if (diag != null)
                {
                    foreach (var (k, v) in diag.VectorParams)
                    {
                        if (k.Contains("tint", StringComparison.OrdinalIgnoreCase) || k.Contains("color", StringComparison.OrdinalIgnoreCase))
                        {
                            fullDumpSb.AppendLine($"      vec: {k} = {v}");
                        }
                    }
                    foreach (var (k, v) in diag.TextureParams)
                    {
                        fullDumpSb.AppendLine($"      tex: {k} = {v}");
                    }
                }
            }
        }
        fullDumpSb.AppendLine("=======================================================");

        // Save full dump to file to prevent console output buffer overflow
        string logPath = "user://untinted_materials_dump.txt";
        string globalLogPath = ProjectSettings.GlobalizePath(logPath);
        try
        {
            using var file = Godot.FileAccess.Open(logPath, Godot.FileAccess.ModeFlags.Write);
            if (file != null)
            {
                file.StoreString(fullDumpSb.ToString());
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[MapsDevTabUI] Failed to save dump to {logPath}: {ex.Message}");
        }

        // Print clean, non-overflowing summary table of the first 25 untinted materials
        int maxConsolePrint = Math.Min(25, untintedList.Count);
        GD.Print("\n=======================================================");
        GD.Print($"    [MAPS DEV] UN-TINTED SURFACES SUMMARY (Showing {maxConsolePrint} of {totalUntinted})");
        GD.Print("=======================================================");

        for (int i = 0; i < maxConsolePrint; i++)
        {
            var item = untintedList[i];
            GD.Print($"#{i + 1:D2}: Mat: '{Path.GetFileName(item.Mat)}' | Model: '{Path.GetFileName(item.Model)}' | Shader: '{item.Diag?.ShaderName ?? "Unknown"}'");
        }

        if (totalUntinted > maxConsolePrint)
        {
            GD.Print($"... and {totalUntinted - maxConsolePrint} more untinted materials.");
        }

        GD.Print("-------------------------------------------------------");
        GD.Print($"Total unique untinted materials found: {totalUntinted}");
        GD.Print($"Full diagnostic log saved to: {globalLogPath}");
        GD.Print("=======================================================\n");

        if (_inspectDetailsLabel != null && _surfaceInspectPanel != null)
        {
            _surfaceInspectPanel.Visible = true;
            _inspectDetailsLabel.Text = $"[color=#55ffaa][b]Dump Completed:[/b][/color] Found [b]{totalUntinted}[/b] truly untinted materials.\nTop {maxConsolePrint} printed to console.\n[color=#88bbff]Full log:[/color] [font_size=11]{globalLogPath}[/font_size]";
        }
    }

    private static void CollectMeshInstances(Node node, System.Collections.Generic.List<MeshInstance3D> list)
    {
        if (node is MeshInstance3D mi)
        {
            list.Add(mi);
        }
        foreach (var child in node.GetChildren())
        {
            CollectMeshInstances(child, list);
        }
    }
}
