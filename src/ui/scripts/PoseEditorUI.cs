#pragma warning disable CS0618
using Godot;
using System;
using System.Collections.Generic;

public partial class PoseEditorUI : CanvasLayer, IBoneUIController
{
    [ExportCategory("Skeleton & Controls")]
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

    [ExportCategory("Bone Layers UI")]
    [Export] private CheckBox _checkPrimary;
    [Export] private CheckBox _checkClothing;
    [Export] private CheckBox _checkFingers;
    [Export] private CheckBox _checkFace;
    [Export] private CheckBox _checkProps;
    [Export] private CheckBox _checkHelpers;

    private AnimationPlayer _animPlayer;
    private BoneLayerManager _layerManager;
    
    private int _selectedBoneIdx = -1;
    private bool _updatingSliders = false;
    
    // An event to notify other scripts (like SkeletonGizmoManager) that X-Ray toggled
    public event Action<bool> OnXRayToggled;
    public bool IsXRayEnabled => _toggleXRayButton != null && _toggleXRayButton.ButtonPressed;

    public event Action<bool> OnLinesToggled;
    public bool IsLinesEnabled => _toggleLinesButton != null && _toggleLinesButton.ButtonPressed;

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

        InitLayerCheckboxes();

        if (_skeleton != null)
        {
            PopulateFastBones();
            PopulateAllBones();
        }
    }

    /// <summary>
    /// Connects the BoneLayerManager instance and configures UI checkbox events and default layer states.
    /// </summary>
    public void SetBoneLayerManager(BoneLayerManager layerManager)
    {
        _layerManager = layerManager;
        HookLayerCheckboxEvents();
        PopulateAllBones();
    }

    private void InitLayerCheckboxes()
    {
        // Set default checkbox visual states per design specification
        if (_checkPrimary != null) _checkPrimary.ButtonPressed = true;   // Body / Primary: Default ON
        if (_checkClothing != null) _checkClothing.ButtonPressed = false; // Clothing & Skirts: Default OFF
        if (_checkFingers != null) _checkFingers.ButtonPressed = false;   // Fingers: Default OFF
        if (_checkFace != null) _checkFace.ButtonPressed = false;         // Face & Eyes: Default OFF
        if (_checkProps != null) _checkProps.ButtonPressed = true;       // Weapons & Props: Default ON
        if (_checkHelpers != null) _checkHelpers.ButtonPressed = false;   // Helpers & Internal: Default OFF

        HookLayerCheckboxEvents();
    }

    private bool _checkboxEventsHooked = false;

    private void HookLayerCheckboxEvents()
    {
        if (_checkboxEventsHooked) return;
        _checkboxEventsHooked = true;

        if (_checkPrimary != null) _checkPrimary.Toggled += OnPrimaryToggled;
        if (_checkClothing != null) _checkClothing.Toggled += OnClothingToggled;
        if (_checkFingers != null) _checkFingers.Toggled += OnFingersToggled;
        if (_checkFace != null) _checkFace.Toggled += OnFaceToggled;
        if (_checkProps != null) _checkProps.Toggled += OnPropsToggled;
        if (_checkHelpers != null) _checkHelpers.Toggled += OnHelpersToggled;
    }

    private void OnPrimaryToggled(bool toggled)  => _layerManager?.SetLayerEnabled(BoneCategory.Primary, toggled);
    private void OnClothingToggled(bool toggled) => _layerManager?.SetLayerEnabled(BoneCategory.Clothing, toggled);
    private void OnFingersToggled(bool toggled)  => _layerManager?.SetLayerEnabled(BoneCategory.Fingers, toggled);
    private void OnFaceToggled(bool toggled)     => _layerManager?.SetLayerEnabled(BoneCategory.Face, toggled);
    private void OnPropsToggled(bool toggled)    => _layerManager?.SetLayerEnabled(BoneCategory.Props, toggled);
    private void OnHelpersToggled(bool toggled)  => _layerManager?.SetLayerEnabled(BoneCategory.Helpers, toggled);
    
    public void SetSkeleton(Skeleton3D skeleton)
    {
        _skeleton = skeleton;
        EnforceClothTwoSidedMaterials(_skeleton);
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
            BoneCategory category = BoneLayerManager.ClassifyBone(bName);

            string displayName = $"[{category}] {bName}";
            _allBonesOptionButton.AddItem(displayName, idx);
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

    /// <summary>
    /// Applies the keyframes from the first frame of an animation into the skeleton's bone poses.
    /// Preserves 100% of unkeyed bones (such as cloth / skirt / coat bones) in their rest pose relative
    /// to their parent, preventing mesh deformation tearing or lost skinning.
    /// </summary>
    private void ApplyPoseFromAnimation(string animName)
    {
        if (_animPlayer == null || _skeleton == null) return;

        var anim = _animPlayer.GetAnimation(animName);
        if (anim == null) return;

        // Reset all bone poses cleanly to rest
        for (int i = 0; i < _skeleton.GetBoneCount(); i++)
        {
            _skeleton.ResetBonePose(i);
        }

        int trackCount = anim.GetTrackCount();
        for (int i = 0; i < trackCount; i++)
        {
            var trackType = anim.TrackGetType(i);
            if (trackType == Animation.TrackType.Position3D ||
                trackType == Animation.TrackType.Rotation3D ||
                trackType == Animation.TrackType.Scale3D)
            {
                string trackPath = anim.TrackGetPath(i).ToString();
                string[] parts = trackPath.Split(':');
                if (parts.Length > 1)
                {
                    string boneName = parts[parts.Length - 1];
                    int boneIdx = _skeleton.FindBone(boneName);
                    if (boneIdx != -1)
                    {
                        if (trackType == Animation.TrackType.Position3D && anim.TrackGetKeyCount(i) > 0)
                        {
                            Vector3 pos = (Vector3)anim.PositionTrackInterpolate(i, 0.0);
                            _skeleton.SetBonePosePosition(boneIdx, pos);
                        }
                        else if (trackType == Animation.TrackType.Rotation3D && anim.TrackGetKeyCount(i) > 0)
                        {
                            Quaternion rot = (Quaternion)anim.RotationTrackInterpolate(i, 0.0);
                            _skeleton.SetBonePoseRotation(boneIdx, rot);
                        }
                    }
                }
            }
        }
        
        _skeleton.ForceUpdateAllBoneTransforms();

        // Conform unkeyed procedural cloth root bones to follow pelvis / chest rigidly without reparenting
        ConformProceduralClothPoses(_skeleton);

        UpdateSlidersFromBone();
    }

    /// <summary>
    /// Forwards conforming request to ProceduralClothSolver service.
    /// </summary>
    public static void ConformProceduralClothPoses(Skeleton3D skeleton)
    {
        ProceduralClothSolver.Conform(skeleton);
    }

    /// <summary>
    /// Forwards material two-sided enforcement to ProceduralClothSolver service.
    /// </summary>
    public static void EnforceClothTwoSidedMaterials(Node characterRoot)
    {
        ProceduralClothSolver.EnforceClothTwoSidedMaterials(characterRoot);
    }
    
    private void PopulateFastBones()
    {
        if (_fastBoneGrid == null || _skeleton == null) return;
        
        foreach (Node child in _fastBoneGrid.GetChildren())
        {
            child.QueueFree();
        }

        var fastBones = new Dictionary<string, string>
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
                string bName = _skeleton.GetBoneName(i)?.ToLowerInvariant() ?? "";
                if (bName.Contains(kvp.Value.ToLowerInvariant()))
                {
                    foundIdx = i;
                    break;
                }
            }

            if (foundIdx != -1)
            {
                var btn = new Button
                {
                    Text = kvp.Key
                };
                btn.Pressed += () => {
                    SelectBone(foundIdx);
                    OnBoneSelectedFromUI?.Invoke(foundIdx);
                };
                _fastBoneGrid.AddChild(btn);
            }
        }
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
            BoneCategory cat = BoneLayerManager.ClassifyBone(bName);
            if (_selectedBoneLabel != null)
            {
                _selectedBoneLabel.Text = $"[{cat}] {bName}";
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
        ConformProceduralClothPoses(_skeleton);
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
        
        ConformProceduralClothPoses(_skeleton);
        UpdateSlidersFromBone();
    }

    private void OnToggleXRayPressed(bool toggledOn)
    {
        OnXRayToggled?.Invoke(toggledOn);
    }
}
