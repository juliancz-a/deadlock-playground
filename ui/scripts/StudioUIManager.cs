using Godot;
using System;
using System.IO;

public partial class StudioUIManager : CanvasLayer
{
    [ExportCategory("Top Bar Controls")]
    [Export] private Button _btnSettings;
    [Export] private Button _btnAbout;
    [Export] private Button _btnQuit;

    [ExportCategory("Tab Navigation Buttons")]
    [Export] private Button _btnTabCharacter;
    [Export] private Button _btnTabBones;
    [Export] private Button _btnTabCamera;
    [Export] private Button _btnTabPose;
    [Export] private Button _btnTabScene;
    [Export] private Button _btnTabEffects;
    [Export] private Button _btnTabLighting;
    [Export] private Button _btnTabShading;
    [Export] private Button _btnTabPaint;

    [ExportCategory("Tab Sub-Scene Instances (in Editor)")]
    [Export] private Container _tabContentContainer;
    [Export] private CharacterTabUI _tabCharacter;
    [Export] private BonesTabUI _tabBones;
    [Export] private CameraTabUI _tabCamera;
    [Export] private PoseTabUI _tabPose;
    [Export] private SceneTabUI _tabScene;
    [Export] private EffectsTabUI _tabEffects;
    [Export] private LightingTabUI _tabLighting;
    [Export] private ShadingTabUI _tabShading;
    [Export] private PaintTabUI _tabPaint;

    [ExportCategory("Floating & Overlay Panels")]
    [Export] private ExportPanelUI _exportPanel;
    [Export] private PaintModExportPanelUI _paintModExportPanel;
    [Export] private Control _navBadge;
    [Export] private Button _btnQuickXRay;
    [Export] private Button _btnQuickFullscreen;

    private SkeletonGizmoManager _currentSkeletonGizmoManager;

    [ExportCategory("Modals & Dialogs")]
    [Export] private Control _modalsLayer;
    [Export] private SettingsDialog _settingsModal;
    [Export] private PanelContainer _aboutModal;
    [Export] private PanelContainer _loadingOverlay;
    [Export] private Label _loadingLabel;
    [Export] private Button _btnAboutClose;
    [Export] private FileDialog _gameFolderDialog;
    [Export] private FileDialog _saveFolderDialog;

    private CharacterIKManager _currentIKManager;

    // Services
    private GamePathManager _pathManager;
    private AcceptDialog _fallbackPathDialog;
    private PanelContainer _toastPanel;
    private Label _toastLabel;
    private Button _toastBtn;
    private Godot.Timer _toastTimer;
    private string _lastExportedFilePath = "";

    public string SavePath => _pathManager?.CurrentSavePath ?? OS.GetSystemDir(OS.SystemDir.Pictures);

    private Control[] _tabScenes;
    private Button[] _tabButtons;
    private int _currentTabIndex = 0;

    public override void _Ready()
    {
        GetViewport().TransparentBg = false;

        _pathManager = new GamePathManager();
        AddChild(_pathManager);

        LinkNodes();
        CreateToastUI();
        CreateFallbackDialog();
        InitTabs();
        ConnectEvents();
        InitializePaths();
        ApplyCurrentGraphicsSettings();
        ApplySubViewportOptimizations();

        UpdateCharacterDependencyState(false);

        // Switch to default Character tab
        SwitchTab(0);

        PlaygroundThemeHelper.AutoDecorate(this);
    }

    private SubViewport _worldViewport;

    public void ToggleFullscreen()
    {
        var win = GetWindow();
        if (win == null) return;

        if (win.IsEmbedded())
        {
            ShowToast("Fullscreen is not supported while embedded in the Godot Editor. Run in a separate window.");
            return;
        }

        var currentMode = DisplayServer.WindowGetMode();
        bool isFs = currentMode == DisplayServer.WindowMode.Fullscreen ||
                    currentMode == DisplayServer.WindowMode.ExclusiveFullscreen ||
                    win.Mode == Window.ModeEnum.Fullscreen ||
                    win.Mode == Window.ModeEnum.ExclusiveFullscreen;

        if (isFs)
        {
            DisplayServer.WindowSetMaxSize(new Vector2I(1152,648));
            DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed);
            win.Mode = Window.ModeEnum.Windowed;
 
            DisplayServer.WindowSetMaxSize(new Vector2I(1920,1080));
            if (_btnQuickFullscreen != null) _btnQuickFullscreen.Text = "Fullscreen";
        }
        else
        {
            DisplayServer.WindowSetMode(DisplayServer.WindowMode.ExclusiveFullscreen);
            win.Mode = Window.ModeEnum.ExclusiveFullscreen;
            if (_btnQuickFullscreen != null) _btnQuickFullscreen.Text = "Windowed";
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventKey key && key.Pressed && !key.Echo && key.Keycode == Key.F11)
        {
            ToggleFullscreen();
            GetViewport().SetInputAsHandled();
            return;
        }

        if (@event.IsActionPressed("view_xray"))
        {
            if (_btnQuickXRay != null)
            {
                _btnQuickXRay.ButtonPressed = !_btnQuickXRay.ButtonPressed;
            }
            else
            {
                _tabBones?.ToggleXRay();
            }
            GetViewport().SetInputAsHandled();
            return;
        }

        _worldViewport ??= GetNodeOrNull<SubViewport>("MainHUD/VBoxContainer/MainSplit/ViewportArea/SubViewportContainer/WorldViewport")
                        ?? GetTree().Root.FindChild("WorldViewport", true, false) as SubViewport;

        _worldViewport?.PushInput(@event);
    }

    private void LinkNodes()
    {
        // Top Bar
        _btnSettings ??= GetNodeOrNull<Button>("MainHUD/VBoxContainer/TopBar/HBoxContainer/SettingsButton")
                      ?? GetNodeOrNull<Button>("MainHUD/TopBar/HBoxContainer/SettingsButton");
        _btnAbout ??= GetNodeOrNull<Button>("MainHUD/VBoxContainer/TopBar/HBoxContainer/AboutButton")
                   ?? GetNodeOrNull<Button>("MainHUD/TopBar/HBoxContainer/AboutButton");
        _btnQuit ??= GetNodeOrNull<Button>("MainHUD/VBoxContainer/TopBar/HBoxContainer/QuitButton")
                  ?? GetNodeOrNull<Button>("MainHUD/TopBar/HBoxContainer/QuitButton");

        // Tab Buttons
        _btnTabCharacter ??= GetNodeOrNull<Button>("MainHUD/VBoxContainer/MainSplit/LeftPanel/VBoxContainer/TabGrid/CharacterButton")
                          ?? GetNodeOrNull<Button>("MainHUD/LeftPanel/VBoxContainer/TabGrid/CharacterButton");
        _btnTabBones ??= GetNodeOrNull<Button>("MainHUD/VBoxContainer/MainSplit/LeftPanel/VBoxContainer/TabGrid/BonesButton")
                      ?? GetNodeOrNull<Button>("MainHUD/LeftPanel/VBoxContainer/TabGrid/BonesButton");
        _btnTabCamera ??= GetNodeOrNull<Button>("MainHUD/VBoxContainer/MainSplit/LeftPanel/VBoxContainer/TabGrid/CameraButton")
                       ?? GetNodeOrNull<Button>("MainHUD/LeftPanel/VBoxContainer/TabGrid/CameraButton");
        _btnTabPose ??= GetNodeOrNull<Button>("MainHUD/VBoxContainer/MainSplit/LeftPanel/VBoxContainer/TabGrid/PoseButton")
                     ?? GetNodeOrNull<Button>("MainHUD/LeftPanel/VBoxContainer/TabGrid/PoseButton");
        _btnTabScene ??= GetNodeOrNull<Button>("MainHUD/VBoxContainer/MainSplit/LeftPanel/VBoxContainer/TabGrid/SceneButton")
                      ?? GetNodeOrNull<Button>("MainHUD/LeftPanel/VBoxContainer/TabGrid/SceneButton");
        _btnTabEffects ??= GetNodeOrNull<Button>("MainHUD/VBoxContainer/MainSplit/LeftPanel/VBoxContainer/TabGrid/EffectsButton")
                        ?? GetNodeOrNull<Button>("MainHUD/LeftPanel/VBoxContainer/TabGrid/EffectsButton");
        _btnTabLighting ??= GetNodeOrNull<Button>("MainHUD/VBoxContainer/MainSplit/LeftPanel/VBoxContainer/TabGrid/LightButton")
                         ?? GetNodeOrNull<Button>("MainHUD/LeftPanel/VBoxContainer/TabGrid/LightButton");
        _btnTabShading ??= GetNodeOrNull<Button>("MainHUD/VBoxContainer/MainSplit/LeftPanel/VBoxContainer/TabGrid/ShadingButton")
                        ?? GetNodeOrNull<Button>("MainHUD/LeftPanel/VBoxContainer/TabGrid/ShadingButton");
        _btnTabPaint ??= GetNodeOrNull<Button>("MainHUD/VBoxContainer/MainSplit/LeftPanel/VBoxContainer/TabGrid/PaintButton")
                      ?? GetNodeOrNull<Button>("MainHUD/LeftPanel/VBoxContainer/TabGrid/PaintButton");

        // Tab Content
        _tabContentContainer ??= GetNodeOrNull<Container>("MainHUD/VBoxContainer/MainSplit/LeftPanel/VBoxContainer/TabContentPanel/MarginContainer/ScrollContainer/TabContentContainer")
                              ?? GetNodeOrNull<Container>("MainHUD/LeftPanel/VBoxContainer/TabContentPanel/MarginContainer/ScrollContainer/TabContentContainer");
        _tabCharacter ??= _tabContentContainer?.GetNodeOrNull<CharacterTabUI>("CharacterTab");
        _tabBones ??= _tabContentContainer?.GetNodeOrNull<BonesTabUI>("BonesTab");
        _tabCamera ??= _tabContentContainer?.GetNodeOrNull<CameraTabUI>("CameraTab");
        _tabPose ??= _tabContentContainer?.GetNodeOrNull<PoseTabUI>("PoseTab");
        _tabScene ??= _tabContentContainer?.GetNodeOrNull<SceneTabUI>("SceneTab");
        _tabEffects ??= _tabContentContainer?.GetNodeOrNull<EffectsTabUI>("EffectsTab");
        _tabLighting ??= _tabContentContainer?.GetNodeOrNull<LightingTabUI>("LightingTab");
        _tabShading ??= _tabContentContainer?.GetNodeOrNull<ShadingTabUI>("ShadingTab");
        _tabPaint ??= _tabContentContainer?.GetNodeOrNull<PaintTabUI>("PaintTab");

        // Floating Panels & Quick Actions
        _exportPanel ??= GetNodeOrNull<ExportPanelUI>("MainHUD/VBoxContainer/MainSplit/ViewportArea/ExportPanel")
                      ?? GetNodeOrNull<ExportPanelUI>("MainHUD/ExportPanel");
        _paintModExportPanel ??= GetNodeOrNull<PaintModExportPanelUI>("MainHUD/VBoxContainer/MainSplit/ViewportArea/PaintModExportPanel")
                              ?? GetTree().Root.FindChild("PaintModExportPanel", true, false) as PaintModExportPanelUI;
        _navBadge ??= GetNodeOrNull<Control>("MainHUD/VBoxContainer/MainSplit/ViewportArea/NavBadge")
                   ?? GetNodeOrNull<Control>("MainHUD/NavBadge");

        _btnQuickXRay ??= GetNodeOrNull<Button>("MainHUD/VBoxContainer/MainSplit/ViewportArea/QuickActionsStrip/BtnQuickXRay")
                       ?? GetTree().Root.FindChild("BtnQuickXRay", true, false) as Button;
        _btnQuickFullscreen ??= GetNodeOrNull<Button>("MainHUD/VBoxContainer/MainSplit/ViewportArea/QuickActionsStrip/BtnQuickFullscreen")
                             ?? GetTree().Root.FindChild("BtnQuickFullscreen", true, false) as Button;

        // Modals
        _modalsLayer ??= GetNodeOrNull<Control>("MainHUD/ModalsLayer");
        _settingsModal ??= GetNodeOrNull<SettingsDialog>("MainHUD/ModalsLayer/SettingsModal")
                        ?? GetTree().Root.FindChild("SettingsModal", true, false) as SettingsDialog;
        if (_settingsModal != null)
        {
            _settingsModal.Setup(_pathManager);
        }

        _aboutModal ??= GetNodeOrNull<PanelContainer>("MainHUD/ModalsLayer/AboutModal");
        _loadingOverlay ??= GetNodeOrNull<PanelContainer>("MainHUD/ModalsLayer/LoadingOverlay");
        _loadingLabel ??= GetNodeOrNull<Label>("MainHUD/ModalsLayer/LoadingOverlay/Label");

        _btnAboutClose ??= GetNodeOrNull<Button>("MainHUD/ModalsLayer/AboutModal/MarginContainer/VBoxContainer/Button");

        _gameFolderDialog ??= GetNodeOrNull<FileDialog>("MainHUD/ModalsLayer/GameFolderDialog");
        _saveFolderDialog ??= GetNodeOrNull<FileDialog>("MainHUD/ModalsLayer/ScreenshotFolderDialog");
    }

    private void InitTabs()
    {
        _tabScenes = new Control[]
        {
            _tabCharacter,
            _tabBones,
            _tabCamera,
            _tabPose,
            _tabScene,
            _tabEffects,
            _tabLighting,
            _tabShading,
            _tabPaint
        };

        _tabButtons = new Button[]
        {
            _btnTabCharacter,
            _btnTabBones,
            _btnTabCamera,
            _btnTabPose,
            _btnTabScene,
            _btnTabEffects,
            _btnTabLighting,
            _btnTabShading,
            _btnTabPaint
        };

        for (int i = 0; i < _tabButtons.Length; i++)
        {
            if (_tabButtons[i] != null)
            {
                PlaygroundThemeHelper.MakeTabButton(_tabButtons[i], i == _currentTabIndex);
                int tabIdx = i;
                _tabButtons[i].Pressed += () => SwitchTab(tabIdx);
            }
        }
    }

    public void SwitchTab(int tabIndex)
    {
        int prevTab = _currentTabIndex;
        _currentTabIndex = tabIndex;

        for (int i = 0; i < _tabScenes.Length; i++)
        {
            if (_tabScenes[i] != null)
            {
                _tabScenes[i].Visible = (i == tabIndex);
            }

            if (_tabButtons[i] != null)
            {
                bool active = (i == tabIndex);
                PlaygroundThemeHelper.UpdateTabButtonState(_tabButtons[i], active);
            }
        }

        if (prevTab == 8 && tabIndex != 8)
        {
            _tabPaint?.OnTabDeactivated();
            if (_paintModExportPanel != null) _paintModExportPanel.Visible = false;
            if (_exportPanel != null) _exportPanel.Visible = true;
            _currentSkeletonGizmoManager?.SetGizmoEnabled(true);
            _tabShading?.SetPaintingModeActive(false);
        }
        else if (tabIndex == 8)
        {
            var hero = GetVpkLoader()?.CurrentHeroNode;
            if (hero != null && _tabPaint?.CurrentHero == null)
            {
                _tabPaint?.SetCurrentHero(hero);
            }
            _tabPaint?.OnTabActivated();

            if (_exportPanel != null) _exportPanel.Visible = false;
            if (_paintModExportPanel != null)
            {
                _paintModExportPanel.Visible = true;
                _paintModExportPanel.Setup(_tabPaint?.LayerManager, _tabPaint?.MeshHierarchy, _tabPaint?.CurrentHero);
            }
            _currentSkeletonGizmoManager?.SetGizmoEnabled(false);
            _tabShading?.SetPaintingModeActive(true);
        }
    }

    private void ConnectEvents()
    {
        // Top Bar
        if (_btnSettings != null)
        {
            _btnSettings.Pressed += () =>
            {
                if (_modalsLayer != null) _modalsLayer.Visible = true;
                _settingsModal?.Open();
            };
        }

        if (_settingsModal != null)
        {
            _settingsModal.DialogClosed += () =>
            {
                if (_modalsLayer != null && (_aboutModal == null || !_aboutModal.Visible))
                {
                    _modalsLayer.Visible = false;
                }
                UpdateVpkLoaderPath(_pathManager.CurrentGamePath);
            };
        }

        if (_btnAbout != null)
        {
            _btnAbout.Pressed += () =>
            {
                if (_modalsLayer != null) _modalsLayer.Visible = true;
                if (_aboutModal != null)
                {
                    _aboutModal.Visible = true;
                    _aboutModal.MoveToFront();
                }
            };
        }

        if (_btnQuit != null)
        {
            _btnQuit.Pressed += () => GetTree().Quit();
        }

        if (_btnAboutClose != null)
        {
            _btnAboutClose.Pressed += () =>
            {
                if (_aboutModal != null) _aboutModal.Visible = false;
                if (_modalsLayer != null && (_settingsModal == null || !_settingsModal.Visible))
                {
                    _modalsLayer.Visible = false;
                }
            };
        }

        if (_gameFolderDialog != null) _gameFolderDialog.DirSelected += OnGameDirSelected;
        if (_saveFolderDialog != null) _saveFolderDialog.DirSelected += OnSaveDirSelected;

        // UserSettings & Gizmo Display event bindings
        UserSettings.Msaa3DChanged += (v) => ApplyCurrentGraphicsSettings();
        UserSettings.ShadowQualityChanged += (v) => ApplyCurrentGraphicsSettings();
        UserSettings.StudioEnvironmentChanged += (v) => ApplyCurrentGraphicsSettings();
        UserSettings.ShowStudioBackgroundChanged += (v) => ApplyCurrentGraphicsSettings();
        UserSettings.PerformanceSettingsChanged += () => UserSettings.ApplyPerformanceSettings();
        GizmoDisplaySettings.OnSettingsChanged += () => _currentIKManager?.SetHandlesOpacity(GizmoDisplaySettings.IKHandlesOpacity);

        // Quick Action Buttons
        if (_btnQuickXRay != null)
        {
            _btnQuickXRay.ToggleMode = true;
            _btnQuickXRay.Toggled += (pressed) =>
            {
                _tabBones?.SetXRay(pressed);
            };

            if (_tabBones != null)
            {
                _tabBones.OnXRayToggled += (pressed) =>
                {
                    if (_btnQuickXRay != null && _btnQuickXRay.ButtonPressed != pressed)
                    {
                        _btnQuickXRay.ButtonPressed = pressed;
                    }
                };
            }
        }

        if (_btnQuickFullscreen != null)
        {
            var win = GetWindow();
            if (win != null)
            {
                var mode = DisplayServer.WindowGetMode();
                bool isFs = mode == DisplayServer.WindowMode.Fullscreen || mode == DisplayServer.WindowMode.ExclusiveFullscreen;
                _btnQuickFullscreen.Text = isFs ? "Windowed" : "Fullscreen";
            }
            _btnQuickFullscreen.Pressed += ToggleFullscreen;
        }

        // VPK Loader Bridge
        var loader = GetVpkLoader();
        if (loader != null)
        {
            loader.LoadStarted += () => ShowLoading(true);
            loader.LoadFinished += () => ShowLoading(false);
            loader.HeroLoaded += OnHeroLoaded;
            loader.HeroUnloaded += OnHeroUnloaded;
        }
    }

    public void UpdateCharacterDependencyState(bool hasCharacter)
    {
        if (_btnTabBones != null)
        {
            _btnTabBones.Disabled = !hasCharacter;
            _btnTabBones.Modulate = hasCharacter ? Colors.White : new Color(0.5f, 0.5f, 0.5f, 0.5f);
            _btnTabBones.TooltipText = hasCharacter ? "" : "Requires an active character model";
        }

        if (_btnTabPose != null)
        {
            _btnTabPose.Disabled = !hasCharacter;
            _btnTabPose.Modulate = hasCharacter ? Colors.White : new Color(0.5f, 0.5f, 0.5f, 0.5f);
            _btnTabPose.TooltipText = hasCharacter ? "" : "Requires an active character model";
        }

        if (_btnTabPaint != null)
        {
            _btnTabPaint.Disabled = !hasCharacter;
            _btnTabPaint.Modulate = hasCharacter ? Colors.White : new Color(0.5f, 0.5f, 0.5f, 0.5f);
            _btnTabPaint.TooltipText = hasCharacter ? "" : "Requires an active character model";
        }

        _tabCharacter?.SetControlsEnabled(hasCharacter);

        if (!hasCharacter && (_currentTabIndex == 1 || _currentTabIndex == 3 || _currentTabIndex == 8))
        {
            SwitchTab(0);
        }
    }

    private VpkLoaderTest GetVpkLoader()
    {
        return GetNodeOrNull<VpkLoaderTest>("/root/Main/UIRoot/MainHUD/VBoxContainer/MainSplit/ViewportArea/SubViewportContainer/WorldViewport/VpkLoaderTest")
            ?? GetNodeOrNull<VpkLoaderTest>("/root/Main/VpkLoaderTest")
            ?? GetTree().Root.FindChild("VpkLoaderTest", true, false) as VpkLoaderTest;
    }

    private void OnHeroLoaded(Node3D heroNode)
    {
        if (heroNode == null) return;

        UpdateCharacterDependencyState(true);

        // Populate Character & Feature Tabs
        _tabCharacter?.SetHero(heroNode);
        _tabPaint?.SetCurrentHero(heroNode);
        _tabShading?.SetHero(heroNode);

        if (_currentTabIndex == 8)
        {
            _tabShading?.SetPaintingModeActive(true);
        }

        // Find Skeleton & Animation Player
        var skeleton = SearchSkeleton(heroNode);
        var animPlayer = SearchAnimationPlayer(heroNode);

        if (skeleton != null)
        {
            _tabBones?.SetSkeleton(skeleton);
            _tabPose?.SetSkeleton(skeleton);

            ProceduralClothSolver.Conform(skeleton);

            // Configure SkeletonGizmoManager
            var gizmoManager = new SkeletonGizmoManager
            {
                Name = "SkeletonGizmoManager",
                TargetSkeleton = skeleton
            };
            skeleton.AddChild(gizmoManager);
            _currentSkeletonGizmoManager = gizmoManager;
            if (_currentTabIndex == 8)
            {
                gizmoManager.SetGizmoEnabled(false);
            }

            if (_tabBones != null)
            {
                gizmoManager.UIManager = _tabBones;
            }

            // Configure CharacterIKManager
            var ikManager = new CharacterIKManager
            {
                Name = "CharacterIKManager"
            };
            skeleton.AddChild(ikManager);
            ikManager.Setup(skeleton);
            _currentIKManager = ikManager;
            _currentIKManager.SetHandlesOpacity(GizmoDisplaySettings.IKHandlesOpacity);
            _tabBones?.SetIKManager(ikManager);
        }

        if (animPlayer != null)
        {
            _tabPose?.SetAnimationPlayer(animPlayer);
        }
    }

    private void OnHeroUnloaded()
    {
        _currentIKManager = null;
        _tabCharacter?.ClearSubmeshes();
        _tabShading?.ClearHero();
        _tabPaint?.ClearHero();
        _tabBones?.SetSkeleton(null);
        _tabBones?.SetIKManager(null);
        _tabPose?.SetSkeleton(null);
        _tabPose?.SetAnimationPlayer(null);
        UpdateCharacterDependencyState(false);
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

    public void ShowLoading(bool show, string message = "Loading character model...")
    {
        if (_modalsLayer != null) _modalsLayer.Visible = show || (_settingsModal != null && _settingsModal.Visible) || (_aboutModal != null && _aboutModal.Visible);
        if (_loadingOverlay != null) _loadingOverlay.Visible = show;
        if (_loadingLabel != null && message != null) _loadingLabel.Text = message;
    }

    #region Toast Notification System
    private void CreateToastUI()
    {
        _toastPanel = new PanelContainer();
        _toastPanel.Visible = false;

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 16);
        margin.AddThemeConstantOverride("margin_top", 10);
        margin.AddThemeConstantOverride("margin_right", 16);
        margin.AddThemeConstantOverride("margin_bottom", 10);

        var hbox = new HBoxContainer();
        hbox.AddThemeConstantOverride("separation", 16);

        _toastLabel = new Label();
        _toastLabel.Text = "Screenshot saved!";

        _toastBtn = new Button();
        _toastBtn.Text = "Open Folder";
        _toastBtn.Pressed += () =>
        {
            if (!string.IsNullOrEmpty(_lastExportedFilePath))
            {
                string folder = Path.GetDirectoryName(_lastExportedFilePath);
                if (Directory.Exists(folder))
                {
                    OS.ShellOpen(folder);
                }
            }
        };

        hbox.AddChild(_toastLabel);
        hbox.AddChild(_toastBtn);
        margin.AddChild(hbox);
        _toastPanel.AddChild(margin);

        PlaygroundThemeHelper.MakeCard(_toastPanel);
        PlaygroundThemeHelper.MakeAccentButton(_toastBtn);
        PlaygroundThemeHelper.MakeHeaderLabel(_toastLabel);

        _toastPanel.SetAnchorsPreset(Control.LayoutPreset.CenterBottom);
        _toastPanel.Position = new Vector2(_toastPanel.Position.X, -60);

        var hud = GetNodeOrNull("MainHUD") ?? this;
        hud.AddChild(_toastPanel);

        _toastTimer = new Godot.Timer
        {
            OneShot = true,
            WaitTime = 5.0
        };
        _toastTimer.Timeout += () => _toastPanel.Visible = false;
        AddChild(_toastTimer);
    }

    public void ShowToast(string message, string filePath = null)
    {
        _lastExportedFilePath = filePath ?? "";
        if (_toastLabel != null) _toastLabel.Text = message;
        if (_toastBtn != null) _toastBtn.Visible = !string.IsNullOrEmpty(filePath);
        if (_toastPanel != null) _toastPanel.Visible = true;
        _toastTimer?.Start();
    }
    #endregion

    #region Path & Settings Management
    private void CreateFallbackDialog()
    {
        _fallbackPathDialog = new AcceptDialog
        {
            Title = "Deadlock Path Not Found",
            DialogText = "Could not automatically detect the Deadlock installation path.\nPlease select the Deadlock game directory manually.",
            Exclusive = true
        };
        _fallbackPathDialog.Confirmed += () => _gameFolderDialog?.PopupCentered();

        var modals = GetNodeOrNull("MainHUD/ModalsLayer") ?? this;
        modals.AddChild(_fallbackPathDialog);
    }

    private async void InitializePaths()
    {
        ShowLoading(true, "Detecting Deadlock path...");

        if (string.IsNullOrEmpty(_pathManager.CurrentGamePath) || !_pathManager.ValidateDeadlockPath(_pathManager.CurrentGamePath))
        {
            string autoPath = await _pathManager.AutoDetectDeadlockPathAsync();
            if (!string.IsNullOrEmpty(autoPath))
            {
                _pathManager.SaveConfig(autoPath, _pathManager.CurrentSavePath);
                GD.Print($"[StudioUI] Deadlock auto-detected at: {autoPath}");
            }
            else
            {
                ShowLoading(false);
                _fallbackPathDialog?.PopupCentered();
                return;
            }
        }

        UpdateVpkLoaderPath(_pathManager.CurrentGamePath);
        ShowLoading(false);
    }

    private void UpdateVpkLoaderPath(string basePath)
    {
        var loader = GetVpkLoader();
        if (loader != null)
        {
            loader.VpkPath = Path.Combine(basePath, "game", "citadel", "pak01_dir.vpk");
        }

        var charTab = GetNodeOrNull<CharacterTabUI>("MainHUD/VBoxContainer/MainSplit/LeftPanel/VBoxContainer/TabContentPanel/MarginContainer/ScrollContainer/TabContentContainer/CharacterTab")
                   ?? GetTree().Root.FindChild("CharacterTab", true, false) as CharacterTabUI;
        charTab?.PopulateAddonDropdown();
    }

    private void OnGameDirSelected(string dir)
    {
        if (_pathManager.ValidateDeadlockPath(dir))
        {
            _pathManager.SaveConfig(dir, _pathManager.CurrentSavePath);
            UpdateVpkLoaderPath(dir);
            ShowToast("Game path updated!");
        }
        else
        {
            var alert = new AcceptDialog
            {
                Title = "Invalid Game Folder",
                DialogText = "The selected directory does not appear to be a valid Deadlock installation.\nExpected game/citadel/pak01_dir.vpk"
            };
            GetTree().Root.AddChild(alert);
            alert.PopupCentered();
        }
    }

    private void OnSaveDirSelected(string dir)
    {
        _pathManager.SaveConfig(_pathManager.CurrentGamePath, dir);
        ShowToast("Screenshots path updated!");
    }

    private void ApplyCurrentGraphicsSettings()
    {
        var worldVp = GetNodeOrNull<SubViewport>("/root/Main/UIRoot/MainHUD/VBoxContainer/MainSplit/ViewportArea/SubViewportContainer/WorldViewport")
                   ?? GetTree().Root.FindChild("WorldViewport", true, false) as SubViewport;
        var dirLight = GetTree().Root.FindChild("DirectionalLight3D", true, false) as DirectionalLight3D;
        var bgRect = GetNodeOrNull<ColorRect>("/root/Main/UIRoot/MainHUD/VBoxContainer/MainSplit/ViewportArea/BGCanvas/ColorRect")
                  ?? GetTree().Root.FindChild("ColorRect", true, false) as ColorRect;
        var envNode = GetNodeOrNull<WorldEnvironment>("/root/Main/UIRoot/MainHUD/VBoxContainer/MainSplit/ViewportArea/SubViewportContainer/WorldViewport/WorldEnvironment")
                   ?? GetNodeOrNull<WorldEnvironment>("/root/Main/WorldEnvironment")
                   ?? GetTree().Root.FindChild("WorldEnvironment", true, false) as WorldEnvironment;
        var bgTex = GetNodeOrNull<TextureRect>("/root/Main/UIRoot/MainHUD/VBoxContainer/MainSplit/ViewportArea/BGCanvas/BackgroundRect")
                 ?? GetTree().Root.FindChild("BackgroundRect", true, false) as TextureRect;
        var stagePlatform = GetNodeOrNull<Node3D>("/root/Main/UIRoot/MainHUD/VBoxContainer/MainSplit/ViewportArea/SubViewportContainer/WorldViewport/StagePlatform")
                         ?? GetTree().Root.FindChild("StagePlatform", true, false) as Node3D;

        UserSettings.ApplyGraphicsSettings(GetViewport(), worldVp, dirLight, bgRect, envNode, bgTex, stagePlatform);
    }

    private void ApplySubViewportOptimizations()
    {
        var worldViewport = GetNodeOrNull<SubViewport>("/root/Main/UIRoot/MainHUD/VBoxContainer/MainSplit/ViewportArea/SubViewportContainer/WorldViewport")
                         ?? GetTree().Root.FindChild("WorldViewport", true, false) as SubViewport;

        if (worldViewport != null)
        {
            // Cap shadow atlas to 2048 to prevent 4K/8K shadow buffer overhead
            worldViewport.PositionalShadowAtlasSize = 2048;
            worldViewport.PositionalShadowAtlas16Bits = true;
        }

        var gizmoViewport = GetNodeOrNull<SubViewport>("/root/Main/UIRoot/MainHUD/VBoxContainer/MainSplit/ViewportArea/GizmoViewportContainer/GizmoViewport")
                         ?? GetTree().Root.FindChild("GizmoViewport", true, false) as SubViewport;

        if (gizmoViewport != null)
        {
            // Gizmo viewport only renders line wireframes & markers on Layer 2, so zero shadow allocation needed
            gizmoViewport.PositionalShadowAtlasSize = 0;
        }
    }
    #endregion
}
