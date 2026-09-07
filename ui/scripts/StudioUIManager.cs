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

    [ExportCategory("Floating & Overlay Panels")]
    [Export] private ExportPanelUI _exportPanel;
    [Export] private Control _navBadge;
    [Export] private Button _btnQuickXRay;
    [Export] private Button _btnQuickFullscreen;

    [ExportCategory("Modals & Dialogs")]
    [Export] private Control _modalsLayer;
    [Export] private PanelContainer _settingsModal;
    [Export] private PanelContainer _aboutModal;
    [Export] private PanelContainer _loadingOverlay;
    [Export] private Label _loadingLabel;
    [Export] private Button _btnSettingsClose;
    [Export] private Button _btnAboutClose;
    [Export] private FileDialog _gameFolderDialog;
    [Export] private FileDialog _saveFolderDialog;

    [ExportCategory("Settings Modal Controls")]
    [Export] private OptionButton _langOption;
    [Export] private LineEdit _gamePathInput;
    [Export] private Button _btnBrowseGamePath;
    [Export] private LineEdit _savePathInput;
    [Export] private Button _btnBrowseSavePath;
    [Export] private OptionButton _texQualityOption;
    [Export] private ColorPickerButton _xrayLineColorPicker;
    [Export] private HSlider _xrayLineOpacitySlider;
    [Export] private Label _xrayLineOpacityLabel;
    [Export] private ColorPickerButton _bonePrimaryColorPicker;
    [Export] private ColorPickerButton _boneClothingColorPicker;
    [Export] private HSlider _boneMarkerOpacitySlider;
    [Export] private Label _boneMarkerOpacityLabel;
    [Export] private HSlider _boneMarkerSizeSlider;
    [Export] private Label _boneMarkerSizeLabel;
    [Export] private Button _btnResetDisplaySettings;

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
        GetViewport().TransparentBg = true;
        
        var envNode = GetNodeOrNull<WorldEnvironment>("/root/Main/UIRoot/MainHUD/VBoxContainer/MainSplit/ViewportArea/SubViewportContainer/WorldViewport/WorldEnvironment")
                   ?? GetNodeOrNull<WorldEnvironment>("/root/Main/WorldEnvironment")
                   ?? GetTree().Root.FindChild("WorldEnvironment", true, false) as WorldEnvironment;

        if (envNode != null && envNode.Environment != null)
        {
            envNode.Environment.BackgroundMode = Godot.Environment.BGMode.Canvas;
            envNode.Environment.BackgroundCanvasMaxLayer = -1;

            envNode.Environment.AmbientLightSource = Godot.Environment.AmbientSource.Color;
            envNode.Environment.AmbientLightColor = new Color(0.28f, 0.29f, 0.32f, 1f);
            envNode.Environment.AmbientLightEnergy = 1.0f;
            envNode.Environment.ReflectedLightSource = Godot.Environment.ReflectionSource.Sky;
        }

        _pathManager = new GamePathManager();
        AddChild(_pathManager);

        LinkNodes();
        CreateToastUI();
        CreateFallbackDialog();
        InitTabs();
        ConnectEvents();
        InitializeDisplaySettingsUI();
        InitializePaths();

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
            DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed);
            win.Mode = Window.ModeEnum.Windowed;
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

        // Floating Panels & Quick Actions
        _exportPanel ??= GetNodeOrNull<ExportPanelUI>("MainHUD/VBoxContainer/MainSplit/ViewportArea/ExportPanel")
                      ?? GetNodeOrNull<ExportPanelUI>("MainHUD/ExportPanel");
        _navBadge ??= GetNodeOrNull<Control>("MainHUD/VBoxContainer/MainSplit/ViewportArea/NavBadge")
                   ?? GetNodeOrNull<Control>("MainHUD/NavBadge");

        _btnQuickXRay ??= GetNodeOrNull<Button>("MainHUD/VBoxContainer/MainSplit/ViewportArea/QuickActionsStrip/BtnQuickXRay")
                       ?? GetTree().Root.FindChild("BtnQuickXRay", true, false) as Button;
        _btnQuickFullscreen ??= GetNodeOrNull<Button>("MainHUD/VBoxContainer/MainSplit/ViewportArea/QuickActionsStrip/BtnQuickFullscreen")
                             ?? GetTree().Root.FindChild("BtnQuickFullscreen", true, false) as Button;

        // Modals
        _modalsLayer ??= GetNodeOrNull<Control>("MainHUD/ModalsLayer");
        _settingsModal ??= GetNodeOrNull<PanelContainer>("MainHUD/ModalsLayer/SettingsModal");
        _aboutModal ??= GetNodeOrNull<PanelContainer>("MainHUD/ModalsLayer/AboutModal");
        _loadingOverlay ??= GetNodeOrNull<PanelContainer>("MainHUD/ModalsLayer/LoadingOverlay");
        _loadingLabel ??= GetNodeOrNull<Label>("MainHUD/ModalsLayer/LoadingOverlay/Label");

        _btnSettingsClose ??= GetNodeOrNull<Button>("MainHUD/ModalsLayer/SettingsModal/VBoxContainer/CloseButton");
        _btnAboutClose ??= GetNodeOrNull<Button>("MainHUD/ModalsLayer/AboutModal/MarginContainer/VBoxContainer/Button");

        _gameFolderDialog ??= GetNodeOrNull<FileDialog>("MainHUD/ModalsLayer/GameFolderDialog");
        _saveFolderDialog ??= GetNodeOrNull<FileDialog>("MainHUD/ModalsLayer/ScreenshotFolderDialog");

        // Settings Controls
        _langOption ??= GetNodeOrNull<OptionButton>("MainHUD/ModalsLayer/SettingsModal/VBoxContainer/TabContainer/General/VBoxContainer/LanguageButton");
        _gamePathInput ??= GetNodeOrNull<LineEdit>("MainHUD/ModalsLayer/SettingsModal/VBoxContainer/TabContainer/General/VBoxContainer/GamePathSearchContainer/GamePathLine");
        _btnBrowseGamePath ??= GetNodeOrNull<Button>("MainHUD/ModalsLayer/SettingsModal/VBoxContainer/TabContainer/General/VBoxContainer/GamePathSearchContainer/BrowseGamePathButton");
        _savePathInput ??= GetNodeOrNull<LineEdit>("MainHUD/ModalsLayer/SettingsModal/VBoxContainer/TabContainer/General/VBoxContainer/SavesPathSearchContainer/SavesPathLine");
        _btnBrowseSavePath ??= GetNodeOrNull<Button>("MainHUD/ModalsLayer/SettingsModal/VBoxContainer/TabContainer/General/VBoxContainer/SavesPathSearchContainer/BrowseSavePathButton");
        _texQualityOption ??= GetNodeOrNull<OptionButton>("MainHUD/ModalsLayer/SettingsModal/VBoxContainer/TabContainer/Graphics/VBoxContainer/TexQualityButton");

        _xrayLineColorPicker ??= GetNodeOrNull<ColorPickerButton>("MainHUD/ModalsLayer/SettingsModal/VBoxContainer/TabContainer/UI/VBoxContainer/HBoxContainerLineColor/ColorPickerButton");
        _xrayLineOpacitySlider ??= GetNodeOrNull<HSlider>("MainHUD/ModalsLayer/SettingsModal/VBoxContainer/TabContainer/UI/VBoxContainer/HBoxContainerLineOpacity/HSlider");
        _xrayLineOpacityLabel ??= GetNodeOrNull<Label>("MainHUD/ModalsLayer/SettingsModal/VBoxContainer/TabContainer/UI/VBoxContainer/HBoxContainerLineOpacity/LabelCurrOpacity");

        _bonePrimaryColorPicker ??= GetNodeOrNull<ColorPickerButton>("MainHUD/ModalsLayer/SettingsModal/VBoxContainer/TabContainer/UI/VBoxContainer/HBoxContainerPrimary/ColorPickerButton");
        _boneClothingColorPicker ??= GetNodeOrNull<ColorPickerButton>("MainHUD/ModalsLayer/SettingsModal/VBoxContainer/TabContainer/UI/VBoxContainer/HBoxContainerClothing/ColorPickerButton");
        _boneMarkerOpacitySlider ??= GetNodeOrNull<HSlider>("MainHUD/ModalsLayer/SettingsModal/VBoxContainer/TabContainer/UI/VBoxContainer/HBoxContainerBoneOpacity/HSlider");
        _boneMarkerOpacityLabel ??= GetNodeOrNull<Label>("MainHUD/ModalsLayer/SettingsModal/VBoxContainer/TabContainer/UI/VBoxContainer/HBoxContainerBoneOpacity/LabelCurrOpacity");
        _boneMarkerSizeSlider ??= GetNodeOrNull<HSlider>("MainHUD/ModalsLayer/SettingsModal/VBoxContainer/TabContainer/UI/VBoxContainer/HBoxContainerBoneSize/HSlider");
        _boneMarkerSizeLabel ??= GetNodeOrNull<Label>("MainHUD/ModalsLayer/SettingsModal/VBoxContainer/TabContainer/UI/VBoxContainer/HBoxContainerBoneSize/LabelCurrSize");
        _btnResetDisplaySettings ??= GetNodeOrNull<Button>("MainHUD/ModalsLayer/SettingsModal/VBoxContainer/TabContainer/UI/VBoxContainer/ResetButton");
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
            _tabShading
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
            _btnTabShading
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
    }

    private void ConnectEvents()
    {
        // Top Bar
        if (_btnSettings != null)
        {
            _btnSettings.Pressed += () =>
            {
                if (_modalsLayer != null) _modalsLayer.Visible = true;
                if (_settingsModal != null)
                {
                    _settingsModal.Visible = true;
                    _settingsModal.MoveToFront();
                }
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

        if (_btnSettingsClose != null)
        {
            _btnSettingsClose.Pressed += () =>
            {
                if (_settingsModal != null) _settingsModal.Visible = false;
                if (_modalsLayer != null && (_aboutModal == null || !_aboutModal.Visible))
                {
                    _modalsLayer.Visible = false;
                }
            };
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

        // Settings Browsing
        if (_btnBrowseGamePath != null) _btnBrowseGamePath.Pressed += () => _gameFolderDialog?.PopupCentered();
        if (_btnBrowseSavePath != null) _btnBrowseSavePath.Pressed += () => _saveFolderDialog?.PopupCentered();

        if (_gameFolderDialog != null) _gameFolderDialog.DirSelected += OnGameDirSelected;
        if (_saveFolderDialog != null) _saveFolderDialog.DirSelected += OnSaveDirSelected;

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

        _tabCharacter?.SetControlsEnabled(hasCharacter);

        if (!hasCharacter && (_currentTabIndex == 1 || _currentTabIndex == 3))
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

        // Populate Character Tab
        _tabCharacter?.SetHero(heroNode);
        _tabShading?.SetHero(heroNode);

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

            if (_tabBones != null)
            {
                gizmoManager.UIManager = _tabBones;
            }
        }

        if (animPlayer != null)
        {
            _tabPose?.SetAnimationPlayer(animPlayer);
        }
    }

    private void OnHeroUnloaded()
    {
        _tabCharacter?.ClearSubmeshes();
        _tabShading?.ClearHero();
        _tabBones?.SetSkeleton(null);
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
        if (_gamePathInput != null) _gamePathInput.Text = _pathManager.CurrentGamePath;
        if (_savePathInput != null) _savePathInput.Text = _pathManager.CurrentSavePath;

        ShowLoading(false);
    }

    private void UpdateVpkLoaderPath(string basePath)
    {
        var loader = GetVpkLoader();
        if (loader != null)
        {
            loader.VpkPath = Path.Combine(basePath, "game", "citadel", "pak01_dir.vpk");
        }
    }

    private void OnGameDirSelected(string dir)
    {
        if (_pathManager.ValidateDeadlockPath(dir))
        {
            _pathManager.SaveConfig(dir, _pathManager.CurrentSavePath);
            if (_gamePathInput != null) _gamePathInput.Text = dir;
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
        if (_savePathInput != null) _savePathInput.Text = dir;
        ShowToast("Screenshots path updated!");
    }

    private void InitializeDisplaySettingsUI()
    {
        if (_xrayLineColorPicker != null)
        {
            _xrayLineColorPicker.Color = GizmoDisplaySettings.XRayLineColor;
            _xrayLineColorPicker.ColorChanged += (c) => GizmoDisplaySettings.XRayLineColor = c;
        }

        if (_xrayLineOpacitySlider != null)
        {
            _xrayLineOpacitySlider.Value = GizmoDisplaySettings.XRayLineOpacity;
            if (_xrayLineOpacityLabel != null) _xrayLineOpacityLabel.Text = $"{GizmoDisplaySettings.XRayLineOpacity:P0}";
            _xrayLineOpacitySlider.ValueChanged += (v) =>
            {
                GizmoDisplaySettings.XRayLineOpacity = (float)v;
                if (_xrayLineOpacityLabel != null) _xrayLineOpacityLabel.Text = $"{v:P0}";
            };
        }

        if (_bonePrimaryColorPicker != null)
        {
            _bonePrimaryColorPicker.Color = GizmoDisplaySettings.BonePrimaryColor;
            _bonePrimaryColorPicker.ColorChanged += (c) => GizmoDisplaySettings.BonePrimaryColor = c;
        }

        if (_boneClothingColorPicker != null)
        {
            _boneClothingColorPicker.Color = GizmoDisplaySettings.BoneClothingColor;
            _boneClothingColorPicker.ColorChanged += (c) => GizmoDisplaySettings.BoneClothingColor = c;
        }

        if (_boneMarkerOpacitySlider != null)
        {
            _boneMarkerOpacitySlider.Value = GizmoDisplaySettings.BoneMarkerOpacity;
            if (_boneMarkerOpacityLabel != null) _boneMarkerOpacityLabel.Text = $"{GizmoDisplaySettings.BoneMarkerOpacity:P0}";
            _boneMarkerOpacitySlider.ValueChanged += (v) =>
            {
                GizmoDisplaySettings.BoneMarkerOpacity = (float)v;
                if (_boneMarkerOpacityLabel != null) _boneMarkerOpacityLabel.Text = $"{v:P0}";
            };
        }

        if (_boneMarkerSizeSlider != null)
        {
            _boneMarkerSizeSlider.Value = GizmoDisplaySettings.BoneMarkerScale;
            if (_boneMarkerSizeLabel != null) _boneMarkerSizeLabel.Text = $"{GizmoDisplaySettings.BoneMarkerScale:F1}x";
            _boneMarkerSizeSlider.ValueChanged += (v) =>
            {
                GizmoDisplaySettings.BoneMarkerScale = (float)v;
                if (_boneMarkerSizeLabel != null) _boneMarkerSizeLabel.Text = $"{v:F1}x";
            };
        }

        if (_btnResetDisplaySettings != null)
        {
            _btnResetDisplaySettings.Pressed += () =>
            {
                GizmoDisplaySettings.ResetToDefaults();
                InitializeDisplaySettingsUI();
            };
        }
    }
    #endregion
}
