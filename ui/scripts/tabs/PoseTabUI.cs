#pragma warning disable CS0618
using Godot;
using System;
using System.Collections.Generic;

public partial class PoseTabUI : VBoxContainer
{
    [Export] private OptionButton _animOptionButton;
    [Export] private Button _btnApplyPose;
    [Export] private Button _btnResetAllPoses;

    private Skeleton3D _skeleton;
    private AnimationPlayer _animPlayer;

    public override void _Ready()
    {
        if (_animOptionButton != null)
        {
            _animOptionButton.ItemSelected += OnAnimSelected;
        }

        if (_btnApplyPose != null)
        {
            _btnApplyPose.Pressed += OnApplyPosePressed;
        }

        if (_btnResetAllPoses != null)
        {
            _btnResetAllPoses.Pressed += ResetAllPoses;
        }
    }

    public void SetSkeleton(Skeleton3D skeleton)
    {
        _skeleton = skeleton;
    }

    public void SetAnimationPlayer(AnimationPlayer animPlayer)
    {
        _animPlayer = animPlayer;
        PopulateAnimations();

        // Auto-select and apply the default idle pose on load
        if (_animPlayer != null && _skeleton != null)
        {
            var animList = _animPlayer.GetAnimationList();
            int bestIdx = -1;
            string bestAnim = "";

            for (int i = 0; i < animList.Length; i++)
            {
                string lower = animList[i].ToLower();
                if (lower.EndsWith("stand_idle") || lower == "shoot_idle" || lower == "idle_loadout" || lower.EndsWith("out_of_combat_stand_idle"))
                {
                    bestIdx = i + 1; // 1-indexed in _animOptionButton
                    bestAnim = animList[i];
                    break;
                }
            }

            if (bestIdx > 0)
            {
                _animOptionButton?.Select(bestIdx);
                ApplyPoseFromAnimation(bestAnim);
            }
        }
    }

    private void PopulateAnimations()
    {
        if (_animOptionButton == null || _animPlayer == null) return;

        _animOptionButton.Clear();
        _animOptionButton.AddItem("Select Animation Pose...", 0);
        _animOptionButton.SetItemDisabled(0, true);

        var list = _animPlayer.GetAnimationList();
        int idx = 1;
        foreach (var animName in list)
        {
            string friendlyName = FormatAnimationDisplayName(animName);
            _animOptionButton.AddItem(friendlyName, idx);
            _animOptionButton.SetItemMetadata(idx, animName);
            idx++;
        }
    }

    private string FormatAnimationDisplayName(string rawName)
    {
        if (string.IsNullOrEmpty(rawName)) return "";

        string name = rawName;
        // Strip common VRF glTF prefixes: e.g. "models_heroes_staging_archer_clips_" or "models_heroes_..._clips_"
        int clipsIdx = name.IndexOf("_clips_", StringComparison.OrdinalIgnoreCase);
        if (clipsIdx != -1)
        {
            name = name.Substring(clipsIdx + 7);
        }
        else
        {
            int heroesIdx = name.IndexOf("heroes_", StringComparison.OrdinalIgnoreCase);
            if (heroesIdx != -1)
            {
                name = name.Substring(heroesIdx + 7);
            }
        }

        // Replace underscores with spaces and capitalize words for pleasant studio reading
        return name.Replace('_', ' ');
    }

    private void OnAnimSelected(long index)
    {
        if (index <= 0 || _animOptionButton == null) return;
        string animName = _animOptionButton.GetItemMetadata((int)index).AsString();
        ApplyPoseFromAnimation(animName);
    }

    private void OnApplyPosePressed()
    {
        if (_animOptionButton != null && _animOptionButton.Selected > 0)
        {
            string animName = _animOptionButton.GetItemMetadata(_animOptionButton.Selected).AsString();
            ApplyPoseFromAnimation(animName);
        }
    }

    public void ApplyPoseFromAnimation(string animName)
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
        ProceduralClothSolver.Conform(_skeleton);
    }

    public void ResetAllPoses()
    {
        if (_skeleton == null) return;

        for (int i = 0; i < _skeleton.GetBoneCount(); i++)
        {
            _skeleton.ResetBonePose(i);
        }

        _skeleton.ForceUpdateAllBoneTransforms();
        ProceduralClothSolver.Conform(_skeleton);
    }
}
