using Godot;
using System;

public partial class GizmoOverlaySync : SubViewport
{
    private SubViewport _worldViewport;
    private Camera3D _mainCamera;
    private Camera3D _gizmoCamera;

    public override void _Ready()
    {
        _gizmoCamera = GetNodeOrNull<Camera3D>("GizmoCamera3D");
        if (_gizmoCamera != null)
        {
            _gizmoCamera.CullMask = 2; // Layer 2 only (Gizmo & Bone Visuals)
        }

        FindReferences();
    }

    private void FindReferences()
    {
        _worldViewport ??= GetNodeOrNull<SubViewport>("/root/Main/UIRoot/MainHUD/VBoxContainer/MainSplit/ViewportArea/SubViewportContainer/WorldViewport")
                        ?? GetTree().Root.FindChild("WorldViewport", true, false) as SubViewport;

        if (_worldViewport != null)
        {
            World3D = _worldViewport.FindWorld3D();
            _mainCamera = _worldViewport.GetCamera3D();
            Size = _worldViewport.Size;
        }
    }

    public override void _Process(double delta)
    {
        if (_worldViewport == null || _mainCamera == null || World3D == null)
        {
            FindReferences();
        }

        if (_worldViewport != null)
        {
            if (Size != _worldViewport.Size)
            {
                Size = _worldViewport.Size;
            }
        }

        if (_mainCamera != null && _gizmoCamera != null)
        {
            _gizmoCamera.GlobalTransform = _mainCamera.GlobalTransform;
            _gizmoCamera.Fov = _mainCamera.Fov;
            _gizmoCamera.Size = _mainCamera.Size;
            _gizmoCamera.Projection = _mainCamera.Projection;
            _gizmoCamera.Near = _mainCamera.Near;
            _gizmoCamera.Far = _mainCamera.Far;
        }
    }
}
