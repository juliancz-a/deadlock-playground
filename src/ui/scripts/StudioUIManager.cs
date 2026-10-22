using Godot;
using System;
using System.IO;
using System.Collections.Generic;
using System.Threading.Tasks;
using DeadlockPlayground.UI;
using DeadlockPlayground.Tools;
using DeadlockPlayground.Workspace;
using DeadlockPlayground.Core;
using DeadlockPlayground.Painter;

public partial class StudioUIManager : CanvasLayer
{
    [ExportCategory("Top Bar Controls")]
    [Export] private Button _btnSettings;
    [Export] private Button _btnFeedback;
    [Export] private Button _btnAbout;
    [Export] private Button _btnQuit;
    [Export] private Button _btnNews;
    [Export] private MenuButton _btnAdjustments;
    [Export] private MenuButton _btnFileMenu;

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
    [Export] private PlayerTabUI _tabPlayer;
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
    [Export] private QuickColorPaletteUI _quickColorPalette;
    private Label _painterKeymapLabel;
    private bool _uvCanvasUserWantsOpen = true;

    private SkeletonGizmoManager _currentSkeletonGizmoManager;

    [ExportCategory("Modals & Dialogs")]
    [Export] private Control _modalsLayer;
    [Export] private ColorRect _modalBackdrop;
    [Export] private SettingsDialog _settingsModal;
    [Export] private FeedbackDialog _feedbackModal;
    [Export] private PanelContainer _aboutModal;
    [Export] private ChangelogModalUI _changelogModal;
    [Export] private PanelContainer _loadingOverlay;
    [Export] private Label _loadingLabel;
    [Export] private Label _loadingLogLabel;
    [Export] private Button _btnAboutClose;
    [Export] private FileDialog _gameFolderDialog;
    [Export] private FileDialog _saveFolderDialog;
    [Export] private ColorAdjustmentModalUI _colorAdjustmentModal;
    [Export] private ExportBaseTexturesModalUI _exportBaseTexturesModal;

    private Control _unreadBadge;
    private CharacterIKManager _currentIKManager;
    private bool _isParticleActive = false;
    private Button _btnModChanges;
    private ModChangesModalUI _modChangesModal;
    private PanelContainer _unsavedChangesModal;
    private FileDialog _unsavedSaveDialog;

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
        StartupDiagnosticTracker.Mark("MainUI: _Ready begin");
        Instance = this;
        GetTree().AutoAcceptQuit = false;
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
        StartupDiagnosticTracker.Mark("MainUI: window configured");

        _pathManager = new GamePathManager();
        StartupDiagnosticTracker.Mark("MainUI: GamePathManager created");

        // Restore persisted gizmo display settings (bone opacity, wireframe opacity, etc.)
        // before any tab or subsystem reads GizmoDisplaySettings values.
        GizmoDisplaySettings.LoadConfig();

        LinkNodes();
        StartupDiagnosticTracker.Mark("MainUI: LinkNodes done (Topbar/Modals linked)");
        CreateToastUI();

        // Check if application was restarted after an update swap
        if (UpdateChecker.CleanupPostUpdate() || UpdateChecker.IsPostUpdateLaunch)
        {
            ShowToast("Deadlock Playground updated successfully!");
        }

        CreateUpdateNotificationUI();
        CreateFallbackDialog();
        StartupDiagnosticTracker.Mark("MainUI: Toast/Update/Fallback dialogs created");
        InitTabs();
        StartupDiagnosticTracker.Mark("MainUI: InitTabs done (Tabs/Painter initialized)");
        ConnectEvents();
        StartupDiagnosticTracker.Mark("MainUI: ConnectEvents done");
        InitializePaths();
        ApplyCurrentGraphicsSettings();
        ApplySubViewportOptimizations();
        StartupDiagnosticTracker.Mark("MainUI: graphics settings applied");

        UpdateCharacterDependencyState(false);

        // Switch to default Character tab
        SwitchTab(0);
        StartupDiagnosticTracker.Mark("MainUI: default tab selected");

        UpdateNewsBadgeState();

        DeadlockPlayground.Particles.ParticleStage3D.OnParticleActiveStateChanged += UpdateParticleState;
        ModWorkspaceSession.Instance.OnWorkspaceChanged += UpdateModChangesBadge;
        UpdateModChangesBadge();

        PlaygroundThemeHelper.AutoDecorate(this);
        StartupDiagnosticTracker.Mark("MainUI: theme decoration done");

        // Non-blocking update check on application startup
        StartupDiagnosticTracker.Mark("MainUI: starting background update check");
        _ = CheckUpdatesOnStartupAsync();
        StartupDiagnosticTracker.Mark("MainUI: _Ready complete");
    }

    public override void _Notification(int what)
    {
        base._Notification(what);
        if (what == NotificationWMCloseRequest)
        {
            HandleQuitRequested();
        }
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
        if (ColorAdjustmentModalUI.IsOpen || ExportBaseTexturesModalUI.IsOpen) return;

        if ((_settingsModal != null && _settingsModal.Visible) ||
            (_feedbackModal != null && _feedbackModal.Visible) ||
            (_aboutModal != null && _aboutModal.Visible) ||
            (_changelogModal != null && _changelogModal.Visible) ||
            (_loadingOverlay != null && _loadingOverlay.Visible))
        {
            if (key.Keycode == Key.Escape)
            {
                if (_changelogModal != null && _changelogModal.Visible)
                {
                    _changelogModal.Close();
                    UpdateModalsState();
                    UpdateNewsBadgeState();
                    GetViewport().SetInputAsHandled();
                }
            }
            return;
        }

        if (key.Keycode == Key.F11)
        {
            ToggleFullscreen();
            GetViewport().SetInputAsHandled();
            return;
        }

        // Color Adjustments shortcuts (Only active in Paint Tab)
        if (_currentTabIndex == 8 && _tabPaint != null)
        {
            if (key.CtrlPressed && !key.ShiftPressed && !key.AltPressed && key.Keycode == Key.U)
            {
                HandleAdjustmentTriggered(AdjustmentType.HueSaturation);
                GetViewport()?.SetInputAsHandled();
                return;
            }
            if (key.CtrlPressed && !key.ShiftPressed && !key.AltPressed && key.Keycode == Key.B)
            {
                HandleAdjustmentTriggered(AdjustmentType.ColorBalance);
                GetViewport()?.SetInputAsHandled();
                return;
            }
            if (key.CtrlPressed && !key.ShiftPressed && !key.AltPressed && key.Keycode == Key.I)
            {
                HandleAdjustmentTriggered(AdjustmentType.Invert);
                GetViewport()?.SetInputAsHandled();
                return;
            }
            if (key.CtrlPressed && key.ShiftPressed && !key.AltPressed && key.Keycode == Key.U)
            {
                HandleAdjustmentTriggered(AdjustmentType.Desaturate);
                GetViewport()?.SetInputAsHandled();
                return;
            }

            // File Menu Shortcuts (Paint Tab)
            if (key.CtrlPressed && !key.ShiftPressed && !key.AltPressed && key.Keycode == Key.O)
            {
                _tabPaint?.OpenLoadProjectDialog();
                GetViewport()?.SetInputAsHandled();
                return;
            }
            if (key.CtrlPressed && !key.ShiftPressed && !key.AltPressed && key.Keycode == Key.S)
            {
                _tabPaint?.OpenSaveProjectDialog();
                GetViewport()?.SetInputAsHandled();
                return;
            }
            if (key.CtrlPressed && key.ShiftPressed && !key.AltPressed && key.Keycode == Key.I)
            {
                _tabPaint?.OpenImportImageDialog();
                GetViewport()?.SetInputAsHandled();
                return;
            }
            if (key.CtrlPressed && !key.ShiftPressed && !key.AltPressed && key.Keycode == Key.E)
            {
                _tabPaint?.OpenExportModDialog();
                GetViewport()?.SetInputAsHandled();
                return;
            }
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
            _tabPlayer?.ResetAllPoses();
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
        _btnNews ??= GetNodeOrNull<Button>("MainHUD/VBoxContainer/TopBar/HBoxContainer/NewsButton")
                  ?? GetNodeOrNull<Button>("MainHUD/TopBar/HBoxContainer/NewsButton")
                  ?? GetNodeOrNull<Button>("MainHUD/VBoxContainer/TopBar/HBoxContainer/ChangelogButton")
                  ?? GetNodeOrNull<Button>("MainHUD/VBoxContainer/MainSplit/LeftPanel/VBoxContainer/NewsButton")
                  ?? GetTree().Root.FindChild("NewsButton", true, false) as Button
                  ?? GetTree().Root.FindChild("BtnNews", true, false) as Button;

        _unreadBadge ??= _btnNews?.FindChild("UnreadBadge", true, false) as Control;

        var topBarHBox = GetNodeOrNull<HBoxContainer>("MainHUD/VBoxContainer/TopBar/HBoxContainer")
                      ?? GetNodeOrNull<HBoxContainer>("MainHUD/TopBar/HBoxContainer");
        if (topBarHBox != null && _btnModChanges == null)
        {
            _btnModChanges = new Button
            {
                Name = "ModChangesButton",
                Text = "MOD CHANGES",
                TooltipText = "View and manage staged mod changes across heroes, extra models, and VFX",
                CustomMinimumSize = new Vector2(110, 28),
                FocusMode = Control.FocusModeEnum.None
            };
            PlaygroundThemeHelper.MakeSecondaryButton(_btnModChanges);
            var colus = PlaygroundThemeHelper.GetFontColus();
            if (colus != null) _btnModChanges.AddThemeFontOverride("font", colus);
            _btnModChanges.AddThemeFontSizeOverride("font_size", 12);
            _btnModChanges.Pressed += OpenModChangesModal;

            if (_btnNews != null && _btnNews.GetParent() == topBarHBox)
            {
                topBarHBox.AddChild(_btnModChanges);
                topBarHBox.MoveChild(_btnModChanges, _btnNews.GetIndex());
            }
            else
            {
                topBarHBox.AddChild(_btnModChanges);
            }
        }

        _btnAdjustments ??= GetNodeOrNull<MenuButton>("MainHUD/VBoxContainer/TopBar/HBoxContainer/AdjustmentsMenuButton")
                         ?? GetNodeOrNull<MenuButton>("MainHUD/TopBar/HBoxContainer/AdjustmentsMenuButton")
                         ?? GetTree().Root.FindChild("AdjustmentsMenuButton", true, false) as MenuButton;

        if (_btnAdjustments != null)
        {
            _btnAdjustments.Visible = (_currentTabIndex == 8);
            var popup = _btnAdjustments.GetPopup();
            popup.Clear();
            popup.AddItem("Brightness / Contrast...", (int)AdjustmentType.BrightnessContrast);
            popup.AddItem("Hue / Saturation...", (int)AdjustmentType.HueSaturation);
            popup.SetItemAccelerator(1, (Key)((long)KeyModifierMask.MaskCtrl | (long)Key.U));
            popup.AddItem("Color Balance...", (int)AdjustmentType.ColorBalance);
            popup.SetItemAccelerator(2, (Key)((long)KeyModifierMask.MaskCtrl | (long)Key.B));
            popup.AddItem("Exposure...", (int)AdjustmentType.Exposure);
            popup.AddSeparator();
            popup.AddItem("Invert", (int)AdjustmentType.Invert);
            popup.SetItemAccelerator(5, (Key)((long)KeyModifierMask.MaskCtrl | (long)Key.I));
            popup.AddItem("Desaturate", (int)AdjustmentType.Desaturate);
            popup.SetItemAccelerator(6, (Key)((long)KeyModifierMask.MaskCtrl | (long)KeyModifierMask.MaskShift | (long)Key.U));

            popup.AboutToPopup += OnAdjustmentsMenuAboutToPopup;
            popup.IdPressed += (id) => HandleAdjustmentTriggered((AdjustmentType)id);
        }

        _btnFileMenu ??= GetNodeOrNull<MenuButton>("MainHUD/VBoxContainer/TopBar/HBoxContainer/FileMenuButton")
                      ?? GetNodeOrNull<MenuButton>("MainHUD/TopBar/HBoxContainer/FileMenuButton")
                      ?? GetTree().Root.FindChild("FileMenuButton", true, false) as MenuButton;

        if (_btnFileMenu != null)
        {
            _btnFileMenu.Visible = (_currentTabIndex == 8);
            var popup = _btnFileMenu.GetPopup();
            popup.Clear();
            popup.AddItem("New / Reset Workspace...", 0);
            popup.AddItem("Open Project (.dpp)...", 1);
            popup.SetItemAccelerator(popup.GetItemIndex(1), (Key)((long)KeyModifierMask.MaskCtrl | (long)Key.O));
            popup.AddItem("Save Project (.dpp)...", 2);
            popup.SetItemAccelerator(popup.GetItemIndex(2), (Key)((long)KeyModifierMask.MaskCtrl | (long)Key.S));
            popup.AddSeparator();
            popup.AddItem("Import Image as Layer (.png / .jpg)...", 3);
            popup.SetItemAccelerator(popup.GetItemIndex(3), (Key)((long)KeyModifierMask.MaskCtrl | (long)KeyModifierMask.MaskShift | (long)Key.I));
            popup.AddItem("Export Base Textures...", 4);
            popup.AddItem("Export Layers (Baked PNG) (.png)...", 5);
            popup.AddItem("Export Active Layer Only (.png)...", 6);
            popup.AddSeparator();
            popup.AddItem("Export Mod (.vpk)...", 7);
            popup.SetItemAccelerator(popup.GetItemIndex(7), (Key)((long)KeyModifierMask.MaskCtrl | (long)Key.E));
            popup.AddItem("Open Deadlock Addons Folder", 8);

            popup.IdPressed += OnFileMenuItemPressed;
        }

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
        _tabPlayer ??= _tabContentContainer?.GetNodeOrNull<PlayerTabUI>("PoseTab");
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

        _quickColorPalette ??= GetNodeOrNull<QuickColorPaletteUI>("MainHUD/VBoxContainer/MainSplit/ViewportArea/QuickColorPalette")
                            ?? GetTree().Root.FindChild("QuickColorPalette", true, false) as QuickColorPaletteUI;
        if (_quickColorPalette == null)
        {
            var viewportArea = GetNodeOrNull<Control>("MainHUD/VBoxContainer/MainSplit/ViewportArea")
                            ?? GetTree().Root.FindChild("ViewportArea", true, false) as Control;
            if (viewportArea != null)
            {
                var scene = GD.Load<PackedScene>("res://ui/scenes/components/QuickColorPalette.tscn");
                if (scene != null)
                {
                    _quickColorPalette = scene.Instantiate<QuickColorPaletteUI>();
                    viewportArea.AddChild(_quickColorPalette);
                }
            }
        }
        if (_quickColorPalette != null)
        {
            _quickColorPalette.Visible = false;
            _quickColorPalette.Setup(_tabPaint?.Painter, _tabPaint?.BrushPalette, _btnOpenUVCanvas);
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
                _quickColorPalette?.UpdateBarPosition();
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

        _changelogModal ??= GetNodeOrNull<ChangelogModalUI>("MainHUD/ModalsLayer/ChangelogModal")
                         ?? GetTree().Root.FindChild("ChangelogModal", true, false) as ChangelogModalUI;

        if (_changelogModal == null && _modalsLayer != null)
        {
            var modalScene = GD.Load<PackedScene>("res://ui/scenes/modals/ChangelogModal.tscn");
            if (modalScene != null)
            {
                _changelogModal = modalScene.Instantiate<ChangelogModalUI>();
                _modalsLayer.AddChild(_changelogModal);
            }
        }

        _colorAdjustmentModal ??= GetNodeOrNull<ColorAdjustmentModalUI>("MainHUD/ModalsLayer/ColorAdjustmentModal")
                               ?? GetTree().Root.FindChild("ColorAdjustmentModal", true, false) as ColorAdjustmentModalUI;

        if (_colorAdjustmentModal == null && _modalsLayer != null)
        {
            var modalScene = GD.Load<PackedScene>("res://ui/scenes/modals/ColorAdjustmentModal.tscn");
            if (modalScene != null)
            {
                _colorAdjustmentModal = modalScene.Instantiate<ColorAdjustmentModalUI>();
                _modalsLayer.AddChild(_colorAdjustmentModal);
            }
        }

        _exportBaseTexturesModal ??= GetNodeOrNull<ExportBaseTexturesModalUI>("MainHUD/ModalsLayer/ExportBaseTexturesModal")
                                  ?? GetTree().Root.FindChild("ExportBaseTexturesModal", true, false) as ExportBaseTexturesModalUI;

        if (_exportBaseTexturesModal == null && _modalsLayer != null)
        {
            var modalScene = GD.Load<PackedScene>("res://ui/scenes/modals/ExportBaseTexturesModal.tscn");
            if (modalScene != null)
            {
                _exportBaseTexturesModal = modalScene.Instantiate<ExportBaseTexturesModalUI>();
                _modalsLayer.AddChild(_exportBaseTexturesModal);
            }
        }

        _modChangesModal ??= _modalsLayer?.FindChild("ModChangesModal", true, false) as ModChangesModalUI;
        if (_modChangesModal == null && _modalsLayer != null)
        {
            _modChangesModal = new ModChangesModalUI();
            _modalsLayer.AddChild(_modChangesModal);
        }
        if (_modChangesModal != null)
        {
            _modChangesModal.ModalClosed += UpdateModalsState;
            _modChangesModal.OnExportModRequested += () =>
            {
                _modChangesModal.CloseModal();
                UpdateModalsState();
                SwitchTab(8);
                _tabPaint?.OpenExportModDialog();
            };
            _modChangesModal.OnExportHeroModRequested += (heroId, heroDisplayName) =>
            {
                _modChangesModal.CloseModal();
                UpdateModalsState();
                SwitchTab(8);
                _tabPaint?.OpenExportModDialog(heroId, heroDisplayName);
            };
                _modChangesModal.OnLoadModelRequested += async (heroId, vpkPath) =>
            {
                _modChangesModal.CloseModal();
                UpdateModalsState();
                var ldr = GetVpkLoader();
                if (ldr == null) return;

                // 1. Si hay una partícula activa, cerrarla primero
                var stage = GetTree()?.Root?.FindChild("ParticleStage3D", true, false) as DeadlockPlayground.Particles.ParticleStage3D;
                if (stage != null && stage.HasActiveParticle)
                {
                    stage.UnloadActiveParticle();
                }

                // Determinar si el asset pedido está registrado como ExtraModel en el Workspace o si difiere del VMDL base del catálogo
                var heroCatalogEntry = DeadlockPlayground.Catalog.DeadlockHeroCatalog.GetByCodename(heroId)
                                    ?? DeadlockPlayground.Catalog.DeadlockHeroCatalog.ResolveHero(heroId);
                string baseVmdl = heroCatalogEntry?.VmdlRelativePath?.Replace('\\', '/')?.TrimStart('/') ?? "";
                string reqVmdl = (vpkPath ?? "").Replace('\\', '/').TrimStart('/');

                bool isExtraRequest = false;
                if (ModWorkspaceSession.Instance.Manifest.Heroes.TryGetValue(heroId, out var wsHero))
                {
                    var wsAsset = wsHero.Assets.Find(a => string.Equals(a.VpkPath, vpkPath, StringComparison.OrdinalIgnoreCase));
                    if (wsAsset != null)
                    {
                        isExtraRequest = string.Equals(wsAsset.Category, "ExtraModel", StringComparison.OrdinalIgnoreCase);
                    }
                }
                if (!isExtraRequest && !string.IsNullOrEmpty(baseVmdl) && !string.IsNullOrEmpty(reqVmdl))
                {
                    string baseNoC = baseVmdl.EndsWith("_c", StringComparison.OrdinalIgnoreCase) ? baseVmdl[..^2] : baseVmdl;
                    string reqNoC = reqVmdl.EndsWith("_c", StringComparison.OrdinalIgnoreCase) ? reqVmdl[..^2] : reqVmdl;
                    if (!string.Equals(baseNoC, reqNoC, StringComparison.OrdinalIgnoreCase))
                    {
                        isExtraRequest = true;
                    }
                }

                string currentLoadedHero = ldr.LastLoadedHeroEntry?.InternalCodename ?? ldr.LastLoadedHeroKey ?? "";

                // 2. Si pide el Character Model base y ese mismo héroe YA está cargado en escena, no recargar!
                if (!isExtraRequest && !ldr.IsExtraModelLoaded && ldr.CurrentHeroNode != null &&
                    string.Equals(currentLoadedHero, heroId, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                // 3. Si pide un Extra Model y ese mismo Extra Model YA está cargado en escena, no recargar!
                if (isExtraRequest && ldr.IsExtraModelLoaded && ldr.CurrentHeroNode != null &&
                    string.Equals(ldr.LastLoadedVmdlPath?.Replace('\\', '/'), reqVmdl, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                if (isExtraRequest)
                {
                    await ldr.LoadExtraModelAsync(vpkPath, heroId, System.IO.Path.GetFileNameWithoutExtension(vpkPath));
                }
                else if (heroCatalogEntry != null)
                {
                    await ldr.LoadHeroModelAsync(heroCatalogEntry);
                }
                else
                {
                    await ldr.LoadHeroAsync(heroId, heroId);
                }
            };
            _modChangesModal.OnLoadParticleRequested += (heroId, vpcfPath) =>
            {
                _modChangesModal.CloseModal();
                UpdateModalsState();
                _tabCharacter?.LoadParticleDirectly(vpcfPath, ownerHeroId: heroId);
            };
        }
    }

    private void InitTabs()
    {
        _tabScenes = new Control[]
        {
            _tabCharacter,
            _tabBones,
            _tabCamera,
            _tabPlayer,
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

    public int CurrentTabIndex => _currentTabIndex;

    public void SwitchTab(int tabIndex)
    {
        if (tabIndex == 1 && _isParticleActive)
        {
            ShowToast("Bones tab is disabled while viewing particles");
            return;
        }

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
            var stage = GetTree()?.Root?.FindChild("ParticleStage3D", true, false) as DeadlockPlayground.Particles.ParticleStage3D;
            bool hasActiveParticle = stage != null && stage.HasActiveParticle;

            var badge = GetTree()?.Root?.FindChild("ActiveParticleBadge", true, false) as DeadlockPlayground.UI.ActiveParticleBadgeUI;
            badge?.SetPaintingMode(false);

            if (_exportPanel != null)
            {
                _exportPanel.Visible = !hasActiveParticle;
                _exportPanel.SetFramingOverlayVisible(!hasActiveParticle);
            }
            if (!hasActiveParticle && _currentSkeletonGizmoManager != null && GodotObject.IsInstanceValid(_currentSkeletonGizmoManager))
            {
                bool showGizmo = (tabIndex == 1) || (_btnQuickXRay != null && _btnQuickXRay.ButtonPressed);
                _currentSkeletonGizmoManager.SetGizmoEnabled(showGizmo);
            }
            _tabShading?.SetPaintingModeActive(false);

            if (_uvCanvasPanel != null) _uvCanvasPanel.Visible = false;
            if (_painterKeymapBadge != null) _painterKeymapBadge.Visible = false;
            if (_btnOpenUVCanvas != null) _btnOpenUVCanvas.Visible = false;
            if (_quickColorPalette != null) _quickColorPalette.Visible = false;
            if (_navBadge != null) _navBadge.Visible = true;
            if (_btnAdjustments != null) _btnAdjustments.Visible = false;
            if (_btnFileMenu != null) _btnFileMenu.Visible = false;
            _colorAdjustmentModal?.CancelAndClose();
            _exportBaseTexturesModal?.Close();
        }
      else if (tabIndex == 8)
        {
            if (_btnAdjustments != null) _btnAdjustments.Visible = true;
            if (_btnFileMenu != null) _btnFileMenu.Visible = true;
            var hero = GetVpkLoader()?.CurrentHeroNode;
            if (hero != null && _tabPaint?.CurrentHero == null)
            {
                _tabPaint?.SetCurrentHero(hero);
            }
            if (_uvCanvasPanel != null)
            {
                _uvCanvasPanel.Setup(_tabPaint?.LayerManager, _tabPaint?.Painter, _tabPaint?.MeshHierarchy, _tabPaint?.BrushPalette);
            }
            _tabPaint?.OnTabActivated();

            var badge = GetTree()?.Root?.FindChild("ActiveParticleBadge", true, false) as DeadlockPlayground.UI.ActiveParticleBadgeUI;
            badge?.SetPaintingMode(true);

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
           if (_currentSkeletonGizmoManager != null && GodotObject.IsInstanceValid(_currentSkeletonGizmoManager))
            {
                _currentSkeletonGizmoManager.SetGizmoEnabled(false);
            }
            _tabShading?.SetPaintingModeActive(true);

            if (_uvCanvasPanel != null)
            {
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
            if (_quickColorPalette != null)
            {
                _quickColorPalette.Setup(_tabPaint?.Painter, _tabPaint?.BrushPalette, _btnOpenUVCanvas);
                _quickColorPalette.Visible = true;
                _quickColorPalette.UpdateBarPosition();
            }
            if (_navBadge != null) _navBadge.Visible = true;
        }
        else if (tabIndex == 6)
        {
            _tabLighting?.SyncUIToScene();
        }
        if (tabIndex != 8 && _currentSkeletonGizmoManager != null && GodotObject.IsInstanceValid(_currentSkeletonGizmoManager))
        {
            bool showBones = (tabIndex == 1) || (_btnQuickXRay != null && _btnQuickXRay.ButtonPressed);
            _currentSkeletonGizmoManager.SetGizmoEnabled(showBones);
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
            _btnQuit.Pressed += HandleQuitRequested;
        }

        if (_btnNews != null)
        {
            _btnNews.Pressed += () =>
            {
                _changelogModal?.Open();
                UpdateModalsState();
                UpdateNewsBadgeState();
            };
        }

        if (_changelogModal != null)
        {
            _changelogModal.DialogClosed += () =>
            {
                UpdateModalsState();
                UpdateNewsBadgeState();
            };
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
            _btnQuickXRay.Text = _btnQuickXRay.ButtonPressed ? "X-Ray: ON" : "X-Ray: OFF";
            _btnQuickXRay.Toggled += (pressed) =>
            {
                _btnQuickXRay.Text = pressed ? "X-Ray: ON" : "X-Ray: OFF";
                _tabBones?.SetXRay(pressed);
                if (_currentSkeletonGizmoManager != null && GodotObject.IsInstanceValid(_currentSkeletonGizmoManager))
                {
                    bool showGizmo = (_currentTabIndex == 1) || pressed;
                    _currentSkeletonGizmoManager.SetGizmoEnabled(showGizmo);
                }
            };

            if (_tabBones != null)
            {
                _tabBones.OnXRayToggled += (pressed) =>
                {
                    if (_btnQuickXRay != null)
                    {
                        if (_btnQuickXRay.ButtonPressed != pressed)
                        {
                            _btnQuickXRay.ButtonPressed = pressed;
                        }
                        _btnQuickXRay.Text = pressed ? "X-Ray: ON" : "X-Ray: OFF";
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
            bool canUseBones = hasCharacter && !_isParticleActive;
            _btnTabBones.Disabled = !canUseBones;
            _btnTabBones.Modulate = canUseBones ? Colors.White : new Color(0.5f, 0.5f, 0.5f, 0.5f);
            _btnTabBones.TooltipText = _isParticleActive 
                ? "Bones editing is not available while viewing particles" 
                : (hasCharacter ? "" : "Requires an active character model");
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

    public void UpdateParticleState(bool hasParticle)
    {
        _isParticleActive = hasParticle;
        var hero = GetVpkLoader()?.CurrentHeroNode;
        bool hasCharacter = hero != null;

        if (_btnTabBones != null)
        {
            bool canUseBones = hasCharacter && !_isParticleActive;
            _btnTabBones.Disabled = !canUseBones;
            _btnTabBones.Modulate = canUseBones ? Colors.White : new Color(0.5f, 0.5f, 0.5f, 0.5f);
            _btnTabBones.TooltipText = _isParticleActive 
                ? "Bones editing is not available while viewing particles" 
                : (hasCharacter ? "" : "Requires an active character model");
        }

        if (_isParticleActive && _currentTabIndex == 1)
        {
            SwitchTab(3);
        }
    }

    private void UpdateModChangesBadge()
    {
        if (_btnModChanges == null) return;
        int count = ModWorkspaceSession.Instance.GetAllModifiedAssets().Count;
        if (count > 0)
        {
            _btnModChanges.Text = $"WORKSPACE ({count})";
            _btnModChanges.Modulate = new Color(0.95f, 0.75f, 0.3f);
        }
        else
        {
            _btnModChanges.Text = "WORKSPACE";
            _btnModChanges.Modulate = Colors.White;
        }
    }

    public void OpenModChangesModal()
    {
        if (_modChangesModal != null)
        {
            _modChangesModal.OpenModal();
            UpdateModalsState();
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

        _currentSkeletonGizmoManager = null;
        _currentIKManager = null;

        if (skeleton != null)
        {
            _tabBones?.SetSkeleton(skeleton);
            _tabPlayer?.SetSkeleton(skeleton);

            ProceduralClothSolver.Conform(skeleton);

            // Configure SkeletonGizmoManager
            var gizmoManager = new SkeletonGizmoManager
            {
                Name = "SkeletonGizmoManager",
                TargetSkeleton = skeleton
            };
            skeleton.AddChild(gizmoManager);
            _currentSkeletonGizmoManager = gizmoManager;

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

            bool shouldShowBones = (_currentTabIndex == 1) || (_btnQuickXRay != null && _btnQuickXRay.ButtonPressed);
            gizmoManager.SetGizmoEnabled(shouldShowBones);
            if (!shouldShowBones)
            {
                _currentIKManager.SetMasterIKEnabled(false);
                _tabBones?.DisableMasterIK();
                _tabBones?.SetXRay(false);
            }

            // Garantizar en el siguiente frame que ningún _Ready() hijo haya reactivado los gizmos fuera del tab Bones
            Callable.From(() =>
            {
                bool showNow = (_currentTabIndex == 1) || (_btnQuickXRay != null && _btnQuickXRay.ButtonPressed);
                if (GodotObject.IsInstanceValid(gizmoManager))
                {
                    gizmoManager.SetGizmoEnabled(showNow);
                }
                if (!showNow && GodotObject.IsInstanceValid(ikManager))
                {
                    ikManager.SetMasterIKEnabled(false);
                    _tabBones?.DisableMasterIK();
                    if (_btnQuickXRay == null || !_btnQuickXRay.ButtonPressed)
                    {
                        _tabBones?.SetXRay(false);
                    }
                }
            }).CallDeferred();

            // Synchronize Weapon Bone Attachments
            SyncWeaponBoneAttachments(heroNode, skeleton);
        }

        if (animPlayer != null)
        {
            _tabPlayer?.SetAnimationPlayer(animPlayer);
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
        _currentSkeletonGizmoManager = null;
        _tabCharacter?.ClearSubmeshes();
        _tabShading?.ClearHero();
        _tabPaint?.ClearHero();
        _tabPaint?.LayerManager?.ClearHistory();
        _tabPaint?.Painter?.ResetSession();
        _tabBones?.SetSkeleton(null);
        _tabBones?.SetIKManager(null);
        _tabPlayer?.SetSkeleton(null);
        _tabPlayer?.SetAnimationPlayer(null);
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

    public bool IsAnyModalOpen()
    {
        var particleModal = _modalsLayer?.FindChild("ParticlePickerModal", true, false) as Control;
        var heroModal = _modalsLayer?.FindChild("HeroPickerModal", true, false) as Control;
        var extraModelModal = _modalsLayer?.FindChild("ExtraModelPickerModal", true, false) as Control;
        var modModal = _modalsLayer?.FindChild("ModPickerModal", true, false) as Control;
        var retargetModal = _modalsLayer?.FindChild("AnimationRetargetModal", true, false) as Control;

        return (_settingsModal != null && _settingsModal.Visible)
            || (_feedbackModal != null && _feedbackModal.Visible)
            || (_aboutModal != null && _aboutModal.Visible)
            || (_changelogModal != null && _changelogModal.Visible)
            || (_loadingOverlay != null && _loadingOverlay.Visible)
            || (_gameFolderDialog != null && _gameFolderDialog.Visible)
            || (_saveFolderDialog != null && _saveFolderDialog.Visible)
            || (_fallbackPathDialog != null && _fallbackPathDialog.Visible)
            || (_modChangesModal != null && _modChangesModal.Visible)
            || (_unsavedChangesModal != null && _unsavedChangesModal.Visible)
            || (_colorAdjustmentModal != null && _colorAdjustmentModal.Visible)
            || (_exportBaseTexturesModal != null && _exportBaseTexturesModal.Visible)
            || (particleModal != null && particleModal.Visible)
            || (heroModal != null && heroModal.Visible)
            || (extraModelModal != null && extraModelModal.Visible)
            || (modModal != null && modModal.Visible)
            || (retargetModal != null && retargetModal.Visible);
    }

    public void UpdateModalsState()
    {
        bool anyModalOpen = IsAnyModalOpen();

        if (_modalsLayer != null)
        {
            _modalsLayer.Visible = anyModalOpen;
        }

        if (_modalBackdrop != null)
        {
            bool isAdjustmentOpen = _colorAdjustmentModal != null && _colorAdjustmentModal.Visible;
            _modalBackdrop.Visible = anyModalOpen && !isAdjustmentOpen;
        }

        if (anyModalOpen)
        {
            _quickColorPalette?.ClosePopup();
        }
    }

    private void OnAdjustmentsMenuAboutToPopup()
    {
        var activeLayer = _tabPaint?.LayerManager?.ActiveLayer;
        bool canAdjust = activeLayer != null && activeLayer.IsVisible && !activeLayer.IsLocked;
        var popup = _btnAdjustments?.GetPopup();
        if (popup != null)
        {
            for (int i = 0; i < popup.ItemCount; i++)
            {
                if (!popup.IsItemSeparator(i))
                {
                    popup.SetItemDisabled(i, !canAdjust);
                }
            }
        }
    }

    private void HandleAdjustmentTriggered(AdjustmentType type)
    {
        if (_currentTabIndex != 8 || _tabPaint == null) return;
        var layer = _tabPaint.LayerManager?.ActiveLayer;
        if (layer == null)
        {
            ShowToast("No active paint layer selected.");
            return;
        }
        if (!layer.IsVisible)
        {
            ShowToast($"Cannot adjust layer '{layer.Name}' because it is hidden.");
            return;
        }
        if (layer.IsLocked)
        {
            ShowToast($"Cannot adjust layer '{layer.Name}' because it is locked.");
            return;
        }

        if (type == AdjustmentType.Invert || type == AdjustmentType.Desaturate)
        {
            _tabPaint.ApplyImmediateAdjustment(type);
        }
        else
        {
            _tabPaint.OpenAdjustmentModal(type);
        }
    }

    private void OnFileMenuItemPressed(long id)
    {
        switch (id)
        {
            case 0:
                _tabPaint?.PromptResetWorkspace();
                break;
            case 1:
                _tabPaint?.OpenLoadProjectDialog();
                break;
            case 2:
                _tabPaint?.OpenSaveProjectDialog();
                break;
            case 3:
                _tabPaint?.OpenImportImageDialog();
                break;
            case 4:
                OpenExportBaseTexturesModal();
                break;
            case 5:
                _tabPaint?.OpenExportBakedPngDialog();
                break;
            case 6:
                _tabPaint?.OpenExportActiveLayerDialog();
                break;
            case 7:
                _tabPaint?.OpenExportModDialog();
                break;
            case 8:
                OpenAddonsFolder();
                break;
        }
    }

    public void OpenExportBaseTexturesModal()
    {
        if (_exportBaseTexturesModal != null && _tabPaint != null)
        {
            _exportBaseTexturesModal.Open(_tabPaint);
        }
        else
        {
            _tabPaint?.OpenExportBaseTexturesModal();
        }
    }

    public void OpenAddonsFolder()
    {
        string addonsDir = _tabCharacter?.GetResolvedAddonsDir();
        if (string.IsNullOrEmpty(addonsDir) || !Directory.Exists(addonsDir))
        {
            var pathManager = new GamePathManager();
            pathManager.LoadConfig();
            if (!string.IsNullOrEmpty(pathManager.CurrentGamePath))
            {
                addonsDir = Path.Combine(pathManager.CurrentGamePath, "game", "citadel", "addons");
            }
        }

        if (!string.IsNullOrEmpty(addonsDir))
        {
            try
            {
                if (!Directory.Exists(addonsDir))
                {
                    Directory.CreateDirectory(addonsDir);
                }
                OS.ShellOpen(addonsDir);
                ShowToast("Addons Folder", addonsDir);
            }
            catch (Exception ex)
            {
                ShowToast("Error opening folder", ex.Message);
            }
        }
        else
        {
            ShowToast("Deadlock addons folder not found", "Configure game path in Settings");
        }
    }

    public void UpdateNewsBadgeState()
    {
        if (_unreadBadge != null)
        {
            _unreadBadge.Visible = ChangelogModalUI.HasUnreadChangelog();
        }
    }

    private void HandleQuitRequested()
    {
        if (ModWorkspaceSession.Instance.HasUnsavedChanges)
        {
            _tabPaint?.FlushActiveChangesToWorkspace(true);
            ShowUnsavedChangesDialog();
        }
        else
        {
            ModWorkspaceSession.Instance.ClearSession();
            GetTree().Quit();
        }
    }

    private void ShowUnsavedChangesDialog()
    {
        if (_unsavedChangesModal == null)
        {
            _unsavedChangesModal = new PanelContainer
            {
                Name = "UnsavedChangesModal",
                Visible = false,
                CustomMinimumSize = new Vector2(500, 220)
            };
            _unsavedChangesModal.SetAnchorsPreset(Control.LayoutPreset.Center);
            _unsavedChangesModal.GrowHorizontal = Control.GrowDirection.Both;
            _unsavedChangesModal.GrowVertical = Control.GrowDirection.Both;

            var panelStyle = new StyleBoxFlat
            {
                BgColor = new Color(0.10f, 0.12f, 0.16f, 0.98f),
                BorderWidthBottom = 2,
                BorderWidthLeft = 2,
                BorderWidthRight = 2,
                BorderWidthTop = 2,
                BorderColor = new Color(0.95f, 0.75f, 0.30f, 0.9f),
                CornerRadiusBottomLeft = 8,
                CornerRadiusBottomRight = 8,
                CornerRadiusTopLeft = 8,
                CornerRadiusTopRight = 8,
                ShadowColor = new Color(0, 0, 0, 0.6f),
                ShadowSize = 16
            };
            _unsavedChangesModal.AddThemeStyleboxOverride("panel", panelStyle);

            var margin = new MarginContainer();
            margin.AddThemeConstantOverride("margin_left", 24);
            margin.AddThemeConstantOverride("margin_right", 24);
            margin.AddThemeConstantOverride("margin_top", 20);
            margin.AddThemeConstantOverride("margin_bottom", 20);
            _unsavedChangesModal.AddChild(margin);

            var vbox = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
            vbox.AddThemeConstantOverride("separation", 14);
            margin.AddChild(vbox);

            var colus = PlaygroundThemeHelper.GetFontColus();

            var titleLbl = new Label
            {
                Text = "UNSAVED WORKSPACE CHANGES",
                Modulate = new Color(0.95f, 0.75f, 0.30f)
            };
            if (colus != null) titleLbl.AddThemeFontOverride("font", colus);
            titleLbl.AddThemeFontSizeOverride("font_size", 16);
            vbox.AddChild(titleLbl);

            int assetCount = ModWorkspaceSession.Instance.GetAllModifiedAssets().Count;
            var bodyLbl = new Label
            {
                Text = $"You have {assetCount} modified asset(s) staged in your current workspace session.\nDo you want to save your project (.dlproj) before exiting?",
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                SizeFlagsVertical = Control.SizeFlags.ExpandFill
            };
            bodyLbl.AddThemeFontSizeOverride("font_size", 12);
            vbox.AddChild(bodyLbl);

            var btnRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, Alignment = BoxContainer.AlignmentMode.End };
            btnRow.AddThemeConstantOverride("separation", 10);

            var btnCancel = new Button { Text = "Cancel", CustomMinimumSize = new Vector2(80, 32) };
            btnCancel.Pressed += () =>
            {
                _unsavedChangesModal.Visible = false;
                UpdateModalsState();
            };
            btnRow.AddChild(btnCancel);

            var btnDiscard = new Button { Text = "Discard & Exit", CustomMinimumSize = new Vector2(110, 32) };
            btnDiscard.Modulate = new Color(0.9f, 0.4f, 0.4f);
            btnDiscard.Pressed += () =>
            {
                ModWorkspaceSession.Instance.ClearSession();
                GetTree().Quit();
            };
            btnRow.AddChild(btnDiscard);

            var btnSaveExit = new Button { Text = "Save & Exit", CustomMinimumSize = new Vector2(110, 32) };
            PlaygroundThemeHelper.MakeAccentButton(btnSaveExit);
            btnSaveExit.Pressed += () =>
            {
                _unsavedChangesModal.Visible = false;
                UpdateModalsState();
                ShowUnsavedSaveFileDialog();
            };
            btnRow.AddChild(btnSaveExit);

            vbox.AddChild(btnRow);

            if (_modalsLayer != null)
            {
                _modalsLayer.AddChild(_unsavedChangesModal);
            }
            else
            {
                AddChild(_unsavedChangesModal);
            }
        }

        _unsavedChangesModal.Visible = true;
        UpdateModalsState();
    }

   private void ShowUnsavedSaveFileDialog()
    {
        if (_unsavedSaveDialog == null)
        {
            _unsavedSaveDialog = new FileDialog
            {
                Title = "Save Project (.dpp) & Exit",
                FileMode = FileDialog.FileModeEnum.SaveFile,
                Access = FileDialog.AccessEnum.Filesystem,
                Filters = new string[] { "*.dpp ; Deadlock Playground Project (*.dpp)" },
                UseNativeDialog = true
            };
            _unsavedSaveDialog.FileSelected += (path) =>
            {
                if (!string.IsNullOrEmpty(path))
                {
                    if (!path.EndsWith(".dpp", StringComparison.OrdinalIgnoreCase))
                        path += ".dpp";
                    _tabPaint?.FlushActiveChangesToWorkspace(true);
                    ModWorkspaceSession.Instance.PackageProject(path);
                    ModWorkspaceSession.Instance.ClearSession();
                    GetTree().Quit();
                }
            };
            AddChild(_unsavedSaveDialog);
        }
        _unsavedSaveDialog.CurrentFile = "workspace_project.dpp";
        _unsavedSaveDialog.PopupCentered();
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
        if (_isUpdatePrepared)
        {
            if (OS.HasFeature("editor"))
            {
                ShowToast("Update verified and staged! Executable replacement is skipped in Editor mode.");
                return;
            }

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
                        if (_updateNotificationTitle != null && status.StartsWith("Extracting", StringComparison.OrdinalIgnoreCase))
                        {
                            _updateNotificationTitle.Text = "Extracting Update...";
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
            StartupDiagnosticTracker.Log("Update check: started");
            var result = await UpdateChecker.CheckForUpdatesAsync().ConfigureAwait(false);
            StartupDiagnosticTracker.Log("Update check: finished");
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
        StartupDiagnosticTracker.Mark("MainUI: InitializePaths begin");

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
        StartupDiagnosticTracker.Mark("MainUI: InitializePaths done");
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
