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

    private Camera3D _camera;
    private bool _isOrbiting = false;

    // Current state variables
    private float _yaw = 0f;
    private float _pitch = 0f;
    
    private float _targetZoom = 5f;
    private float _currentZoom = 5f;

    private Vector3 _targetPan;
    private Vector3 _currentPan;

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

        _targetPan = Position;
        _currentPan = Position;

        _currentZoom = _camera.Position.Length();
        if (_currentZoom == 0) _currentZoom = 5f;
        _targetZoom = _currentZoom;
    }

    public override void _Input(InputEvent @event)
    {
        // Orbit (Middle Mouse Button)
        if (@event is InputEventMouseButton mouseBtnEvent)
        {
            if (mouseBtnEvent.ButtonIndex == MouseButton.Middle)
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
            }

            // Zoom (Scroll Wheel)
            if (mouseBtnEvent.Pressed)
            {
                if (mouseBtnEvent.ButtonIndex == MouseButton.WheelUp)
                {
                    _targetZoom = Mathf.Clamp(_targetZoom - ZoomSensitivity, ZoomMin, ZoomMax);
                }
                else if (mouseBtnEvent.ButtonIndex == MouseButton.WheelDown)
                {
                    _targetZoom = Mathf.Clamp(_targetZoom + ZoomSensitivity, ZoomMin, ZoomMax);
                }
            }
        }
        else if (@event is InputEventMouseMotion mouseMotionEvent && _isOrbiting)
        {
            _yaw -= mouseMotionEvent.Relative.X * OrbitSensitivity;
            _pitch -= mouseMotionEvent.Relative.Y * OrbitSensitivity;
            _pitch = Mathf.Clamp(_pitch, PitchMin, PitchMax);
        }
    }

    public override void _Process(double delta)
    {
        float fDelta = (float)delta;

        // Apply rotation
        Rotation = new Vector3(_pitch, _yaw, 0);

        // Apply Zoom smoothly
        _currentZoom = Mathf.Lerp(_currentZoom, _targetZoom, ZoomLerpSpeed * fDelta);
        if (_camera != null)
        {
            _camera.Position = new Vector3(0, 0, _currentZoom);
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
