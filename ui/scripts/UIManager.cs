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

    // Campos de Configuración
    private OptionButton _langOption;
    private LineEdit _gamePathInput;
    private LineEdit _savePathInput;
    private OptionButton _resOption;
    private OptionButton _texQualityOption;
    private CheckBox _transparentBgCheck;

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

        LinkNodes();
        PopulateSelectors();
        ConnectEvents();
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

        // _gameFolderDialog = GetNode<FileDialog>("MainHUD/ModalsLayer/GameFolderDialog");
        // _saveFolderDialog = GetNode<FileDialog>("MainHUD/ModalsLayer/ScreenshotFolderDialog");

        // Controles de Configuración
        _langOption = GetNode<OptionButton>("MainHUD/ModalsLayer/SettingsModal/VBoxContainer/TabContainer/General/VBoxContainer/LanguageButton");
        _gamePathInput = GetNode<LineEdit>("MainHUD/ModalsLayer/SettingsModal/VBoxContainer/TabContainer/General/VBoxContainer/GamePathSearchContainer/GamePathLine");
        _savePathInput = GetNode<LineEdit>("MainHUD/ModalsLayer/SettingsModal/VBoxContainer/TabContainer/General/VBoxContainer/SavesPathSearchContainer/SavesPathLine");
        _resOption = GetNode<OptionButton>("MainHUD/ModalsLayer/SettingsModal/VBoxContainer/TabContainer/Graphics/VBoxContainer/ScreenshotResButton");
        _texQualityOption = GetNode<OptionButton>("MainHUD/ModalsLayer/SettingsModal/VBoxContainer/TabContainer/Graphics/VBoxContainer/TexQualityButton");
        _transparentBgCheck = GetNode<CheckBox>("MainHUD/ModalsLayer/SettingsModal/VBoxContainer/TabContainer/Camera/VBoxContainer/TransparentBgCheck");
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
        // GetNode<Button>("MainHUD/ModalsLayer/SettingsModal/TabContainer/General/VBoxContainer/GamePathSearchContainer/BrowseGamePathButton").Pressed += 
        //     () => _gameFolderDialog.PopupCentered();
            
        // GetNode<Button>("MainHUD/ModalsLayer/SettingsModal/TabContainer/General/VBoxContainer/SavesPathSearchContainer/BrowseSavePathButton").Pressed += 
        //     () => _saveFolderDialog.PopupCentered();

        // _gameFolderDialog.DirSelected += OnGameDirSelected;
        // _saveFolderDialog.DirSelected += OnSaveDirSelected;
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
            if (!loader.IsConnected("LoadStarted", Callable.From(() => ShowLoading(true))))
            {
                loader.Connect("LoadStarted", Callable.From(() => ShowLoading(true)));
                loader.Connect("LoadFinished", Callable.From(() => ShowLoading(false)));
            }
            
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
        GD.Print($"[UI] Captura pedida (Mult: {multiplier}x, Alfa: {transparent})");
        EmitSignal(SignalName.ScreenshotRequested, multiplier, transparent);
    }

    private void OnGameDirSelected(string dir)
    {
        _gamePathInput.Text = dir;
        EmitSignal(SignalName.GamePathChanged, dir);
    }

    private void OnSaveDirSelected(string dir)
    {
        _savePathInput.Text = dir;
    }

    public void ShowLoading(bool show)
    {
        if (_loadingOverlay != null)
        {
            _loadingOverlay.Visible = show;
        }
    }
}