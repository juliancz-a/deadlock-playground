using System;
using Godot;
using DeadlockPlayground.Maps.Editor;

namespace DeadlockPlayground.UI;

/// <summary>
/// Selected Object Property Inspector panel allowing real-time editing of
/// Transform, Color Tint (rendercolor), In-game collision mode (solid 6/2/0), and light parameters.
/// </summary>
public partial class MapObjectInspectorUI : PanelContainer
{
    [Export] private LineEdit _nameEdit;
    [Export] private Label _typeLabel;
    [Export] private Label _modelLabel;

    // Transform SpinBoxes
    [Export] private SpinBox _posX;
    [Export] private SpinBox _posY;
    [Export] private SpinBox _posZ;

    [Export] private SpinBox _rotX;
    [Export] private SpinBox _rotY;
    [Export] private SpinBox _rotZ;

    [Export] private SpinBox _scaleX;
    [Export] private SpinBox _scaleY;
    [Export] private SpinBox _scaleZ;
    [Export] private CheckBox _uniformScaleCheck;

    // Visual & Physics
    [Export] private ColorPickerButton _tintPicker;
    [Export] private OptionButton _solidModeOption;

    // Lighting Controls
    [Export] private VBoxContainer _lightGroup;
    [Export] private ColorPickerButton _lightColorPicker;
    [Export] private HSlider _lightEnergySlider;
    [Export] private Label _lightEnergyLabel;
    [Export] private HSlider _lightRangeSlider;
    [Export] private Label _lightRangeLabel;
    [Export] private HSlider _spotAngleSlider;
    [Export] private Label _spotAngleLabel;

    // Actions
    [Export] private Button _btnDuplicate;
    [Export] private Button _btnDelete;
    [Export] private Button _btnFocus;

    private bool _isUpdatingUi = false;

    public override void _Ready()
    {
        LinkReferences();
        WireSignals();
    }

    private void LinkReferences()
    {
        _nameEdit ??= FindChild("NameEdit", true, false) as LineEdit;
        _typeLabel ??= FindChild("TypeLabel", true, false) as Label;
        _modelLabel ??= FindChild("ModelLabel", true, false) as Label;

        _posX ??= FindChild("PosX", true, false) as SpinBox;
        _posY ??= FindChild("PosY", true, false) as SpinBox;
        _posZ ??= FindChild("PosZ", true, false) as SpinBox;

        _rotX ??= FindChild("RotX", true, false) as SpinBox;
        _rotY ??= FindChild("RotY", true, false) as SpinBox;
        _rotZ ??= FindChild("RotZ", true, false) as SpinBox;

        _scaleX ??= FindChild("ScaleX", true, false) as SpinBox;
        _scaleY ??= FindChild("ScaleY", true, false) as SpinBox;
        _scaleZ ??= FindChild("ScaleZ", true, false) as SpinBox;
        _uniformScaleCheck ??= FindChild("UniformScaleCheck", true, false) as CheckBox;

        _tintPicker ??= FindChild("TintPicker", true, false) as ColorPickerButton;
        _solidModeOption ??= FindChild("SolidModeOption", true, false) as OptionButton;

        _lightGroup ??= FindChild("LightGroup", true, false) as VBoxContainer;
        _lightColorPicker ??= FindChild("LightColorPicker", true, false) as ColorPickerButton;
        _lightEnergySlider ??= FindChild("LightEnergySlider", true, false) as HSlider;
        _lightEnergyLabel ??= FindChild("LightEnergyLabel", true, false) as Label;
        _lightRangeSlider ??= FindChild("LightRangeSlider", true, false) as HSlider;
        _lightRangeLabel ??= FindChild("LightRangeLabel", true, false) as Label;
        _spotAngleSlider ??= FindChild("SpotAngleSlider", true, false) as HSlider;
        _spotAngleLabel ??= FindChild("SpotAngleLabel", true, false) as Label;

        _btnDuplicate ??= FindChild("BtnDuplicate", true, false) as Button;
        _btnDelete ??= FindChild("BtnDelete", true, false) as Button;
        _btnFocus ??= FindChild("BtnFocus", true, false) as Button;

        if (_btnDuplicate != null)
        {
            _btnDuplicate.Icon = GD.Load<Texture2D>("res://assets/at-icons/duplicate.svg");
        }
        if (_btnDelete != null)
        {
            _btnDelete.Icon = GD.Load<Texture2D>("res://assets/at-icons/trash.svg");
        }

        // Populate SolidMode options
        if (_solidModeOption != null)
        {
            _solidModeOption.Clear();
            _solidModeOption.AddItem("Solid (Walkable Havok - solid=6)", 0);
            _solidModeOption.AddItem("Bounding Box Solid (solid=2)", 1);
            _solidModeOption.AddItem("Non-Solid Decorative (solid=0)", 2);
        }
    }

    private void WireSignals()
    {
        if (MapSelectionManager.Instance != null)
        {
            MapSelectionManager.Instance.SelectionChanged += OnSelectionChanged;
            MapSelectionManager.Instance.ObjectTransformed += OnObjectTransformed;
        }

        if (_nameEdit != null)
            _nameEdit.TextChanged += OnNameChanged;

        // Transform SpinBoxes
        if (_posX != null) _posX.ValueChanged += _ => OnTransformSpinBoxChanged();
        if (_posY != null) _posY.ValueChanged += _ => OnTransformSpinBoxChanged();
        if (_posZ != null) _posZ.ValueChanged += _ => OnTransformSpinBoxChanged();

        if (_rotX != null) _rotX.ValueChanged += _ => OnTransformSpinBoxChanged();
        if (_rotY != null) _rotY.ValueChanged += _ => OnTransformSpinBoxChanged();
        if (_rotZ != null) _rotZ.ValueChanged += _ => OnTransformSpinBoxChanged();

        if (_scaleX != null) _scaleX.ValueChanged += OnScaleXChanged;
        if (_scaleY != null) _scaleY.ValueChanged += _ => OnTransformSpinBoxChanged();
        if (_scaleZ != null) _scaleZ.ValueChanged += _ => OnTransformSpinBoxChanged();

        if (_tintPicker != null)
            _tintPicker.ColorChanged += OnTintChanged;

        if (_solidModeOption != null)
            _solidModeOption.ItemSelected += OnSolidModeChanged;

        // Light sliders
        if (_lightColorPicker != null) _lightColorPicker.ColorChanged += OnLightColorChanged;
        if (_lightEnergySlider != null) _lightEnergySlider.ValueChanged += OnLightEnergyChanged;
        if (_lightRangeSlider != null) _lightRangeSlider.ValueChanged += OnLightRangeChanged;
        if (_spotAngleSlider != null) _spotAngleSlider.ValueChanged += OnSpotAngleChanged;

        // Actions
        if (_btnDuplicate != null) _btnDuplicate.Pressed += () => MapEditorManager.Instance?.DuplicateSelected();
        if (_btnDelete != null) _btnDelete.Pressed += () => MapEditorManager.Instance?.DeleteSelected();
    }

    private void OnSelectionChanged(string objectId)
    {
        var data = MapSelectionManager.Instance?.SelectedData;
        if (data == null)
        {
            Visible = false;
            return;
        }

        Visible = true;
        PopulateFromData(data);
    }

    private void OnObjectTransformed(string objectId)
    {
        var data = MapSelectionManager.Instance?.SelectedData;
        if (data != null)
        {
            UpdateTransformSpinBoxes(data);
        }
    }

    private void PopulateFromData(MapObjectData data)
    {
        _isUpdatingUi = true;

        if (_nameEdit != null) _nameEdit.Text = data.Name;
        if (_typeLabel != null) _typeLabel.Text = $"Type: {data.EntityType}";
        if (_modelLabel != null) _modelLabel.Text = string.IsNullOrWhiteSpace(data.ModelPath) ? "-" : System.IO.Path.GetFileName(data.ModelPath);

        UpdateTransformSpinBoxes(data);

        if (_tintPicker != null) _tintPicker.Color = data.ColorTint;

        if (_solidModeOption != null)
        {
            int idx = data.SolidMode switch
            {
                6 => 0,
                2 => 1,
                _ => 2
            };
            _solidModeOption.Select(idx);
        }

        // Lighting group visibility
        bool isLight = data.EntityType is MapEntitySpawner.EntityLightOmni or MapEntitySpawner.EntityLightSpot or MapEntitySpawner.EntityLightSun;
        if (_lightGroup != null)
        {
            _lightGroup.Visible = isLight;
            if (isLight)
            {
                if (_lightColorPicker != null) _lightColorPicker.Color = data.LightColor;
                if (_lightEnergySlider != null) _lightEnergySlider.Value = data.LightEnergy;
                if (_lightEnergyLabel != null) _lightEnergyLabel.Text = $"{data.LightEnergy:F1}x";
                if (_lightRangeSlider != null) _lightRangeSlider.Value = data.LightRange;
                if (_lightRangeLabel != null) _lightRangeLabel.Text = $"{data.LightRange:F1} m";
                if (_spotAngleSlider != null)
                {
                    _spotAngleSlider.Visible = data.EntityType == MapEntitySpawner.EntityLightSpot;
                    _spotAngleSlider.Value = data.SpotAngle;
                }
                if (_spotAngleLabel != null)
                {
                    _spotAngleLabel.Visible = data.EntityType == MapEntitySpawner.EntityLightSpot;
                    _spotAngleLabel.Text = $"{data.SpotAngle:F0}°";
                }
            }
        }

        _isUpdatingUi = false;
    }

    private void UpdateTransformSpinBoxes(MapObjectData data)
    {
        _isUpdatingUi = true;

        if (_posX != null) _posX.Value = data.Position.X;
        if (_posY != null) _posY.Value = data.Position.Y;
        if (_posZ != null) _posZ.Value = data.Position.Z;

        if (_rotX != null) _rotX.Value = data.RotationDegrees.X;
        if (_rotY != null) _rotY.Value = data.RotationDegrees.Y;
        if (_rotZ != null) _rotZ.Value = data.RotationDegrees.Z;

        if (_scaleX != null) _scaleX.Value = data.Scale.X;
        if (_scaleY != null) _scaleY.Value = data.Scale.Y;
        if (_scaleZ != null) _scaleZ.Value = data.Scale.Z;

        _isUpdatingUi = false;
    }

    private void OnTransformSpinBoxChanged()
    {
        if (_isUpdatingUi) return;

        var data = MapSelectionManager.Instance?.SelectedData;
        var node = MapSelectionManager.Instance?.SelectedNode;
        if (data == null || node == null) return;

        data.Position = new Vector3((float)_posX.Value, (float)_posY.Value, (float)_posZ.Value);
        data.RotationDegrees = new Vector3((float)_rotX.Value, (float)_rotY.Value, (float)_rotZ.Value);
        data.Scale = new Vector3((float)_scaleX.Value, (float)_scaleY.Value, (float)_scaleZ.Value);

        node.Transform = data.GetTransform3D();
        MapSelectionManager.Instance?.NotifyTransformDirectlyChanged();
    }

    private void OnScaleXChanged(double val)
    {
        if (_isUpdatingUi) return;

        if (_uniformScaleCheck?.ButtonPressed == true)
        {
            if (_scaleY != null) _scaleY.Value = val;
            if (_scaleZ != null) _scaleZ.Value = val;
        }

        OnTransformSpinBoxChanged();
    }

    private void OnNameChanged(string newName)
    {
        if (_isUpdatingUi) return;
        var data = MapSelectionManager.Instance?.SelectedData;
        if (data != null) data.Name = newName;
    }

    private void OnTintChanged(Color newColor)
    {
        if (_isUpdatingUi) return;
        var data = MapSelectionManager.Instance?.SelectedData;
        var node = MapSelectionManager.Instance?.SelectedNode;
        if (data == null || node == null) return;

        data.ColorTint = newColor;

        // Apply tint in real-time to Godot mesh
        foreach (var child in node.FindChildren("*", "MeshInstance3D", true, false))
        {
            if (child is MeshInstance3D mi && mi.MaterialOverride is StandardMaterial3D stdMat)
            {
                stdMat.AlbedoColor = newColor;
            }
        }
    }

    private void OnSolidModeChanged(long index)
    {
        if (_isUpdatingUi) return;
        var data = MapSelectionManager.Instance?.SelectedData;
        if (data == null) return;

        data.SolidMode = index switch
        {
            0 => 6,
            1 => 2,
            _ => 0
        };
    }

    private void OnLightColorChanged(Color color)
    {
        var data = MapSelectionManager.Instance?.SelectedData;
        var node = MapSelectionManager.Instance?.SelectedNode;
        if (data != null && node != null)
        {
            data.LightColor = color;
            MapEntitySpawner.UpdateEntityProperties(node, data);
        }
    }

    private void OnLightEnergyChanged(double val)
    {
        var data = MapSelectionManager.Instance?.SelectedData;
        var node = MapSelectionManager.Instance?.SelectedNode;
        if (data != null && node != null)
        {
            data.LightEnergy = (float)val;
            if (_lightEnergyLabel != null) _lightEnergyLabel.Text = $"{data.LightEnergy:F1}x";
            MapEntitySpawner.UpdateEntityProperties(node, data);
        }
    }

    private void OnLightRangeChanged(double val)
    {
        var data = MapSelectionManager.Instance?.SelectedData;
        var node = MapSelectionManager.Instance?.SelectedNode;
        if (data != null && node != null)
        {
            data.LightRange = (float)val;
            if (_lightRangeLabel != null) _lightRangeLabel.Text = $"{data.LightRange:F1} m";
            MapEntitySpawner.UpdateEntityProperties(node, data);
        }
    }

    private void OnSpotAngleChanged(double val)
    {
        var data = MapSelectionManager.Instance?.SelectedData;
        var node = MapSelectionManager.Instance?.SelectedNode;
        if (data != null && node != null)
        {
            data.SpotAngle = (float)val;
            if (_spotAngleLabel != null) _spotAngleLabel.Text = $"{data.SpotAngle:F0}°";
            MapEntitySpawner.UpdateEntityProperties(node, data);
        }
    }
}
