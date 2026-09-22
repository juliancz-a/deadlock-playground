using Godot;
using System;
using System.IO;
using DeadlockPlayground.UI;
using DeadlockPlayground.Tools;

public partial class StudioUIManager : CanvasLayer
{
    [ExportCategory("Top Bar Controls")]
    [Export] private Button _btnSettings;
    [Export] private Button _btnFeedback;
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
    [Export] private UVCanvas2DUI _uvCanvasPanel;
    [Export] private Control _navBadge;
    [Export] private Control _painterKeymapBadge;
    [Export] private Button _btnQuickXRay;
    [Export] private Button _btnQuickFullscreen;
    [Export] private Button _btnOpenUVCanvas;
    private Label _painterKeymapLabel;
    private bool _uvCanvasUserWantsOpen = true;

    private SkeletonGizmoManager _currentSkeletonGizmoManager;

    [ExportCategory("Modals & Dialogs")]
    [Export] private Control _modalsLayer;
    [Export] private ColorRect _modalBackdrop;
    [Export] private SettingsDialog _settingsModal;
    [Export] private FeedbackDialog _feedbackModal;
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

    public static StudioUIManager Instance { get; private set; }
    public string SavePath => _pathManager?.CurrentSavePath ?? OS.GetSystemDir(OS.SystemDir.Pictures);

    private Control[] _tabScenes;
    private Button[] _tabButtons;
    private int _currentTabIndex = 0;

    public override void _Ready()
    {
        Instance = this;
        GetViewport().TransparentBg = false;

        _pathManager = new GamePathManager();
        AddChild(_pathManager);

        // Restore persisted gizmo display settings (bone opacity, wireframe opacity, etc.)
        // before any tab or subsystem reads GizmoDisplaySettings values.
        GizmoDisplaySettings.LoadConfig();

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
    private bool _isFullscreen = true;

    public void ToggleFullscreen()
    {
        var win = GetWindow();
        if (win == null) return;

        if (win.IsEmbedded())
        {
            ShowToast("Fullscreen is not supported while embedded in the Godot Editor. Run in a separate window.");
            return;
        }

        int screen = DisplayServer.WindowGetCurrentScreen();
        Vector2I screenSize = DisplayServer.ScreenGetSize(screen);
        Vector2I winSize = win.Size;
        var currentDsMode = DisplayServer.WindowGetMode();

        bool isSpanningScreen = (winSize.X >= screenSize.X - 60 && winSize.Y >= screenSize.Y - 60);

        bool isFs = _isFullscreen ||
                    win.Mode == Window.ModeEnum.Fullscreen ||
                    win.Mode == Window.ModeEnum.ExclusiveFullscreen ||
                    win.Mode == Window.ModeEnum.Maximized ||
                    currentDsMode == DisplayServer.WindowMode.Fullscreen ||
                    currentDsMode == DisplayServer.WindowMode.ExclusiveFullscreen ||
                    currentDsMode == DisplayServer.WindowMode.Maximized ||
                    (win.Borderless && isSpanningScreen);

        if (isFs)
        {
            // Switch to Windowed mode
            _isFullscreen = false;

            // 1. Remove borderless and fullscreen mode
            DisplayServer.WindowSetMaxSize(new Vector2I(1600, 900));
            DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed);
            win.Mode = Window.ModeEnum.Windowed;
            win.Borderless = false;
            DisplayServer.WindowSetFlag(DisplayServer.WindowFlags.Borderless, false);

            Vector2I targetSize = new Vector2I(1600, 900);
            DisplayServer.WindowSetSize(targetSize);
            win.Size = targetSize;

            Rect2I screenRect = DisplayServer.ScreenGetUsableRect(screen);
            Vector2I centerPos = screenRect.Position + (screenRect.Size - targetSize) / 2;
            DisplayServer.WindowSetPosition(centerPos);
            win.Position = centerPos;

            DisplayServer.WindowSetMaxSize(new Vector2I(1920, 1080));

            if (_btnQuickFullscreen != null) _btnQuickFullscreen.Text = "Fullscreen";
        }
        else
        {
            // Switch to Fullscreen mode
            _isFullscreen = true;

            // Unclamp MaxSize so it can expand to full monitor resolution
            DisplayServer.WindowSetMaxSize(Vector2I.Zero);

            win.Mode = Window.ModeEnum.Fullscreen;
            DisplayServer.WindowSetMode(DisplayServer.WindowMode.Fullscreen);

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

        if (@event.IsActionPressed("paint_uv_toggle"))
        {
            if (_tabPaint != null && _tabPaint.Visible && _uvCanvasPanel != null)
            {
                _uvCanvasPanel.ToggleVisibility();
                GetViewport().SetInputAsHandled();
                return;
            }
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
        _btnFeedback ??= GetNodeOrNull<Button>("MainHUD/VBoxContainer/TopBar/HBoxContainer/FeedbackButton")
                      ?? GetNodeOrNull<Button>("MainHUD/TopBar/HBoxContainer/FeedbackButton");
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
        _navBadge ??= GetNodeOrNull<Control>("MainHUD/VBoxContainer/MainSplit/ViewportArea/BottomBadgesContainer/NavBadge")
                   ?? GetTree().Root.FindChild("NavBadge", true, false) as Control;

        _uvCanvasPanel ??= GetNodeOrNull<UVCanvas2DUI>("MainHUD/VBoxContainer/MainSplit/UVCanvasPanel")
                        ?? GetTree().Root.FindChild("UVCanvasPanel", true, false) as UVCanvas2DUI;

        _painterKeymapBadge ??= GetNodeOrNull<Control>("MainHUD/VBoxContainer/MainSplit/ViewportArea/BottomBadgesContainer/PainterKeymapBadge")
                             ?? GetTree().Root.FindChild("PainterKeymapBadge", true, false) as Control;
        _painterKeymapLabel ??= _painterKeymapBadge?.FindChild("PainterKeymapLabel", true, false) as Label;
        if (_painterKeymapLabel != null)
        {
            _painterKeymapLabel.Text = KeybindsManager.GetFullPainterCheatsheet();
        }

        _btnQuickXRay ??= GetNodeOrNull<Button>("MainHUD/VBoxContainer/MainSplit/ViewportArea/QuickActionsStrip/BtnQuickXRay")
                       ?? GetTree().Root.FindChild("BtnQuickXRay", true, false) as Button;
        _btnQuickFullscreen ??= GetNodeOrNull<Button>("MainHUD/VBoxContainer/MainSplit/ViewportArea/QuickActionsStrip/BtnQuickFullscreen")
                             ?? GetTree().Root.FindChild("BtnQuickFullscreen", true, false) as Button;
        _btnOpenUVCanvas ??= GetNodeOrNull<Button>("MainHUD/VBoxContainer/MainSplit/ViewportArea/BtnOpenUVCanvas")
                          ?? GetTree().Root.FindChild("BtnOpenUVCanvas", true, false) as Button;
        if (_btnOpenUVCanvas != null)
        {
            if (_btnOpenUVCanvas.Icon == null)
            {
                var uvIcon = GD.Load<Texture2D>("res://assets/at-icons/uv_layer.svg");
                if (uvIcon != null)
                {
                    _btnOpenUVCanvas.Icon = uvIcon;
                    _btnOpenUVCanvas.ExpandIcon = true;
                    _btnOpenUVCanvas.IconAlignment = HorizontalAlignment.Center;
                    _btnOpenUVCanvas.Text = string.Empty;
                }
            }
            _btnOpenUVCanvas.Visible = false;
            _btnOpenUVCanvas.Pressed += () =>
            {
                _uvCanvasUserWantsOpen = true;
                if (_uvCanvasPanel != null)
                {
                    _uvCanvasPanel.SetVisibleState(true);
                }
            };
        }

        if (_uvCanvasPanel != null)
        {
            _uvCanvasPanel.VisibilityToggled += (isVisible) =>
            {
                _uvCanvasUserWantsOpen = isVisible;
                if (_btnOpenUVCanvas != null)
                {
                    _btnOpenUVCanvas.Visible = (_currentTabIndex == 8 && !isVisible);
                }
            };
        }

        // Modals
        _modalsLayer ??= GetNodeOrNull<Control>("MainHUD/ModalsLayer");
        _modalBackdrop ??= GetNodeOrNull<ColorRect>("MainHUD/ModalsLayer/ModalBackdrop");
        _settingsModal ??= GetNodeOrNull<SettingsDialog>("MainHUD/ModalsLayer/SettingsModal")
                        ?? GetTree().Root.FindChild("SettingsModal", true, false) as SettingsDialog;
        if (_settingsModal != null)
        {
            _settingsModal.Setup(_pathManager);
        }

        _feedbackModal ??= GetNodeOrNull<FeedbackDialog>("MainHUD/ModalsLayer/FeedbackModal")
                        ?? GetTree().Root.FindChild("FeedbackModal", true, false) as FeedbackDialog;

        _aboutModal ??= GetNodeOrNull<PanelContainer>("MainHUD/ModalsLayer/AboutModal");
        _loadingOverlay ??= GetNodeOrNull<PanelContainer>("MainHUD/ModalsLayer/LoadingOverlay");
        _loadingLabel ??= GetNodeOrNull<Label>("MainHUD/ModalsLayer/LoadingOverlay/Label");

        _btnAboutClose ??= GetNodeOrNull<Button>("MainHUD/ModalsLayer/AboutModal/MarginContainer/VBoxContainer/HeaderBar/BtnClose")
                         ?? GetNodeOrNull<Button>("MainHUD/ModalsLayer/AboutModal/MarginContainer/VBoxContainer/Button")
                         ?? _aboutModal?.FindChild("BtnClose", true, false) as Button
                         ?? _aboutModal?.FindChild("Button", true, false) as Button;

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
            if (_exportPanel != null)
            {
                _exportPanel.Visible = true;
                _exportPanel.SetFramingOverlayVisible(true);
            }
            _currentSkeletonGizmoManager?.SetGizmoEnabled(true);
            _tabShading?.SetPaintingModeActive(false);

            if (_uvCanvasPanel != null) _uvCanvasPanel.Visible = false;
            if (_painterKeymapBadge != null) _painterKeymapBadge.Visible = false;
            if (_btnOpenUVCanvas != null) _btnOpenUVCanvas.Visible = false;
            if (_navBadge != null) _navBadge.Visible = true;
        }
        else if (tabIndex == 8)
        {
            var hero = GetVpkLoader()?.CurrentHeroNode;
            if (hero != null && _tabPaint?.CurrentHero == null)
            {
                _tabPaint?.SetCurrentHero(hero);
            }
            _tabPaint?.OnTabActivated();

            if (_exportPanel != null)
            {
                _exportPanel.Visible = false;
                _exportPanel.SetFramingOverlayVisible(false);
            }
            else
            {
                var overlay = GetNodeOrNull<Control>("MainHUD/VBoxContainer/MainSplit/ViewportArea/FramingOverlay");
                if (overlay != null) overlay.Visible = false;
            }
            if (_paintModExportPanel != null)
            {
                _paintModExportPanel.Visible = true;
                _paintModExportPanel.Setup(_tabPaint?.LayerManager, _tabPaint?.MeshHierarchy, _tabPaint?.CurrentHero);
            }
            _currentSkeletonGizmoManager?.SetGizmoEnabled(false);
            _tabShading?.SetPaintingModeActive(true);

            if (_uvCanvasPanel != null)
            {
                _uvCanvasPanel.Setup(_tabPaint?.LayerManager, _tabPaint?.Painter, _tabPaint?.MeshHierarchy, _tabPaint?.BrushPalette);
                _uvCanvasPanel.SetPaintActive(_uvCanvasUserWantsOpen);
            }
            if (_painterKeymapBadge != null)
            {
                _painterKeymapBadge.Visible = true;
                if (_painterKeymapLabel != null)
                {
                    _painterKeymapLabel.Text = KeybindsManager.GetFullPainterCheatsheet();
                }
            }
            if (_btnOpenUVCanvas != null)
            {
                _btnOpenUVCanvas.Visible = (_uvCanvasPanel == null || !_uvCanvasPanel.Visible);
            }
            if (_navBadge != null) _navBadge.Visible = true;
        }
        else if (tabIndex == 6)
        {
            _tabLighting?.SyncUIToScene();
        }
    }

    private void ConnectEvents()
    {
        // Top Bar
        if (_btnSettings != null)
        {
            _btnSettings.Pressed += () =>
            {
                _settingsModal?.Open();
                UpdateModalsState();
            };
        }

        if (_settingsModal != null)
        {
            _settingsModal.DialogClosed += () =>
            {
                UpdateModalsState();
                UpdateVpkLoaderPath(_pathManager.CurrentGamePath);
            };
        }

        if (_btnFeedback != null)
        {
            _btnFeedback.Pressed += () =>
            {
                _feedbackModal?.Open();
                UpdateModalsState();
            };
        }

        if (_feedbackModal != null)
        {
            _feedbackModal.DialogClosed += UpdateModalsState;
        }

        if (_btnAbout != null)
        {
            _btnAbout.Pressed += () =>
            {
                if (_aboutModal != null)
                {
                    _aboutModal.Visible = true;
                }
                UpdateModalsState();
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
                UpdateModalsState();
            };
        }
        var btnAboutBottomClose = _aboutModal?.FindChild("BtnBottomClose", true, false) as Button;
        if (btnAboutBottomClose != null && btnAboutBottomClose != _btnAboutClose)
        {
            btnAboutBottomClose.Pressed += () =>
            {
                if (_aboutModal != null) _aboutModal.Visible = false;
                UpdateModalsState();
            };
        }

        var aboutRtl = _aboutModal?.FindChild("RichTextLabel", true, false) as RichTextLabel;
        if (aboutRtl != null)
        {
            aboutRtl.MetaClicked += (meta) =>
            {
                string url = meta.AsString();
                if (!string.IsNullOrEmpty(url))
                {
                    OS.ShellOpen(url);
                }
            };
        }

        if (_gameFolderDialog != null)
        {
            _gameFolderDialog.DirSelected += (d) => { OnGameDirSelected(d); UpdateModalsState(); };
            _gameFolderDialog.Canceled += UpdateModalsState;
        }
        if (_saveFolderDialog != null)
        {
            _saveFolderDialog.DirSelected += (d) => { OnSaveDirSelected(d); UpdateModalsState(); };
            _saveFolderDialog.Canceled += UpdateModalsState;
        }

        // UserSettings & Gizmo Display event bindings
        UserSettings.Msaa3DChanged += (v) => ApplyCurrentGraphicsSettings();
        UserSettings.ShadowQualityChanged += (v) => ApplyCurrentGraphicsSettings();
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
            _btnQuickFullscreen.Text = _isFullscreen ? "Windowed" : "Fullscreen";
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
                Name = "CharacterIKManager",
                LayerManager = gizmoManager.LayerManager
            };
            skeleton.AddChild(ikManager);
            ikManager.Setup(skeleton);
            _currentIKManager = ikManager;
            gizmoManager.IKManager = ikManager;
            _currentIKManager.SetHandlesOpacity(GizmoDisplaySettings.IKHandlesOpacity);
            _tabBones?.SetIKManager(ikManager);

            // Synchronize Weapon Bone Attachments
            SyncWeaponBoneAttachments(heroNode, skeleton);
        }

        if (animPlayer != null)
        {
            _tabPose?.SetAnimationPlayer(animPlayer);
        }
    }

    private void SyncWeaponBoneAttachments(Node3D heroNode, Skeleton3D skeleton)
    {
        if (heroNode == null || skeleton == null) return;

        var weaponKeywords = new[] { "weapon", "gun", "blaster", "rifle", "pistol" };
        var candidateMeshes = new System.Collections.Generic.List<MeshInstance3D>();
        FindMeshesMatching(heroNode, weaponKeywords, candidateMeshes);

        if (candidateMeshes.Count == 0) return;

        string[] mountBoneCandidates = new[]
        {
            "weapon_hand_R", "weapon_hand_r", "hold_R", "hold_r", "hand_R", "hand_r",
            "weapon_hand_L", "weapon_hand_l", "hold_L", "hold_l", "hand_L", "hand_l"
        };

        int mountBoneIdx = -1;
        string mountBoneName = string.Empty;

        foreach (var name in mountBoneCandidates)
        {
            int idx = skeleton.FindBone(name);
            if (idx != -1)
            {
                mountBoneIdx = idx;
                mountBoneName = skeleton.GetBoneName(idx);
                break;
            }
        }

        if (mountBoneIdx == -1)
        {
            int total = skeleton.GetBoneCount();
            foreach (var name in mountBoneCandidates)
            {
                for (int i = 0; i < total; i++)
                {
                    if (string.Equals(skeleton.GetBoneName(i), name, StringComparison.OrdinalIgnoreCase))
                    {
                        mountBoneIdx = i;
                        mountBoneName = skeleton.GetBoneName(i);
                        break;
                    }
                }
                if (mountBoneIdx != -1) break;
            }
        }

        if (mountBoneIdx == -1 || string.IsNullOrEmpty(mountBoneName)) return;

        BoneAttachment3D attachment = null;
        foreach (Node child in skeleton.GetChildren())
        {
            if (child is BoneAttachment3D ba && ba.BoneName == mountBoneName)
            {
                attachment = ba;
                break;
            }
        }

        if (attachment == null)
        {
            attachment = new BoneAttachment3D
            {
                Name = $"WeaponAttachment_{mountBoneName}",
                BoneName = mountBoneName
            };
            skeleton.AddChild(attachment);
        }

        foreach (var mesh in candidateMeshes)
        {
            if (mesh.Skin != null) continue;
            if (mesh.GetParent() == attachment) continue;

            Transform3D globalXform = mesh.GlobalTransform;
            mesh.GetParent()?.RemoveChild(mesh);
            attachment.AddChild(mesh);
            mesh.GlobalTransform = globalXform;
            GD.Print($"[StudioUI] Attached unskinned weapon mesh '{mesh.Name}' to bone '{mountBoneName}'");
        }
    }

    private void FindMeshesMatching(Node node, string[] keywords, System.Collections.Generic.List<MeshInstance3D> results)
    {
        if (node is MeshInstance3D mi)
        {
            string name = mi.Name.ToString().ToLowerInvariant();
            foreach (var kw in keywords)
            {
                if (name.Contains(kw))
                {
                    results.Add(mi);
                    break;
                }
            }
        }

        foreach (Node child in node.GetChildren())
        {
            FindMeshesMatching(child, keywords, results);
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
        if (_loadingOverlay != null) _loadingOverlay.Visible = show;
        if (_loadingLabel != null && message != null) _loadingLabel.Text = message;
        UpdateModalsState();
    }

    public void UpdateModalsState()
    {
        bool anyModalOpen = (_settingsModal != null && _settingsModal.Visible)
                         || (_feedbackModal != null && _feedbackModal.Visible)
                         || (_aboutModal != null && _aboutModal.Visible)
                         || (_loadingOverlay != null && _loadingOverlay.Visible)
                         || (_gameFolderDialog != null && _gameFolderDialog.Visible)
                         || (_saveFolderDialog != null && _saveFolderDialog.Visible)
                         || (_fallbackPathDialog != null && _fallbackPathDialog.Visible);

        if (_modalsLayer != null)
        {
            _modalsLayer.Visible = anyModalOpen;
        }

        if (_modalBackdrop != null)
        {
            _modalBackdrop.Visible = anyModalOpen;
        }
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
        _fallbackPathDialog.Confirmed += () =>
        {
            _gameFolderDialog?.PopupCentered();
            UpdateModalsState();
        };
        _fallbackPathDialog.Canceled += UpdateModalsState;

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
                UpdateModalsState();
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

    public void ApplyCurrentGraphicsSettings()
        {
            var worldVp = GetNodeOrNull<SubViewport>("MainHUD/VBoxContainer/MainSplit/ViewportArea/SubViewportContainer/WorldViewport")
                    ?? GetTree().Root.FindChild("WorldViewport", true, false) as SubViewport;
            var dirLight = GetTree().Root.FindChild("DirectionalLight3D", true, false) as DirectionalLight3D;
            
            var bgCanvas = GetNodeOrNull<Control>("MainHUD/VBoxContainer/MainSplit/ViewportArea/BGCanvas");
            var bgRect = bgCanvas?.GetNodeOrNull<ColorRect>("ColorRect")
                    ?? GetTree().Root.FindChild("ColorRect", true, false) as ColorRect;
            var bgTex = bgCanvas?.GetNodeOrNull<TextureRect>("BackgroundRect")
                    ?? GetTree().Root.FindChild("BackgroundRect", true, false) as TextureRect;

            var envNode = worldVp?.GetNodeOrNull<WorldEnvironment>("WorldEnvironment")
                    ?? GetTree().Root.FindChild("WorldEnvironment", true, false) as WorldEnvironment;
            var stagePlatform = worldVp?.GetNodeOrNull<Node3D>("StagePlatform")
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
