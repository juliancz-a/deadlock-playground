using Godot;
using System;
using System.IO;

public partial class SettingsDialog : PanelContainer
{
    [Signal] public delegate void DialogClosedEventHandler();

    // Top Bar
    private Button _btnClose;

    // Tabs
    private TabContainer _tabContainer;

    // General Tab Controls
    private OptionButton _langOption;
    private LineEdit _gamePathInput;
    private Button _btnBrowseGamePath;
    private LineEdit _savePathInput;
    private Button _btnBrowseSavePath;
    private LineEdit _compilerPathInput;
    private Button _btnBrowseCompilerPath;
    private Button _btnResetGeneral;

    // FileDialogs for General Paths
    private FileDialog _gameFolderDialog;
    private FileDialog _saveFolderDialog;
    private FileDialog _compilerFileDialog;

    // Graphics Tab Controls
    private OptionButton _optMsaa;
    private OptionButton _optShadowQuality;
    private CheckBox _chkShowStudioBg;
    private HSlider _sliderFpsLimit;
    private Label _lblFpsLimit;
    private CheckBox _chkVsync;
    private Button _btnResetGraphics;

    // UI Tab Controls
    private ColorPickerButton _xrayLineColorPicker;
    private HSlider _xrayLineOpacitySlider;
    private Label _xrayLineOpacityLabel;
    private ColorPickerButton _bonePrimaryColorPicker;
    private ColorPickerButton _boneClothingColorPicker;
    private HSlider _boneMarkerOpacitySlider;
    private Label _boneMarkerOpacityLabel;
    private HSlider _boneMarkerSizeSlider;
    private Label _boneMarkerSizeLabel;
    private HSlider _ikHandlesOpacitySlider;
    private Label _ikHandlesOpacityLabel;
    private ColorPickerButton _painterOutlineColorPicker;
    private HSlider _painterOutlineOpacitySlider;
    private Label _painterOutlineOpacityLabel;
    private HSlider _painterOutlineWidthSlider;
    private Label _painterOutlineWidthLabel;
    private Button _btnResetDisplaySettings;

    // Single reusable confirmation dialog for all resets
    private ConfirmationDialog _confirmDialog;
    private Action _pendingConfirmAction;

    // Dependencies
    private GamePathManager _pathManager;

    public override void _Ready()
    {
        LinkControls();
        SetupFileDialogs();
        ConnectEvents();
        SyncFromSettings();
        OnTabChanged(_tabContainer?.CurrentTab ?? 0);
    }

    public void Setup(GamePathManager pathManager)
    {
        _pathManager = pathManager;
        UpdatePathFields();
    }

    private void LinkControls()
    {
        _btnClose = GetNodeOrNull<Button>("VBoxContainer/HeaderBar/BtnClose");
        _tabContainer = GetNodeOrNull<TabContainer>("VBoxContainer/TabContainer");

        // General
        _langOption = GetNodeOrNull<OptionButton>("VBoxContainer/TabContainer/General/VBox/LangRow/LanguageButton");
        _gamePathInput = GetNodeOrNull<LineEdit>("VBoxContainer/TabContainer/General/VBox/GamePathRow/HBox/GamePathLine");
        _btnBrowseGamePath = GetNodeOrNull<Button>("VBoxContainer/TabContainer/General/VBox/GamePathRow/HBox/BrowseGamePathButton");
        _savePathInput = GetNodeOrNull<LineEdit>("VBoxContainer/TabContainer/General/VBox/SavePathRow/HBox/SavesPathLine");
        _btnBrowseSavePath = GetNodeOrNull<Button>("VBoxContainer/TabContainer/General/VBox/SavePathRow/HBox/BrowseSavePathButton");
        _compilerPathInput = GetNodeOrNull<LineEdit>("VBoxContainer/TabContainer/General/VBox/CompilerPathRow/HBox/CompilerPathLine");
        _btnBrowseCompilerPath = GetNodeOrNull<Button>("VBoxContainer/TabContainer/General/VBox/CompilerPathRow/HBox/BrowseCompilerPathButton");
        _btnResetGeneral = GetNodeOrNull<Button>("VBoxContainer/TabContainer/General/VBox/BtnResetGeneral");

        // Graphics
        _optMsaa = GetNodeOrNull<OptionButton>("VBoxContainer/TabContainer/Graphics/VBox/MsaaRow/OptMsaa");
        _optShadowQuality = GetNodeOrNull<OptionButton>("VBoxContainer/TabContainer/Graphics/VBox/ShadowRow/OptShadowQuality");
        _chkShowStudioBg = GetNodeOrNull<CheckBox>("VBoxContainer/TabContainer/Graphics/VBox/ShowBgRow/ChkShowStudioBg");
        _sliderFpsLimit = GetNodeOrNull<HSlider>("VBoxContainer/TabContainer/Graphics/VBox/FpsRow/HBox/SliderFpsLimit");
        _lblFpsLimit = GetNodeOrNull<Label>("VBoxContainer/TabContainer/Graphics/VBox/FpsRow/HBox/LblFpsLimit");
        _chkVsync = GetNodeOrNull<CheckBox>("VBoxContainer/TabContainer/Graphics/VBox/VsyncRow/ChkVsync");
        _btnResetGraphics = GetNodeOrNull<Button>("VBoxContainer/TabContainer/Graphics/VBox/BtnResetGraphics");

        // UI
        _xrayLineColorPicker = GetNodeOrNull<ColorPickerButton>("VBoxContainer/TabContainer/UI/Scroll/VBox/HBoxContainerLineColor/ColorPickerButton");
        _xrayLineOpacitySlider = GetNodeOrNull<HSlider>("VBoxContainer/TabContainer/UI/Scroll/VBox/HBoxContainerLineOpacity/HSlider");
        _xrayLineOpacityLabel = GetNodeOrNull<Label>("VBoxContainer/TabContainer/UI/Scroll/VBox/HBoxContainerLineOpacity/LabelCurrOpacity");
        _bonePrimaryColorPicker = GetNodeOrNull<ColorPickerButton>("VBoxContainer/TabContainer/UI/Scroll/VBox/HBoxContainerPrimary/ColorPickerButton");
        _boneClothingColorPicker = GetNodeOrNull<ColorPickerButton>("VBoxContainer/TabContainer/UI/Scroll/VBox/HBoxContainerClothing/ColorPickerButton");
        _boneMarkerOpacitySlider = GetNodeOrNull<HSlider>("VBoxContainer/TabContainer/UI/Scroll/VBox/HBoxContainerBoneOpacity/HSlider");
        _boneMarkerOpacityLabel = GetNodeOrNull<Label>("VBoxContainer/TabContainer/UI/Scroll/VBox/HBoxContainerBoneOpacity/LabelCurrOpacity");
        _boneMarkerSizeSlider = GetNodeOrNull<HSlider>("VBoxContainer/TabContainer/UI/Scroll/VBox/HBoxContainerBoneSize/HSlider");
        _boneMarkerSizeLabel = GetNodeOrNull<Label>("VBoxContainer/TabContainer/UI/Scroll/VBox/HBoxContainerBoneSize/LabelCurrSize");
        _ikHandlesOpacitySlider = GetNodeOrNull<HSlider>("VBoxContainer/TabContainer/UI/Scroll/VBox/HBoxContainerIKOpacity/HSlider");
        _ikHandlesOpacityLabel = GetNodeOrNull<Label>("VBoxContainer/TabContainer/UI/Scroll/VBox/HBoxContainerIKOpacity/LabelCurrOpacity");
        _painterOutlineColorPicker = GetNodeOrNull<ColorPickerButton>("VBoxContainer/TabContainer/UI/Scroll/VBox/HBoxContainerPainterColor/ColorPickerButton");
        _painterOutlineOpacitySlider = GetNodeOrNull<HSlider>("VBoxContainer/TabContainer/UI/Scroll/VBox/HBoxContainerPainterOpacity/HSlider");
        _painterOutlineOpacityLabel = GetNodeOrNull<Label>("VBoxContainer/TabContainer/UI/Scroll/VBox/HBoxContainerPainterOpacity/LabelCurrOpacity");
        _painterOutlineWidthSlider = GetNodeOrNull<HSlider>("VBoxContainer/TabContainer/UI/Scroll/VBox/HBoxContainerPainterWidth/HSlider");
        _painterOutlineWidthLabel = GetNodeOrNull<Label>("VBoxContainer/TabContainer/UI/Scroll/VBox/HBoxContainerPainterWidth/LabelCurrWidth");
        _btnResetDisplaySettings = GetNodeOrNull<Button>("VBoxContainer/TabContainer/UI/Scroll/VBox/ResetButton");
    }

    private void SetupFileDialogs()
    {
        _gameFolderDialog = new FileDialog
        {
            Title = "Select Deadlock Game Directory (root containing 'game')",
            FileMode = FileDialog.FileModeEnum.OpenDir,
            Access = FileDialog.AccessEnum.Filesystem,
            Size = new Vector2I(750, 500)
        };
        _gameFolderDialog.DirSelected += OnGameDirSelected;
        AddChild(_gameFolderDialog);

        _saveFolderDialog = new FileDialog
        {
            Title = "Select Screenshots / Saves Directory",
            FileMode = FileDialog.FileModeEnum.OpenDir,
            Access = FileDialog.AccessEnum.Filesystem,
            Size = new Vector2I(750, 500)
        };
        _saveFolderDialog.DirSelected += OnSaveDirSelected;
        AddChild(_saveFolderDialog);

        _compilerFileDialog = new FileDialog
        {
            Title = "Select resourcecompiler.exe",
            FileMode = FileDialog.FileModeEnum.OpenFile,
            Access = FileDialog.AccessEnum.Filesystem,
            Filters = new[] { "resourcecompiler.exe, *.exe ; Executables", "*.* ; All Files" },
            Size = new Vector2I(750, 500)
        };
        _compilerFileDialog.FileSelected += OnCompilerFileSelected;
        AddChild(_compilerFileDialog);
    }

    private void ConnectEvents()
    {
        if (_btnClose != null) _btnClose.Pressed += Close;

        if (_tabContainer != null)
        {
            _tabContainer.TabChanged += OnTabChanged;
        }

        // General
        if (_btnBrowseGamePath != null) _btnBrowseGamePath.Pressed += () => _gameFolderDialog.PopupCentered();
        if (_btnBrowseSavePath != null) _btnBrowseSavePath.Pressed += () => _saveFolderDialog.PopupCentered();
        if (_btnBrowseCompilerPath != null) _btnBrowseCompilerPath.Pressed += () => _compilerFileDialog.PopupCentered();

        if (_compilerPathInput != null)
        {
            _compilerPathInput.TextChanged += (txt) =>
            {
                if (File.Exists(txt)) _pathManager?.SaveResourceCompilerPath(txt);
            };
        }

        if (_btnResetGeneral != null)
        {
            _btnResetGeneral.Pressed += () =>
            {
                ShowConfirmDialog(
                    "Reset General Settings?",
                    "Reset configured Deadlock game directory and export paths back to default?",
                    () =>
                    {
                        _pathManager?.SaveConfig("", "");
                        _pathManager?.SaveResourceCompilerPath("");
                        UpdatePathFields();
                    });
            };
        }

        // Graphics
        if (_optMsaa != null)
        {
            _optMsaa.Clear();
            _optMsaa.AddItem("Disabled", 0);
            _optMsaa.AddItem("2x MSAA", 1);
            _optMsaa.AddItem("4x MSAA", 2);
            _optMsaa.AddItem("8x MSAA", 3);
            _optMsaa.ItemSelected += (idx) => UserSettings.Msaa3D = (int)idx;
        }

        if (_optShadowQuality != null)
        {
            _optShadowQuality.Clear();
            _optShadowQuality.AddItem("Off", 0);
            _optShadowQuality.AddItem("Low (Soft)", 1);
            _optShadowQuality.AddItem("High (Detailed)", 2);
            _optShadowQuality.ItemSelected += (idx) => UserSettings.ShadowQuality = (int)idx;
        }

        if (_chkShowStudioBg != null)
        {
            _chkShowStudioBg.Toggled += (enabled) => UserSettings.ShowStudioBackground = enabled;
        }

        if (_sliderFpsLimit != null)
        {
            _sliderFpsLimit.ValueChanged += (v) =>
            {
                int fps = (int)v;
                UserSettings.MaxFps = (fps >= 245) ? 0 : fps;
                UpdateFpsLabel(fps);
            };
        }

        if (_chkVsync != null)
        {
            _chkVsync.Toggled += (enabled) => UserSettings.VSync = enabled;
        }

        if (_btnResetGraphics != null)
        {
            _btnResetGraphics.Pressed += () =>
            {
                ShowConfirmDialog(
                    "Reset Graphics Settings?",
                    "Reset MSAA, shadow quality, background, and performance settings to default values?",
                    () =>
                    {
                        UserSettings.ResetGraphicsDefaults();
                        SyncFromSettings();
                    });
            };
        }

        // UI Tab
        InitDisplaySettingsEvents();
    }

    private void ShowConfirmDialog(string title, string message, Action onConfirm)
    {
        if (_confirmDialog == null)
        {
            _confirmDialog = new ConfirmationDialog
            {
                Title = title,
                DialogText = message,
                OkButtonText = "Reset",
                CancelButtonText = "Cancel"
            };
            _confirmDialog.Confirmed += () =>
            {
                _pendingConfirmAction?.Invoke();
                _pendingConfirmAction = null;
            };
            AddChild(_confirmDialog);
        }
        else
        {
            _confirmDialog.Title = title;
            _confirmDialog.DialogText = message;
        }
        _pendingConfirmAction = onConfirm;
        _confirmDialog.PopupCentered();
    }

    private void UpdateFpsLabel(int fps)
    {
        if (_lblFpsLimit != null)
        {
            _lblFpsLimit.Text = (fps >= 245 || fps == 0) ? "Uncapped" : $"{fps} FPS";
        }
    }

    private void SyncFromSettings()
    {
        // Graphics
        if (_optMsaa != null) _optMsaa.Select(Math.Clamp(UserSettings.Msaa3D, 0, 3));
        if (_optShadowQuality != null) _optShadowQuality.Select(Math.Clamp(UserSettings.ShadowQuality, 0, 2));
        if (_chkShowStudioBg != null) _chkShowStudioBg.ButtonPressed = UserSettings.ShowStudioBackground;

        int fps = UserSettings.MaxFps;
        if (_sliderFpsLimit != null)
        {
            _sliderFpsLimit.Value = (fps == 0) ? 245 : fps;
        }
        UpdateFpsLabel(fps);

        if (_chkVsync != null) _chkVsync.ButtonPressed = UserSettings.VSync;

        // UI
        SyncDisplaySettingsUI();
    }

    private void UpdatePathFields()
    {
        if (_pathManager == null) return;
        if (_gamePathInput != null) _gamePathInput.Text = _pathManager.CurrentGamePath ?? "";
        if (_savePathInput != null) _savePathInput.Text = _pathManager.CurrentSavePath ?? "";
        if (_compilerPathInput != null) _compilerPathInput.Text = _pathManager.ResolveResourceCompilerExe() ?? "";
    }

    private void OnGameDirSelected(string dir)
    {
        if (_pathManager != null && _pathManager.ValidateDeadlockPath(dir))
        {
            _pathManager.SaveConfig(dir, _pathManager.CurrentSavePath);
            if (_gamePathInput != null) _gamePathInput.Text = dir;
        }
    }

    private void OnSaveDirSelected(string dir)
    {
        if (_pathManager != null)
        {
            _pathManager.SaveConfig(_pathManager.CurrentGamePath, dir);
            if (_savePathInput != null) _savePathInput.Text = dir;
        }
    }

    private void OnCompilerFileSelected(string file)
    {
        if (_pathManager != null)
        {
            _pathManager.SaveResourceCompilerPath(file);
            if (_compilerPathInput != null) _compilerPathInput.Text = file;
        }
    }

    private void InitDisplaySettingsEvents()
    {
        if (_xrayLineColorPicker != null)
            _xrayLineColorPicker.ColorChanged += (c) => GizmoDisplaySettings.XRayLineColor = c;

        if (_xrayLineOpacitySlider != null)
        {
            _xrayLineOpacitySlider.ValueChanged += (v) =>
            {
                GizmoDisplaySettings.XRayLineOpacity = (float)v;
                if (_xrayLineOpacityLabel != null) _xrayLineOpacityLabel.Text = $"{v:P0}";
            };
        }

        if (_bonePrimaryColorPicker != null)
            _bonePrimaryColorPicker.ColorChanged += (c) => GizmoDisplaySettings.BonePrimaryColor = c;

        if (_boneClothingColorPicker != null)
            _boneClothingColorPicker.ColorChanged += (c) => GizmoDisplaySettings.BoneClothingColor = c;

        if (_boneMarkerOpacitySlider != null)
        {
            _boneMarkerOpacitySlider.ValueChanged += (v) =>
            {
                GizmoDisplaySettings.BoneMarkerOpacity = (float)v;
                if (_boneMarkerOpacityLabel != null) _boneMarkerOpacityLabel.Text = $"{v:P0}";
            };
        }

        if (_boneMarkerSizeSlider != null)
        {
            _boneMarkerSizeSlider.ValueChanged += (v) =>
            {
                GizmoDisplaySettings.BoneMarkerScale = (float)v;
                if (_boneMarkerSizeLabel != null) _boneMarkerSizeLabel.Text = $"{v:F1}x";
            };
        }

        if (_ikHandlesOpacitySlider != null)
        {
            _ikHandlesOpacitySlider.ValueChanged += (v) =>
            {
                GizmoDisplaySettings.IKHandlesOpacity = (float)v;
                if (_ikHandlesOpacityLabel != null) _ikHandlesOpacityLabel.Text = $"{v:P0}";
            };
        }

        if (_painterOutlineColorPicker != null)
            _painterOutlineColorPicker.ColorChanged += (c) => GizmoDisplaySettings.PainterOutlineColor = c;

        if (_painterOutlineOpacitySlider != null)
        {
            _painterOutlineOpacitySlider.ValueChanged += (v) =>
            {
                GizmoDisplaySettings.PainterOutlineOpacity = (float)v;
                if (_painterOutlineOpacityLabel != null) _painterOutlineOpacityLabel.Text = $"{v:P0}";
            };
        }

        if (_painterOutlineWidthSlider != null)
        {
            _painterOutlineWidthSlider.ValueChanged += (v) =>
            {
                GizmoDisplaySettings.PainterOutlineWidth = (float)v;
                if (_painterOutlineWidthLabel != null) _painterOutlineWidthLabel.Text = $"{v:F1}px";
            };
        }

        if (_btnResetDisplaySettings != null)
        {
            _btnResetDisplaySettings.Pressed += () =>
            {
                ShowConfirmDialog(
                    "Reset Display Settings?",
                    "Reset all bone marker colors, line opacities, and painter highlight settings to default values?",
                    () =>
                    {
                        GizmoDisplaySettings.ResetToDefaults();
                        SyncDisplaySettingsUI();
                    });
            };
        }
    }

    private void SyncDisplaySettingsUI()
    {
        if (_xrayLineColorPicker != null) _xrayLineColorPicker.Color = GizmoDisplaySettings.XRayLineColor;
        if (_xrayLineOpacitySlider != null) _xrayLineOpacitySlider.Value = GizmoDisplaySettings.XRayLineOpacity;
        if (_xrayLineOpacityLabel != null) _xrayLineOpacityLabel.Text = $"{GizmoDisplaySettings.XRayLineOpacity:P0}";

        if (_bonePrimaryColorPicker != null) _bonePrimaryColorPicker.Color = GizmoDisplaySettings.BonePrimaryColor;
        if (_boneClothingColorPicker != null) _boneClothingColorPicker.Color = GizmoDisplaySettings.BoneClothingColor;
        if (_boneMarkerOpacitySlider != null) _boneMarkerOpacitySlider.Value = GizmoDisplaySettings.BoneMarkerOpacity;
        if (_boneMarkerOpacityLabel != null) _boneMarkerOpacityLabel.Text = $"{GizmoDisplaySettings.BoneMarkerOpacity:P0}";

        if (_boneMarkerSizeSlider != null) _boneMarkerSizeSlider.Value = GizmoDisplaySettings.BoneMarkerScale;
        if (_boneMarkerSizeLabel != null) _boneMarkerSizeLabel.Text = $"{GizmoDisplaySettings.BoneMarkerScale:F1}x";

        if (_ikHandlesOpacitySlider != null) _ikHandlesOpacitySlider.Value = GizmoDisplaySettings.IKHandlesOpacity;
        if (_ikHandlesOpacityLabel != null) _ikHandlesOpacityLabel.Text = $"{GizmoDisplaySettings.IKHandlesOpacity:P0}";

        if (_painterOutlineColorPicker != null) _painterOutlineColorPicker.Color = GizmoDisplaySettings.PainterOutlineColor;
        if (_painterOutlineOpacitySlider != null) _painterOutlineOpacitySlider.Value = GizmoDisplaySettings.PainterOutlineOpacity;
        if (_painterOutlineOpacityLabel != null) _painterOutlineOpacityLabel.Text = $"{GizmoDisplaySettings.PainterOutlineOpacity:P0}";

        if (_painterOutlineWidthSlider != null) _painterOutlineWidthSlider.Value = GizmoDisplaySettings.PainterOutlineWidth;
        if (_painterOutlineWidthLabel != null) _painterOutlineWidthLabel.Text = $"{GizmoDisplaySettings.PainterOutlineWidth:F1}px";
    }

    public void Open()
    {
        Visible = true;
        MoveToFront();
        SyncFromSettings();
        UpdatePathFields();
        OnTabChanged(_tabContainer?.CurrentTab ?? 0);
    }

    public void Close()
    {
        Visible = false;
        EmitSignal(SignalName.DialogClosed);
    }

    private void OnTabChanged(long tabIndex)
    {
        if (_tabContainer == null) return;
        for (int i = 0; i < _tabContainer.GetChildCount(); i++)
        {
            if (_tabContainer.GetChild(i) is Control child)
            {
                child.Visible = (i == tabIndex);
            }
        }
    }
}
