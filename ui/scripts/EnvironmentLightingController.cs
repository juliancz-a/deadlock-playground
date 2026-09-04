using Godot;
using System;

public partial class EnvironmentLightingController : MenuButton
{
    [ExportCategory("Top Bar & Navigation")]
    [Export] private MenuButton _menuButton;
    [Export] private Node _dropdownPanel; // PopupPanel (Window) or Control (PanelContainer/FoldableContainer)
    [Export] private ScrollContainer _scrollContainer;

    [ExportCategory("Scene 3D References")]
    [Export] private DirectionalLight3D _sunLight;
    [Export] private WorldEnvironment _worldEnv;
    [Export] private Camera3D _camera;

    [ExportCategory("Directional Sun Light")]
    [Export] private ColorPickerButton _sunColorPicker;
    [Export] private HSlider _sunEnergySlider;
    [Export] private Label _sunEnergyLabel;
    [Export] private HSlider _sunHeadingSlider;
    [Export] private Label _sunHeadingLabel;
    [Export] private HSlider _sunPitchSlider;
    [Export] private Label _sunPitchLabel;

    [ExportCategory("Ambient / Environment Light")]
    [Export] private ColorPickerButton _ambientColorPicker;
    [Export] private HSlider _ambientEnergySlider;
    [Export] private Label _ambientEnergyLabel;

    [ExportCategory("WorldEnvironment - Depth of Field")]
    [Export] private CheckBox _dofFarToggle;
    [Export] private HSlider _dofFarDistanceSlider;
    [Export] private Label _dofFarDistanceLabel;
    [Export] private CheckBox _dofNearToggle;
    [Export] private HSlider _dofNearDistanceSlider;
    [Export] private Label _dofNearDistanceLabel;

    [ExportCategory("WorldEnvironment - Tonemapping")]
    [Export] private OptionButton _tonemapOption;

    [ExportCategory("WorldEnvironment - Fog")]
    [Export] private CheckBox _fogToggle;
    [Export] private HSlider _fogDensitySlider;
    [Export] private Label _fogDensityLabel;
    [Export] private CheckBox _volumetricFogToggle;

    [ExportCategory("WorldEnvironment - Glow / Bloom")]
    [Export] private CheckBox _glowToggle;
    [Export] private HSlider _glowIntensitySlider;
    [Export] private Label _glowIntensityLabel;

    [ExportCategory("Actions")]
    [Export] private Button _btnResetEnvironment;

    // Default cache for reverting/resetting
    private struct LightingDefaults
    {
        public Color SunColor;
        public float SunEnergy;
        public Vector3 SunRotation;

        public Color AmbientColor;
        public float AmbientEnergy;

        public Godot.Environment.ToneMapper TonemapMode;

        public bool FogEnabled;
        public float FogDensity;
        public bool VolumetricFogEnabled;
        public float VolumetricFogDensity;

        public bool GlowEnabled;
        public float GlowIntensity;

        public bool DofFarEnabled;
        public float DofFarDistance;
        public bool DofNearEnabled;
        public float DofNearDistance;
    }

    private LightingDefaults _defaults;
    private CameraAttributesPractical _cameraAttributesPractical;
    private bool _isSyncingUI = false;

    public override void _Ready()
    {
        LinkDependencies();
        InitCameraAttributes();
        CaptureDefaults();
        PopulateTonemapOptions();
        ConnectEvents();
        SyncUIToScene();
    }

    public void OpenDropdown()
    {
        if (_dropdownPanel == null)
        {
            _dropdownPanel = GetNodeOrNull<Node>("EnvironmentPopup")
                          ?? GetNodeOrNull<Node>("PopupPanel")
                          ?? FindChild("EnvironmentPopup", true, false)
                          ?? FindChild("PopupPanel", true, false);
            if (_dropdownPanel == null)
            {
                GD.PrintErr("[EnvironmentLightingController] Dropdown panel not found! Please assign _dropdownPanel in the Inspector or add a PopupPanel child.");
                return;
            }
        }

        SyncUIToScene();

        // Calculate available vertical space below TopBar
        float viewportHeight = GetViewport().GetVisibleRect().Size.Y;
        float topY = GlobalPosition.Y + Size.Y + 4;
        int maxHeight = Mathf.Clamp((int)(viewportHeight - topY - 20), 280, 520);
        int width = 320;

        _scrollContainer ??= (_dropdownPanel as Node)?.GetNodeOrNull<ScrollContainer>("ScrollContainer")
                          ?? (_dropdownPanel as Node)?.FindChild("*Scroll*", true, false) as ScrollContainer;

        if (_scrollContainer != null)
        {
            _scrollContainer.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
            _scrollContainer.VerticalScrollMode = ScrollContainer.ScrollMode.Auto;
            _scrollContainer.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            _scrollContainer.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            _scrollContainer.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        }

        if (_dropdownPanel is PopupPanel popupPanel)
        {
            // Disable WrapControls so Godot stops expanding the window to full content height
            popupPanel.WrapControls = false;
            if (popupPanel.Size.X > 200) width = popupPanel.Size.X;

            Vector2I pos = new Vector2I((int)GlobalPosition.X, (int)topY);
            popupPanel.Position = pos;
            popupPanel.Size = new Vector2I(width, maxHeight);
            popupPanel.Popup();
        }
        else if (_dropdownPanel is Control control)
        {
            control.GlobalPosition = new Vector2(GlobalPosition.X, topY);
            control.CustomMinimumSize = new Vector2(width, maxHeight);
            control.Size = new Vector2(control.Size.X > 200 ? control.Size.X : width, maxHeight);
            control.Visible = true;
            control.MoveToFront();
        }
    }

    public void ToggleDropdown()
    {
        if (_dropdownPanel is PopupPanel popupPanel && popupPanel.Visible)
        {
            popupPanel.Hide();
        }
        else if (_dropdownPanel is Control control && control.Visible)
        {
            control.Visible = false;
        }
        else
        {
            OpenDropdown();
        }
    }

    // Alias for compatibility
    public void TogglePanel() => ToggleDropdown();

    public void CloseDropdown()
    {
        if (_dropdownPanel == null) return;

        if (_dropdownPanel is PopupPanel popupPanel)
        {
            popupPanel.Hide();
        }
        else if (_dropdownPanel is Control control)
        {
            control.Visible = false;
        }
    }

    private void LinkDependencies()
    {
        _sunLight ??= GetNodeOrNull<DirectionalLight3D>("/root/Main/DirectionalLight3D");
        _worldEnv ??= GetNodeOrNull<WorldEnvironment>("/root/Main/WorldEnvironment");
        _camera ??= GetNodeOrNull<Camera3D>("/root/Main/CameraPivot/Camera3D")
                    ?? GetTree()?.Root?.FindChild("Camera3D", true, false) as Camera3D;

        _menuButton ??= this;

        _dropdownPanel ??= GetNodeOrNull<Node>("EnvironmentPopup")
                           ?? GetNodeOrNull<Node>("EnvironmentDropdown")
                           ?? GetNodeOrNull<Node>("PopupPanel")
                           ?? GetNodeOrNull<Node>("DropdownPanel")
                           ?? FindChild("EnvironmentPopup", true, false)
                           ?? FindChild("PopupPanel", true, false);

        _scrollContainer ??= (_dropdownPanel as Node)?.GetNodeOrNull<ScrollContainer>("ScrollContainer")
                          ?? (_dropdownPanel as Node)?.FindChild("*Scroll*", true, false) as ScrollContainer;
    }

    private void InitCameraAttributes()
    {
        if (_worldEnv != null)
        {
            if (_worldEnv.CameraAttributes is CameraAttributesPractical practical)
            {
                _cameraAttributesPractical = practical;
            }
            else if (_camera != null && _camera.Attributes is CameraAttributesPractical camPractical)
            {
                _cameraAttributesPractical = camPractical;
            }
            else
            {
                _cameraAttributesPractical = new CameraAttributesPractical();
                _worldEnv.CameraAttributes = _cameraAttributesPractical;
                if (_camera != null)
                {
                    _camera.Attributes = _cameraAttributesPractical;
                }
            }
        }
    }

    private void CaptureDefaults()
    {
        if (_sunLight != null)
        {
            _defaults.SunColor = _sunLight.LightColor;
            _defaults.SunEnergy = _sunLight.LightEnergy;
            _defaults.SunRotation = _sunLight.RotationDegrees;
        }
        else
        {
            _defaults.SunColor = Colors.White;
            _defaults.SunEnergy = 1.0f;
            _defaults.SunRotation = Vector3.Zero;
        }

        var env = _worldEnv?.Environment;
        if (env != null)
        {
            _defaults.AmbientColor = env.AmbientLightColor;
            _defaults.AmbientEnergy = env.AmbientLightEnergy;
            _defaults.TonemapMode = env.TonemapMode;

            _defaults.FogEnabled = env.FogEnabled;
            _defaults.FogDensity = env.FogDensity;
            _defaults.VolumetricFogEnabled = env.VolumetricFogEnabled;
            _defaults.VolumetricFogDensity = env.VolumetricFogDensity;

            _defaults.GlowEnabled = env.GlowEnabled;
            _defaults.GlowIntensity = env.GlowIntensity;
        }

        if (_cameraAttributesPractical != null)
        {
            _defaults.DofFarEnabled = _cameraAttributesPractical.DofBlurFarEnabled;
            _defaults.DofFarDistance = (float)_cameraAttributesPractical.DofBlurFarDistance;
            _defaults.DofNearEnabled = _cameraAttributesPractical.DofBlurNearEnabled;
            _defaults.DofNearDistance = (float)_cameraAttributesPractical.DofBlurNearDistance;
        }
        else
        {
            _defaults.DofFarEnabled = false;
            _defaults.DofFarDistance = 10.0f;
            _defaults.DofNearEnabled = false;
            _defaults.DofNearDistance = 2.0f;
        }
    }

    private void PopulateTonemapOptions()
    {
        if (_tonemapOption == null) return;

        _tonemapOption.Clear();
        foreach (var mode in Enum.GetValues<Godot.Environment.ToneMapper>())
        {
            _tonemapOption.AddItem(mode.ToString(), (int)mode);
        }
    }

    private void ConnectEvents()
    {
        var popup = GetPopup();
        if (popup != null)
        {
            popup.AboutToPopup += OnMenuAboutToPopup;
        }

        // Sun Events
        if (_sunColorPicker != null)
        {
            _sunColorPicker.ColorChanged += OnSunColorChanged;
        }

        if (_sunEnergySlider != null)
        {
            _sunEnergySlider.ValueChanged += OnSunEnergyChanged;
        }

        if (_sunHeadingSlider != null)
        {
            _sunHeadingSlider.ValueChanged += (_) => UpdateSunRotation();
        }

        if (_sunPitchSlider != null)
        {
            _sunPitchSlider.ValueChanged += (_) => UpdateSunRotation();
        }

        // Ambient Events
        if (_ambientColorPicker != null)
        {
            _ambientColorPicker.ColorChanged += OnAmbientColorChanged;
        }

        if (_ambientEnergySlider != null)
        {
            _ambientEnergySlider.ValueChanged += OnAmbientEnergyChanged;
        }

        // DoF Events
        if (_dofFarToggle != null)
        {
            _dofFarToggle.Toggled += OnDofFarToggled;
        }

        if (_dofFarDistanceSlider != null)
        {
            _dofFarDistanceSlider.ValueChanged += OnDofFarDistanceChanged;
        }

        if (_dofNearToggle != null)
        {
            _dofNearToggle.Toggled += OnDofNearToggled;
        }

        if (_dofNearDistanceSlider != null)
        {
            _dofNearDistanceSlider.ValueChanged += OnDofNearDistanceChanged;
        }

        // Tonemap Event
        if (_tonemapOption != null)
        {
            _tonemapOption.ItemSelected += OnTonemapSelected;
        }

        // Fog Events
        if (_fogToggle != null)
        {
            _fogToggle.Toggled += OnFogToggled;
        }

        if (_fogDensitySlider != null)
        {
            _fogDensitySlider.ValueChanged += OnFogDensityChanged;
        }

        if (_volumetricFogToggle != null)
        {
            _volumetricFogToggle.Toggled += OnVolumetricFogToggled;
        }

        // Glow Events
        if (_glowToggle != null)
        {
            _glowToggle.Toggled += OnGlowToggled;
        }

        if (_glowIntensitySlider != null)
        {
            _glowIntensitySlider.ValueChanged += OnGlowIntensityChanged;
        }

        // Reset Button
        if (_btnResetEnvironment != null)
        {
            _btnResetEnvironment.Pressed += ResetToDefaults;
        }
    }

    public void SyncUIToScene()
    {
        _isSyncingUI = true;

        // 1. Sun
        if (_sunLight != null)
        {
            if (_sunColorPicker != null) _sunColorPicker.Color = _sunLight.LightColor;
            if (_sunEnergySlider != null)
            {
                _sunEnergySlider.Value = _sunLight.LightEnergy;
                if (_sunEnergyLabel != null) _sunEnergyLabel.Text = $"{_sunLight.LightEnergy:F2}";
            }

            Vector3 rot = _sunLight.RotationDegrees;
            if (_sunHeadingSlider != null)
            {
                _sunHeadingSlider.Value = rot.Y;
                if (_sunHeadingLabel != null) _sunHeadingLabel.Text = $"{(int)rot.Y}°";
            }
            if (_sunPitchSlider != null)
            {
                _sunPitchSlider.Value = rot.X;
                if (_sunPitchLabel != null) _sunPitchLabel.Text = $"{(int)rot.X}°";
            }
        }

        // 2. Environment
        var env = _worldEnv?.Environment;
        if (env != null)
        {
            if (_ambientColorPicker != null) _ambientColorPicker.Color = env.AmbientLightColor;
            if (_ambientEnergySlider != null)
            {
                _ambientEnergySlider.Value = env.AmbientLightEnergy;
                if (_ambientEnergyLabel != null) _ambientEnergyLabel.Text = $"{env.AmbientLightEnergy:F2}";
            }

            if (_tonemapOption != null)
            {
                int id = (int)env.TonemapMode;
                for (int i = 0; i < _tonemapOption.ItemCount; i++)
                {
                    if (_tonemapOption.GetItemId(i) == id)
                    {
                        _tonemapOption.Select(i);
                        break;
                    }
                }
            }

            if (_fogToggle != null) _fogToggle.ButtonPressed = env.FogEnabled;
            if (_fogDensitySlider != null)
            {
                _fogDensitySlider.Value = env.FogDensity;
                if (_fogDensityLabel != null) _fogDensityLabel.Text = $"{env.FogDensity:F3}";
            }
            if (_volumetricFogToggle != null) _volumetricFogToggle.ButtonPressed = env.VolumetricFogEnabled;

            if (_glowToggle != null) _glowToggle.ButtonPressed = env.GlowEnabled;
            if (_glowIntensitySlider != null)
            {
                _glowIntensitySlider.Value = env.GlowIntensity;
                if (_glowIntensityLabel != null) _glowIntensityLabel.Text = $"{env.GlowIntensity:F2}";
            }
        }

        // 3. DoF
        if (_cameraAttributesPractical != null)
        {
            if (_dofFarToggle != null) _dofFarToggle.ButtonPressed = _cameraAttributesPractical.DofBlurFarEnabled;
            if (_dofFarDistanceSlider != null)
            {
                _dofFarDistanceSlider.Value = _cameraAttributesPractical.DofBlurFarDistance;
                if (_dofFarDistanceLabel != null) _dofFarDistanceLabel.Text = $"{_cameraAttributesPractical.DofBlurFarDistance:F1}m";
            }

            if (_dofNearToggle != null) _dofNearToggle.ButtonPressed = _cameraAttributesPractical.DofBlurNearEnabled;
            if (_dofNearDistanceSlider != null)
            {
                _dofNearDistanceSlider.Value = _cameraAttributesPractical.DofBlurNearDistance;
                if (_dofNearDistanceLabel != null) _dofNearDistanceLabel.Text = $"{_cameraAttributesPractical.DofBlurNearDistance:F1}m";
            }
        }

        _isSyncingUI = false;
    }

    private void OnMenuAboutToPopup()
    {
        var popup = GetPopup();
        popup?.CallDeferred(Window.MethodName.Hide);
        Callable.From(OpenDropdown).CallDeferred();
    }

    private void OnSunColorChanged(Color color)
    {
        if (_isSyncingUI || _sunLight == null) return;
        _sunLight.LightColor = color;
    }

    private void OnSunEnergyChanged(double value)
    {
        if (_isSyncingUI || _sunLight == null) return;
        _sunLight.LightEnergy = (float)value;
        if (_sunEnergyLabel != null) _sunEnergyLabel.Text = $"{value:F2}";
    }

    private void UpdateSunRotation()
    {
        if (_isSyncingUI || _sunLight == null) return;
        float heading = _sunHeadingSlider != null ? (float)_sunHeadingSlider.Value : _sunLight.RotationDegrees.Y;
        float pitch = _sunPitchSlider != null ? (float)_sunPitchSlider.Value : _sunLight.RotationDegrees.X;

        _sunLight.RotationDegrees = new Vector3(pitch, heading, 0);

        if (_sunHeadingLabel != null) _sunHeadingLabel.Text = $"{(int)heading}°";
        if (_sunPitchLabel != null) _sunPitchLabel.Text = $"{(int)pitch}°";
    }

    private void OnAmbientColorChanged(Color color)
    {
        if (_isSyncingUI) return;
        var env = _worldEnv?.Environment;
        if (env != null)
        {
            env.AmbientLightColor = color;
        }
    }

    private void OnAmbientEnergyChanged(double value)
    {
        if (_isSyncingUI) return;
        var env = _worldEnv?.Environment;
        if (env != null)
        {
            env.AmbientLightEnergy = (float)value;
        }
        if (_ambientEnergyLabel != null) _ambientEnergyLabel.Text = $"{value:F2}";
    }

    private void OnDofFarToggled(bool isToggled)
    {
        if (_isSyncingUI) return;
        EnsureCameraAttributes();
        if (_cameraAttributesPractical != null)
        {
            _cameraAttributesPractical.DofBlurFarEnabled = isToggled;
        }
    }

    private void OnDofFarDistanceChanged(double value)
    {
        if (_isSyncingUI) return;
        EnsureCameraAttributes();
        if (_cameraAttributesPractical != null)
        {
            _cameraAttributesPractical.DofBlurFarDistance = (float)value;
        }
        if (_dofFarDistanceLabel != null) _dofFarDistanceLabel.Text = $"{value:F1}m";
    }

    private void OnDofNearToggled(bool isToggled)
    {
        if (_isSyncingUI) return;
        EnsureCameraAttributes();
        if (_cameraAttributesPractical != null)
        {
            _cameraAttributesPractical.DofBlurNearEnabled = isToggled;
        }
    }

    private void OnDofNearDistanceChanged(double value)
    {
        if (_isSyncingUI) return;
        EnsureCameraAttributes();
        if (_cameraAttributesPractical != null)
        {
            _cameraAttributesPractical.DofBlurNearDistance = (float)value;
        }
        if (_dofNearDistanceLabel != null) _dofNearDistanceLabel.Text = $"{value:F1}m";
    }

    private void EnsureCameraAttributes()
    {
        if (_cameraAttributesPractical == null)
        {
            InitCameraAttributes();
        }
    }

    private void OnTonemapSelected(long index)
    {
        if (_isSyncingUI) return;
        if (_tonemapOption == null) return;
        int id = _tonemapOption.GetItemId((int)index);
        var env = _worldEnv?.Environment;
        if (env != null)
        {
            env.TonemapMode = (Godot.Environment.ToneMapper)id;
        }
    }

    private void OnFogToggled(bool isToggled)
    {
        if (_isSyncingUI) return;
        var env = _worldEnv?.Environment;
        if (env != null)
        {
            env.FogEnabled = isToggled;
        }
    }

    private void OnFogDensityChanged(double value)
    {
        if (_isSyncingUI) return;
        var env = _worldEnv?.Environment;
        if (env != null)
        {
            env.FogDensity = (float)value;
        }
        if (_fogDensityLabel != null) _fogDensityLabel.Text = $"{value:F3}";
    }

    private void OnVolumetricFogToggled(bool isToggled)
    {
        if (_isSyncingUI) return;
        var env = _worldEnv?.Environment;
        if (env != null)
        {
            env.VolumetricFogEnabled = isToggled;
        }
    }

    private void OnGlowToggled(bool isToggled)
    {
        if (_isSyncingUI) return;
        var env = _worldEnv?.Environment;
        if (env != null)
        {
            env.GlowEnabled = isToggled;
        }
    }

    private void OnGlowIntensityChanged(double value)
    {
        if (_isSyncingUI) return;
        var env = _worldEnv?.Environment;
        if (env != null)
        {
            env.GlowIntensity = (float)value;
        }
        if (_glowIntensityLabel != null) _glowIntensityLabel.Text = $"{value:F2}";
    }

    public void ResetToDefaults()
    {
        // 1. Sun
        if (_sunLight != null)
        {
            _sunLight.LightColor = _defaults.SunColor;
            _sunLight.LightEnergy = _defaults.SunEnergy;
            _sunLight.RotationDegrees = _defaults.SunRotation;
        }

        // 2. WorldEnvironment
        var env = _worldEnv?.Environment;
        if (env != null)
        {
            env.AmbientLightColor = _defaults.AmbientColor;
            env.AmbientLightEnergy = _defaults.AmbientEnergy;
            env.TonemapMode = _defaults.TonemapMode;

            env.FogEnabled = _defaults.FogEnabled;
            env.FogDensity = _defaults.FogDensity;
            env.VolumetricFogEnabled = _defaults.VolumetricFogEnabled;
            env.VolumetricFogDensity = _defaults.VolumetricFogDensity;

            env.GlowEnabled = _defaults.GlowEnabled;
            env.GlowIntensity = _defaults.GlowIntensity;
        }

        // 3. DoF
        if (_cameraAttributesPractical != null)
        {
            _cameraAttributesPractical.DofBlurFarEnabled = _defaults.DofFarEnabled;
            _cameraAttributesPractical.DofBlurFarDistance = _defaults.DofFarDistance;
            _cameraAttributesPractical.DofBlurNearEnabled = _defaults.DofNearEnabled;
            _cameraAttributesPractical.DofBlurNearDistance = _defaults.DofNearDistance;
        }

        // 4. Update UI
        SyncUIToScene();
    }
}
