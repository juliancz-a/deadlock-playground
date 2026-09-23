#pragma warning disable CS0618
using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using DeadlockPlayground.Materials;
using DeadlockPlayground.Tools;

public partial class PoseTabUI : VBoxContainer
{
    [Export] private LineEdit _animSearch;
    [Export] private Tree _animTree;
    [Export] private HSlider _timelineSlider;
    [Export] private Label _timeLabel;
    [Export] private Button _btnPlayPause;
    [Export] private Button _btnStop;
    [Export] private Button _btnLoop;
    [Export] private OptionButton _optSpeed;
    [Export] private Button _btnApplyPose;
    [Export] private Button _btnResetAllPoses;

    private Skeleton3D _skeleton;
    private AnimationPlayer _animPlayer;
    private VpkLoaderTest _vpkLoader;

    private List<DeadlockAnimLoader.AnimSequenceInfo> _currentHeroAnimations = new();
    private string _activeAnimName = "";
    private float _activeAnimLength = 0.0f;
    private bool _isLooping = true;
    private bool _isUserDraggingSlider = false;

    private Texture2D _iconPlay;
    private Texture2D _iconPause;
    private Texture2D _iconFolder;
    private Texture2D _iconLoop;

    public override void _Ready()
    {
        _iconPlay = GD.Load<Texture2D>("res://assets/at-icons/play.svg");
        _iconPause = GD.Load<Texture2D>("res://assets/at-icons/pause.svg");
        _iconFolder = GD.Load<Texture2D>("res://assets/at-icons/folder.svg");
        _iconLoop = GD.Load<Texture2D>("res://assets/icons/loop.svg");

        if (_btnLoop != null)
        {
            _btnLoop.Icon = _iconLoop;
        }

        if (_animSearch != null)
        {
            _animSearch.TextChanged += OnSearchTextChanged;
        }

        if (_animTree != null)
        {
            _animTree.SetColumnExpand(0, true);
            _animTree.ItemSelected += OnTreeItemSelected;
        }

        if (_btnPlayPause != null)
        {
            _btnPlayPause.Toggled += OnPlayPauseToggled;
        }

        if (_btnStop != null)
        {
            _btnStop.Pressed += OnStopPressed;
        }

        if (_btnLoop != null)
        {
            _btnLoop.Toggled += OnLoopToggled;
        }

        if (_optSpeed != null)
        {
            _optSpeed.ItemSelected += OnSpeedSelected;
        }

        if (_timelineSlider != null)
        {
            _timelineSlider.DragStarted += () => _isUserDraggingSlider = true;
            _timelineSlider.DragEnded += (bool _) => _isUserDraggingSlider = false;
            _timelineSlider.ValueChanged += OnTimelineSliderValueChanged;
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

    public override void _Process(double delta)
    {
        if (_animPlayer != null && _animPlayer.IsPlaying() && !_isUserDraggingSlider)
        {
            float currentPos = (float)_animPlayer.CurrentAnimationPosition;
            _timelineSlider?.SetValueNoSignal(currentPos);
            UpdateTimeLabel(currentPos, _activeAnimLength);

            // Handle non-looping animation ending
            if (!_isLooping && currentPos >= _activeAnimLength - 0.03f)
            {
                _animPlayer.Pause();
                if (_btnPlayPause != null)
                {
                    _btnPlayPause.ButtonPressed = false;
                    _btnPlayPause.Icon = _iconPlay;
                }
            }
        }
    }

    public void SetSkeleton(Skeleton3D skeleton)
    {
        _skeleton = skeleton;
    }


    public void SetAnimationPlayer(AnimationPlayer animPlayer)
    {
        _animPlayer = animPlayer;
        if (_animPlayer == null)
        {
            ResetTransportUI();
            _animTree?.Clear();
            _currentHeroAnimations.Clear();
            _activeAnimName = "";
            _activeAnimLength = 0f;
            return;
        }

        PopulateAnimationTreeFromPlayer();
        AutoSelectDefaultIdle();
    }

    private void PopulateAnimationTreeFromPlayer()
    {
        if (_animPlayer == null) return;

        _currentHeroAnimations.Clear();
        var animList = _animPlayer.GetAnimationList();

        foreach (var animName in animList)
        {
            var anim = _animPlayer.GetAnimation(animName);
            string cleanName = DeadlockAnimLoader.SanitizePoseName(animName);
            string displayName = DeadlockAnimLoader.FormatDisplayName(cleanName);
            string category = DeadlockAnimLoader.ClassifyCategory(cleanName);
            float duration = (float)(anim?.Length ?? 0.1f);
            bool isLooping = DeadlockAnimLoader.DetermineLooping(cleanName, category);

            _currentHeroAnimations.Add(new DeadlockAnimLoader.AnimSequenceInfo
            {
                RawName = animName,
                CleanName = cleanName,
                DisplayName = displayName,
                Category = category,
                Duration = duration,
                IsLooping = isLooping
            });
        }

        // Sort by category priority, then display name
        _currentHeroAnimations.Sort((a, b) =>
        {
            int catOrderA = DeadlockAnimLoader.GetCategoryPriority(a.Category);
            int catOrderB = DeadlockAnimLoader.GetCategoryPriority(b.Category);
            if (catOrderA != catOrderB) return catOrderA.CompareTo(catOrderB);
            return string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
        });

        PopulateAnimationTree();
    }

    private void FindVpkLoader()
    {
        if (_vpkLoader == null)
        {
            _vpkLoader = GetNodeOrNull<VpkLoaderTest>("/root/Main/UIRoot/MainHUD/VBoxContainer/MainSplit/ViewportArea/SubViewportContainer/WorldViewport/VpkLoaderTest")
                      ?? GetNodeOrNull<VpkLoaderTest>("/root/Main/VpkLoaderTest")
                      ?? GetTree().Root.FindChild("VpkLoaderTest", true, false) as VpkLoaderTest;
        }
    }

    private void PopulateAnimationTree()
    {
        if (_animTree == null) return;

        _animTree.Clear();
        TreeItem root = _animTree.CreateItem(); // Hidden root

        string filter = _animSearch?.Text.Trim().ToLowerInvariant() ?? "";

        // Defined category order matching prompt requirements
        string[] categories = new[]
        {
            "Idle",
            "Locomotion",
            "Combat",
            "Abilities",
            "Emotes & Expressions",
            "Reactions",
            "Misc / Other"
        };

        foreach (var category in categories)
        {
            var itemsInCategory = _currentHeroAnimations
                .Where(a => a.Category.Equals(category, StringComparison.OrdinalIgnoreCase))
                .Where(a => string.IsNullOrEmpty(filter) ||
                            a.DisplayName.ToLowerInvariant().Contains(filter) ||
                            a.CleanName.ToLowerInvariant().Contains(filter) ||
                            a.RawName.ToLowerInvariant().Contains(filter))
                .ToList();

            if (itemsInCategory.Count == 0) continue;

            TreeItem categoryItem = _animTree.CreateItem(root);
            categoryItem.SetIcon(0, _iconFolder);
            categoryItem.SetIconMaxWidth(0, 16);
            categoryItem.SetCustomColor(0, new Color(0.95f, 0.85f, 0.6f, 1.0f));
            categoryItem.SetCustomMinimumHeight(28);
            categoryItem.SetText(0, $"{category} ({itemsInCategory.Count})");
            categoryItem.SetSelectable(0, false);
            categoryItem.Collapsed = false;

            foreach (var anim in itemsInCategory)
            {
                TreeItem item = _animTree.CreateItem(categoryItem);
                item.SetAutowrapMode(0, TextServer.AutowrapMode.WordSmart);
                item.SetTextOverrunBehavior(0, TextServer.OverrunBehavior.TrimEllipsis);

                string itemText = $"{anim.DisplayName}  [{anim.Duration:F2}s]";
                int charLen = itemText.Length;
                int minHeight = charLen > 40 ? 46 : (charLen > 22 ? 36 : 26);
                item.SetCustomMinimumHeight(minHeight);

                item.SetText(0, itemText);
                item.SetTooltipText(0, $"{anim.DisplayName} ({anim.Duration:F2}s)\nRaw: {anim.RawName}");
                item.SetMetadata(0, anim.RawName);
                item.SetSelectable(0, true);

                if (anim.RawName == _activeAnimName || anim.CleanName == _activeAnimName)
                {
                    item.Select(0);
                }
            }
        }
    }

    private void AutoSelectDefaultIdle()
    {
        if (_currentHeroAnimations.Count == 0) return;

        DeadlockAnimLoader.AnimSequenceInfo bestAnim = null;

        // Try high-value idles first
        bestAnim = _currentHeroAnimations.FirstOrDefault(a =>
            a.Category == "Idle" && (
                a.CleanName.EndsWith("stand_idle") ||
                a.CleanName == "shoot_idle" ||
                a.CleanName == "idle_loadout" ||
                a.CleanName.EndsWith("out_of_combat_stand_idle") ||
                a.CleanName.Contains("primary_idle")
            ));

        if (bestAnim == null)
        {
            bestAnim = _currentHeroAnimations.FirstOrDefault(a => a.Category == "Idle");
        }

        if (bestAnim == null)
        {
            bestAnim = _currentHeroAnimations.FirstOrDefault();
        }

        if (bestAnim != null)
        {
            SelectAnimation(bestAnim, autoPlay: false);
            HighlightTreeItem(bestAnim.RawName);
        }
    }

    private void HighlightTreeItem(string animKey)
    {
        if (_animTree == null) return;
        TreeItem root = _animTree.GetRoot();
        if (root == null) return;

        foreach (TreeItem cat in root.GetChildren())
        {
            foreach (TreeItem child in cat.GetChildren())
            {
                string itemKey = child.GetMetadata(0).AsString();
                if (itemKey == animKey || itemKey.EndsWith(animKey))
                {
                    child.Select(0);
                    return;
                }
            }
        }
    }

    public void SelectAnimation(DeadlockAnimLoader.AnimSequenceInfo info, bool autoPlay = false)
    {
        if (info == null || _animPlayer == null || _skeleton == null) return;

        string targetAnim = info.RawName;
        if (!_animPlayer.HasAnimation(targetAnim))
        {
            if (_animPlayer.HasAnimation(info.CleanName))
            {
                targetAnim = info.CleanName;
            }
            else
            {
                FindVpkLoader();
                if (_vpkLoader != null)
                {
                    _vpkLoader.DecodeAnimation(info, _skeleton, _animPlayer);
                }
                if (_animPlayer.HasAnimation(info.CleanName))
                {
                    targetAnim = info.CleanName;
                }
            }
        }

        if (!_animPlayer.HasAnimation(targetAnim)) return;

        _activeAnimName = targetAnim;
        var godotAnim = _animPlayer.GetAnimation(targetAnim);
        _activeAnimLength = (float)(godotAnim?.Length ?? info.Duration);

        // Configure animation playback parameters
        if (godotAnim != null)
        {
            DeadlockAnimLoader.StripClothTracks(godotAnim);
            godotAnim.LoopMode = _isLooping ? Godot.Animation.LoopModeEnum.Linear : Godot.Animation.LoopModeEnum.None;
        }

        // Configure transport UI
        if (_timelineSlider != null)
        {
            _timelineSlider.MinValue = 0.0;
            _timelineSlider.MaxValue = Math.Max(0.01, _activeAnimLength);
            _timelineSlider.Step = 0.01;
            _timelineSlider.SetValueNoSignal(0.0);
        }

        if (_btnLoop != null)
        {
            _btnLoop.ButtonPressed = _isLooping;
        }

        UpdateTimeLabel(0.0f, _activeAnimLength);

        // Reset all bone poses cleanly to rest/bind pose
        for (int i = 0; i < _skeleton.GetBoneCount(); i++)
        {
            _skeleton.ResetBonePose(i);
        }

        if (autoPlay)
        {
            _animPlayer.Play(_activeAnimName);
            if (_btnPlayPause != null)
            {
                _btnPlayPause.ButtonPressed = true;
                _btnPlayPause.Icon = _iconPause;
            }
        }
        else
        {
            // Evaluate at frame 0
            _animPlayer.Play(_activeAnimName);
            _animPlayer.Seek(0.0, update: true);
            _animPlayer.Pause();

            if (_btnPlayPause != null)
            {
                _btnPlayPause.ButtonPressed = false;
                _btnPlayPause.Icon = _iconPlay;
            }
        }

        _skeleton.ForceUpdateAllBoneTransforms();
        ProceduralClothSolver.MarkDirty();
        ProceduralClothSolver.Conform(_skeleton, force: true);
        NotifyPoseChanged(_activeAnimName);
    }

    public void ApplyPoseFromAnimation(string animName)
    {
        var info = _currentHeroAnimations.FirstOrDefault(a => a.RawName == animName || a.CleanName == animName);

        if (info != null)
        {
            SelectAnimation(info, autoPlay: false);
            HighlightTreeItem(info.RawName);
        }
        else if (_animPlayer != null && _animPlayer.HasAnimation(animName))
        {
            _activeAnimName = animName;
            var godotAnim = _animPlayer.GetAnimation(animName);
            if (godotAnim != null)
            {
                DeadlockAnimLoader.StripClothTracks(godotAnim);
            }
            _activeAnimLength = (float)(godotAnim?.Length ?? 0.1f);

            for (int i = 0; i < _skeleton.GetBoneCount(); i++)
            {
                _skeleton.ResetBonePose(i);
            }

            _animPlayer.Play(animName);
            _animPlayer.Seek(0.0, update: true);
            _animPlayer.Pause();

            if (_btnPlayPause != null)
            {
                _btnPlayPause.ButtonPressed = false;
                _btnPlayPause.Icon = _iconPlay;
            }

            _timelineSlider?.SetValueNoSignal(0.0);
            UpdateTimeLabel(0.0f, _activeAnimLength);

            _skeleton.ForceUpdateAllBoneTransforms();
            ProceduralClothSolver.MarkDirty();
            ProceduralClothSolver.Conform(_skeleton, force: true);
            NotifyPoseChanged(animName);
        }
    }

    private void NotifyPoseChanged(string animName)
    {
        if (_skeleton == null) return;
        Node3D heroRoot = _skeleton;
        while (heroRoot != null && !heroRoot.Name.ToString().StartsWith("Hero_") && heroRoot.GetParent() is Node3D parent3D)
        {
            heroRoot = parent3D;
        }
        DeadlockMaterialResolver.OnPoseChanged(heroRoot, heroRoot?.Name.ToString() ?? "", animName);
    }

    private void OnSearchTextChanged(string newText)
    {
        PopulateAnimationTree();
    }

    private void OnTreeItemSelected()
    {
        TreeItem selected = _animTree?.GetSelected();
        if (selected == null) return;

        var meta = selected.GetMetadata(0);
        if (meta.VariantType == Variant.Type.Nil) return;

        string animKey = meta.AsString();
        if (string.IsNullOrEmpty(animKey)) return;

        var info = _currentHeroAnimations.FirstOrDefault(a => a.RawName == animKey || a.CleanName == animKey);
        if (info == null) return;

        bool wasPlaying = _animPlayer != null && _animPlayer.IsPlaying();
        SelectAnimation(info, autoPlay: wasPlaying);
    }

    private void OnPlayPauseToggled(bool isPlaying)
    {
        if (_animPlayer == null || string.IsNullOrEmpty(_activeAnimName)) return;

        if (isPlaying)
        {
            if (_animPlayer.CurrentAnimationPosition >= _activeAnimLength - 0.05f)
            {
                _animPlayer.Seek(0.0, update: true);
            }
            _animPlayer.Play(_activeAnimName);
            if (_btnPlayPause != null) _btnPlayPause.Icon = _iconPause;
        }
        else
        {
            _animPlayer.Pause();
            if (_btnPlayPause != null) _btnPlayPause.Icon = _iconPlay;

            if (_skeleton != null)
            {
                _skeleton.ForceUpdateAllBoneTransforms();
                ProceduralClothSolver.MarkDirty();
                ProceduralClothSolver.Conform(_skeleton, force: true);
            }
        }
    }

    private void OnStopPressed()
    {
        if (_animPlayer == null) return;

        _animPlayer.Stop();
        _animPlayer.Seek(0.0, update: true);

        if (_btnPlayPause != null)
        {
            _btnPlayPause.ButtonPressed = false;
            _btnPlayPause.Icon = _iconPlay;
        }

        _timelineSlider?.SetValueNoSignal(0.0);
        UpdateTimeLabel(0.0f, _activeAnimLength);

        if (_skeleton != null)
        {
            _skeleton.ForceUpdateAllBoneTransforms();
            ProceduralClothSolver.MarkDirty();
            ProceduralClothSolver.Conform(_skeleton, force: true);
        }
    }

    private void OnLoopToggled(bool looping)
    {
        _isLooping = looping;
        if (_animPlayer != null && !string.IsNullOrEmpty(_activeAnimName) && _animPlayer.HasAnimation(_activeAnimName))
        {
            var anim = _animPlayer.GetAnimation(_activeAnimName);
            if (anim != null)
            {
                anim.LoopMode = looping ? Godot.Animation.LoopModeEnum.Linear : Godot.Animation.LoopModeEnum.None;
            }
        }
    }

    private void OnSpeedSelected(long index)
    {
        float speed = index switch
        {
            0 => 0.25f,
            1 => 0.5f,
            2 => 1.0f,
            3 => 2.0f,
            _ => 1.0f
        };

        if (_animPlayer != null)
        {
            _animPlayer.SpeedScale = speed;
        }
    }

    private void OnTimelineSliderValueChanged(double val)
    {
        if (_animPlayer == null || string.IsNullOrEmpty(_activeAnimName)) return;

        // Immediate seek to evaluate bone matrices in real time
        _animPlayer.Seek(val, update: true);
        UpdateTimeLabel((float)val, _activeAnimLength);

        if (_skeleton != null)
        {
            _skeleton.ForceUpdateAllBoneTransforms();
            ProceduralClothSolver.MarkDirty();
            ProceduralClothSolver.Conform(_skeleton, force: true);
        }
    }

    private void OnApplyPosePressed()
    {
        if (_animPlayer != null && !string.IsNullOrEmpty(_activeAnimName))
        {
            _animPlayer.Pause();
            if (_btnPlayPause != null)
            {
                _btnPlayPause.ButtonPressed = false;
                _btnPlayPause.Icon = _iconPlay;
            }

            if (_skeleton != null)
            {
                _skeleton.ForceUpdateAllBoneTransforms();
                ProceduralClothSolver.MarkDirty();
                ProceduralClothSolver.Conform(_skeleton, force: true);
                NotifyPoseChanged(_activeAnimName);
            }
        }
    }

    public void ResetAllPoses()
    {
        _animPlayer?.Stop();

        if (_btnPlayPause != null)
        {
            _btnPlayPause.ButtonPressed = false;
            _btnPlayPause.Icon = _iconPlay;
        }

        _timelineSlider?.SetValueNoSignal(0.0);
        UpdateTimeLabel(0.0f, _activeAnimLength);

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

    private void ResetTransportUI()
    {
        if (_btnPlayPause != null)
        {
            _btnPlayPause.ButtonPressed = false;
            _btnPlayPause.Icon = _iconPlay;
        }

        _timelineSlider?.SetValueNoSignal(0.0);
        UpdateTimeLabel(0.0f, 0.0f);
    }

    private void UpdateTimeLabel(float current, float total)
    {
        if (_timeLabel == null) return;
        _timeLabel.Text = $"{current:00.00}s / {total:00.00}s";
    }
}
