using Godot;
using System;

public partial class PoseEditorUI : CanvasLayer
{
    private Skeleton3D _skeleton;
    private Tree _boneTree;
    private HSlider _sliderX, _sliderY, _sliderZ;
    private Label _labelX, _labelY, _labelZ;
    private Label _selectedBoneLabel;
    
    private int _selectedBoneIdx = -1;
    private bool _updatingSliders = false;
    
    public override void _Ready()
    {
        // Build the UI programmatically
        var margin = new MarginContainer();
        margin.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        margin.AddThemeConstantOverride("margin_left", 20);
        margin.AddThemeConstantOverride("margin_top", 20);
        margin.AddThemeConstantOverride("margin_right", 20);
        margin.AddThemeConstantOverride("margin_bottom", 20);
        margin.MouseFilter = Control.MouseFilterEnum.Ignore;
        AddChild(margin);
        
        var hbox = new HBoxContainer();
        hbox.MouseFilter = Control.MouseFilterEnum.Ignore;
        margin.AddChild(hbox);
        
        // Left Panel - Bone Tree
        var leftPanel = new PanelContainer();
        leftPanel.CustomMinimumSize = new Vector2(300, 0);
        hbox.AddChild(leftPanel);
        
        var vboxLeft = new VBoxContainer();
        leftPanel.AddChild(vboxLeft);
        
        var titleLabel = new Label { Text = "Bone Hierarchy", HorizontalAlignment = HorizontalAlignment.Center };
        vboxLeft.AddChild(titleLabel);
        
        _boneTree = new Tree();
        _boneTree.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        _boneTree.HideRoot = true;
        _boneTree.ItemSelected += OnBoneSelected;
        vboxLeft.AddChild(_boneTree);
        
        // Spacer
        var spacer = new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore };
        hbox.AddChild(spacer);
        
        // Right Panel - Controls
        var rightPanel = new PanelContainer();
        rightPanel.CustomMinimumSize = new Vector2(300, 0);
        rightPanel.SizeFlagsVertical = Control.SizeFlags.ShrinkBegin;
        hbox.AddChild(rightPanel);
        
        var vboxRight = new VBoxContainer();
        rightPanel.AddChild(vboxRight);
        
        _selectedBoneLabel = new Label { Text = "Selected Bone: None", HorizontalAlignment = HorizontalAlignment.Center };
        vboxRight.AddChild(_selectedBoneLabel);
        
        vboxRight.AddChild(new HSeparator());
        
        _sliderX = CreateAxisSlider("X (Pitch)", vboxRight, out _labelX);
        _sliderY = CreateAxisSlider("Y (Yaw)", vboxRight, out _labelY);
        _sliderZ = CreateAxisSlider("Z (Roll)", vboxRight, out _labelZ);
        
        vboxRight.AddChild(new HSeparator());
        
        var resetButton = new Button { Text = "Reset All Poses" };
        resetButton.Pressed += OnResetPressed;
        vboxRight.AddChild(resetButton);
    }
    
    private HSlider CreateAxisSlider(string name, Control parent, out Label valueLabel)
    {
        var vbox = new VBoxContainer();
        parent.AddChild(vbox);
        
        var hbox = new HBoxContainer();
        vbox.AddChild(hbox);
        
        var nameLabel = new Label { Text = name, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        hbox.AddChild(nameLabel);
        
        valueLabel = new Label { Text = "0.0°", CustomMinimumSize = new Vector2(50, 0), HorizontalAlignment = HorizontalAlignment.Right };
        hbox.AddChild(valueLabel);
        
        var slider = new HSlider();
        slider.MinValue = -180;
        slider.MaxValue = 180;
        slider.Step = 0.1;
        slider.ValueChanged += OnSliderValueChanged;
        vbox.AddChild(slider);
        
        return slider;
    }
    
    public void SetSkeleton(Skeleton3D skeleton)
    {
        _skeleton = skeleton;
        PopulateTree();
    }
    
    private void PopulateTree()
    {
        _boneTree.Clear();
        if (_skeleton == null) return;
        
        var root = _boneTree.CreateItem();
        
        int boneCount = _skeleton.GetBoneCount();
        var treeItems = new TreeItem[boneCount];
        
        // Ensure bones are processed in hierarchical order (parents before children)
        for (int i = 0; i < boneCount; i++)
        {
            int parentIdx = _skeleton.GetBoneParent(i);
            TreeItem parentItem = parentIdx >= 0 ? treeItems[parentIdx] : root;
            
            var item = _boneTree.CreateItem(parentItem);
            item.SetText(0, _skeleton.GetBoneName(i));
            item.SetMetadata(0, i); // Store bone index
            treeItems[i] = item;
            
            // Expand the item by default for better visibility
            item.Collapsed = false;
        }
    }
    
    private void OnBoneSelected()
    {
        var selectedItem = _boneTree.GetSelected();
        if (selectedItem == null || _skeleton == null) return;
        
        _selectedBoneIdx = (int)selectedItem.GetMetadata(0);
        _selectedBoneLabel.Text = $"Selected Bone: {_skeleton.GetBoneName(_selectedBoneIdx)}";
        
        UpdateSlidersFromBone();
    }
    
    private void UpdateSlidersFromBone()
    {
        if (_selectedBoneIdx < 0 || _skeleton == null) return;
        
        _updatingSliders = true;
        
        Quaternion rot = _skeleton.GetBonePoseRotation(_selectedBoneIdx);
        Vector3 eulerRads = rot.GetEuler();
        
        // Convert to degrees for the UI
        _sliderX.Value = Mathf.RadToDeg(eulerRads.X);
        _sliderY.Value = Mathf.RadToDeg(eulerRads.Y);
        _sliderZ.Value = Mathf.RadToDeg(eulerRads.Z);
        
        UpdateLabels();
        
        _updatingSliders = false;
    }
    
    private void OnSliderValueChanged(double value)
    {
        if (_updatingSliders || _selectedBoneIdx < 0 || _skeleton == null) return;
        
        UpdateLabels();
        
        Vector3 eulerRads = new Vector3(
            Mathf.DegToRad((float)_sliderX.Value),
            Mathf.DegToRad((float)_sliderY.Value),
            Mathf.DegToRad((float)_sliderZ.Value)
        );
        
        _skeleton.SetBonePoseRotation(_selectedBoneIdx, Quaternion.FromEuler(eulerRads));
    }
    
    private void UpdateLabels()
    {
        _labelX.Text = $"{_sliderX.Value:F1}°";
        _labelY.Text = $"{_sliderY.Value:F1}°";
        _labelZ.Text = $"{_sliderZ.Value:F1}°";
    }
    
    private void OnResetPressed()
    {
        if (_skeleton == null) return;
        
        for (int i = 0; i < _skeleton.GetBoneCount(); i++)
        {
            _skeleton.ResetBonePose(i);
        }
        
        UpdateSlidersFromBone();
    }
}
