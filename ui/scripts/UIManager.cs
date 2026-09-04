using Godot;
using System;
using System.Collections.Generic;

public partial class UIManager : CanvasLayer
{
    // Signals to decouple UI from 3D loading and camera logic
    [Signal] public delegate void CharacterSelectedEventHandler(string heroInternalName);
    [Signal] public delegate void BackgroundSelectedEventHandler(string backgroundId);
    [Signal] public delegate void ScreenshotRequestedEventHandler(int resolutionMultiplier, bool transparentBg);
    [Signal] public delegate void GamePathChangedEventHandler(string newPath);

    #region Exported Nodes - Settings Tabs
    [ExportCategory("Settings Tabs Controls")]

    [ExportGroup("Settings - General Tab")]
    [Export] private OptionButton _langOption;
    [Export] private LineEdit _gamePathInput;
    [Export] private Button _btnBrowseGamePath;
    [Export] private LineEdit _savePathInput;
    [Export] private Button _btnBrowseSavePath;

    [ExportGroup("Settings - Graphics Tab")]
    [Export] private OptionButton _resOption;
    [Export] private OptionButton _texQualityOption;

    [ExportGroup("Settings - Camera Tab")]
    [Export] private CheckBox _transparentBgCheck;
    [Export] private CheckBox _jpgFormatCheck;

    [ExportGroup("Settings - UI Properties Tab")]
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
    #endregion

    #region Exported Nodes - Navigation & Modals
    [ExportCategory("HUD & Modal Controls")]

    [ExportGroup("SideBar & Navigation")]
    [Export] private OptionButton _charOption;
    [Export] private OptionButton _bgOption;
    [Export] private Button _btnScreenshot;
    [Export] private Button _btnAbout;
    [Export] private Button _btnSettings;

    [ExportGroup("Modals & Dialogs")]
    [Export] private Control _modalsLayer;
    [Export] private PanelContainer _settingsModal;
    [Export] private PanelContainer _aboutModal;
    [Export] private PanelContainer _loadingOverlay;
    [Export] private Button _btnAboutClose;
    [Export] private Button _btnSettingsClose;
    [Export] private FileDialog _gameFolderDialog;
    [Export] private FileDialog _saveFolderDialog;
    #endregion

    // Managers & Internals
    private GamePathManager _pathManager;
    private ScreenshotHelper _screenshotHelper;
    private AcceptDialog _fallbackPathDialog;
    private PanelContainer _toastPanel;
    private Label _toastLabel;
    private Button _toastBtn;
    private Godot.Timer _toastTimer;

    // Character mapping: UI Name -> folder/model identifier
    private readonly List<(string DisplayName, string InternalId)> _characters = new()
    {
        ("Abrams", "abrams"),
        ("Bebop", "bebop"),
        ("Dynamo", "dynamo"),
        ("Grey Talon", "archer"),
        ("Haze", "haze"),
        ("Infernus", "chronos"),
        ("Ivy", "tengu"),
        ("Kelvin", "kelvin"),
        ("Lady Geist", "ghost"),
        ("Lash", "lash_v2"),
        ("McGinnis", "engineer"),
        ("Mirage", "mirage"),
        ("Mo & Krill", "digger"),
        ("Paradox", "chrono"),
        ("Pocket", "pocket"),
        ("Seven", "wrecker"),
        ("Shiv", "shiv"),
        ("Vindicta", "hornet"),
        ("Viscous", "viscous"),
        ("Warden", "warden"),
        ("Wraith", "wraith"),
        ("Yamato", "yamato")
    };

    private readonly List<(string DisplayName, string BgId)> _backgrounds = new()
    {
        ("Classic_Menu", "classic_menu")
    };

    public override void _Ready()
    {
        GetViewport().TransparentBg = true;
        
        var envNode = GetNodeOrNull<WorldEnvironment>("/root/Main/WorldEnvironment");
        if (envNode != null && envNode.Environment != null)
        {
            envNode.Environment.BackgroundMode = Godot.Environment.BGMode.Canvas;
            envNode.Environment.BackgroundCanvasMaxLayer = -1;

            envNode.Environment.AmbientLightSource = Godot.Environment.AmbientSource.Sky;
            envNode.Environment.ReflectedLightSource = Godot.Environment.ReflectionSource.Sky;
        }

        // Managers
        _pathManager = new GamePathManager();
        AddChild(_pathManager);
        
        _screenshotHelper = new ScreenshotHelper();
        _screenshotHelper.ScreenshotSaved += OnScreenshotSaved;
        AddChild(_screenshotHelper);

        LinkNodes();
        CreateToastUI();
        CreateFallbackDialog();
        PopulateSelectors();
        ConnectEvents();

        InitializePaths();
    }

    /// <summary>
    /// Fallback resolution for any node that has not been explicitly wired in the Godot Inspector.
    /// This allows both drag-and-drop Inspector assignment and zero-setup runtime fallbacks.
    /// </summary>
    private void LinkNodes()
    {
        // 1. Navigation & SideBar
        _charOption ??= GetNodeOrNull<OptionButton>("MainHUD/SideBar/VBoxContainer/CharacterButton");
        _bgOption ??= GetNodeOrNull<OptionButton>("MainHUD/SideBar/VBoxContainer/BGButton");
        _btnScreenshot ??= GetNodeOrNull<Button>("MainHUD/SideBar/VBoxContainer/SSButton");
        _btnAbout ??= GetNodeOrNull<Button>("MainHUD/SideBar/VBoxContainer/AboutButton");
        _btnSettings ??= GetNodeOrNull<Button>("MainHUD/TopBar/HBoxContainer/SettingsButton");

        // 2. Modals & Dialogs
        _modalsLayer ??= GetNodeOrNull<Control>("MainHUD/ModalsLayer");
        _settingsModal ??= GetNodeOrNull<PanelContainer>("MainHUD/ModalsLayer/SettingsModal");
        _aboutModal ??= GetNodeOrNull<PanelContainer>("MainHUD/ModalsLayer/AboutModal");
        _loadingOverlay ??= GetNodeOrNull<PanelContainer>("MainHUD/ModalsLayer/LoadingOverlay");

        _btnAboutClose ??= GetNodeOrNull<Button>("MainHUD/ModalsLayer/AboutModal/MarginContainer/VBoxContainer/Button");
        _btnSettingsClose ??= GetNodeOrNull<Button>("MainHUD/ModalsLayer/SettingsModal/VBoxContainer/CloseButton");

        _gameFolderDialog ??= GetNodeOrNull<FileDialog>("MainHUD/ModalsLayer/GameFolderDialog");
        if (_gameFolderDialog == null)
        {
            _gameFolderDialog = new FileDialog
            {
                Name = "GameFolderDialog",
                FileMode = FileDialog.FileModeEnum.OpenDir,
                Title = "Select Game Folder",
                Access = FileDialog.AccessEnum.Filesystem
            };
            GetNodeOrNull("MainHUD/ModalsLayer")?.AddChild(_gameFolderDialog);
        }

        _saveFolderDialog ??= GetNodeOrNull<FileDialog>("MainHUD/ModalsLayer/ScreenshotFolderDialog");
        if (_saveFolderDialog == null)
        {
            _saveFolderDialog = new FileDialog
            {
                Name = "ScreenshotFolderDialog",
                FileMode = FileDialog.FileModeEnum.OpenDir,
                Title = "Select Screenshots Folder",
                Access = FileDialog.AccessEnum.Filesystem
            };
            GetNodeOrNull("MainHUD/ModalsLayer")?.AddChild(_saveFolderDialog);
        }

        // 3. Settings - General Tab
        _langOption ??= GetNodeOrNull<OptionButton>("MainHUD/ModalsLayer/SettingsModal/VBoxContainer/TabContainer/General/VBoxContainer/LanguageButton");
        _gamePathInput ??= GetNodeOrNull<LineEdit>("MainHUD/ModalsLayer/SettingsModal/VBoxContainer/TabContainer/General/VBoxContainer/GamePathSearchContainer/GamePathLine");
        _btnBrowseGamePath ??= GetNodeOrNull<Button>("MainHUD/ModalsLayer/SettingsModal/VBoxContainer/TabContainer/General/VBoxContainer/GamePathSearchContainer/BrowseGamePathButton");
        _savePathInput ??= GetNodeOrNull<LineEdit>("MainHUD/ModalsLayer/SettingsModal/VBoxContainer/TabContainer/General/VBoxContainer/SavesPathSearchContainer/SavesPathLine");
        _btnBrowseSavePath ??= GetNodeOrNull<Button>("MainHUD/ModalsLayer/SettingsModal/VBoxContainer/TabContainer/General/VBoxContainer/SavesPathSearchContainer/BrowseSavePathButton");

        // 4. Settings - Graphics Tab
        _resOption ??= GetNodeOrNull<OptionButton>("MainHUD/ModalsLayer/SettingsModal/VBoxContainer/TabContainer/Graphics/VBoxContainer/ScreenshotResButton");
        _texQualityOption ??= GetNodeOrNull<OptionButton>("MainHUD/ModalsLayer/SettingsModal/VBoxContainer/TabContainer/Graphics/VBoxContainer/TexQualityButton");

        // 5. Settings - Camera Tab
        var cameraVBox = GetNodeOrNull<VBoxContainer>("MainHUD/ModalsLayer/SettingsModal/VBoxContainer/TabContainer/Camera/VBoxContainer");
        _transparentBgCheck ??= cameraVBox?.GetNodeOrNull<CheckBox>("TransparentBgCheck");
        if (_jpgFormatCheck == null && cameraVBox != null)
        {
            _jpgFormatCheck = cameraVBox.GetNodeOrNull<CheckBox>("JpgFormatCheck");
            if (_jpgFormatCheck == null)
            {
                _jpgFormatCheck = new CheckBox
                {
                    Name = "JpgFormatCheck",
                    Text = "Save as JPG format (forces background on)"
                };
                cameraVBox.AddChild(_jpgFormatCheck);
            }
        }

        // 6. Settings - UI Properties Tab
        InitializeDisplaySettingsUI();
    }

    private void CreateToastUI()
    {
        _toastPanel = new PanelContainer();
        _toastPanel.Visible = false;
        
        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 15);
        margin.AddThemeConstantOverride("margin_top", 10);
        margin.AddThemeConstantOverride("margin_right", 15);
        margin.AddThemeConstantOverride("margin_bottom", 10);
        
        var hbox = new HBoxContainer();
        hbox.AddThemeConstantOverride("separation", 15);

        _toastLabel = new Label();
        _toastLabel.Text = "Screenshot saved!";
        
        _toastBtn = new Button();
        _toastBtn.Text = "Open Folder";
        
        hbox.AddChild(_toastLabel);
        hbox.AddChild(_toastBtn);
        margin.AddChild(hbox);
        _toastPanel.AddChild(margin);

        var style = new StyleBoxFlat();
        style.BgColor = new Color(0.1f, 0.1f, 0.1f, 0.9f);
        style.CornerRadiusTopLeft = 8;
        style.CornerRadiusTopRight = 8;
        style.CornerRadiusBottomLeft = 8;
        style.CornerRadiusBottomRight = 8;
        _toastPanel.AddThemeStyleboxOverride("panel", style);

        _toastPanel.SetAnchorsPreset(Control.LayoutPreset.CenterBottom);
        _toastPanel.Position = new Vector2(_toastPanel.Position.X, -50);

        GetNode("MainHUD").AddChild(_toastPanel);
        
        _toastTimer = new Godot.Timer();
        _toastTimer.OneShot = true;
        _toastTimer.WaitTime = 5.0;
        _toastTimer.Timeout += () => _toastPanel.Visible = false;
        AddChild(_toastTimer);
    }

    private void CreateFallbackDialog()
    {
        _fallbackPathDialog = new AcceptDialog();
        _fallbackPathDialog.Title = "Deadlock Path Not Found";
        _fallbackPathDialog.DialogText = "Could not automatically detect the Deadlock installation path.\nPlease select the Deadlock game directory manually.";
        _fallbackPathDialog.Exclusive = true;
        
        _fallbackPathDialog.Confirmed += () => 
        {
            _gameFolderDialog?.PopupCentered();
        };

        GetNode("MainHUD/ModalsLayer").AddChild(_fallbackPathDialog);
    }

    private async void InitializePaths()
    {
        ShowLoading(true);
        var loadLabel = GetNodeOrNull<Label>("MainHUD/ModalsLayer/LoadingOverlay/Label");
        if (loadLabel != null) loadLabel.Text = "Detecting Deadlock path...";

        if (string.IsNullOrEmpty(_pathManager.CurrentGamePath) || !_pathManager.ValidateDeadlockPath(_pathManager.CurrentGamePath))
        {
            string autoPath = await _pathManager.AutoDetectDeadlockPathAsync();
            if (!string.IsNullOrEmpty(autoPath))
            {
                _pathManager.SaveConfig(autoPath, _pathManager.CurrentSavePath);
                GD.Print($"[UI] Deadlock detectado en: {autoPath}");
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
        if (loadLabel != null) loadLabel.Text = "Loading character model...";
    }

    private void UpdateVpkLoaderPath(string basePath)
    {
        var loader = GetNodeOrNull<VpkLoaderTest>("/root/Main/VpkLoaderTest");
        if (loader != null)
        {
            loader.VpkPath = System.IO.Path.Combine(basePath, "game/citadel/pak01_dir.vpk");
        }
    }

    private void PopulateSelectors()
    {
        // 1. Selector de Personajes
        if (_charOption != null)
        {
            _charOption.Clear();
            _charOption.AddItem("--- Select a Hero ---", -1);
            _charOption.SetItemDisabled(0, true);

            for (int i = 0; i < _characters.Count; i++)
            {
                _charOption.AddItem(_characters[i].DisplayName, i);
            }
            _charOption.Select(0);
        }

        // 2. Selector de Fondos
        if (_bgOption != null)
        {
            _bgOption.Clear();
            for (int i = 0; i < _backgrounds.Count; i++)
            {
                _bgOption.AddItem(_backgrounds[i].DisplayName, i);
            }
        }

        // 3. Opciones de Idioma
        if (_langOption != null)
        {
            _langOption.Clear();
            _langOption.AddItem("Español", 0);
            _langOption.AddItem("English", 1);
        }

        // 4. Resoluciones de Captura
        if (_resOption != null)
        {
            _resOption.Clear();
            _resOption.AddItem("1080p", 1);
            _resOption.AddItem("1440p", 2);
            _resOption.AddItem("2160p", 4);
        }

        // 5. Calidad de Mipmaps
        if (_texQualityOption != null)
        {
            _texQualityOption.Clear();
            _texQualityOption.AddItem("High (1024px)", 0);
            _texQualityOption.AddItem("Medium (512px)", 1);
            _texQualityOption.AddItem("Low (256px)", 2);
        }
    }

    private void ConnectEvents()
    {
        // Selección en SideBar
        if (_charOption != null) _charOption.ItemSelected += OnCharacterSelected;
        if (_bgOption != null) _bgOption.ItemSelected += OnBackgroundSelected;
        if (_btnScreenshot != null) _btnScreenshot.Pressed += OnScreenshotPressed;

        // Modals
        if (_btnSettings != null)
        {
            _btnSettings.Pressed += () => 
            {
                if (_modalsLayer == null) _modalsLayer = GetNodeOrNull<Control>("MainHUD/ModalsLayer");
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
                if (_modalsLayer == null) _modalsLayer = GetNodeOrNull<Control>("MainHUD/ModalsLayer");
                if (_modalsLayer != null) _modalsLayer.Visible = true;
                if (_aboutModal != null)
                {
                    _aboutModal.Visible = true;
                    _aboutModal.MoveToFront();
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

        // Botones "Buscar..." en Settings (vía exported buttons sin rutas hardcodeadas)
        if (_btnBrowseGamePath != null) _btnBrowseGamePath.Pressed += () => _gameFolderDialog?.PopupCentered();
        if (_btnBrowseSavePath != null) _btnBrowseSavePath.Pressed += () => _saveFolderDialog?.PopupCentered();

        if (_gameFolderDialog != null) _gameFolderDialog.DirSelected += OnGameDirSelected;
        if (_saveFolderDialog != null) _saveFolderDialog.DirSelected += OnSaveDirSelected;

        var loader = GetNodeOrNull<VpkLoaderTest>("/root/Main/VpkLoaderTest");
        if (loader != null)
        {
            loader.LoadStarted += () => ShowLoading(true);
            loader.LoadFinished += () => ShowLoading(false);
        }
    }

    private void OnCharacterSelected(long index)
    {
        if (_charOption == null) return;
        int id = _charOption.GetItemId((int)index);
        if (id < 0 || id >= _characters.Count) return;

        string internalId = _characters[id].InternalId;
        GD.Print($"[UI] Héroe seleccionado: {_characters[id].DisplayName} ({internalId})");
        EmitSignal(SignalName.CharacterSelected, internalId);

        var loader = GetNodeOrNull<VpkLoaderTest>("/root/Main/VpkLoaderTest");
        if (loader != null)
        {
            _ = loader.LoadHeroAsync(internalId, internalId);
        }
    }

    private void OnBackgroundSelected(long index)
    {
        if (index < 0 || index >= _backgrounds.Count) return;
        string bgId = _backgrounds[(int)index].BgId;
        GD.Print($"[UI] Fondo seleccionado: {bgId}");
        EmitSignal(SignalName.BackgroundSelected, bgId);

        var bgRect = GetNodeOrNull<TextureRect>("BackgroundRect");
        if (bgRect != null && bgId == "classic_menu")
        {
            bgRect.Texture = GD.Load<Texture2D>("res://assets/backgrounds/classic_menu_bg.png");
        }
    }

    private void OnScreenshotPressed()
    {
        int multiplier = _resOption != null ? _resOption.GetSelectedId() : 1;
        bool transparent = _transparentBgCheck != null && _transparentBgCheck.ButtonPressed;
        bool useJpg = _jpgFormatCheck != null && _jpgFormatCheck.ButtonPressed;

        GD.Print($"[UI] Captura pedida (Mult: {multiplier}x, Alfa: {transparent}, JPG: {useJpg})");
        EmitSignal(SignalName.ScreenshotRequested, multiplier, transparent);

        var camera = GetNodeOrNull<Camera3D>("/root/Main/CameraPivot/Camera3D");
        var loader = GetNodeOrNull<VpkLoaderTest>("/root/Main/VpkLoaderTest");
        
        Node3D gizmo = null;
        if (loader != null && loader.GetChildCount() > 0)
        {
            var heroNode = loader.GetChildOrNull<Node3D>(0);
            if (heroNode != null)
            {
                var skeleton = FindSkeleton(heroNode);
                if (skeleton != null)
                {
                    gizmo = skeleton.GetNodeOrNull<Node3D>("SkeletonGizmoManager");
                }
            }
        }

        if (camera != null && _screenshotHelper != null)
        {
            _ = _screenshotHelper.CaptureAsync(
                camera, 
                multiplier, 
                transparent, 
                useJpg, 
                _pathManager?.CurrentSavePath, 
                gizmo
            );
        }
    }

    private Skeleton3D FindSkeleton(Node node)
    {
        if (node is Skeleton3D sk) return sk;
        foreach (Node child in node.GetChildren())
        {
            var res = FindSkeleton(child);
            if (res != null) return res;
        }
        return null;
    }

    private void OnScreenshotSaved(string path)
    {
        if (_toastPanel == null || _toastLabel == null || _toastBtn == null) return;

        _toastLabel.Text = $"Screenshot saved: {System.IO.Path.GetFileName(path)}";
        _toastBtn.Pressed -= OpenSavedFolder;
        _toastBtn.Pressed += OpenSavedFolder;
        
        _toastPanel.Visible = true;
        _toastTimer?.Start();
    }

    private void OpenSavedFolder()
    {
        string path = _pathManager?.CurrentSavePath;
        if (string.IsNullOrEmpty(path)) return;

        path = ProjectSettings.GlobalizePath(path);
        OS.ShellOpen(path);
    }

    private void OnGameDirSelected(string dir)
    {
        if (_pathManager != null && _pathManager.ValidateDeadlockPath(dir))
        {
            if (_gamePathInput != null) _gamePathInput.Text = dir;
            _pathManager.SaveConfig(dir, _pathManager.CurrentSavePath);
            UpdateVpkLoaderPath(dir);
            EmitSignal(SignalName.GamePathChanged, dir);
        }
        else
        {
            var alert = new AcceptDialog();
            alert.DialogText = "Invalid Deadlock path. Could not find game/citadel/pak01_dir.vpk";
            GetNode("MainHUD/ModalsLayer").AddChild(alert);
            alert.PopupCentered();
            
            if (string.IsNullOrEmpty(_pathManager?.CurrentGamePath))
            {
                alert.Confirmed += () => _fallbackPathDialog?.PopupCentered();
            }
        }
    }

    private void OnSaveDirSelected(string dir)
    {
        if (_savePathInput != null) _savePathInput.Text = dir;
        _pathManager?.SaveConfig(_pathManager.CurrentGamePath, dir);
    }

    public void ShowLoading(bool show)
    {
        if (_loadingOverlay != null)
        {
            _loadingOverlay.Visible = show;
        }
    }

    private void InitializeDisplaySettingsUI()
    {
        GizmoDisplaySettings.LoadConfig();

        // If not wired via [Export], search scene dynamically or create fallback tab
        if (_xrayLineColorPicker == null)
        {
            var tabContainer = GetNodeOrNull<TabContainer>("MainHUD/ModalsLayer/SettingsModal/VBoxContainer/TabContainer");
            if (tabContainer != null)
            {
                var uiTab = tabContainer.GetNodeOrNull<Control>("UIProperties") ?? 
                            tabContainer.GetNodeOrNull<Control>("UI Properties") ??
                            tabContainer.GetNodeOrNull<Control>("Viewport");

                VBoxContainer contentVBox = null;
                if (uiTab != null)
                {
                    contentVBox = uiTab.GetNodeOrNull<VBoxContainer>("VBoxContainer") ?? uiTab as VBoxContainer;
                }

                if (uiTab == null)
                {
                    var margin = new MarginContainer { Name = "UIProperties" };
                    margin.AddThemeConstantOverride("margin_left", 16);
                    margin.AddThemeConstantOverride("margin_top", 12);
                    margin.AddThemeConstantOverride("margin_right", 16);
                    margin.AddThemeConstantOverride("margin_bottom", 12);

                    contentVBox = new VBoxContainer { Name = "VBoxContainer" };
                    contentVBox.AddThemeConstantOverride("separation", 10);
                    margin.AddChild(contentVBox);

                    tabContainer.AddChild(margin);
                    tabContainer.SetTabTitle(tabContainer.GetTabCount() - 1, "UI Properties");
                }

                if (contentVBox != null)
                {
                    _xrayLineColorPicker = contentVBox.GetNodeOrNull<ColorPickerButton>("XRayLineColorPicker");
                    _xrayLineOpacitySlider = contentVBox.GetNodeOrNull<HSlider>("XRayLineOpacitySlider");
                    _xrayLineOpacityLabel = contentVBox.GetNodeOrNull<Label>("LineOpacityLabel");
                    _bonePrimaryColorPicker = contentVBox.GetNodeOrNull<ColorPickerButton>("BonePrimaryColorPicker");
                    _boneClothingColorPicker = contentVBox.GetNodeOrNull<ColorPickerButton>("BoneClothingColorPicker");
                    _boneMarkerOpacitySlider = contentVBox.GetNodeOrNull<HSlider>("BoneMarkerOpacitySlider");
                    _boneMarkerOpacityLabel = contentVBox.GetNodeOrNull<Label>("MarkerOpacityLabel");
                    _boneMarkerSizeSlider = contentVBox.GetNodeOrNull<HSlider>("BoneMarkerSizeSlider");
                    _boneMarkerSizeLabel = contentVBox.GetNodeOrNull<Label>("MarkerSizeLabel");
                    _btnResetDisplaySettings = contentVBox.GetNodeOrNull<Button>("ResetDisplayDefaultsButton");

                    if (_xrayLineColorPicker == null)
                    {
                        BuildDisplaySettingsControls(contentVBox);
                    }
                }
            }
        }

        SyncDisplaySettingsValues();
        ConnectDisplaySettingsEvents();
    }

    private void BuildDisplaySettingsControls(VBoxContainer contentVBox)
    {
        var xrayHeader = new Label { Text = "X-Ray Wireframe Lines" };
        xrayHeader.AddThemeColorOverride("font_color", new Color(0.9f, 0.9f, 0.9f));
        contentVBox.AddChild(xrayHeader);

        var lineColHBox = new HBoxContainer();
        var lineColLabel = new Label { Text = "Line Color", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _xrayLineColorPicker = new ColorPickerButton { CustomMinimumSize = new Vector2(60, 24) };
        lineColHBox.AddChild(lineColLabel);
        lineColHBox.AddChild(_xrayLineColorPicker);
        contentVBox.AddChild(lineColHBox);

        var lineOpHBox = new HBoxContainer();
        var lineOpLabel = new Label { Text = "Line Opacity", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _xrayLineOpacitySlider = new HSlider { MinValue = 0.05, MaxValue = 1.0, Step = 0.05, CustomMinimumSize = new Vector2(100, 0) };
        _xrayLineOpacityLabel = new Label { CustomMinimumSize = new Vector2(40, 0), HorizontalAlignment = HorizontalAlignment.Right };
        lineOpHBox.AddChild(lineOpLabel);
        lineOpHBox.AddChild(_xrayLineOpacitySlider);
        lineOpHBox.AddChild(_xrayLineOpacityLabel);
        contentVBox.AddChild(lineOpHBox);

        contentVBox.AddChild(new HSeparator());

        var handlesHeader = new Label { Text = "Bone Control Handles" };
        handlesHeader.AddThemeColorOverride("font_color", new Color(0.9f, 0.9f, 0.9f));
        contentVBox.AddChild(handlesHeader);

        var primColHBox = new HBoxContainer();
        var primColLabel = new Label { Text = "Primary Bones Color", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _bonePrimaryColorPicker = new ColorPickerButton { CustomMinimumSize = new Vector2(60, 24) };
        primColHBox.AddChild(primColLabel);
        primColHBox.AddChild(_bonePrimaryColorPicker);
        contentVBox.AddChild(primColHBox);

        var clothColHBox = new HBoxContainer();
        var clothColLabel = new Label { Text = "Clothing Bones Color", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _boneClothingColorPicker = new ColorPickerButton { CustomMinimumSize = new Vector2(60, 24) };
        clothColHBox.AddChild(clothColLabel);
        clothColHBox.AddChild(_boneClothingColorPicker);
        contentVBox.AddChild(clothColHBox);

        var handleOpHBox = new HBoxContainer();
        var handleOpLabel = new Label { Text = "Handle Opacity", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _boneMarkerOpacitySlider = new HSlider { MinValue = 0.1, MaxValue = 1.0, Step = 0.05, CustomMinimumSize = new Vector2(100, 0) };
        _boneMarkerOpacityLabel = new Label { CustomMinimumSize = new Vector2(40, 0), HorizontalAlignment = HorizontalAlignment.Right };
        handleOpHBox.AddChild(handleOpLabel);
        handleOpHBox.AddChild(_boneMarkerOpacitySlider);
        handleOpHBox.AddChild(_boneMarkerOpacityLabel);
        contentVBox.AddChild(handleOpHBox);

        var handleSizeHBox = new HBoxContainer();
        var handleSizeLabel = new Label { Text = "Handle Size Scale", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _boneMarkerSizeSlider = new HSlider { MinValue = 0.2, MaxValue = 3.0, Step = 0.1, CustomMinimumSize = new Vector2(100, 0) };
        _boneMarkerSizeLabel = new Label { CustomMinimumSize = new Vector2(40, 0), HorizontalAlignment = HorizontalAlignment.Right };
        handleSizeHBox.AddChild(handleSizeLabel);
        handleSizeHBox.AddChild(_boneMarkerSizeSlider);
        handleSizeHBox.AddChild(_boneMarkerSizeLabel);
        contentVBox.AddChild(handleSizeHBox);

        contentVBox.AddChild(new HSeparator());

        _btnResetDisplaySettings = new Button { Text = "Reset Display Defaults" };
        contentVBox.AddChild(_btnResetDisplaySettings);
    }

    private void SyncDisplaySettingsValues()
    {
        if (_xrayLineColorPicker != null) _xrayLineColorPicker.Color = GizmoDisplaySettings.XRayLineColor;
        if (_xrayLineOpacitySlider != null)
        {
            _xrayLineOpacitySlider.Value = GizmoDisplaySettings.XRayLineOpacity;
            if (_xrayLineOpacityLabel != null) _xrayLineOpacityLabel.Text = $"{(int)(GizmoDisplaySettings.XRayLineOpacity * 100)}%";
        }

        if (_bonePrimaryColorPicker != null) _bonePrimaryColorPicker.Color = GizmoDisplaySettings.BonePrimaryColor;
        if (_boneClothingColorPicker != null) _boneClothingColorPicker.Color = GizmoDisplaySettings.BoneClothingColor;

        if (_boneMarkerOpacitySlider != null)
        {
            _boneMarkerOpacitySlider.Value = GizmoDisplaySettings.BoneMarkerOpacity;
            if (_boneMarkerOpacityLabel != null) _boneMarkerOpacityLabel.Text = $"{(int)(GizmoDisplaySettings.BoneMarkerOpacity * 100)}%";
        }

        if (_boneMarkerSizeSlider != null)
        {
            _boneMarkerSizeSlider.Value = GizmoDisplaySettings.BoneMarkerScale;
            if (_boneMarkerSizeLabel != null) _boneMarkerSizeLabel.Text = $"{GizmoDisplaySettings.BoneMarkerScale:F1}x";
        }
    }

    private void ConnectDisplaySettingsEvents()
    {
        if (_xrayLineColorPicker != null)
        {
            _xrayLineColorPicker.ColorChanged += (c) =>
            {
                GizmoDisplaySettings.XRayLineColor = c;
                GizmoDisplaySettings.NotifyChanged();
            };
        }

        if (_xrayLineOpacitySlider != null)
        {
            _xrayLineOpacitySlider.ValueChanged += (v) =>
            {
                GizmoDisplaySettings.XRayLineOpacity = (float)v;
                if (_xrayLineOpacityLabel != null) _xrayLineOpacityLabel.Text = $"{(int)(v * 100)}%";
                GizmoDisplaySettings.NotifyChanged();
            };
        }

        if (_bonePrimaryColorPicker != null)
        {
            _bonePrimaryColorPicker.ColorChanged += (c) =>
            {
                GizmoDisplaySettings.BonePrimaryColor = c;
                GizmoDisplaySettings.NotifyChanged();
            };
        }

        if (_boneClothingColorPicker != null)
        {
            _boneClothingColorPicker.ColorChanged += (c) =>
            {
                GizmoDisplaySettings.BoneClothingColor = c;
                GizmoDisplaySettings.NotifyChanged();
            };
        }

        if (_boneMarkerOpacitySlider != null)
        {
            _boneMarkerOpacitySlider.ValueChanged += (v) =>
            {
                GizmoDisplaySettings.BoneMarkerOpacity = (float)v;
                if (_boneMarkerOpacityLabel != null) _boneMarkerOpacityLabel.Text = $"{(int)(v * 100)}%";
                GizmoDisplaySettings.NotifyChanged();
            };
        }

        if (_boneMarkerSizeSlider != null)
        {
            _boneMarkerSizeSlider.ValueChanged += (v) =>
            {
                GizmoDisplaySettings.BoneMarkerScale = (float)v;
                if (_boneMarkerSizeLabel != null) _boneMarkerSizeLabel.Text = $"{v:F1}x";
                GizmoDisplaySettings.NotifyChanged();
            };
        }

        if (_btnResetDisplaySettings != null)
        {
            _btnResetDisplaySettings.Pressed += () =>
            {
                GizmoDisplaySettings.ResetToDefaults();
                SyncDisplaySettingsValues();
            };
        }
    }
}