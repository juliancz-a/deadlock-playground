using Godot;
using System;
using System.Collections.Generic;

public partial class UIManager : CanvasLayer
{
    // Señales para desacoplar la UI de la lógica de carga 3D y cámara
    [Signal] public delegate void CharacterSelectedEventHandler(string heroInternalName);
    [Signal] public delegate void BackgroundSelectedEventHandler(string backgroundId);
    [Signal] public delegate void ScreenshotRequestedEventHandler(int resolutionMultiplier, bool transparentBg);
    [Signal] public delegate void GamePathChangedEventHandler(string newPath);

    // Controles de SideBar
    private OptionButton _charOption;
    private OptionButton _bgOption;
    private Button _btnScreenshot;
    private Button _btnAbout;

    // Controles de TopBar
    private Button _btnSettings;

    // Modales y Diálogos
    private PanelContainer _settingsModal;
    private PanelContainer _aboutModal;
    private PanelContainer _loadingOverlay;

    private Button _btnAboutClose;
    private Button _btnSettingsClose;

    private FileDialog _gameFolderDialog;
    private FileDialog _saveFolderDialog;

    // Controles de Configuración
    private OptionButton _langOption;
    private LineEdit _gamePathInput;
    private LineEdit _savePathInput;
    private OptionButton _resOption;
    private OptionButton _texQualityOption;
    private CheckBox _transparentBgCheck;
    private CheckBox _jpgFormatCheck;

    // Gestores
    private GamePathManager _pathManager;
    private ScreenshotHelper _screenshotHelper;
    private AcceptDialog _fallbackPathDialog;
    private PanelContainer _toastPanel;
    private Label _toastLabel;
    private Button _toastBtn;
    private Godot.Timer _toastTimer;

    // Mapeo de personajes: Nombre legible en UI -> Identificador de carpeta/modelo
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
            envNode.Environment.BackgroundCanvasMaxLayer = -1; // Only draw canvas layers below 0 as background

            // Keep the Sky lighting active even though it's no longer drawn as the background
            envNode.Environment.AmbientLightSource = Godot.Environment.AmbientSource.Sky;
            envNode.Environment.ReflectedLightSource = Godot.Environment.ReflectionSource.Sky;
        }

        // Instanciar Gestores
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

        // Carga inicial y auto-detección
        InitializePaths();
    }


    private void LinkNodes()
    {
        // SideBar
        _charOption = GetNode<OptionButton>("MainHUD/SideBar/VBoxContainer/CharacterButton");
        _bgOption = GetNode<OptionButton>("MainHUD/SideBar/VBoxContainer/BGButton");
        _btnScreenshot = GetNode<Button>("MainHUD/SideBar/VBoxContainer/SSButton");
        _btnAbout = GetNode<Button>("MainHUD/SideBar/VBoxContainer/AboutButton");

        // TopBar
        _btnSettings = GetNode<Button>("MainHUD/TopBar/HBoxContainer/SettingsButton");

        // Modals y Popups
        _settingsModal = GetNode<PanelContainer>("MainHUD/ModalsLayer/SettingsModal");
        _aboutModal = GetNode<PanelContainer>("MainHUD/ModalsLayer/AboutModal");
        _loadingOverlay = GetNodeOrNull<PanelContainer>("MainHUD/ModalsLayer/LoadingOverlay");

        _btnAboutClose = GetNodeOrNull<Button>("MainHUD/ModalsLayer/AboutModal/MarginContainer/VBoxContainer/Button");
        // We assume the user creates a CloseButton for Settings as instructed
        _btnSettingsClose = GetNodeOrNull<Button>("MainHUD/ModalsLayer/SettingsModal/VBoxContainer/CloseButton");

        _gameFolderDialog = GetNodeOrNull<FileDialog>("MainHUD/ModalsLayer/GameFolderDialog");
        if (_gameFolderDialog == null)
        {
            _gameFolderDialog = new FileDialog();
            _gameFolderDialog.FileMode = FileDialog.FileModeEnum.OpenDir;
            _gameFolderDialog.Title = "Select Game Folder";
            _gameFolderDialog.Access = FileDialog.AccessEnum.Filesystem;
            GetNode("MainHUD/ModalsLayer").AddChild(_gameFolderDialog);
        }

        _saveFolderDialog = GetNodeOrNull<FileDialog>("MainHUD/ModalsLayer/ScreenshotFolderDialog");
        if (_saveFolderDialog == null)
        {
            _saveFolderDialog = new FileDialog();
            _saveFolderDialog.FileMode = FileDialog.FileModeEnum.OpenDir;
            _saveFolderDialog.Title = "Select Screenshots Folder";
            _saveFolderDialog.Access = FileDialog.AccessEnum.Filesystem;
            GetNode("MainHUD/ModalsLayer").AddChild(_saveFolderDialog);
        }

        // Controles de Configuración
        _langOption = GetNode<OptionButton>("MainHUD/ModalsLayer/SettingsModal/VBoxContainer/TabContainer/General/VBoxContainer/LanguageButton");
        _gamePathInput = GetNode<LineEdit>("MainHUD/ModalsLayer/SettingsModal/VBoxContainer/TabContainer/General/VBoxContainer/GamePathSearchContainer/GamePathLine");
        _savePathInput = GetNode<LineEdit>("MainHUD/ModalsLayer/SettingsModal/VBoxContainer/TabContainer/General/VBoxContainer/SavesPathSearchContainer/SavesPathLine");
        _resOption = GetNode<OptionButton>("MainHUD/ModalsLayer/SettingsModal/VBoxContainer/TabContainer/Graphics/VBoxContainer/ScreenshotResButton");
        _texQualityOption = GetNode<OptionButton>("MainHUD/ModalsLayer/SettingsModal/VBoxContainer/TabContainer/Graphics/VBoxContainer/TexQualityButton");
        
        var cameraVBox = GetNode<VBoxContainer>("MainHUD/ModalsLayer/SettingsModal/VBoxContainer/TabContainer/Camera/VBoxContainer");
        _transparentBgCheck = cameraVBox.GetNode<CheckBox>("TransparentBgCheck");
        
        _jpgFormatCheck = new CheckBox();
        _jpgFormatCheck.Text = "Save as JPG format (forces background on)";
        cameraVBox.AddChild(_jpgFormatCheck);
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

        // Estilos
        var style = new StyleBoxFlat();
        style.BgColor = new Color(0.1f, 0.1f, 0.1f, 0.9f);
        style.CornerRadiusTopLeft = 8;
        style.CornerRadiusTopRight = 8;
        style.CornerRadiusBottomLeft = 8;
        style.CornerRadiusBottomRight = 8;
        _toastPanel.AddThemeStyleboxOverride("panel", style);

        // Posicionar abajo al centro (usaremos anclas luego)
        _toastPanel.SetAnchorsPreset(Control.LayoutPreset.CenterBottom);
        _toastPanel.Position = new Vector2(_toastPanel.Position.X, -50); // Offset temporal

        GetNode("MainHUD").AddChild(_toastPanel);
        
        // Timer de auto-cierre
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
        _fallbackPathDialog.Exclusive = true; // Bloquea hasta que se cierre
        
        // Queremos que al dar OK, abra el selector de archivos.
        _fallbackPathDialog.Confirmed += () => 
        {
            _gameFolderDialog.PopupCentered();
        };

        GetNode("MainHUD/ModalsLayer").AddChild(_fallbackPathDialog);
    }

    private async void InitializePaths()
    {
        ShowLoading(true);
        GetNode<Label>("MainHUD/ModalsLayer/LoadingOverlay/Label").Text = "Detecting Deadlock path...";

        // Si la ruta no es válida o está vacía
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
                _fallbackPathDialog.PopupCentered();
                return; // Esperamos al usuario
            }
        }

        UpdateVpkLoaderPath(_pathManager.CurrentGamePath);
        _gamePathInput.Text = _pathManager.CurrentGamePath;
        _savePathInput.Text = _pathManager.CurrentSavePath;
        ShowLoading(false);
        GetNode<Label>("MainHUD/ModalsLayer/LoadingOverlay/Label").Text = "Loading character model...";
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
        _charOption.Clear();
        _charOption.AddItem("--- Select a Hero ---", -1);
        _charOption.SetItemDisabled(0, true);

        for (int i = 0; i < _characters.Count; i++)
        {
            // The item index in OptionButton will be i + 1
            _charOption.AddItem(_characters[i].DisplayName, i);
        }

        // Ensure the placeholder is selected by default
        _charOption.Select(0);

        // 2. Selector de Fondos
        _bgOption.Clear();
        for (int i = 0; i < _backgrounds.Count; i++)
        {
            _bgOption.AddItem(_backgrounds[i].DisplayName, i);
        }

        // 3. Opciones de Idioma
        _langOption.Clear();
        _langOption.AddItem("Español", 0);
        _langOption.AddItem("English", 1);

        // 4. Resoluciones de Captura
        _resOption.Clear();
        _resOption.AddItem("1080p", 1);
        _resOption.AddItem("1440p", 2);
        _resOption.AddItem("2160p", 4);

        // 5. Calidad de Mipmaps
        _texQualityOption.Clear();
        _texQualityOption.AddItem("High (1024px)", 0);
        _texQualityOption.AddItem("Medium (512px)", 1);
        _texQualityOption.AddItem("Low (256px)", 2);
    }

    private void ConnectEvents()
    {
        // Selección en SideBar
        _charOption.ItemSelected += OnCharacterSelected;
        _bgOption.ItemSelected += OnBackgroundSelected;
        _btnScreenshot.Pressed += OnScreenshotPressed;

        // Modals
        if (_btnSettings != null) _btnSettings.Pressed += () => _settingsModal.Visible = true;
        if (_btnAbout != null) _btnAbout.Pressed += () => _aboutModal.Visible = true;
        if (_btnAboutClose != null) _btnAboutClose.Pressed += () => _aboutModal.Visible = false;
        if (_btnSettingsClose != null) _btnSettingsClose.Pressed += () => _settingsModal.Visible = false;

        // Botones "Buscar..." en Settings
        GetNode<Button>("MainHUD/ModalsLayer/SettingsModal/VBoxContainer/TabContainer/General/VBoxContainer/GamePathSearchContainer/BrowseGamePathButton").Pressed += 
            () => _gameFolderDialog.PopupCentered();
            
        GetNode<Button>("MainHUD/ModalsLayer/SettingsModal/VBoxContainer/TabContainer/General/VBoxContainer/SavesPathSearchContainer/BrowseSavePathButton").Pressed += 
            () => _saveFolderDialog.PopupCentered();

        _gameFolderDialog.DirSelected += OnGameDirSelected;
        _saveFolderDialog.DirSelected += OnSaveDirSelected;

        var loader = GetNodeOrNull<VpkLoaderTest>("/root/Main/VpkLoaderTest");
        if (loader != null)
        {
            loader.LoadStarted += () => ShowLoading(true);
            loader.LoadFinished += () => ShowLoading(false);
        }
    }

    private void OnCharacterSelected(long index)
    {
        int id = _charOption.GetItemId((int)index);
        if (id < 0 || id >= _characters.Count) return;

        string internalId = _characters[id].InternalId;
        GD.Print($"[UI] Héroe seleccionado: {_characters[id].DisplayName} ({internalId})");
        EmitSignal(SignalName.CharacterSelected, internalId);

        var loader = GetNodeOrNull<VpkLoaderTest>("/root/Main/VpkLoaderTest");
        if (loader != null)
        {
            // We use the same name for hero and model by default 
            _ = loader.LoadHeroAsync(internalId, internalId);
        }
    }

    private void OnBackgroundSelected(long index)
    {
        string bgId = _backgrounds[(int)index].BgId;
        GD.Print($"[UI] Fondo seleccionado: {bgId}");
        EmitSignal(SignalName.BackgroundSelected, bgId);

        // Simple implementation to switch background texture if a TextureRect named BackgroundRect exists
        var bgRect = GetNodeOrNull<TextureRect>("BackgroundRect");
        if (bgRect != null)
        {
            if (bgId == "classic_menu")
            {
                bgRect.Texture = GD.Load<Texture2D>("res://assets/backgrounds/classic_menu_bg.png");
            }
        }
    }

    private void OnScreenshotPressed()
    {
        int multiplier = _resOption.GetSelectedId();
        bool transparent = _transparentBgCheck.ButtonPressed;
        bool useJpg = _jpgFormatCheck.ButtonPressed;
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

        if (camera != null)
        {
            _ = _screenshotHelper.CaptureAsync(camera, multiplier, transparent, useJpg, _pathManager.CurrentSavePath, gizmo);
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
        _toastLabel.Text = $"Screenshot saved:\n{path}";
        
        // Desconectar eventos previos si existen
        var connections = _toastBtn.GetSignalConnectionList(Button.SignalName.Pressed);
        foreach (var conn in connections)
        {
            _toastBtn.Disconnect(Button.SignalName.Pressed, conn["callable"].AsCallable());
        }

        _toastBtn.Pressed += () => 
        {
            string dir = System.IO.Path.GetDirectoryName(path);
            OS.ShellOpen(dir);
        };

        // Layout hack para forzar el centro inferior con margen en Godot UI
        _toastPanel.SetAnchorsPreset(Control.LayoutPreset.CenterBottom);
        _toastPanel.Position = new Vector2(GetViewport().GetVisibleRect().Size.X / 2f - _toastPanel.Size.X / 2f, GetViewport().GetVisibleRect().Size.Y - _toastPanel.Size.Y - 20);

        _toastPanel.Visible = true;
        _toastTimer.Start();
    }

    private void OnGameDirSelected(string dir)
    {
        if (_pathManager.ValidateDeadlockPath(dir))
        {
            _gamePathInput.Text = dir;
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
            
            // Si estábamos en el fallback y el usuario falló, volvemos a mostrar el fallback
            if (string.IsNullOrEmpty(_pathManager.CurrentGamePath))
            {
                alert.Confirmed += () => _fallbackPathDialog.PopupCentered();
            }
        }
    }

    private void OnSaveDirSelected(string dir)
    {
        _savePathInput.Text = dir;
        _pathManager.SaveConfig(_pathManager.CurrentGamePath, dir);
    }

    public void ShowLoading(bool show)
    {
        if (_loadingOverlay != null)
        {
            _loadingOverlay.Visible = show;
        }
    }
}