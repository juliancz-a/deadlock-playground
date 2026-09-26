using Godot;
using System;
using System.IO;
using System.Collections.Generic;
using System.Threading.Tasks;
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
    [Export] private Label _loadingLogLabel;
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
    private readonly List<string> _loadingLogHistory = new();
    private Label _navLabel;

    // Update Notification Banner
    private PanelContainer _updateNotificationPanel;
    private Label _updateNotificationTitle;
    private Label _updateNotificationDetails;
    private ProgressBar _updateProgressBar;
    private Button _btnUpdateNow;
    private Button _btnUpdateAction;
    private Button _btnUpdateDismiss;
    private string _currentReleaseUrl = "";
    private string _currentDirectZipUrl = "";
    private string _currentRemoteVersion = "";
    private bool _isDownloadingUpdate = false;
    private bool _isUpdatePrepared = false;

    public static StudioUIManager Instance { get; private set; }
    public string SavePath => _pathManager?.CurrentSavePath ?? OS.GetSystemDir(OS.SystemDir.Pictures);

    private Control[] _tabScenes;
    private Button[] _tabButtons;
    private int _currentTabIndex = 0;

    public override void _Ready()
    {
        Instance = this;
        GetViewport().TransparentBg = false;

        var win = GetWindow();
        if (win != null)
        {
            win.MinSize = new Vector2I(1280, 720);
            DisplayServer.WindowSetMaxSize(Vector2I.Zero);
            var mode = DisplayServer.WindowGetMode();
            _isFullscreen = mode == DisplayServer.WindowMode.Fullscreen ||
                            mode == DisplayServer.WindowMode.ExclusiveFullscreen ||
                            win.Mode == Window.ModeEnum.Fullscreen ||
                            win.Mode == Window.ModeEnum.ExclusiveFullscreen;
        }

        _pathManager = new GamePathManager();
        AddChild(_pathManager);

        // Restore persisted gizmo display settings (bone opacity, wireframe opacity, etc.)
        // before any tab or subsystem reads GizmoDisplaySettings values.
        GizmoDisplaySettings.LoadConfig();

        LinkNodes();
        CreateToastUI();

        // Check if application was restarted after an update swap
        if (UpdateChecker.CleanupPostUpdate() || UpdateChecker.IsPostUpdateLaunch)
        {
            ShowToast("Deadlock Playground updated successfully!");
        }

        CreateUpdateNotificationUI();
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

        // Non-blocking update check on application startup
        _ = CheckUpdatesOnStartupAsync();
    }

    private SubViewport _worldViewport;
    private bool _isFullscreen = false;

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

        // True Fullscreen is either exclusive fullscreen or borderless fullscreen covering the entire screen.
        // Any windowed state (including Maximized with titlebar) is windowed and can toggle straight to Fullscreen.
        bool isFs = win.Mode == Window.ModeEnum.Fullscreen ||
                    win.Mode == Window.ModeEnum.ExclusiveFullscreen ||
                    currentDsMode == DisplayServer.WindowMode.Fullscreen ||
                    currentDsMode == DisplayServer.WindowMode.ExclusiveFullscreen ||
                    (win.Borderless && winSize.X >= screenSize.X && winSize.Y >= screenSize.Y);

        if (isFs)
        {
            // Switch to Windowed mode
            _isFullscreen = false;

            // 1. Unclamp MaxSize so users on 1440p, 4K, and UltraWide monitors can freely maximize or resize
            DisplayServer.WindowSetMaxSize(Vector2I.Zero);
            DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed);
            win.Mode = Window.ModeEnum.Windowed;
            win.Borderless = false;
            DisplayServer.WindowSetFlag(DisplayServer.WindowFlags.Borderless, false);

            // 2. Query active monitor usable rect and dynamically compute responsive windowed size
            Rect2I usableRect = DisplayServer.ScreenGetUsableRect(screen);
            Vector2I targetSize;

            if (usableRect.Size.X < 1280 || usableRect.Size.Y < 720)
            {
                // If the monitor usable area is smaller than 1280x720 (e.g. netbooks or sub-720p screens), clamp to usableRect - (40, 40)
                targetSize = new Vector2I(
                    Math.Max(640, usableRect.Size.X - 40),
                    Math.Max(480, usableRect.Size.Y - 40)
                );
                win.MinSize = targetSize;
            }
            else
            {
                // Target: ~80% - 85% of usableRect size, clamped to minimum 1280x720
                int targetW = Mathf.Clamp((int)(usableRect.Size.X * 0.85f), 1280, usableRect.Size.X);
                int targetH = Mathf.Clamp((int)(usableRect.Size.Y * 0.85f), 720, usableRect.Size.Y);
                targetSize = new Vector2I(targetW, targetH);
                win.MinSize = new Vector2I(1280, 720);
            }

            DisplayServer.WindowSetSize(targetSize);
            win.Size = targetSize;

            // 3. Center the window dynamically within the active monitor's usable rect
            Vector2I centerPos = usableRect.Position + (usableRect.Size - targetSize) / 2;
            DisplayServer.WindowSetPosition(centerPos);
            win.Position = centerPos;

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

    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if (@event is not InputEventKey key || !key.Pressed || key.Echo) return;

        // Never intercept shortcuts while the user is typing into text inputs
        var focusOwner = GetViewport()?.GuiGetFocusOwner();
        if (focusOwner is LineEdit or TextEdit) return;

        // Block shortcuts when modal dialogs are open
        if ((_settingsModal != null && _settingsModal.Visible) ||
            (_feedbackModal != null && _feedbackModal.Visible) ||
            (_aboutModal != null && _aboutModal.Visible) ||
            (_loadingOverlay != null && _loadingOverlay.Visible))
        {
            return;
        }

        if (key.Keycode == Key.F11)
        {
            ToggleFullscreen();
            GetViewport().SetInputAsHandled();
            return;
        }

        // Camera Actions
        if (@event.IsActionPressed("camera_reset", exactMatch: true))
        {
            _tabCamera?.ResetCameraTransform();
            GetViewport().SetInputAsHandled();
            return;
        }

        if (@event.IsActionPressed("camera_focus", exactMatch: true))
        {
            _tabCamera?.RecenterOnModel();
            GetViewport().SetInputAsHandled();
            return;
        }

        if (@event.IsActionPressed("camera_freecam", exactMatch: true))
        {
            _tabCamera?.ToggleProjection();
            GetViewport().SetInputAsHandled();
            return;
        }

        // Bones Actions
        if (@event.IsActionPressed("view_xray", exactMatch: true))
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

        if (@event.IsActionPressed("bones_ik_toggle", exactMatch: true))
        {
            _tabBones?.ToggleIK();
            GetViewport().SetInputAsHandled();
            return;
        }

        if (@event.IsActionPressed("bones_reset_pose", exactMatch: true))
        {
            _tabPose?.ResetAllPoses();
            GetViewport().SetInputAsHandled();
            return;
        }

        // Paint Actions
        if (@event.IsActionPressed("paint_uv_toggle", exactMatch: true))
        {
            if (_tabPaint != null && _tabPaint.Visible && _uvCanvasPanel != null)
            {
                _uvCanvasPanel.ToggleVisibility();
                GetViewport().SetInputAsHandled();
                return;
            }
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
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

        _navLabel ??= _navBadge?.FindChild("NavLabel", true, false) as Label;
        if (_navLabel != null)
        {
            _navLabel.Text = KeybindsManager.GetNavigationCheatsheet();
        }

        _painterKeymapBadge ??= GetNodeOrNull<Control>("MainHUD/VBoxContainer/MainSplit/ViewportArea/BottomBadgesContainer/PainterKeymapBadge")
                             ?? GetTree().Root.FindChild("PainterKeymapBadge", true, false) as Control;
        _painterKeymapLabel ??= _painterKeymapBadge?.FindChild("PainterKeymapLabel", true, false) as Label;
        if (_painterKeymapLabel != null)
        {
            _painterKeymapLabel.Text = KeybindsManager.GetFullPainterCheatsheet();
        }

        KeybindsManager.OnKeybindsChanged += UpdateKeybindsUI;

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
        _loadingLabel ??= GetNodeOrNull<Label>("MainHUD/ModalsLayer/LoadingOverlay/VBox/Label")
                       ?? GetNodeOrNull<Label>("MainHUD/ModalsLayer/LoadingOverlay/Label");
        _loadingLogLabel ??= GetNodeOrNull<Label>("MainHUD/ModalsLayer/LoadingOverlay/VBox/LogLabel")
                          ?? GetNodeOrNull<Label>("MainHUD/ModalsLayer/LoadingOverlay/LogLabel");

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

            // Disable IK when entering paint mode
            _currentIKManager?.SetMasterIKEnabled(false);
            _tabBones?.DisableMasterIK();

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
            loader.LoadProgress += (msg) =>
            {
                if (string.IsNullOrWhiteSpace(msg)) return;
                _loadingLogHistory.Add(msg);
                if (_loadingLogHistory.Count > 4)
                {
                    _loadingLogHistory.RemoveAt(0);
                }
                if (_loadingLogLabel != null)
                {
                    _loadingLogLabel.Text = string.Join("\n", _loadingLogHistory);
                }
            };
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
            if (_currentTabIndex == 8)
            {
                _currentIKManager.SetMasterIKEnabled(false);
                _tabBones?.DisableMasterIK();
            }

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
        _tabPaint?.LayerManager?.ClearHistory();
        _tabPaint?.Painter?.ResetSession();
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
        if (show)
        {
            _loadingLogHistory.Clear();
            if (_loadingLogLabel != null) _loadingLogLabel.Text = "Initializing load...";
        }
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

    #region Update Notification System
    private void CreateUpdateNotificationUI()
    {
        _updateNotificationPanel = new PanelContainer();
        _updateNotificationPanel.Visible = false;
        _updateNotificationPanel.CustomMinimumSize = new Vector2(460, 0);

        var bgStyle = new StyleBoxFlat
        {
            BgColor = new Color(0.12f, 0.16f, 0.15f, 0.96f),
            BorderColor = PlaygroundThemeHelper.SageGreen,
            BorderWidthLeft = 2,
            BorderWidthTop = 2,
            BorderWidthRight = 2,
            BorderWidthBottom = 2,
            CornerRadiusTopLeft = 8,
            CornerRadiusTopRight = 8,
            CornerRadiusBottomLeft = 8,
            CornerRadiusBottomRight = 8,
            ShadowColor = new Color(0, 0, 0, 0.45f),
            ShadowSize = 8,
            ShadowOffset = new Vector2(0, 4)
        };
        _updateNotificationPanel.AddThemeStyleboxOverride("panel", bgStyle);

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 16);
        margin.AddThemeConstantOverride("margin_top", 12);
        margin.AddThemeConstantOverride("margin_right", 16);
        margin.AddThemeConstantOverride("margin_bottom", 12);

        var hbox = new HBoxContainer();
        hbox.AddThemeConstantOverride("separation", 14);

        // GitHub / Update Icon
        var iconRect = new TextureRect
        {
            CustomMinimumSize = new Vector2(28, 28),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter
        };
        if (ResourceLoader.Exists("res://assets/icons/github.svg"))
        {
            iconRect.Texture = GD.Load<Texture2D>("res://assets/icons/github.svg");
            iconRect.Modulate = PlaygroundThemeHelper.SageGreen;
        }

        // Labels & Progress Bar
        var vboxText = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            Alignment = BoxContainer.AlignmentMode.Center
        };
        vboxText.AddThemeConstantOverride("separation", 2);

        _updateNotificationTitle = new Label
        {
            Text = "Update Available"
        };
        PlaygroundThemeHelper.MakeHeaderLabel(_updateNotificationTitle);
        _updateNotificationTitle.AddThemeFontSizeOverride("font_size", 14);

        _updateNotificationDetails = new Label
        {
            Text = "New version available"
        };
        PlaygroundThemeHelper.MakeMutedLabel(_updateNotificationDetails);
        _updateNotificationDetails.AddThemeFontSizeOverride("font_size", 12);

        _updateProgressBar = new ProgressBar
        {
            MinValue = 0,
            MaxValue = 100,
            Value = 0,
            ShowPercentage = false,
            CustomMinimumSize = new Vector2(0, 6),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            Visible = false
        };
        var bgTrack = new StyleBoxFlat
        {
            BgColor = new Color(0.1f, 0.14f, 0.13f, 0.8f),
            CornerRadiusTopLeft = 3,
            CornerRadiusTopRight = 3,
            CornerRadiusBottomLeft = 3,
            CornerRadiusBottomRight = 3
        };
        var fillTrack = new StyleBoxFlat
        {
            BgColor = PlaygroundThemeHelper.SageGreen,
            CornerRadiusTopLeft = 3,
            CornerRadiusTopRight = 3,
            CornerRadiusBottomLeft = 3,
            CornerRadiusBottomRight = 3
        };
        _updateProgressBar.AddThemeStyleboxOverride("background", bgTrack);
        _updateProgressBar.AddThemeStyleboxOverride("fill", fillTrack);

        vboxText.AddChild(_updateNotificationTitle);
        vboxText.AddChild(_updateNotificationDetails);
        vboxText.AddChild(_updateProgressBar);

        // Action & Dismiss buttons
        var hboxBtns = new HBoxContainer
        {
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter
        };
        hboxBtns.AddThemeConstantOverride("separation", 8);

        _btnUpdateNow = new Button
        {
            Text = "Update Now",
            CustomMinimumSize = new Vector2(100, 30)
        };
        PlaygroundThemeHelper.MakeAccentButton(_btnUpdateNow);
        _btnUpdateNow.Pressed += OnUpdateNowPressed;

        _btnUpdateAction = new Button
        {
            Text = "View Release",
            CustomMinimumSize = new Vector2(95, 30)
        };
        PlaygroundThemeHelper.MakeSecondaryButton(_btnUpdateAction);
        _btnUpdateAction.Pressed += OnUpdateActionPressed;

        _btnUpdateDismiss = new Button
        {
            Text = "✕",
            TooltipText = "Dismiss",
            CustomMinimumSize = new Vector2(30, 30)
        };
        PlaygroundThemeHelper.MakeSecondaryButton(_btnUpdateDismiss);
        _btnUpdateDismiss.Pressed += OnUpdateDismissPressed;

        hboxBtns.AddChild(_btnUpdateNow);
        hboxBtns.AddChild(_btnUpdateAction);
        hboxBtns.AddChild(_btnUpdateDismiss);

        hbox.AddChild(iconRect);
        hbox.AddChild(vboxText);
        hbox.AddChild(hboxBtns);

        margin.AddChild(hbox);
        _updateNotificationPanel.AddChild(margin);

        _updateNotificationPanel.SetAnchorsPreset(Control.LayoutPreset.TopRight);
        _updateNotificationPanel.GrowHorizontal = Control.GrowDirection.Begin;
        _updateNotificationPanel.GrowVertical = Control.GrowDirection.End;
        _updateNotificationPanel.OffsetTop = 46;
        _updateNotificationPanel.OffsetRight = -20;

        var hud = GetNodeOrNull("MainHUD") ?? this;
        hud.AddChild(_updateNotificationPanel);
    }

    private async void OnUpdateNowPressed()
    {
        if (OS.HasFeature("editor"))
        {
            ShowToast("In-app auto-update is disabled in Editor mode. Open GitHub release page instead.");
            if (!string.IsNullOrEmpty(_currentReleaseUrl))
            {
                OS.ShellOpen(_currentReleaseUrl);
            }
            return;
        }

        if (_isUpdatePrepared)
        {
            // Update archive already extracted and staged; execute atomic swap & relaunch
            UpdateChecker.ApplyUpdateAndRestart();
            return;
        }

        if (_isDownloadingUpdate) return;

        if (string.IsNullOrEmpty(_currentDirectZipUrl))
        {
            if (!string.IsNullOrEmpty(_currentReleaseUrl))
            {
                OS.ShellOpen(_currentReleaseUrl);
            }
            return;
        }

        _isDownloadingUpdate = true;
        if (_btnUpdateNow != null) _btnUpdateNow.Disabled = true;
        if (_btnUpdateAction != null) _btnUpdateAction.Disabled = true;
        if (_btnUpdateDismiss != null) _btnUpdateDismiss.Disabled = true;

        if (_updateNotificationTitle != null) _updateNotificationTitle.Text = "Downloading Update...";
        if (_updateNotificationDetails != null) _updateNotificationDetails.Text = "Connecting...";
        if (_updateProgressBar != null)
        {
            _updateProgressBar.Visible = true;
            _updateProgressBar.Value = 0;
        }

        bool success = await Task.Run(async () =>
        {
            return await UpdateChecker.DownloadAndPrepareUpdateAsync(
                _currentDirectZipUrl,
                onProgress: (progress, status) =>
                {
                    Callable.From(() =>
                    {
                        if (_updateNotificationDetails != null)
                        {
                            _updateNotificationDetails.Text = status;
                        }
                        if (_updateProgressBar != null && progress >= 0f)
                        {
                            _updateProgressBar.Value = Math.Clamp(progress * 100f, 0f, 100f);
                        }
                    }).CallDeferred();
                }
            ).ConfigureAwait(false);
        }).ConfigureAwait(true);

        _isDownloadingUpdate = false;

        if (success)
        {
            _isUpdatePrepared = true;
            if (_updateNotificationTitle != null) _updateNotificationTitle.Text = "Update Ready";
            if (_updateNotificationDetails != null) _updateNotificationDetails.Text = "Restart to apply update.";
            if (_updateProgressBar != null) _updateProgressBar.Visible = false;

            if (_btnUpdateNow != null)
            {
                _btnUpdateNow.Text = "Restart & Apply";
                _btnUpdateNow.Disabled = false;
            }
            if (_btnUpdateDismiss != null) _btnUpdateDismiss.Disabled = false;
        }
        else
        {
            // Restore UI state
            _isUpdatePrepared = false;
            string formattedVersion = _currentRemoteVersion.StartsWith("v", StringComparison.OrdinalIgnoreCase)
                ? _currentRemoteVersion
                : $"v{_currentRemoteVersion}";

            if (_updateNotificationTitle != null) _updateNotificationTitle.Text = "Update Available";
            if (_updateNotificationDetails != null) _updateNotificationDetails.Text = $"New version available: {formattedVersion}";
            if (_updateProgressBar != null) _updateProgressBar.Visible = false;

            if (_btnUpdateNow != null)
            {
                _btnUpdateNow.Text = "Update Now";
                _btnUpdateNow.Disabled = false;
            }
            if (_btnUpdateAction != null) _btnUpdateAction.Disabled = false;
            if (_btnUpdateDismiss != null) _btnUpdateDismiss.Disabled = false;

            ShowToast("Update failed to download. Please download manually from GitHub.");
        }
    }

    private void OnUpdateActionPressed()
    {
        if (!string.IsNullOrEmpty(_currentReleaseUrl))
        {
            OS.ShellOpen(_currentReleaseUrl);
        }
        HideUpdateNotification();
    }

    private void OnUpdateDismissPressed()
    {
        HideUpdateNotification();
    }

    public void ShowUpdateNotification(string latestVersion, string releaseUrl, string directZipUrl = "")
    {
        if (_updateNotificationPanel == null) return;

        _currentRemoteVersion = latestVersion;
        _currentReleaseUrl = releaseUrl;
        _currentDirectZipUrl = directZipUrl;
        _isDownloadingUpdate = false;
        _isUpdatePrepared = false;

        string formattedVersion = latestVersion.StartsWith("v", StringComparison.OrdinalIgnoreCase)
            ? latestVersion
            : $"v{latestVersion}";

        if (_updateNotificationTitle != null) _updateNotificationTitle.Text = "Update Available";
        if (_updateNotificationDetails != null) _updateNotificationDetails.Text = $"New version available: {formattedVersion}";
        if (_updateProgressBar != null)
        {
            _updateProgressBar.Visible = false;
            _updateProgressBar.Value = 0;
        }

        if (_btnUpdateNow != null)
        {
            _btnUpdateNow.Text = "Update Now";
            _btnUpdateNow.Disabled = false;
            _btnUpdateNow.Visible = !string.IsNullOrEmpty(directZipUrl);
        }

        if (_btnUpdateAction != null)
        {
            _btnUpdateAction.Disabled = false;
            _btnUpdateAction.Visible = true;
            if (string.IsNullOrEmpty(directZipUrl))
            {
                PlaygroundThemeHelper.MakeAccentButton(_btnUpdateAction);
            }
            else
            {
                PlaygroundThemeHelper.MakeSecondaryButton(_btnUpdateAction);
            }
        }

        if (_btnUpdateDismiss != null)
        {
            _btnUpdateDismiss.Disabled = false;
        }

        _updateNotificationPanel.Modulate = new Color(1, 1, 1, 0.0f);
        _updateNotificationPanel.Visible = true;

        var tween = CreateTween();
        tween.TweenProperty(_updateNotificationPanel, "modulate:a", 1.0f, 0.35f);
    }

    public void HideUpdateNotification()
    {
        if (_updateNotificationPanel == null || !_updateNotificationPanel.Visible) return;
        if (_isDownloadingUpdate) return;

        var tween = CreateTween();
        tween.TweenProperty(_updateNotificationPanel, "modulate:a", 0.0f, 0.2f);
        tween.TweenCallback(Callable.From(() =>
        {
            _updateNotificationPanel.Visible = false;
            _updateNotificationPanel.Modulate = new Color(1, 1, 1, 1.0f);
        }));
    }

    private async Task CheckUpdatesOnStartupAsync()
    {
        try
        {
            var result = await UpdateChecker.CheckForUpdatesAsync().ConfigureAwait(false);
            if (result.IsUpdateAvailable && (!string.IsNullOrEmpty(result.ReleaseUrl) || !string.IsNullOrEmpty(result.DirectZipUrl)))
            {
                Callable.From(() =>
                {
                    ShowUpdateNotification(result.RemoteVersionTag, result.ReleaseUrl, result.DirectZipUrl);
                }).CallDeferred();
            }
        }
        catch (Exception ex)
        {
            GD.Print($"[StudioUIManager] Startup update check failed: {ex.Message}");
        }
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

    private void UpdateKeybindsUI()
    {
        if (_painterKeymapLabel != null)
        {
            _painterKeymapLabel.Text = KeybindsManager.GetFullPainterCheatsheet();
        }
        if (_navLabel != null)
        {
            _navLabel.Text = KeybindsManager.GetNavigationCheatsheet();
        }
    }
    #endregion
}
