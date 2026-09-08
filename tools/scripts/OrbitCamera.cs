using Godot;
using System;

public partial class OrbitCamera : Node3D
{
    [ExportGroup("Orbit Settings")]
    [Export] public float OrbitSensitivity = 0.005f;
    [Export] public float PitchMin = -Mathf.Pi / 2.5f; // Clamp so we don't flip
    [Export] public float PitchMax = Mathf.Pi / 2.5f;

    [ExportGroup("Zoom Settings")]
    [Export] public float ZoomSensitivity = 1.0f;
    [Export] public float ZoomMin = 1.0f;
    [Export] public float ZoomMax = 20.0f;
    [Export] public float ZoomLerpSpeed = 10.0f;

    [ExportGroup("Pan Settings")]
    [Export] public float PanSpeed = 5.0f;
    // Bounding box to avoid losing the character
    [Export] public Vector3 MinPanLimit = new Vector3(-10f, -2f, -10f);
    [Export] public Vector3 MaxPanLimit = new Vector3(10f, 10f, 10f);
    [Export] public float PanLerpSpeed = 10.0f;

    [ExportGroup("Orthogonal Settings")]
    [Export] public float OrthoSizeMin = 0.5f;
    [Export] public float OrthoSizeMax = 20.0f;
    [Export] public float OrthoSizeSensitivity = 0.3f;
    [Export] public bool LockOrbitInOrtho { get; set; } = true;

    private Camera3D _camera;
    private bool _isOrbiting = false;

    // Current state variables
    private float _yaw = 0f;
    private float _pitch = 0f;
    private float _roll = 0f;
    
    private float _targetZoom = 5f;
    private float _currentZoom = 5f;
    private float _targetOrthoSize = 3.0f;

    private Vector3 _targetPan;
    private Vector3 _currentPan;

    public event Action<float, float, float> OnCameraRotated;

    public float PitchDegrees => Mathf.RadToDeg(_pitch);
    public float YawDegrees => Mathf.RadToDeg(_yaw);
    public float RollDegrees => Mathf.RadToDeg(_roll);

    public void SetPitchDegrees(float deg)
    {
        _pitch = Mathf.Clamp(Mathf.DegToRad(deg), PitchMin, PitchMax);
        OnCameraRotated?.Invoke(PitchDegrees, YawDegrees, RollDegrees);
    }

    public void SetYawDegrees(float deg)
    {
        _yaw = Mathf.DegToRad(deg);
        OnCameraRotated?.Invoke(PitchDegrees, YawDegrees, RollDegrees);
    }

    public void SetRollDegrees(float deg)
    {
        _roll = Mathf.DegToRad(deg);
        OnCameraRotated?.Invoke(PitchDegrees, YawDegrees, RollDegrees);
    }

    public void ResetAngles()
    {
        _pitch = 0f;
        _yaw = 0f;
        _roll = 0f;
        OnCameraRotated?.Invoke(0f, 0f, 0f);
    }

    public float GetTargetZoom() => _targetZoom;

    public void SetTargetZoom(float zoom)
    {
        _targetZoom = Mathf.Clamp(zoom, ZoomMin, ZoomMax);
    }

    public void SetOrthoSize(float size)
    {
        _targetOrthoSize = Mathf.Clamp(size, OrthoSizeMin, OrthoSizeMax);
        if (_camera != null && _camera.Projection == Camera3D.ProjectionType.Orthogonal)
        {
            _camera.Size = _targetOrthoSize;
        }
    }

    public float GetOrthoSize() => _camera != null ? _camera.Size : _targetOrthoSize;

    public void NudgePan(float deltaX, float deltaY)
    {
        Vector3 right = GlobalTransform.Basis.X.Normalized();
        Vector3 up = Vector3.Up;
        Vector3 move = right * deltaX + up * deltaY;
        _targetPan += move;
        _targetPan.X = Mathf.Clamp(_targetPan.X, MinPanLimit.X, MaxPanLimit.X);
        _targetPan.Y = Mathf.Clamp(_targetPan.Y, MinPanLimit.Y, MaxPanLimit.Y);
        _targetPan.Z = Mathf.Clamp(_targetPan.Z, MinPanLimit.Z, MaxPanLimit.Z);
    }

    public void NudgeRotation(float deltaPitchDeg, float deltaYawDeg)
    {
        _pitch = Mathf.Clamp(_pitch + Mathf.DegToRad(deltaPitchDeg), PitchMin, PitchMax);
        _yaw += Mathf.DegToRad(deltaYawDeg);
        OnCameraRotated?.Invoke(PitchDegrees, YawDegrees, RollDegrees);
    }

    public void RecenterOnTarget(Vector3? targetCenter = null, float? targetZoom = null)
    {
        Vector3 center = targetCenter ?? new Vector3(0, 1.0f, 0);
        _targetPan = new Vector3(
            Mathf.Clamp(center.X, MinPanLimit.X, MaxPanLimit.X),
            Mathf.Clamp(center.Y, MinPanLimit.Y, MaxPanLimit.Y),
            Mathf.Clamp(center.Z, MinPanLimit.Z, MaxPanLimit.Z)
        );
        if (targetZoom.HasValue)
        {
            SetTargetZoom(targetZoom.Value);
        }
    }

    public void ResetTransform()
    {
        _targetPan = new Vector3(0, 1.0f, 0);
        _pitch = 0f;
        _yaw = 0f;
        _roll = 0f;
        _targetZoom = 5.0f;
        OnCameraRotated?.Invoke(0f, 0f, 0f);
    }

    public override void _Ready()
    {
        _camera = GetNodeOrNull<Camera3D>("Camera3D");
        if (_camera == null)
        {
            GD.PrintErr("OrbitCamera requires a Camera3D child named 'Camera3D'.");
            SetProcess(false);
            SetProcessInput(false);
            return;
        }

        // Initialize state
        Vector3 rot = Rotation;
        _pitch = rot.X;
        _yaw = rot.Y;
        _roll = rot.Z;

        _targetPan = Position;
        _currentPan = Position;

        _currentZoom = _camera.Position.Length();
        if (_currentZoom == 0) _currentZoom = 5f;
        _targetZoom = _currentZoom;
        _targetOrthoSize = _camera.Size > 0 ? _camera.Size : 3.0f;
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        HandleCameraInput(@event);
    }

    public override void _Input(InputEvent @event)
    {
        HandleCameraInput(@event);
    }

    private void HandleCameraInput(InputEvent @event)
    {
        // Orbit (Middle Mouse Button / Right Mouse Button)
        if (@event is InputEventMouseButton mouseBtnEvent)
        {
            if (mouseBtnEvent.ButtonIndex == MouseButton.Middle || mouseBtnEvent.ButtonIndex == MouseButton.Right)
            {
                _isOrbiting = mouseBtnEvent.Pressed;
                if (_isOrbiting)
                {
                    Input.MouseMode = Input.MouseModeEnum.Captured;
                }
                else
                {
                    Input.MouseMode = Input.MouseModeEnum.Visible;
                }
                GetViewport()?.SetInputAsHandled();
                return;
            }

            // Zoom (Scroll Wheel)
            if (mouseBtnEvent.Pressed)
            {
                bool isOrtho = _camera != null && _camera.Projection == Camera3D.ProjectionType.Orthogonal;
                if (mouseBtnEvent.ButtonIndex == MouseButton.WheelUp)
                {
                    if (isOrtho)
                    {
                        _targetOrthoSize = Mathf.Clamp(_targetOrthoSize - OrthoSizeSensitivity, OrthoSizeMin, OrthoSizeMax);
                    }
                    else
                    {
                        _targetZoom = Mathf.Clamp(_targetZoom - ZoomSensitivity, ZoomMin, ZoomMax);
                    }
                    GetViewport()?.SetInputAsHandled();
                    return;
                }
                else if (mouseBtnEvent.ButtonIndex == MouseButton.WheelDown)
                {
                    if (isOrtho)
                    {
                        _targetOrthoSize = Mathf.Clamp(_targetOrthoSize + OrthoSizeSensitivity, OrthoSizeMin, OrthoSizeMax);
                    }
                    else
                    {
                        _targetZoom = Mathf.Clamp(_targetZoom + ZoomSensitivity, ZoomMin, ZoomMax);
                    }
                    GetViewport()?.SetInputAsHandled();
                    return;
                }
            }
        }
        else if (@event is InputEventMouseMotion mouseMotionEvent && _isOrbiting)
        {
            bool isOrtho = _camera != null && _camera.Projection == Camera3D.ProjectionType.Orthogonal;
            if (isOrtho && LockOrbitInOrtho)
            {
                // Lock 3D orbit rotation in Orthographic mode
                return;
            }

            _yaw -= mouseMotionEvent.Relative.X * OrbitSensitivity;
            _pitch -= mouseMotionEvent.Relative.Y * OrbitSensitivity;
            _pitch = Mathf.Clamp(_pitch, PitchMin, PitchMax);
            OnCameraRotated?.Invoke(PitchDegrees, YawDegrees, RollDegrees);
            GetViewport()?.SetInputAsHandled();
            return;
        }
        else if (@event is InputEventKey keyEvent && keyEvent.Pressed && keyEvent.Keycode == Key.Escape)
        {
            if (_isOrbiting)
            {
                _isOrbiting = false;
                Input.MouseMode = Input.MouseModeEnum.Visible;
                GetViewport()?.SetInputAsHandled();
                return;
            }
        }
    }

    public override void _Notification(int what)
    {
        if (what == NotificationApplicationFocusOut)
        {
            if (_isOrbiting)
            {
                _isOrbiting = false;
                Input.MouseMode = Input.MouseModeEnum.Visible;
            }
        }
    }

    public override void _Process(double delta)
    {
        float fDelta = (float)delta;

        // Apply rotation
        Rotation = new Vector3(_pitch, _yaw, _roll);

        // Apply Zoom smoothly
        _currentZoom = Mathf.Lerp(_currentZoom, _targetZoom, ZoomLerpSpeed * fDelta);
        if (_camera != null)
        {
            _camera.Position = new Vector3(0, 0, _currentZoom);
            if (_camera.Projection == Camera3D.ProjectionType.Orthogonal)
            {
                _camera.Size = Mathf.Lerp(_camera.Size, _targetOrthoSize, ZoomLerpSpeed * fDelta);
            }
        }

        // Handle Panning (WASD or Arrows)
        Vector3 inputDir = Vector3.Zero;
        
        // W/S map to Up/Down (Y-axis pan)
        if (Input.IsPhysicalKeyPressed(Key.W) || Input.IsPhysicalKeyPressed(Key.Up)) inputDir.Y += 1;
        if (Input.IsPhysicalKeyPressed(Key.S) || Input.IsPhysicalKeyPressed(Key.Down)) inputDir.Y -= 1;
        
        // A/D map to Left/Right (X-axis pan)
        if (Input.IsPhysicalKeyPressed(Key.A) || Input.IsPhysicalKeyPressed(Key.Left)) inputDir.X -= 1;
        if (Input.IsPhysicalKeyPressed(Key.D) || Input.IsPhysicalKeyPressed(Key.Right)) inputDir.X += 1;

        if (inputDir != Vector3.Zero)
        {
            // Transform input to camera space (ignoring pitch to keep movement horizontal unless specified)
            Vector3 forward = -GlobalTransform.Basis.Z;
            forward.Y = 0;
            forward = forward.Normalized();

            Vector3 right = GlobalTransform.Basis.X;
            right.Y = 0;
            right = right.Normalized();

            Vector3 up = Vector3.Up;

            Vector3 moveDir = (right * inputDir.X + up * inputDir.Y + forward * inputDir.Z).Normalized();
            
            _targetPan += moveDir * PanSpeed * fDelta;

            // Clamp Target Pan within bounding box
            _targetPan.X = Mathf.Clamp(_targetPan.X, MinPanLimit.X, MaxPanLimit.X);
            _targetPan.Y = Mathf.Clamp(_targetPan.Y, MinPanLimit.Y, MaxPanLimit.Y);
            _targetPan.Z = Mathf.Clamp(_targetPan.Z, MinPanLimit.Z, MaxPanLimit.Z);
        }

        // Apply Pan smoothly
        _currentPan = _currentPan.Lerp(_targetPan, PanLerpSpeed * fDelta);
        Position = _currentPan;
    }
}
