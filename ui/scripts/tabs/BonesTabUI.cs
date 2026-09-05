#pragma warning disable CS0618
using Godot;
using System;
using System.Collections.Generic;

public partial class BonesTabUI : VBoxContainer, IBoneUIController
{
    [ExportCategory("Bone Selection & Sliders")]
    [Export] private OptionButton _allBonesOptionButton;
    [Export] private Label _selectedBoneLabel;
    [Export] private HSlider _sliderX, _sliderY, _sliderZ;
    [Export] private Label _labelX, _labelY, _labelZ;
    [Export] private Button _btnResetBone;
    [Export] private Button _btnDeselect;
    [Export] private GridContainer _fastBoneGrid;

    [ExportCategory("Layer Filters")]
    [Export] private CheckBox _checkPrimary;
    [Export] private CheckBox _checkClothing;
    [Export] private CheckBox _checkFace;
    [Export] private CheckBox _checkFingers;
    [Export] private CheckBox _checkProps;
    [Export] private CheckBox _checkHelpers;

    [ExportCategory("X-Ray & Gizmo")]
    [Export] private Button _toggleXRayButton;
    [Export] private CheckBox _toggleLinesButton;

    private Skeleton3D _skeleton;
    private BoneLayerManager _layerManager;
    private int _selectedBoneIdx = -1;
    private bool _updatingSliders = false;

    public event Action<bool> OnXRayToggled;
    public event Action<bool> OnLinesToggled;
    public event Action<int> OnBoneSelectedFromUI;

    public bool IsXRayEnabled => _toggleXRayButton != null && _toggleXRayButton.ButtonPressed;
    public bool IsLinesEnabled => _toggleLinesButton == null || _toggleLinesButton.ButtonPressed;

    public override void _Ready()
    {
        if (_sliderX != null) _sliderX.ValueChanged += (v) => OnSliderValueChanged(v, 0);
        if (_sliderY != null) _sliderY.ValueChanged += (v) => OnSliderValueChanged(v, 1);
        if (_sliderZ != null) _sliderZ.ValueChanged += (v) => OnSliderValueChanged(v, 2);

        if (_btnResetBone != null) _btnResetBone.Pressed += OnResetBonePressed;

        if (_btnDeselect != null)
        {
            _btnDeselect.Pressed += () =>
            {
                SelectBone(-1);
                OnBoneSelectedFromUI?.Invoke(-1);
            };
        }

        if (_toggleXRayButton != null)
        {
            _toggleXRayButton.ToggleMode = true;
            _toggleXRayButton.Toggled += (pressed) => OnXRayToggled?.Invoke(pressed);
        }

        if (_toggleLinesButton != null)
        {
            _toggleLinesButton.ButtonPressed = true;
            _toggleLinesButton.Toggled += (pressed) => OnLinesToggled?.Invoke(pressed);
        }

        if (_allBonesOptionButton != null)
        {
            _allBonesOptionButton.ItemSelected += OnBoneDropdownSelected;
        }

        InitLayerCheckboxes();

        if (_skeleton != null)
        {
            PopulateFastBones();
            PopulateAllBones();
        }
    }

    public void SetSkeleton(Skeleton3D skeleton)
    {
        _skeleton = skeleton;
        _selectedBoneIdx = -1;
        UpdateSelectedBoneLabel();
        PopulateFastBones();
        PopulateAllBones();
    }

    public void SetBoneLayerManager(BoneLayerManager layerManager)
    {
        _layerManager = layerManager;
        HookLayerCheckboxEvents();
        PopulateAllBones();
    }

    private void InitLayerCheckboxes()
    {
        if (_checkPrimary != null) _checkPrimary.ButtonPressed = true;
        if (_checkClothing != null) _checkClothing.ButtonPressed = false;
        if (_checkFingers != null) _checkFingers.ButtonPressed = false;
        if (_checkFace != null) _checkFace.ButtonPressed = false;
        if (_checkProps != null) _checkProps.ButtonPressed = true;
        if (_checkHelpers != null) _checkHelpers.ButtonPressed = false;

        HookLayerCheckboxEvents();
    }

    private bool _checkboxEventsHooked = false;
    private void HookLayerCheckboxEvents()
    {
        if (_checkboxEventsHooked) return;
        _checkboxEventsHooked = true;

        if (_checkPrimary != null) _checkPrimary.Toggled += (t) => _layerManager?.SetLayerEnabled(BoneCategory.Primary, t);
        if (_checkClothing != null) _checkClothing.Toggled += (t) => _layerManager?.SetLayerEnabled(BoneCategory.Clothing, t);
        if (_checkFingers != null) _checkFingers.Toggled += (t) => _layerManager?.SetLayerEnabled(BoneCategory.Fingers, t);
        if (_checkFace != null) _checkFace.Toggled += (t) => _layerManager?.SetLayerEnabled(BoneCategory.Face, t);
        if (_checkProps != null) _checkProps.Toggled += (t) => _layerManager?.SetLayerEnabled(BoneCategory.Props, t);
        if (_checkHelpers != null) _checkHelpers.Toggled += (t) => _layerManager?.SetLayerEnabled(BoneCategory.Helpers, t);
    }

    private void PopulateAllBones()
    {
        if (_allBonesOptionButton == null || _skeleton == null) return;

        _allBonesOptionButton.Clear();
        _allBonesOptionButton.AddItem("Select a bone...", 0);
        _allBonesOptionButton.SetItemDisabled(0, true);

        int count = _skeleton.GetBoneCount();
        for (int i = 0; i < count; i++)
        {
            string bName = _skeleton.GetBoneName(i);
            BoneCategory cat = BoneLayerManager.ClassifyBone(bName);
            _allBonesOptionButton.AddItem($"[{cat}] {bName}", i + 1);
            _allBonesOptionButton.SetItemMetadata(i + 1, i);
        }
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
            { "Neck", "neck_0" },
            { "Spine 3", "spine_3" },
            { "Spine 2", "spine_2" },
            { "Pelvis", "pelvis" },
            { "Hand R", "hand_R" },
            { "Hand L", "hand_L" },
            { "Foot R", "ankle_R" },
            { "Foot L", "ankle_L" },
            { "Arm R", "arm_upper_R" },
            { "Arm L", "arm_upper_L" },
            { "Thigh R", "leg_upper_R" },
            { "Thigh L", "leg_upper_L" }
        };

        foreach (var pair in fastBones)
        {
            int bIdx = _skeleton.FindBone(pair.Value);
            if (bIdx == -1) continue;

            var btn = new Button
            {
                Text = pair.Key,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(0, 26)
            };

            int localIdx = bIdx;
            btn.Pressed += () =>
            {
                SelectBone(localIdx);
                OnBoneSelectedFromUI?.Invoke(localIdx);
            };

            _fastBoneGrid.AddChild(btn);
        }
    }

    public void SelectBone(int boneIdx)
    {
        _selectedBoneIdx = boneIdx;
        UpdateSelectedBoneLabel();
        UpdateSlidersFromBone();

        if (_allBonesOptionButton != null && boneIdx >= 0)
        {
            for (int i = 1; i < _allBonesOptionButton.ItemCount; i++)
            {
                if (_allBonesOptionButton.GetItemMetadata(i).AsInt32() == boneIdx)
                {
                    _allBonesOptionButton.Select(i);
                    break;
                }
            }
        }
        else if (_allBonesOptionButton != null && boneIdx == -1)
        {
            _allBonesOptionButton.Select(0);
        }
    }

    private void OnBoneDropdownSelected(long index)
    {
        if (index <= 0 || _allBonesOptionButton == null) return;
        int boneIdx = _allBonesOptionButton.GetItemMetadata((int)index).AsInt32();
        SelectBone(boneIdx);
        OnBoneSelectedFromUI?.Invoke(boneIdx);
    }

    private void UpdateSelectedBoneLabel()
    {
        if (_selectedBoneLabel == null) return;

        if (_selectedBoneIdx >= 0 && _skeleton != null && _selectedBoneIdx < _skeleton.GetBoneCount())
        {
            _selectedBoneLabel.Text = _skeleton.GetBoneName(_selectedBoneIdx);
        }
        else
        {
            _selectedBoneLabel.Text = "No Bone Selected";
        }
    }

    public void UpdateSlidersFromBone()
    {
        if (_selectedBoneIdx == -1 || _skeleton == null)
        {
            SetSlidersEnabled(false);
            return;
        }

        SetSlidersEnabled(true);
        _updatingSliders = true;

        Quaternion poseRot = _skeleton.GetBonePoseRotation(_selectedBoneIdx);
        Vector3 euler = poseRot.GetEuler();

        float degX = Mathf.RadToDeg(euler.X);
        float degY = Mathf.RadToDeg(euler.Y);
        float degZ = Mathf.RadToDeg(euler.Z);

        if (_sliderX != null) _sliderX.Value = degX;
        if (_sliderY != null) _sliderY.Value = degY;
        if (_sliderZ != null) _sliderZ.Value = degZ;

        if (_labelX != null) _labelX.Text = $"X: {degX:F1}°";
        if (_labelY != null) _labelY.Text = $"Y: {degY:F1}°";
        if (_labelZ != null) _labelZ.Text = $"Z: {degZ:F1}°";

        _updatingSliders = false;
    }

    private void OnSliderValueChanged(double val, int axis)
    {
        if (_updatingSliders || _selectedBoneIdx == -1 || _skeleton == null) return;

        float degX = (float)(_sliderX?.Value ?? 0);
        float degY = (float)(_sliderY?.Value ?? 0);
        float degZ = (float)(_sliderZ?.Value ?? 0);

        if (axis == 0 && _labelX != null) _labelX.Text = $"X: {val:F1}°";
        if (axis == 1 && _labelY != null) _labelY.Text = $"Y: {val:F1}°";
        if (axis == 2 && _labelZ != null) _labelZ.Text = $"Z: {val:F1}°";

        Vector3 eulerRad = new Vector3(Mathf.DegToRad(degX), Mathf.DegToRad(degY), Mathf.DegToRad(degZ));
        Quaternion newRot = Quaternion.FromEuler(eulerRad);

        _skeleton.SetBonePoseRotation(_selectedBoneIdx, newRot);
        ProceduralClothSolver.Conform(_skeleton);
    }

    private void OnResetBonePressed()
    {
        if (_selectedBoneIdx == -1 || _skeleton == null) return;

        _skeleton.ResetBonePose(_selectedBoneIdx);
        ProceduralClothSolver.Conform(_skeleton);
        UpdateSlidersFromBone();
    }

    private void SetSlidersEnabled(bool enabled)
    {
        if (_sliderX != null) _sliderX.Editable = enabled;
        if (_sliderY != null) _sliderY.Editable = enabled;
        if (_sliderZ != null) _sliderZ.Editable = enabled;
        if (_btnResetBone != null) _btnResetBone.Disabled = !enabled;
    }
}
