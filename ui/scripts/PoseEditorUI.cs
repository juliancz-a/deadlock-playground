using Godot;
using System;

public partial class PoseEditorUI : CanvasLayer
{
    [Export] private Skeleton3D _skeleton;
    [Export] private MenuButton _boneMenuButton;
    [Export] private HSlider _sliderX, _sliderY, _sliderZ;
    [Export] private Label _labelX, _labelY, _labelZ;
    [Export] private Label _selectedBoneLabel;
    [Export] private Button _resetButton;
    [Export] private Button _toggleXRayButton;
    [Export] private CheckBox _toggleLinesButton;
    [Export] private Button _deselectButton;
    [Export] private GridContainer _fastBoneGrid;
    [Export] private OptionButton _poseOptionButton;
    [Export] private OptionButton _allBonesOptionButton;
    
    private AnimationPlayer _animPlayer;
    
    private int _selectedBoneIdx = -1;
    private bool _updatingSliders = false;
    
    // An event to notify other scripts (like the new SkeletonGizmoManager) that X-Ray toggled
    public event Action<bool> OnXRayToggled;
    public bool IsXRayEnabled => _toggleXRayButton != null ? _toggleXRayButton.ButtonPressed : false;

    public event Action<bool> OnLinesToggled;
    public bool IsLinesEnabled => _toggleLinesButton != null ? _toggleLinesButton.ButtonPressed : true;

    // An event to notify other scripts that a bone was selected via UI
    public event Action<int> OnBoneSelectedFromUI;
    
    public override void _Ready()
    {

        if (_sliderX != null) _sliderX.ValueChanged += (v) => OnSliderValueChanged(v, 0);
        if (_sliderY != null) _sliderY.ValueChanged += (v) => OnSliderValueChanged(v, 1);
        if (_sliderZ != null) _sliderZ.ValueChanged += (v) => OnSliderValueChanged(v, 2);

        if (_resetButton != null) _resetButton.Pressed += OnResetPressed;
        
        if (_toggleXRayButton != null)
        {
            _toggleXRayButton.ToggleMode = true;
            _toggleXRayButton.Toggled += OnToggleXRayPressed;
        }
        
        if (_toggleLinesButton != null)
        {
            _toggleLinesButton.Toggled += (v) => OnLinesToggled?.Invoke(v);
        }

        if (_deselectButton != null)
        {
            _deselectButton.Pressed += () => {
                SelectBone(-1);
                OnBoneSelectedFromUI?.Invoke(-1);
            };
        }

        // If skeleton is already assigned in editor, populate fast bones
        if (_skeleton != null)
        {
            PopulateFastBones();
        }
    }
    
    public void SetSkeleton(Skeleton3D skeleton)
    {
        _skeleton = skeleton;
        PopulateFastBones();
        PopulateAllBones();
    }
    
    private void PopulateAllBones()
    {
        if (_allBonesOptionButton == null || _skeleton == null) return;
        
        _allBonesOptionButton.Clear();
        _allBonesOptionButton.AddItem("Select any bone...", 0);
        _allBonesOptionButton.SetItemDisabled(0, true);
        
        int boneCount = _skeleton.GetBoneCount();
        int idx = 1;
        for (int i = 0; i < boneCount; i++)
        {
            string bName = _skeleton.GetBoneName(i);
            _allBonesOptionButton.AddItem(bName, idx);
            _allBonesOptionButton.SetItemMetadata(idx, i); // store actual bone index
            idx++;
        }
        
        _allBonesOptionButton.ItemSelected -= OnAllBonesItemSelected;
        _allBonesOptionButton.ItemSelected += OnAllBonesItemSelected;
    }
    
    private void OnAllBonesItemSelected(long index)
    {
        if (index == 0) return;
        int boneIdx = _allBonesOptionButton.GetItemMetadata((int)index).AsInt32();
        SelectBone(boneIdx);
        OnBoneSelectedFromUI?.Invoke(boneIdx);
        
        // Reset the dropdown so it doesn't stay stuck on the selected bone
        _allBonesOptionButton.Select(0); 
    }

    public void SetAnimationPlayer(AnimationPlayer player)
    {
        _animPlayer = player;
        if (_poseOptionButton == null)
        {
            GD.PrintErr("Pose OptionButton is NOT assigned in the Godot Inspector! Cannot populate poses.");
            return;
        }
        if (_animPlayer == null) return;

        _poseOptionButton.Clear();
        _poseOptionButton.AddItem("Select a Pose...", -1);
        _poseOptionButton.SetItemDisabled(0, true);

        var animList = _animPlayer.GetAnimationList();
        int idx = 1;
        foreach (string animName in animList)
        {
            _poseOptionButton.AddItem(animName, idx);
            _poseOptionButton.SetItemMetadata(idx, animName);
            idx++;
        }
        
        _poseOptionButton.ItemSelected -= OnPoseItemSelected;
        _poseOptionButton.ItemSelected += OnPoseItemSelected;

        _poseOptionButton.Select(0);
    }

    private void OnPoseItemSelected(long index)
    {
        if (index == 0) return;
        string animName = _poseOptionButton.GetItemMetadata((int)index).AsString();
        ApplyPoseFromAnimation(animName);
    }

    private void ApplyPoseFromAnimation(string animName)
    {
        if (_animPlayer == null || _skeleton == null) return;

        var anim = _animPlayer.GetAnimation(animName);
        if (anim == null) return;

        for (int i = 0; i < _skeleton.GetBoneCount(); i++)
        {
            _skeleton.ResetBonePose(i);
        }

        int trackCount = anim.GetTrackCount();
        for (int i = 0; i < trackCount; i++)
        {
            if (anim.TrackGetType(i) == Animation.TrackType.Position3D ||
                anim.TrackGetType(i) == Animation.TrackType.Rotation3D ||
                anim.TrackGetType(i) == Animation.TrackType.Scale3D)
            {
                string trackPath = anim.TrackGetPath(i).ToString();
                string[] parts = trackPath.Split(':');
                if (parts.Length > 1)
                {
                    string boneName = parts[parts.Length - 1];
                    int boneIdx = _skeleton.FindBone(boneName);
                    if (boneIdx != -1)
                    {
                        if (anim.TrackGetType(i) == Animation.TrackType.Position3D && anim.TrackGetKeyCount(i) > 0)
                        {
                            Vector3 pos = (Vector3)anim.PositionTrackInterpolate(i, 0.0);
                            _skeleton.SetBonePosePosition(boneIdx, pos);
                        }
                        else if (anim.TrackGetType(i) == Animation.TrackType.Rotation3D && anim.TrackGetKeyCount(i) > 0)
                        {
                            Quaternion rot = (Quaternion)anim.RotationTrackInterpolate(i, 0.0);
                            _skeleton.SetBonePoseRotation(boneIdx, rot);
                        }
                    }
                }
            }
        }
        
        UpdateSlidersFromBone();
    }
    
    private void PopulateFastBones()
    {
        if (_fastBoneGrid == null)
        {
            GD.PrintErr("Fast Bone Grid is NOT assigned in the Godot Inspector! Cannot generate Fast Bone buttons.");
            return;
        }
        if (_skeleton == null) return;
        
        foreach (Node child in _fastBoneGrid.GetChildren())
        {
            child.QueueFree();
        }

        var fastBones = new System.Collections.Generic.Dictionary<string, string>
        {
            { "Weapon R", "weapon_hand_R" },
            { "Weapon L", "weapon_hand_L" },
            { "Head", "head" },
            { "Spine", "spine" },
            { "Arm R", "arm_upper_R" },
            { "Arm L", "arm_upper_L" },
            { "Leg R", "leg_upper_R" },
            { "Leg L", "leg_upper_L" }
        };

        foreach (var kvp in fastBones)
        {
            int foundIdx = -1;
            for (int i = 0; i < _skeleton.GetBoneCount(); i++)
            {
                string bName = _skeleton.GetBoneName(i)?.ToLower() ?? "";
                if (bName.Contains(kvp.Value.ToLower()))
                {
                    foundIdx = i;
                    break;
                }
            }

            if (foundIdx != -1)
            {
                var btn = new Button();
                btn.Text = kvp.Key;
                btn.Pressed += () => {
                    SelectBone(foundIdx);
                    OnBoneSelectedFromUI?.Invoke(foundIdx);
                };
                _fastBoneGrid.AddChild(btn);
            }
        }
    }
    
    private void OnBoneMenuItemSelected(int boneIdx)
    {
        if (_skeleton == null) return;
        
        SelectBone(boneIdx);
        
        OnBoneSelectedFromUI?.Invoke(boneIdx);
    }
    
    public void SelectBone(int boneIdx)
    {
        _selectedBoneIdx = boneIdx;
        
        if (_selectedBoneIdx == -1)
        {
            if (_selectedBoneLabel != null) _selectedBoneLabel.Text = "Selected Bone: None";
            
            _updatingSliders = true;
            if (_sliderX != null) _sliderX.Value = 0;
            if (_sliderY != null) _sliderY.Value = 0;
            if (_sliderZ != null) _sliderZ.Value = 0;
            UpdateLabels();
            _updatingSliders = false;
            return;
        }

        if (_skeleton != null)
        {
            string bName = _skeleton.GetBoneName(_selectedBoneIdx);
            if (_selectedBoneLabel != null)
            {
                _selectedBoneLabel.Text = $"Selected Bone: {bName}";
            }
            if (_boneMenuButton != null)
            {
                _boneMenuButton.Text = bName;
            }
        }
        
        UpdateSlidersFromBone();
    }
    
    public void UpdateSlidersFromBone()
    {
        if (_selectedBoneIdx < 0 || _skeleton == null) return;
        
        _updatingSliders = true;
        
        Quaternion rot = _skeleton.GetBonePoseRotation(_selectedBoneIdx);
        Vector3 eulerRads = rot.GetEuler();
        
        // Convert to degrees for the UI
        if (_sliderX != null) _sliderX.Value = Mathf.RadToDeg(eulerRads.X);
        if (_sliderY != null) _sliderY.Value = Mathf.RadToDeg(eulerRads.Y);
        if (_sliderZ != null) _sliderZ.Value = Mathf.RadToDeg(eulerRads.Z);
        
        UpdateLabels();
        
        _updatingSliders = false;
    }
    
    private void OnSliderValueChanged(double value, int axisIndex)
    {
        if (_updatingSliders || _selectedBoneIdx < 0 || _skeleton == null) return;
        
        UpdateLabels();
        
        if (_sliderX == null || _sliderY == null || _sliderZ == null) return;

        Vector3 eulerRads = new Vector3(
            Mathf.DegToRad((float)_sliderX.Value),
            Mathf.DegToRad((float)_sliderY.Value),
            Mathf.DegToRad((float)_sliderZ.Value)
        );
        
        _skeleton.SetBonePoseRotation(_selectedBoneIdx, Quaternion.FromEuler(eulerRads));
    }
    
    private void UpdateLabels()
    {
        if (_labelX != null && _sliderX != null) _labelX.Text = $"{_sliderX.Value:F1}°";
        if (_labelY != null && _sliderY != null) _labelY.Text = $"{_sliderY.Value:F1}°";
        if (_labelZ != null && _sliderZ != null) _labelZ.Text = $"{_sliderZ.Value:F1}°";
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

    private void OnToggleXRayPressed(bool toggledOn)
    {
        OnXRayToggled?.Invoke(toggledOn);
    }
}
