using System;
using System.Collections.Generic;
using Godot;
using Gizmo3DPlugin;

namespace DeadlockPlayground.Maps.Editor;

public enum MapGizmoMode
{
    Move,
    Rotate,
    Scale
}

public interface IMapUndoAction
{
    string ActionName { get; }
    void Undo();
    void Redo();
}

/// <summary>
/// Manages 3D object selection, Gizmo3D manipulation, grid/angle snapping,
/// and undo/redo history for Deadlock Playground Forge Mode.
/// </summary>
public partial class MapSelectionManager : Node
{
    public static MapSelectionManager Instance { get; private set; }

    [Signal] public delegate void SelectionChangedEventHandler(string objectId);
    [Signal] public delegate void ObjectTransformedEventHandler(string objectId);
    [Signal] public delegate void UndoRedoStateChangedEventHandler(bool canUndo, bool canRedo);

    private Gizmo3D _gizmo;
    private Node3D _dummyGizmoTarget;
    private Node3D _selectedNode;
    private MapObjectData _selectedData;
    private MeshInstance3D _selectionBoxMesh;
    private Transform3D _transformBeforeDrag;
    private bool _isDragging = false;

    // Snapping configuration
    public float TranslateSnap { get; set; } = 0.5f; // 0.5m default
    public float RotateSnap { get; set; } = 15.0f;    // 15 deg default
    public float ScaleSnap { get; set; } = 0.1f;
    public bool IsSnappingEnabled { get; set; } = true;

    // Undo / Redo Stack
    private readonly Stack<IMapUndoAction> _undoStack = new();
    private readonly Stack<IMapUndoAction> _redoStack = new();

    public MapObjectData SelectedData => _selectedData;
    public Node3D SelectedNode => _selectedNode;
    public string SelectedId => _selectedData?.Id;
    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;

    public bool IsGizmoInteracting => _gizmo != null && GodotObject.IsInstanceValid(_gizmo) && (_gizmo.Editing || _gizmo.Hovering);

    /// <summary>
    /// Checks if the viewport mouse position intersects the 3D gizmo or if the gizmo is actively being dragged.
    /// </summary>
    public bool IsHitGizmo(Vector2 screenPos)
    {
        if (_gizmo == null || !GodotObject.IsInstanceValid(_gizmo) || !_gizmo.Visible) return false;
        return _gizmo.Editing || _gizmo.Hovering || _gizmo.TestHit(screenPos);
    }

    public override void _EnterTree()
    {
        Instance = this;
    }

    public void Initialize(Node3D viewportRoot)
    {
        EnsureGizmoValid(viewportRoot);
    }

    public void ResetGizmo()
    {
        ClearSelection();

        if (_gizmo != null && GodotObject.IsInstanceValid(_gizmo))
        {
            _gizmo.TransformChanged -= OnGizmoTransformChanged;
            _gizmo.TransformEnd -= OnGizmoTransformEnd;
            _gizmo.GetParent()?.RemoveChild(_gizmo);
            _gizmo.QueueFree();
            _gizmo = null;
        }

        if (_dummyGizmoTarget != null && GodotObject.IsInstanceValid(_dummyGizmoTarget))
        {
            _dummyGizmoTarget.GetParent()?.RemoveChild(_dummyGizmoTarget);
            _dummyGizmoTarget.QueueFree();
            _dummyGizmoTarget = null;
        }

        if (_selectionBoxMesh != null && GodotObject.IsInstanceValid(_selectionBoxMesh))
        {
            _selectionBoxMesh.GetParent()?.RemoveChild(_selectionBoxMesh);
            _selectionBoxMesh.QueueFree();
            _selectionBoxMesh = null;
        }
    }

    private Node3D ResolveGizmoParent(Node3D candidate)
    {
        if (candidate != null && GodotObject.IsInstanceValid(candidate) && candidate.IsInsideTree())
            return candidate;

        var forgeRoot = MapEditorManager.Instance?.ForgeStageRoot;
        if (forgeRoot != null && GodotObject.IsInstanceValid(forgeRoot) && forgeRoot.IsInsideTree())
            return forgeRoot;

        var tree = GetTree() ?? Engine.GetMainLoop() as SceneTree;
        var worldVp = (tree?.Root?.FindChild("WorldViewport", true, false) as Node)
                   ?? (tree?.Root?.FindChild("SubViewport", true, false) as Node);
        if (worldVp is Node3D n3d && GodotObject.IsInstanceValid(n3d) && n3d.IsInsideTree())
            return n3d;

        return null;
    }

    public void EnsureGizmoValid(Node3D parentCandidate)
    {
        var parent = ResolveGizmoParent(parentCandidate);
        if (parent == null) return;

        // Ensure dummy target exists and is in tree
        if (_dummyGizmoTarget == null || !GodotObject.IsInstanceValid(_dummyGizmoTarget) || !_dummyGizmoTarget.IsInsideTree())
        {
            if (_dummyGizmoTarget != null && GodotObject.IsInstanceValid(_dummyGizmoTarget))
            {
                _dummyGizmoTarget.GetParent()?.RemoveChild(_dummyGizmoTarget);
                _dummyGizmoTarget.QueueFree();
            }
            _dummyGizmoTarget = new Node3D { Name = "ForgeGizmoDummy" };
            parent.AddChild(_dummyGizmoTarget);
        }

        // Ensure Gizmo3D exists and is in tree
        if (_gizmo == null || !GodotObject.IsInstanceValid(_gizmo) || !_gizmo.IsInsideTree())
        {
            if (_gizmo != null && GodotObject.IsInstanceValid(_gizmo))
            {
                _gizmo.TransformChanged -= OnGizmoTransformChanged;
                _gizmo.TransformEnd -= OnGizmoTransformEnd;
                _gizmo.GetParent()?.RemoveChild(_gizmo);
                _gizmo.QueueFree();
            }
            _gizmo = new Gizmo3D
            {
                Name = "MapForgeGizmo3D",
                Mode = Gizmo3D.ToolMode.Move,
                Layers = 1,
                Visible = false
            };
            _gizmo.Layers &= ~(1u << 20);
            parent.AddChild(_gizmo);

            _gizmo.TransformChanged += OnGizmoTransformChanged;
            _gizmo.TransformEnd += OnGizmoTransformEnd;
        }

        // Ensure Selection Outline Follower Mesh exists and is in tree
        if (_selectionBoxMesh == null || !GodotObject.IsInstanceValid(_selectionBoxMesh) || !_selectionBoxMesh.IsInsideTree())
        {
            if (_selectionBoxMesh != null && GodotObject.IsInstanceValid(_selectionBoxMesh))
            {
                _selectionBoxMesh.GetParent()?.RemoveChild(_selectionBoxMesh);
                _selectionBoxMesh.QueueFree();
            }
            var boxMesh = new BoxMesh();
            var boxMat = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                AlbedoColor = new Color(0.2f, 0.8f, 1.0f, 0.9f),
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled
            };
            _selectionBoxMesh = new MeshInstance3D
            {
                Name = "SelectionOutlineFollower",
                Mesh = boxMesh,
                MaterialOverride = boxMat,
                Visible = false
            };
            _selectionBoxMesh.Layers &= ~(1u << 20);
            parent.AddChild(_selectionBoxMesh);
        }

        ApplySnappingToGizmo();
    }

    public void SetGizmoMode(MapGizmoMode mode)
    {
        if (_gizmo == null || !GodotObject.IsInstanceValid(_gizmo)) return;

        switch (mode)
        {
            case MapGizmoMode.Move:
                _gizmo.Mode = Gizmo3D.ToolMode.Move;
                break;
            case MapGizmoMode.Rotate:
                _gizmo.Mode = Gizmo3D.ToolMode.Rotate;
                break;
            case MapGizmoMode.Scale:
                _gizmo.Mode = Gizmo3D.ToolMode.Scale;
                break;
        }
    }

    public void SetSnapping(bool enabled, float translateSnap = 0.5f, float rotateSnap = 15.0f)
    {
        IsSnappingEnabled = enabled;
        TranslateSnap = translateSnap;
        RotateSnap = rotateSnap;
        ApplySnappingToGizmo();
    }

    private void ApplySnappingToGizmo()
    {
        if (_gizmo == null || !GodotObject.IsInstanceValid(_gizmo)) return;
        _gizmo.TranslateSnap = TranslateSnap;
        _gizmo.RotateSnap = RotateSnap;
    }

    public void SelectObject(Node3D node, MapObjectData data)
    {
        _selectedNode = node;
        _selectedData = data;

        if (node != null && data != null && GodotObject.IsInstanceValid(node))
        {
            EnsureGizmoValid(node.GetParent() as Node3D ?? MapEditorManager.Instance?.ForgeStageRoot);

            if (_dummyGizmoTarget != null && GodotObject.IsInstanceValid(_dummyGizmoTarget))
            {
                _dummyGizmoTarget.GlobalTransform = node.GlobalTransform;
            }

            if (_gizmo != null && GodotObject.IsInstanceValid(_gizmo))
            {
                _gizmo.ClearSelection();
                if (_dummyGizmoTarget != null && GodotObject.IsInstanceValid(_dummyGizmoTarget))
                {
                    _gizmo.Select(_dummyGizmoTarget);
                }
                _gizmo.Visible = true;
            }

            UpdateSelectionVisual(node);
            EmitSignal(SignalName.SelectionChanged, data.Id);
        }
        else
        {
            ClearSelection();
        }
    }

    public void ClearSelection()
    {
        _selectedNode = null;
        _selectedData = null;

        if (_gizmo != null && GodotObject.IsInstanceValid(_gizmo))
        {
            _gizmo.ClearSelection();
            _gizmo.Visible = false;
        }

        if (_selectionBoxMesh != null && GodotObject.IsInstanceValid(_selectionBoxMesh))
        {
            _selectionBoxMesh.Visible = false;
        }

        EmitSignal(SignalName.SelectionChanged, "");
    }

    private void UpdateSelectionVisual(Node3D node)
    {
        if (_selectionBoxMesh == null || !GodotObject.IsInstanceValid(node)) return;

        // Calculate combined AABB
        Aabb totalAabb = new Aabb();
        bool hasAabb = false;

        foreach (var child in node.FindChildren("*", "MeshInstance3D", true, false))
        {
            if (child is MeshInstance3D mi && mi.Mesh != null)
            {
                var meshAabb = mi.Mesh.GetAabb();
                if (!hasAabb)
                {
                    totalAabb = meshAabb;
                    hasAabb = true;
                }
                else
                {
                    totalAabb = totalAabb.Merge(meshAabb);
                }
            }
        }

        if (!hasAabb || totalAabb.Size.LengthSquared() < 1e-4f)
        {
            totalAabb = new Aabb(new Vector3(-0.5f, 0, -0.5f), Vector3.One);
        }

        var boxMesh = new BoxMesh { Size = totalAabb.Size * 1.05f };
        _selectionBoxMesh.Mesh = boxMesh;
        _selectionBoxMesh.GlobalTransform = new Transform3D(node.GlobalBasis, node.GlobalPosition + totalAabb.GetCenter());
        _selectionBoxMesh.Visible = true;
    }

    private void OnGizmoTransformChanged(int mode, Vector3 value)
    {
        if (_selectedNode == null || !GodotObject.IsInstanceValid(_selectedNode)) return;

        if (!_isDragging)
        {
            _isDragging = true;
            _transformBeforeDrag = _selectedNode.Transform;
        }

        if (_dummyGizmoTarget == null || !GodotObject.IsInstanceValid(_dummyGizmoTarget)) return;

        // Apply dummy target's transform to selected node
        var newTransform = _dummyGizmoTarget.GlobalTransform;

        // Apply grid/angle snapping if enabled
        if (IsSnappingEnabled)
        {
            if (mode == (int)Gizmo3D.ToolMode.Move && TranslateSnap > 0.001f)
            {
                var p = newTransform.Origin;
                p.X = Mathf.Round(p.X / TranslateSnap) * TranslateSnap;
                p.Y = Mathf.Round(p.Y / TranslateSnap) * TranslateSnap;
                p.Z = Mathf.Round(p.Z / TranslateSnap) * TranslateSnap;
                newTransform.Origin = p;
            }
            else if (mode == (int)Gizmo3D.ToolMode.Rotate && RotateSnap > 0.001f)
            {
                var euler = newTransform.Basis.GetEuler();
                float snapRad = Mathf.DegToRad(RotateSnap);
                euler.X = Mathf.Round(euler.X / snapRad) * snapRad;
                euler.Y = Mathf.Round(euler.Y / snapRad) * snapRad;
                euler.Z = Mathf.Round(euler.Z / snapRad) * snapRad;
                newTransform.Basis = Basis.FromEuler(euler).Scaled(newTransform.Basis.Scale);
            }
        }

        _selectedNode.GlobalTransform = newTransform;
        _selectedData?.SetFromTransform3D(_selectedNode.Transform);

        UpdateSelectionVisual(_selectedNode);
        EmitSignal(SignalName.ObjectTransformed, _selectedData?.Id ?? "");
    }

    private void OnGizmoTransformEnd(int mode)
    {
        if (_isDragging && _selectedNode != null && _selectedData != null)
        {
            _isDragging = false;
            var endTransform = _selectedNode.Transform;
            if (_transformBeforeDrag != endTransform)
            {
                var action = new TransformUndoAction(_selectedNode, _selectedData, _transformBeforeDrag, endTransform, this);
                PushUndoAction(action);
            }
        }
    }

    public void PushUndoAction(IMapUndoAction action)
    {
        _undoStack.Push(action);
        _redoStack.Clear();
        EmitSignal(SignalName.UndoRedoStateChanged, CanUndo, CanRedo);
    }

    public void Undo()
    {
        if (_undoStack.Count == 0) return;

        var action = _undoStack.Pop();
        action.Undo();
        _redoStack.Push(action);

        if (_selectedNode != null && GodotObject.IsInstanceValid(_selectedNode))
        {
            EnsureGizmoValid(_selectedNode.GetParent() as Node3D);
            if (_dummyGizmoTarget != null && GodotObject.IsInstanceValid(_dummyGizmoTarget))
            {
                _dummyGizmoTarget.GlobalTransform = _selectedNode.GlobalTransform;
            }
            UpdateSelectionVisual(_selectedNode);
        }

        EmitSignal(SignalName.UndoRedoStateChanged, CanUndo, CanRedo);
    }

    public void Redo()
    {
        if (_redoStack.Count == 0) return;

        var action = _redoStack.Pop();
        action.Redo();
        _undoStack.Push(action);

        if (_selectedNode != null && GodotObject.IsInstanceValid(_selectedNode))
        {
            EnsureGizmoValid(_selectedNode.GetParent() as Node3D);
            if (_dummyGizmoTarget != null && GodotObject.IsInstanceValid(_dummyGizmoTarget))
            {
                _dummyGizmoTarget.GlobalTransform = _selectedNode.GlobalTransform;
            }
            UpdateSelectionVisual(_selectedNode);
        }

        EmitSignal(SignalName.UndoRedoStateChanged, CanUndo, CanRedo);
    }

    public void NotifyTransformDirectlyChanged()
    {
        if (_selectedNode != null && GodotObject.IsInstanceValid(_selectedNode))
        {
            EnsureGizmoValid(_selectedNode.GetParent() as Node3D);
            if (_dummyGizmoTarget != null && GodotObject.IsInstanceValid(_dummyGizmoTarget))
            {
                _dummyGizmoTarget.GlobalTransform = _selectedNode.GlobalTransform;
            }
            UpdateSelectionVisual(_selectedNode);
            EmitSignal(SignalName.ObjectTransformed, _selectedData?.Id ?? "");
        }
    }
}

/// <summary>
/// Undo/Redo action for 3D object transformation changes.
/// </summary>
public class TransformUndoAction : IMapUndoAction
{
    public string ActionName => "Transform Object";
    private readonly Node3D _node;
    private readonly MapObjectData _data;
    private readonly Transform3D _before;
    private readonly Transform3D _after;
    private readonly MapSelectionManager _manager;

    public TransformUndoAction(Node3D node, MapObjectData data, Transform3D before, Transform3D after, MapSelectionManager manager)
    {
        _node = node;
        _data = data;
        _before = before;
        _after = after;
        _manager = manager;
    }

    public void Undo()
    {
        if (GodotObject.IsInstanceValid(_node))
        {
            _node.Transform = _before;
            _data?.SetFromTransform3D(_before);
            _manager?.NotifyTransformDirectlyChanged();
        }
    }

    public void Redo()
    {
        if (GodotObject.IsInstanceValid(_node))
        {
            _node.Transform = _after;
            _data?.SetFromTransform3D(_after);
            _manager?.NotifyTransformDirectlyChanged();
        }
    }
}
