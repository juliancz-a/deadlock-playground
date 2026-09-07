using Godot;
using System;

public partial class EffectsTabUI : VBoxContainer
{
    [ExportCategory("Scene References")]
    [Export] private WorldEnvironment _worldEnv;
    [Export] private Camera3D _camera;

    [ExportCategory("Depth Of Field (DoF)")]
    [Export] private CheckBox _dofFarToggle;
    [Export] private HSlider _dofFarDistanceSlider;
    [Export] private Label _dofFarDistanceLabel;
    [Export] private CheckBox _dofNearToggle;
    [Export] private HSlider _dofNearDistanceSlider;
    [Export] private Label _dofNearDistanceLabel;
    [Export] private HSlider _dofBlurAmountSlider;
    [Export] private Label _dofBlurAmountLabel;

    [ExportCategory("Tonemapping")]
    [Export] private OptionButton _tonemapOption;

    [ExportCategory("Atmosphere & Fog")]
    [Export] private CheckBox _fogToggle;
    [Export] private HSlider _fogDensitySlider;
    [Export] private Label _fogDensityLabel;
    [Export] private CheckBox _volumetricFogToggle;

    [ExportCategory("Glow & Bloom")]
    [Export] private CheckBox _glowToggle;
    [Export] private HSlider _glowIntensitySlider;
    [Export] private Label _glowIntensityLabel;
    [Export] private HSlider _glowBloomSlider;
    [Export] private Label _glowBloomLabel;
    [Export] private HSlider _glowHdrSlider;
    [Export] private Label _glowHdrLabel;
    [Export] private OptionButton _glowBlendOption;

    [ExportCategory("Actions")]
    [Export] private Button _btnResetEffects;

    private CameraAttributesPractical _cameraAttributesPractical;
    private bool _isSyncing = false;

    public override void _Ready()
    {
        EnsureCameraAttributes();
        PopulateDropdowns();
        ConnectEvents();
        SyncUIToScene();
    }

    private void LinkReferences()
    {
        _worldEnv ??= GetNodeOrNull<WorldEnvironment>("/root/Main/UIRoot/MainHUD/VBoxContainer/MainSplit/ViewportArea/SubViewportContainer/WorldViewport/WorldEnvironment")
                   ?? GetNodeOrNull<WorldEnvironment>("/root/Main/WorldEnvironment")
                   ?? GetTree()?.Root?.FindChild("WorldEnvironment", true, false) as WorldEnvironment;

        _camera ??= GetNodeOrNull<Camera3D>("/root/Main/UIRoot/MainHUD/VBoxContainer/MainSplit/ViewportArea/SubViewportContainer/WorldViewport/CameraPivot/Camera3D")
                 ?? GetNodeOrNull<Camera3D>("/root/Main/CameraPivot/Camera3D")
                 ?? GetTree()?.Root?.FindChild("Camera3D", true, false) as Camera3D;
    }

    private CameraAttributesPractical EnsureCameraAttributes()
    {
        if (_cameraAttributesPractical != null) return _cameraAttributesPractical;

        LinkReferences();

        if (_worldEnv?.CameraAttributes is CameraAttributesPractical envPractical)
        {
            _cameraAttributesPractical = envPractical;
        }
        else if (_camera?.Attributes is CameraAttributesPractical camPractical)
        {
            _cameraAttributesPractical = camPractical;
        }
        else
        {
            _cameraAttributesPractical = new CameraAttributesPractical
            {
                DofBlurFarEnabled = false,
                DofBlurFarDistance = 5.0f,
                DofBlurFarTransition = 1.5f,
                DofBlurNearEnabled = false,
                DofBlurNearDistance = 3.5f,
                DofBlurNearTransition = 1.5f,
                DofBlurAmount = 0.35f
            };
        }

        if (_camera != null) _camera.Attributes = _cameraAttributesPractical;
        if (_worldEnv != null) _worldEnv.CameraAttributes = _cameraAttributesPractical;

        return _cameraAttributesPractical;
    }

    private void PopulateDropdowns()
    {
        if (_tonemapOption != null)
        {
            _tonemapOption.Clear();
            foreach (var mode in Enum.GetValues<Godot.Environment.ToneMapper>())
            {
                _tonemapOption.AddItem(mode.ToString(), (int)mode);
            }
        }

        if (_glowBlendOption != null)
        {
            _glowBlendOption.Clear();
            _glowBlendOption.AddItem("Screen", (int)Godot.Environment.GlowBlendModeEnum.Screen);
            _glowBlendOption.AddItem("Softlight", (int)Godot.Environment.GlowBlendModeEnum.Softlight);
            _glowBlendOption.AddItem("Additive", (int)Godot.Environment.GlowBlendModeEnum.Additive);
            _glowBlendOption.AddItem("Mix", (int)Godot.Environment.GlowBlendModeEnum.Mix);
        }
    }

    private void ConnectEvents()
    {
        // DoF Far
        if (_dofFarToggle != null)
        {
            _dofFarToggle.Toggled += (enabled) =>
            {
                var attrs = EnsureCameraAttributes();
                if (!_isSyncing && attrs != null)
                    attrs.DofBlurFarEnabled = enabled;
            };
        }

        if (_dofFarDistanceSlider != null)
        {
            _dofFarDistanceSlider.ValueChanged += (v) =>
            {
                if (_dofFarDistanceLabel != null) _dofFarDistanceLabel.Text = $"{v:F1}m";
                var attrs = EnsureCameraAttributes();
                if (!_isSyncing && attrs != null)
                    attrs.DofBlurFarDistance = (float)v;
            };
        }

        // DoF Near
        if (_dofNearToggle != null)
        {
            _dofNearToggle.Toggled += (enabled) =>
            {
                var attrs = EnsureCameraAttributes();
                if (!_isSyncing && attrs != null)
                    attrs.DofBlurNearEnabled = enabled;
            };
        }

        if (_dofNearDistanceSlider != null)
        {
            _dofNearDistanceSlider.ValueChanged += (v) =>
            {
                if (_dofNearDistanceLabel != null) _dofNearDistanceLabel.Text = $"{v:F1}m";
                var attrs = EnsureCameraAttributes();
                if (!_isSyncing && attrs != null)
                    attrs.DofBlurNearDistance = (float)v;
            };
        }

        // DoF Blur Amount
        if (_dofBlurAmountSlider != null)
        {
            _dofBlurAmountSlider.ValueChanged += (v) =>
            {
                if (_dofBlurAmountLabel != null) _dofBlurAmountLabel.Text = $"{v:F2}";
                var attrs = EnsureCameraAttributes();
                if (!_isSyncing && attrs != null)
                    attrs.DofBlurAmount = (float)v;
            };
        }

        // Tonemap
        if (_tonemapOption != null)
        {
            _tonemapOption.ItemSelected += (idx) =>
            {
                if (!_isSyncing && _worldEnv?.Environment != null)
                    _worldEnv.Environment.TonemapMode = (Godot.Environment.ToneMapper)_tonemapOption.GetItemId((int)idx);
            };
        }

        // Fog
        if (_fogToggle != null)
        {
            _fogToggle.Toggled += (enabled) =>
            {
                if (!_isSyncing && _worldEnv?.Environment != null)
                    _worldEnv.Environment.FogEnabled = enabled;
            };
        }

        if (_fogDensitySlider != null)
        {
            _fogDensitySlider.ValueChanged += (v) =>
            {
                if (_fogDensityLabel != null) _fogDensityLabel.Text = $"{v:F3}";
                if (!_isSyncing && _worldEnv?.Environment != null)
                    _worldEnv.Environment.FogDensity = (float)v;
            };
        }

        if (_volumetricFogToggle != null)
        {
            _volumetricFogToggle.Toggled += (enabled) =>
            {
                if (!_isSyncing && _worldEnv?.Environment != null)
                    _worldEnv.Environment.VolumetricFogEnabled = enabled;
            };
        }

        // Glow & Bloom
        if (_glowToggle != null)
        {
            _glowToggle.Toggled += (enabled) =>
            {
                if (!_isSyncing && _worldEnv?.Environment != null)
                    _worldEnv.Environment.GlowEnabled = enabled;
            };
        }

        if (_glowIntensitySlider != null)
        {
            _glowIntensitySlider.ValueChanged += (v) =>
            {
                if (_glowIntensityLabel != null) _glowIntensityLabel.Text = $"{v:F2}";
                if (!_isSyncing && _worldEnv?.Environment != null)
                    _worldEnv.Environment.GlowIntensity = (float)v;
            };
        }

        if (_glowBloomSlider != null)
        {
            _glowBloomSlider.ValueChanged += (v) =>
            {
                if (_glowBloomLabel != null) _glowBloomLabel.Text = $"{v:F2}";
                if (!_isSyncing && _worldEnv?.Environment != null)
                    _worldEnv.Environment.GlowBloom = (float)v;
            };
        }

        if (_glowHdrSlider != null)
        {
            _glowHdrSlider.ValueChanged += (v) =>
            {
                if (_glowHdrLabel != null) _glowHdrLabel.Text = $"{v:F2}";
                if (!_isSyncing && _worldEnv?.Environment != null)
                    _worldEnv.Environment.GlowHdrThreshold = (float)v;
            };
        }

        if (_glowBlendOption != null)
        {
            _glowBlendOption.ItemSelected += (idx) =>
            {
                if (!_isSyncing && _worldEnv?.Environment != null)
                    _worldEnv.Environment.GlowBlendMode = (Godot.Environment.GlowBlendModeEnum)_glowBlendOption.GetItemId((int)idx);
            };
        }

        // Reset
        if (_btnResetEffects != null)
        {
            _btnResetEffects.Pressed += ResetToDefaults;
        }
    }

    public void SyncUIToScene()
    {
        _isSyncing = true;

        var attrs = EnsureCameraAttributes();
        if (attrs != null)
        {
            if (_dofFarToggle != null) _dofFarToggle.ButtonPressed = attrs.DofBlurFarEnabled;
            if (_dofFarDistanceSlider != null)
            {
                _dofFarDistanceSlider.Value = attrs.DofBlurFarDistance;
                if (_dofFarDistanceLabel != null) _dofFarDistanceLabel.Text = $"{attrs.DofBlurFarDistance:F1}m";
            }

            if (_dofNearToggle != null) _dofNearToggle.ButtonPressed = attrs.DofBlurNearEnabled;
            if (_dofNearDistanceSlider != null)
            {
                _dofNearDistanceSlider.Value = attrs.DofBlurNearDistance;
                if (_dofNearDistanceLabel != null) _dofNearDistanceLabel.Text = $"{attrs.DofBlurNearDistance:F1}m";
            }

            if (_dofBlurAmountSlider != null)
            {
                _dofBlurAmountSlider.Value = attrs.DofBlurAmount;
                if (_dofBlurAmountLabel != null) _dofBlurAmountLabel.Text = $"{attrs.DofBlurAmount:F2}";
            }
        }

        if (_worldEnv?.Environment != null)
        {
            if (_tonemapOption != null)
            {
                for (int i = 0; i < _tonemapOption.ItemCount; i++)
                {
                    if (_tonemapOption.GetItemId(i) == (int)_worldEnv.Environment.TonemapMode)
                    {
                        _tonemapOption.Select(i);
                        break;
                    }
                }
            }

            if (_fogToggle != null) _fogToggle.ButtonPressed = _worldEnv.Environment.FogEnabled;
            if (_fogDensitySlider != null)
            {
                _fogDensitySlider.Value = _worldEnv.Environment.FogDensity;
                if (_fogDensityLabel != null) _fogDensityLabel.Text = $"{_worldEnv.Environment.FogDensity:F3}";
            }
            if (_volumetricFogToggle != null) _volumetricFogToggle.ButtonPressed = _worldEnv.Environment.VolumetricFogEnabled;

            if (_glowToggle != null) _glowToggle.ButtonPressed = _worldEnv.Environment.GlowEnabled;
            if (_glowIntensitySlider != null)
            {
                _glowIntensitySlider.Value = _worldEnv.Environment.GlowIntensity;
                if (_glowIntensityLabel != null) _glowIntensityLabel.Text = $"{_worldEnv.Environment.GlowIntensity:F2}";
            }

            if (_glowBloomSlider != null)
            {
                _glowBloomSlider.Value = _worldEnv.Environment.GlowBloom;
                if (_glowBloomLabel != null) _glowBloomLabel.Text = $"{_worldEnv.Environment.GlowBloom:F2}";
            }

            if (_glowHdrSlider != null)
            {
                _glowHdrSlider.Value = _worldEnv.Environment.GlowHdrThreshold;
                if (_glowHdrLabel != null) _glowHdrLabel.Text = $"{_worldEnv.Environment.GlowHdrThreshold:F2}";
            }

            if (_glowBlendOption != null)
            {
                for (int i = 0; i < _glowBlendOption.ItemCount; i++)
                {
                    if (_glowBlendOption.GetItemId(i) == (int)_worldEnv.Environment.GlowBlendMode)
                    {
                        _glowBlendOption.Select(i);
                        break;
                    }
                }
            }
        }

        _isSyncing = false;
    }

    private void ResetToDefaults()
    {
        var attrs = EnsureCameraAttributes();
        if (attrs != null)
        {
            attrs.DofBlurFarEnabled = false;
            attrs.DofBlurFarDistance = 5.0f;
            attrs.DofBlurFarTransition = 1.5f;
            attrs.DofBlurNearEnabled = false;
            attrs.DofBlurNearDistance = 3.5f;
            attrs.DofBlurNearTransition = 1.5f;
            attrs.DofBlurAmount = 0.35f;
        }

        if (_worldEnv?.Environment != null)
        {
            _worldEnv.Environment.TonemapMode = Godot.Environment.ToneMapper.Linear;
            _worldEnv.Environment.FogEnabled = false;
            _worldEnv.Environment.FogDensity = 0.005f;
            _worldEnv.Environment.VolumetricFogEnabled = false;
            _worldEnv.Environment.GlowEnabled = true;
            _worldEnv.Environment.GlowIntensity = 1.0f;
            _worldEnv.Environment.GlowBloom = 0.35f;
            _worldEnv.Environment.GlowHdrThreshold = 0.85f;
            _worldEnv.Environment.GlowBlendMode = Godot.Environment.GlowBlendModeEnum.Screen;
        }

        SyncUIToScene();
    }
}
