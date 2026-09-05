using Godot;
using System;

public partial class CameraTabUI : VBoxContainer
{
    [ExportCategory("Scene References")]
    [Export] private Camera3D _camera;
    [Export] private OrbitCamera _orbitCamera;

    [ExportCategory("FOV Controls")]
    [Export] private HSlider _fovSlider;
    [Export] private Label _fovLabel;

    [ExportCategory("Camera Presets")]
    [Export] private Button _btnPresetPortrait;
    [Export] private Button _btnPresetFullBody;
    [Export] private Button _btnPresetCloseup;

    [ExportCategory("Sensitivity Controls")]
    [Export] private HSlider _orbitSensSlider;
    [Export] private Label _orbitSensLabel;
    [Export] private HSlider _panSpeedSlider;
    [Export] private Label _panSpeedLabel;

    [ExportCategory("Actions")]
    [Export] private Button _btnResetCamera;

    private float _defaultFov = 75.0f;
    private float _defaultOrbitSens = 0.005f;
    private float _defaultPanSpeed = 5.0f;
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
        _camera ??= GetNodeOrNull<Camera3D>("/root/Main/CameraPivot/Camera3D");
        _orbitCamera ??= GetNodeOrNull<OrbitCamera>("/root/Main/CameraPivot");
    }

    private void CaptureDefaults()
    {
        if (_camera != null) _defaultFov = _camera.Fov;
        if (_orbitCamera != null)
        {
            _defaultOrbitSens = _orbitCamera.OrbitSensitivity;
            _defaultPanSpeed = _orbitCamera.PanSpeed;
        }
    }

    private void ConnectEvents()
    {
        if (_fovSlider != null)
        {
            _fovSlider.ValueChanged += (v) =>
            {
                if (_fovLabel != null) _fovLabel.Text = $"{v:F0}°";
                if (!_isSyncing && _camera != null) _camera.Fov = (float)v;
            };
        }

        if (_orbitSensSlider != null)
        {
            _orbitSensSlider.ValueChanged += (v) =>
            {
                if (_orbitSensLabel != null) _orbitSensLabel.Text = $"{v:F3}";
                if (!_isSyncing && _orbitCamera != null) _orbitCamera.OrbitSensitivity = (float)v;
            };
        }

        if (_panSpeedSlider != null)
        {
            _panSpeedSlider.ValueChanged += (v) =>
            {
                if (_panSpeedLabel != null) _panSpeedLabel.Text = $"{v:F1}";
                if (!_isSyncing && _orbitCamera != null) _orbitCamera.PanSpeed = (float)v;
            };
        }

        if (_btnPresetPortrait != null) _btnPresetPortrait.Pressed += ApplyPortraitPreset;
        if (_btnPresetFullBody != null) _btnPresetFullBody.Pressed += ApplyFullBodyPreset;
        if (_btnPresetCloseup != null) _btnPresetCloseup.Pressed += ApplyCloseupPreset;

        if (_btnResetCamera != null) _btnResetCamera.Pressed += ResetCamera;
    }

    public void SyncUIToScene()
    {
        _isSyncing = true;

        if (_camera != null && _fovSlider != null)
        {
            _fovSlider.Value = _camera.Fov;
            if (_fovLabel != null) _fovLabel.Text = $"{_camera.Fov:F0}°";
        }

        if (_orbitCamera != null)
        {
            if (_orbitSensSlider != null)
            {
                _orbitSensSlider.Value = _orbitCamera.OrbitSensitivity;
                if (_orbitSensLabel != null) _orbitSensLabel.Text = $"{_orbitCamera.OrbitSensitivity:F3}";
            }

            if (_panSpeedSlider != null)
            {
                _panSpeedSlider.Value = _orbitCamera.PanSpeed;
                if (_panSpeedLabel != null) _panSpeedLabel.Text = $"{_orbitCamera.PanSpeed:F1}";
            }
        }

        _isSyncing = false;
    }

    public void ApplyPortraitPreset()
    {
        if (_camera != null)
        {
            _camera.Fov = 35.0f;
            if (_fovSlider != null) _fovSlider.Value = 35.0f;
        }

        if (_orbitCamera != null)
        {
            _orbitCamera.Position = new Vector3(0, 1.45f, 0);
            _orbitCamera.Rotation = new Vector3(-0.05f, 0, 0);
        }
    }

    public void ApplyFullBodyPreset()
    {
        if (_camera != null)
        {
            _camera.Fov = 45.0f;
            if (_fovSlider != null) _fovSlider.Value = 45.0f;
        }

        if (_orbitCamera != null)
        {
            _orbitCamera.Position = new Vector3(0, 1.0f, 0);
            _orbitCamera.Rotation = Vector3.Zero;
        }
    }

    public void ApplyCloseupPreset()
    {
        if (_camera != null)
        {
            _camera.Fov = 25.0f;
            if (_fovSlider != null) _fovSlider.Value = 25.0f;
        }

        if (_orbitCamera != null)
        {
            _orbitCamera.Position = new Vector3(0, 1.60f, 0);
            _orbitCamera.Rotation = new Vector3(-0.02f, 0, 0);
        }
    }

    public void ResetCamera()
    {
        if (_camera != null)
        {
            _camera.Fov = _defaultFov;
        }

        if (_orbitCamera != null)
        {
            _orbitCamera.Position = new Vector3(0, 1.0f, 0);
            _orbitCamera.Rotation = Vector3.Zero;
            _orbitCamera.OrbitSensitivity = _defaultOrbitSens;
            _orbitCamera.PanSpeed = _defaultPanSpeed;
        }

        SyncUIToScene();
    }
}
