using Godot;
using System;

public partial class CameraTabUI : VBoxContainer
{
    [ExportCategory("Scene References")]
    [Export] private Camera3D _camera;
    [Export] private OrbitCamera _orbitCamera;

    [ExportCategory("Projection Controls")]
    [Export] private OptionButton _projectionOption;
    [Export] private Control _sizeGroup;
    [Export] private HSlider _sizeSlider;
    [Export] private Label _sizeLabel;

    [ExportCategory("Camera Angle Controls")]
    [Export] private HSlider _pitchSlider;
    [Export] private Label _pitchLabel;
    [Export] private HSlider _yawSlider;
    [Export] private Label _yawLabel;
    [Export] private HSlider _rollSlider;
    [Export] private Label _rollLabel;
    [Export] private Button _btnResetAngles;

    [ExportCategory("Camera Presets")]
    [Export] private Button _btnPresetPortrait;
    [Export] private Button _btnPresetFullBody;
    [Export] private Button _btnPresetCloseup;

    [ExportCategory("Sensitivity Controls")]
    [Export] private HSlider _orbitSensSlider;
    [Export] private Label _orbitSensLabel;
    [Export] private HSlider _panSpeedSlider;
    [Export] private Label _panSpeedLabel;
    [Export] private HSlider _zoomSensSlider;
    [Export] private Label _zoomSensLabel;

    [ExportCategory("Actions")]
    [Export] private Button _btnResetCamera;
    [Export] private Button _btnRecenterOnModel;
    [Export] private Button _btnResetCameraTransform;

    private float _defaultOrthoSize = 3.0f;
    private float _defaultOrbitSens = 0.005f;
    private float _defaultPanSpeed = 5.0f;
    private float _defaultZoomSens = 0.25f;
    private bool _isSyncing = false;

    public override void _Ready()
    {
        LinkReferences();
        CaptureDefaults();
        ConnectEvents();
        SyncUIToScene();
    }

    public override void _ExitTree()
    {
        if (_orbitCamera != null)
        {
            _orbitCamera.OnCameraRotated -= OnCameraRotatedFromOrbit;
        }
    }

    private void OnCameraRotatedFromOrbit(float p, float y, float r)
    {
        SyncAngles();
    }

    private void LinkReferences()
    {
        _camera ??= GetNodeOrNull<Camera3D>("/root/Main/UIRoot/MainHUD/VBoxContainer/MainSplit/ViewportArea/SubViewportContainer/WorldViewport/CameraPivot/Camera3D")
                 ?? GetNodeOrNull<Camera3D>("/root/Main/CameraPivot/Camera3D")
                 ?? GetTree().Root.FindChild("Camera3D", true, false) as Camera3D;

        _orbitCamera ??= GetNodeOrNull<OrbitCamera>("/root/Main/UIRoot/MainHUD/VBoxContainer/MainSplit/ViewportArea/SubViewportContainer/WorldViewport/CameraPivot")
                      ?? GetNodeOrNull<OrbitCamera>("/root/Main/CameraPivot")
                      ?? GetTree().Root.FindChild("CameraPivot", true, false) as OrbitCamera;
    }

    private void CaptureDefaults()
    {
        if (_camera != null)
        {
            _defaultOrthoSize = _camera.Size > 0 ? _camera.Size : 3.0f;
        }
        if (_orbitCamera != null)
        {
            _defaultOrbitSens = _orbitCamera.OrbitSensitivity;
            _defaultPanSpeed = _orbitCamera.PanSpeed;
            _defaultZoomSens = _orbitCamera.ZoomSensitivity;
        }
    }

    private void ConnectEvents()
    {
        if (_projectionOption != null)
        {
            _projectionOption.ItemSelected += (idx) =>
            {
                var mode = (idx == 1) ? Camera3D.ProjectionType.Orthogonal : Camera3D.ProjectionType.Perspective;
                SetProjection(mode);
            };
        }

        if (_sizeSlider != null)
        {
            _sizeSlider.ValueChanged += (v) =>
            {
                if (_sizeLabel != null) _sizeLabel.Text = $"{v:F1}";
                if (!_isSyncing)
                {
                    if (_camera != null) _camera.Size = (float)v;
                    if (_orbitCamera != null) _orbitCamera.SetOrthoSize((float)v);
                }
            };
        }

        // Angle Sliders
        if (_pitchSlider != null)
        {
            _pitchSlider.ValueChanged += (v) =>
            {
                if (_pitchLabel != null) _pitchLabel.Text = $"{v:F0}°";
                if (!_isSyncing && _orbitCamera != null) _orbitCamera.SetPitchDegrees((float)v);
            };
        }

        if (_yawSlider != null)
        {
            _yawSlider.ValueChanged += (v) =>
            {
                if (_yawLabel != null) _yawLabel.Text = $"{v:F0}°";
                if (!_isSyncing && _orbitCamera != null) _orbitCamera.SetYawDegrees((float)v);
            };
        }

        if (_rollSlider != null)
        {
            _rollSlider.ValueChanged += (v) =>
            {
                if (_rollLabel != null) _rollLabel.Text = $"{v:F0}°";
                if (!_isSyncing && _orbitCamera != null) _orbitCamera.SetRollDegrees((float)v);
            };
        }

        if (_btnResetAngles != null)
        {
            _btnResetAngles.Pressed += () =>
            {
                if (_orbitCamera != null) _orbitCamera.ResetAngles();
                SyncAngles();
            };
        }

        if (_orbitCamera != null)
        {
            _orbitCamera.OnCameraRotated -= OnCameraRotatedFromOrbit;
            _orbitCamera.OnCameraRotated += OnCameraRotatedFromOrbit;
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

        if (_zoomSensSlider != null)
        {
            _zoomSensSlider.ValueChanged += (v) =>
            {
                if (_zoomSensLabel != null) _zoomSensLabel.Text = $"{v:F2}";
                if (!_isSyncing && _orbitCamera != null)
                {
                    _orbitCamera.ZoomSensitivity = (float)v;
                    _orbitCamera.OrthoSizeSensitivity = (float)v;
                    UserSettings.CameraZoomSensitivity = (float)v;
                }
            };
        }

        if (_btnPresetPortrait != null) _btnPresetPortrait.Pressed += ApplyPortraitPreset;
        if (_btnPresetFullBody != null) _btnPresetFullBody.Pressed += ApplyFullBodyPreset;
        if (_btnPresetCloseup != null) _btnPresetCloseup.Pressed += ApplyCloseupPreset;

        if (_btnResetCamera != null) _btnResetCamera.Pressed += ResetCamera;
        if (_btnRecenterOnModel != null) _btnRecenterOnModel.Pressed += RecenterOnModel;
        if (_btnResetCameraTransform != null) _btnResetCameraTransform.Pressed += ResetCameraTransform;
    }

    public void SetProjection(Camera3D.ProjectionType mode)
    {
        LinkReferences();
        if (_camera != null)
        {
            _camera.Projection = mode;
        }

        bool isOrtho = (mode == Camera3D.ProjectionType.Orthogonal);
        if (_sizeGroup != null) _sizeGroup.Visible = isOrtho;

        if (_projectionOption != null)
        {
            _projectionOption.Select(isOrtho ? 1 : 0);
        }

        if (isOrtho && _orbitCamera != null)
        {
            _orbitCamera.SetOrthoSize(_camera?.Size ?? _defaultOrthoSize);

            // Snap to classic isometric view angles (pitch -30°, yaw 45°)
            _orbitCamera.SetPitchDegrees(-30.0f);
            _orbitCamera.SetYawDegrees(45.0f);
            SyncAngles();
        }
    }

    public void SyncUIToScene()
    {
        _isSyncing = true;
        LinkReferences();

        if (_camera != null)
        {
            bool isOrtho = (_camera.Projection == Camera3D.ProjectionType.Orthogonal);
            if (_projectionOption != null) _projectionOption.Select(isOrtho ? 1 : 0);
            if (_sizeGroup != null) _sizeGroup.Visible = isOrtho;

            if (_sizeSlider != null)
            {
                float size = _camera.Size > 0 ? _camera.Size : 3.0f;
                _sizeSlider.Value = size;
                if (_sizeLabel != null) _sizeLabel.Text = $"{size:F1}";
            }
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

            if (_zoomSensSlider != null)
            {
                _zoomSensSlider.Value = _orbitCamera.ZoomSensitivity;
                if (_zoomSensLabel != null) _zoomSensLabel.Text = $"{_orbitCamera.ZoomSensitivity:F2}";
            }

            SyncAngles();
        }

        _isSyncing = false;
    }

    private void SyncAngles()
    {
        if (_orbitCamera == null) return;
        bool prevSync = _isSyncing;
        _isSyncing = true;

        if (_pitchSlider != null)
        {
            _pitchSlider.Value = _orbitCamera.PitchDegrees;
            if (_pitchLabel != null) _pitchLabel.Text = $"{_orbitCamera.PitchDegrees:F0}°";
        }

        if (_yawSlider != null)
        {
            _yawSlider.Value = _orbitCamera.YawDegrees;
            if (_yawLabel != null) _yawLabel.Text = $"{_orbitCamera.YawDegrees:F0}°";
        }

        if (_rollSlider != null)
        {
            _rollSlider.Value = _orbitCamera.RollDegrees;
            if (_rollLabel != null) _rollLabel.Text = $"{_orbitCamera.RollDegrees:F0}°";
        }

        _isSyncing = prevSync;
    }

    public void ApplyPortraitPreset()
    {
        SetProjection(Camera3D.ProjectionType.Perspective);

        if (_orbitCamera != null)
        {
            _orbitCamera.Position = new Vector3(0, 1.45f, 0);
            _orbitCamera.Rotation = new Vector3(-0.05f, 0, 0);
            _orbitCamera.SetTargetZoom(3.5f);
            SyncAngles();
        }
    }

    public void ApplyFullBodyPreset()
    {
        SetProjection(Camera3D.ProjectionType.Perspective);

        if (_orbitCamera != null)
        {
            _orbitCamera.Position = new Vector3(0, 1.0f, 0);
            _orbitCamera.Rotation = Vector3.Zero;
            _orbitCamera.SetTargetZoom(5.0f);
            SyncAngles();
        }
    }

    public void ApplyCloseupPreset()
    {
        SetProjection(Camera3D.ProjectionType.Perspective);

        if (_orbitCamera != null)
        {
            _orbitCamera.Position = new Vector3(0, 1.60f, 0);
            _orbitCamera.Rotation = new Vector3(-0.02f, 0, 0);
            _orbitCamera.SetTargetZoom(2.2f);
            SyncAngles();
        }
    }

    public void ResetCamera()
    {
        ResetCameraTransform();
    }

    public void ResetCameraTransform()
    {
        SetProjection(Camera3D.ProjectionType.Perspective);
        if (_camera != null)
        {
            _camera.Size = _defaultOrthoSize;
        }

        if (_orbitCamera != null)
        {
            _orbitCamera.ResetTransform();
            _orbitCamera.OrbitSensitivity = _defaultOrbitSens;
            _orbitCamera.PanSpeed = _defaultPanSpeed;
            _orbitCamera.ZoomSensitivity = _defaultZoomSens;
            _orbitCamera.OrthoSizeSensitivity = _defaultZoomSens;
            UserSettings.CameraZoomSensitivity = _defaultZoomSens;
            _orbitCamera.SetOrthoSize(_defaultOrthoSize);
        }

        SyncUIToScene();
    }

    public void RecenterOnModel()
    {
        LinkReferences();
        if (_orbitCamera == null) return;

        var vpkLoader = GetNodeOrNull<VpkLoaderTest>("/root/Main/UIRoot/MainHUD/VBoxContainer/MainSplit/ViewportArea/SubViewportContainer/WorldViewport/VpkLoaderTest")
                     ?? GetNodeOrNull<VpkLoaderTest>("/root/Main/VpkLoaderTest")
                     ?? GetTree()?.Root?.FindChild("VpkLoaderTest", true, false) as VpkLoaderTest;

        Node3D heroNode = vpkLoader?.CurrentHeroNode;
        Vector3 targetCenter = new Vector3(0, 1.0f, 0);

        if (heroNode != null && GodotObject.IsInstanceValid(heroNode))
        {
            // Find chest / spine bone
            var skeleton = SearchSkeleton(heroNode);
            if (skeleton != null)
            {
                string[] candidates = { "chest", "spine_3", "spine_2", "spine_1", "pelvis" };
                foreach (var boneName in candidates)
                {
                    int idx = skeleton.FindBone(boneName);
                    if (idx != -1)
                    {
                        targetCenter = (skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(idx)).Origin;
                        break;
                    }
                }
            }
        }

        _orbitCamera.RecenterOnTarget(targetCenter);
    }

    public void ToggleProjection()
    {
        LinkReferences();
        if (_camera == null) return;
        var newType = _camera.Projection == Camera3D.ProjectionType.Perspective
            ? Camera3D.ProjectionType.Orthogonal
            : Camera3D.ProjectionType.Perspective;
        SetProjection(newType);
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
}
