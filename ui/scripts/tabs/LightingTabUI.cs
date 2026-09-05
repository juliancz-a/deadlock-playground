using Godot;
using System;

public partial class LightingTabUI : VBoxContainer
{
    [ExportCategory("Scene References")]
    [Export] private DirectionalLight3D _sunLight;
    [Export] private WorldEnvironment _worldEnv;

    [ExportCategory("Sun Light Controls")]
    [Export] private ColorPickerButton _sunColorPicker;
    [Export] private HSlider _sunEnergySlider;
    [Export] private Label _sunEnergyLabel;
    [Export] private HSlider _sunHeadingSlider;
    [Export] private Label _sunHeadingLabel;
    [Export] private HSlider _sunPitchSlider;
    [Export] private Label _sunPitchLabel;

    [ExportCategory("Ambient Light Controls")]
    [Export] private ColorPickerButton _ambientColorPicker;
    [Export] private HSlider _ambientEnergySlider;
    [Export] private Label _ambientEnergyLabel;

    [ExportCategory("Actions")]
    [Export] private Button _btnResetLighting;

    private Color _defaultSunColor;
    private float _defaultSunEnergy;
    private Vector3 _defaultSunRotation;
    private Color _defaultAmbientColor;
    private float _defaultAmbientEnergy;
    private bool _isSyncing = false;

    public override void _Ready()
    {
        LinkReferences();
        CaptureDefaults();
        ConnectEvents();
        SyncUIToScene();
    }

    private void LinkReferences()
    {
        _sunLight ??= GetNodeOrNull<DirectionalLight3D>("/root/Main/DirectionalLight3D");
        _worldEnv ??= GetNodeOrNull<WorldEnvironment>("/root/Main/WorldEnvironment");
    }

    private void CaptureDefaults()
    {
        if (_sunLight != null)
        {
            _defaultSunColor = _sunLight.LightColor;
            _defaultSunEnergy = _sunLight.LightEnergy;
            _defaultSunRotation = _sunLight.RotationDegrees;
        }

        if (_worldEnv?.Environment != null)
        {
            _worldEnv.Environment.AmbientLightSource = Godot.Environment.AmbientSource.Color;
            if (_worldEnv.Environment.AmbientLightColor.R == 0 && _worldEnv.Environment.AmbientLightColor.G == 0 && _worldEnv.Environment.AmbientLightColor.B == 0)
            {
                _worldEnv.Environment.AmbientLightColor = new Color(0.28f, 0.29f, 0.32f, 1f);
            }
            _defaultAmbientColor = _worldEnv.Environment.AmbientLightColor;
            _defaultAmbientEnergy = _worldEnv.Environment.AmbientLightEnergy;
        }
    }

    private void ConnectEvents()
    {
        if (_sunColorPicker != null)
        {
            _sunColorPicker.ColorChanged += (c) =>
            {
                if (!_isSyncing && _sunLight != null) _sunLight.LightColor = c;
            };
        }

        if (_sunEnergySlider != null)
        {
            _sunEnergySlider.ValueChanged += (v) =>
            {
                if (_sunEnergyLabel != null) _sunEnergyLabel.Text = $"{v:F2}";
                if (!_isSyncing && _sunLight != null) _sunLight.LightEnergy = (float)v;
            };
        }

        if (_sunHeadingSlider != null)
        {
            _sunHeadingSlider.ValueChanged += (v) =>
            {
                if (_sunHeadingLabel != null) _sunHeadingLabel.Text = $"{v:F0}°";
                if (!_isSyncing && _sunLight != null)
                {
                    Vector3 rot = _sunLight.RotationDegrees;
                    rot.Y = (float)v;
                    _sunLight.RotationDegrees = rot;
                }
            };
        }

        if (_sunPitchSlider != null)
        {
            _sunPitchSlider.ValueChanged += (v) =>
            {
                if (_sunPitchLabel != null) _sunPitchLabel.Text = $"{v:F0}°";
                if (!_isSyncing && _sunLight != null)
                {
                    Vector3 rot = _sunLight.RotationDegrees;
                    rot.X = (float)v;
                    _sunLight.RotationDegrees = rot;
                }
            };
        }

        if (_ambientColorPicker != null)
        {
            _ambientColorPicker.ColorChanged += (c) =>
            {
                if (!_isSyncing && _worldEnv?.Environment != null)
                {
                    _worldEnv.Environment.AmbientLightSource = Godot.Environment.AmbientSource.Color;
                    _worldEnv.Environment.AmbientLightColor = c;
                }
            };
        }

        if (_ambientEnergySlider != null)
        {
            _ambientEnergySlider.ValueChanged += (v) =>
            {
                if (_ambientEnergyLabel != null) _ambientEnergyLabel.Text = $"{v:F2}";
                if (!_isSyncing && _worldEnv?.Environment != null)
                {
                    _worldEnv.Environment.AmbientLightSource = Godot.Environment.AmbientSource.Color;
                    _worldEnv.Environment.AmbientLightEnergy = (float)v;
                }
            };
        }

        if (_btnResetLighting != null)
        {
            _btnResetLighting.Pressed += ResetToDefaults;
        }
    }

    public void SyncUIToScene()
    {
        _isSyncing = true;

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
                if (_sunHeadingLabel != null) _sunHeadingLabel.Text = $"{rot.Y:F0}°";
            }
            if (_sunPitchSlider != null)
            {
                _sunPitchSlider.Value = rot.X;
                if (_sunPitchLabel != null) _sunPitchLabel.Text = $"{rot.X:F0}°";
            }
        }

        if (_worldEnv?.Environment != null)
        {
            if (_ambientColorPicker != null) _ambientColorPicker.Color = _worldEnv.Environment.AmbientLightColor;
            if (_ambientEnergySlider != null)
            {
                _ambientEnergySlider.Value = _worldEnv.Environment.AmbientLightEnergy;
                if (_ambientEnergyLabel != null) _ambientEnergyLabel.Text = $"{_worldEnv.Environment.AmbientLightEnergy:F2}";
            }
        }

        _isSyncing = false;
    }

    public void ResetToDefaults()
    {
        if (_sunLight != null)
        {
            _sunLight.LightColor = _defaultSunColor;
            _sunLight.LightEnergy = _defaultSunEnergy;
            _sunLight.RotationDegrees = _defaultSunRotation;
        }

        if (_worldEnv?.Environment != null)
        {
            _worldEnv.Environment.AmbientLightColor = _defaultAmbientColor;
            _worldEnv.Environment.AmbientLightEnergy = _defaultAmbientEnergy;
        }

        SyncUIToScene();
    }
}
