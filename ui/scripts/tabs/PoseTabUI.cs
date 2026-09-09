using Godot;
using System;
using System.Collections.Generic;
using DeadlockPlayground.Materials;

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
                if (lower.EndsWith("stand_idle") || lower == "shoot_idle" || lower == "idle_loadout" || lower.EndsWith("out_of_combat_stand_idle") || lower.Contains("primary_idle"))
                {
                    bestIdx = i + 1; // 1-indexed in _animOptionButton
                    bestAnim = animList[i];
                    break;
                }
            }

            if (bestIdx == -1)
            {
                for (int i = 0; i < animList.Length; i++)
                {
                    string lower = animList[i].ToLower();
                    if (lower.Contains("idle"))
                    {
                        bestIdx = i + 1;
                        bestAnim = animList[i];
                        break;
                    }
                }
            }

            if (bestIdx == -1 && animList.Length > 0)
            {
                bestIdx = 1;
                bestAnim = animList[0];
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
        if (_animPlayer == null || _skeleton == null || string.IsNullOrEmpty(animName)) return;

        if (!_animPlayer.HasAnimation(animName)) return;

        // Reset all bone poses cleanly to rest
        for (int i = 0; i < _skeleton.GetBoneCount(); i++)
        {
            _skeleton.ResetBonePose(i);
        }

        // Evaluate the pose at frame 0 using Godot's C++ native animation engine
        // This guarantees 100% correct, undeformed bone matrices, retargeting, and hierarchy propagation
        _animPlayer.Play(animName);
        _animPlayer.Seek(0.0, update: true);
        _animPlayer.Pause();

        _skeleton.ForceUpdateAllBoneTransforms();
        ProceduralClothSolver.Conform(_skeleton);

        // Dynamically update context submeshes (e.g. Doorman parry_door vs reload_door)
        Node3D heroRoot = _skeleton;
        while (heroRoot != null && !heroRoot.Name.ToString().StartsWith("Hero_") && heroRoot.GetParent() is Node3D parent3D)
        {
            heroRoot = parent3D;
        }
        DeadlockMaterialResolver.OnPoseChanged(heroRoot, heroRoot?.Name.ToString() ?? "", animName);
    }

    public void ResetAllPoses()
    {
        _animPlayer?.Stop();

        if (_skeleton != null)
        {
            for (int i = 0; i < _skeleton.GetBoneCount(); i++)
            {
                _skeleton.ResetBonePose(i);
            }

            _skeleton.ForceUpdateAllBoneTransforms();
            ProceduralClothSolver.Conform(_skeleton);
        }
    }
}
