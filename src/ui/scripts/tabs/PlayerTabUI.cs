#pragma warning disable CS0618
using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using DeadlockPlayground.Materials;
using DeadlockPlayground.Tools;
using DeadlockPlayground.Particles;
using DeadlockPlayground.UI;
using DeadlockPlayground.Workspace;
using DeadlockPlayground.Animation;

public partial class PlayerTabUI : VBoxContainer
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

    private AnimationStudioDock _animStudioDock;

    private Skeleton3D _skeleton;
    private AnimationPlayer _animPlayer;
    private VpkLoaderTest _vpkLoader;
    private ParticleStage3D _particleStage;
    private VBoxContainer _particleControlsGroup;
    private HSlider _sliderCharge;
    private Label _labelCharge;
    private Button _btnRelease;
    private CheckBox _chkAutoLoop;
    private const bool _isIdleUnderlayEnabled = false;
    private CheckBox _chkInPlaceMotion;
    private bool _isInPlaceMotionEnabled = true;
    private DeadlockAnimLoader.AnimSequenceInfo _activeAnimInfo;

    private List<DeadlockAnimLoader.AnimSequenceInfo> _currentHeroAnimations = new();
    private string _activeAnimName = "";
    private float _activeAnimLength = 0.0f;
    private bool _isLooping = true;
    private bool _isUserDraggingSlider = false;
    private readonly List<string> _decodedAnimLru = new();
    private const int MaxCachedDecodedAnims = 5;

    private Texture2D _iconPlay;
    private Texture2D _iconPause;
    private Texture2D _iconFolder;
    private Texture2D _iconLoop;

    public override void _Ready()
    {
        ProcessPriority = 100;

        _iconPlay = GD.Load<Texture2D>("res://assets/at-icons/play.svg");
        _iconPause = GD.Load<Texture2D>("res://assets/at-icons/pause.svg");
        _iconFolder = GD.Load<Texture2D>("res://assets/at-icons/folder.svg");
        _iconLoop = GD.Load<Texture2D>("res://assets/icons/loop.svg");

        if (_btnLoop != null)
        {
            _btnLoop.Icon = _iconLoop;
        }

        AnimationTransportService.Instance.PlaybackStateChanged += OnTransportPlaybackStateChanged;
        AnimationTransportService.Instance.TimeChanged += OnTransportTimeChanged;
        AnimationTransportService.Instance.LoopChanged += OnTransportLoopChanged;
        ModWorkspaceSession.Instance.OnAnimationDiscarded += OnWorkspaceAnimationDiscarded;
        ModWorkspaceSession.Instance.OnWorkspaceCleared += OnWorkspaceCleared;

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

        InitParticleControls();

        var animGroup = GetNodeOrNull<VBoxContainer>("AnimGroup");
        if (animGroup != null)
        {
            _chkInPlaceMotion = new CheckBox
            {
                Name = "ChkInPlaceMotion",
                Text = "In-Place Motion",
                ButtonPressed = _isInPlaceMotionEnabled,
                TooltipText = "Locks horizontal root motion translation so the character stays centered in the viewport during runs/dashes/rolls."
            };
            _chkInPlaceMotion.Toggled += OnInPlaceMotionToggled;
            animGroup.AddChild(_chkInPlaceMotion);
            animGroup.MoveChild(_chkInPlaceMotion, 2);

            var btnImportAnim = new Button
            {
                Name = "BtnImportExternalAnimSide",
                Text = "Import Animation (.glb/.fbx)...",
                TooltipText = "Import external animation (.glb, .gltf, .fbx from Mixamo, Mesh2Motion, etc.) and retarget to Deadlock skeleton"
            };
            var impIcon = GD.Load<Texture2D>("res://assets/at-icons/file_arrow_down.svg");
            if (impIcon != null) btnImportAnim.Icon = impIcon;
            btnImportAnim.Pressed += () =>
            {
                EnsureAnimationStudioDockMounted();
                _animStudioDock?.OpenImportFileDialog();
            };
            animGroup.AddChild(btnImportAnim);
            animGroup.MoveChild(btnImportAnim, 3);
        }

        var stage = GetTree()?.Root?.FindChild("ParticleStage3D", true, false) as ParticleStage3D;
        if (stage != null)
        {
            SetParticleStage(stage);
        }

        InitAnimationStudioDock();
    }

    public override void _ExitTree()
    {
        SkeletonGizmoManager.OnBonePoseChanged -= OnGizmoBonePoseChanged;
        AnimationTransportService.Instance.PlaybackStateChanged -= OnTransportPlaybackStateChanged;
        AnimationTransportService.Instance.TimeChanged -= OnTransportTimeChanged;
        AnimationTransportService.Instance.LoopChanged -= OnTransportLoopChanged;
        if (ModWorkspaceSession.Instance != null)
        {
            ModWorkspaceSession.Instance.OnAnimationDiscarded -= OnWorkspaceAnimationDiscarded;
            ModWorkspaceSession.Instance.OnWorkspaceCleared -= OnWorkspaceCleared;
        }
        if (_animStudioDock != null && GodotObject.IsInstanceValid(_animStudioDock))
        {
            _animStudioDock.AnimationModified -= OnAnimStudioAnimationModified;
            _animStudioDock.VisibilityToggled -= OnAnimStudioVisibilityToggled;
        }
    }

    public override void _Notification(int what)
    {
        if (what == NotificationVisibilityChanged)
        {
            bool isVis = IsVisibleInTree();
            if (_animStudioDock != null && GodotObject.IsInstanceValid(_animStudioDock))
            {
                _animStudioDock.Visible = isVis;
                if (isVis)
                {
                    _animStudioDock.UpdateFloatingViewportControls();
                }
                else
                {
                    _animStudioDock.ResetFloatingViewportControls();
                }
            }
        }

        if (what == NotificationVisibilityChanged && IsVisibleInTree())
        {
            if (_particleStage == null)
            {
                var stage = GetTree()?.Root?.FindChild("ParticleStage3D", true, false) as ParticleStage3D;
                if (stage != null)
                {
                    SetParticleStage(stage);
                }
            }

            if (_particleStage != null && _particleStage.HasActiveParticle)
            {
                OnParticleStageLoaded(_particleStage.ActiveVpcfPath, _particleStage.ActiveVariantName, _particleStage.ActiveAbilityName);
            }
            else if (_particleStage != null && !_particleStage.HasActiveParticle && _particleControlsGroup != null && _particleControlsGroup.Visible)
            {
                OnParticleStageUnloaded();
            }
        }
    }

    private void InitAnimationStudioDock()
    {
        Callable.From(EnsureAnimationStudioDockMounted).CallDeferred();
        SkeletonGizmoManager.OnBonePoseChanged += OnGizmoBonePoseChanged;
    }

    private void EnsureAnimationStudioDockMounted()
    {
        if (_animStudioDock != null && GodotObject.IsInstanceValid(_animStudioDock)) return;

        var viewportArea = GetTree()?.Root?.FindChild("ViewportArea", true, false) as Control;
        if (viewportArea != null)
        {
            _animStudioDock = viewportArea.FindChild("AnimationStudioDock", true, false) as AnimationStudioDock;
            if (_animStudioDock == null)
            {
                var dockScene = GD.Load<PackedScene>("res://scenes/ui/animation/AnimationStudioDock.tscn")
                             ?? GD.Load<PackedScene>("res://ui/scenes/components/AnimationStudioDock.tscn");
                if (dockScene != null)
                {
                    _animStudioDock = dockScene.Instantiate<AnimationStudioDock>();
                    viewportArea.AddChild(_animStudioDock);
                }
            }

            if (_animStudioDock != null)
            {
                _animStudioDock.Visible = IsVisibleInTree();
                _animStudioDock.AnimationModified += OnAnimStudioAnimationModified;
                _animStudioDock.VisibilityToggled += OnAnimStudioVisibilityToggled;

                if (_skeleton != null && _animPlayer != null && !string.IsNullOrEmpty(_activeAnimName) && _animPlayer.HasAnimation(_activeAnimName))
                {
                    var anim = _animPlayer.GetAnimation(_activeAnimName);
                    if (anim != null)
                    {
                        _animStudioDock.BindTarget(_skeleton, _animPlayer, anim, _activeAnimName, fps: 30f);
                    }
                }
            }
        }
    }

    private void OnAnimStudioVisibilityToggled(bool isOpen)
    {
    }

    public void ToggleAnimationStudio()
    {
        EnsureAnimationStudioDockMounted();
        if (_animStudioDock != null)
        {
            _animStudioDock.ToggleOpen();
        }
    }

    private void OnGizmoBonePoseChanged(int boneIdx, Vector3 pos, Quaternion rot)
    {
        _animStudioDock?.OnBoneTransformedByGizmo(boneIdx, pos, rot);
    }

    private void OnAnimStudioAnimationModified(string animName)
    {
        NotifyPoseChanged(animName);

        try
        {
            if (_animPlayer != null && _animPlayer.HasAnimation(animName) && _skeleton != null)
            {
                var anim = _animPlayer.GetAnimation(animName);
                FindVpkLoader();
                string heroId = _vpkLoader?.LastLoadedHeroEntry?.InternalCodename ?? "";
                string heroDisplayName = _vpkLoader?.LastLoadedHeroEntry?.DisplayName ?? heroId;
                string modelVpkPath = _vpkLoader?.LastLoadedHeroEntry?.VmdlRelativePath ?? "";

                var info = _currentHeroAnimations.FirstOrDefault(a => 
                    a.CleanName.Equals(animName, StringComparison.OrdinalIgnoreCase) ||
                    a.RawName.Equals(animName, StringComparison.OrdinalIgnoreCase));

                string targetVpkPath = info != null && !string.IsNullOrEmpty(info.VpkClipPath)
                    ? info.VpkClipPath
                    : DeadlockAnimLoader.ResolveHeroClipVpkPath(heroId, modelVpkPath, animName, _vpkLoader?.GetOrCreateBasePackage());

                if (!string.IsNullOrEmpty(heroId) && anim != null)
                {
                    ModWorkspaceSession.Instance.StageAnimation(
                        heroId: heroId,
                        heroDisplayName: heroDisplayName,
                        modelVpkPath: modelVpkPath,
                        animName: animName,
                        cleanName: animName,
                        godotAnim: anim,
                        fps: 30f,
                        skeleton: _skeleton,
                        targetVpkPath: targetVpkPath
                    );
                }
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[PlayerTabUI] Failed to stage modified animation to workspace: {ex.Message}");
        }
    }

    private void OnWorkspaceAnimationDiscarded(string heroId, string cleanName)
    {
        FindVpkLoader();
        string curHeroId = _vpkLoader?.LastLoadedHeroEntry?.InternalCodename ?? "";
        if (!string.IsNullOrEmpty(curHeroId) && !string.Equals(curHeroId, heroId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        DeadlockAnimLoader.KnownRemappedAnimations.Remove(cleanName);
        var matchingInfo = _currentHeroAnimations.FirstOrDefault(a =>
            a.CleanName.Equals(cleanName, StringComparison.OrdinalIgnoreCase) ||
            a.RawName.Equals(cleanName, StringComparison.OrdinalIgnoreCase));
        if (matchingInfo != null)
        {
            DeadlockAnimLoader.KnownRemappedAnimations.Remove(matchingInfo.CleanName);
            DeadlockAnimLoader.KnownRemappedAnimations.Remove(matchingInfo.RawName);
            matchingInfo.Category = DeadlockAnimLoader.ClassifyCategory(matchingInfo.CleanName);
        }

        if (_animPlayer != null)
        {
            var animLib = _animPlayer.HasAnimationLibrary("") ? _animPlayer.GetAnimationLibrary("") : null;
            if (animLib != null)
            {
                if (animLib.HasAnimation(cleanName))
                {
                    var a = animLib.GetAnimation(cleanName);
                    a?.RemoveMeta("is_retargeted");
                    a?.RemoveMeta("decoded_by_anim_loader");
                    animLib.RemoveAnimation(cleanName);
                }
                if (matchingInfo != null && animLib.HasAnimation(matchingInfo.RawName))
                {
                    var a = animLib.GetAnimation(matchingInfo.RawName);
                    a?.RemoveMeta("is_retargeted");
                    a?.RemoveMeta("decoded_by_anim_loader");
                    animLib.RemoveAnimation(matchingInfo.RawName);
                }
            }

            bool wasActive = string.Equals(_activeAnimName, cleanName, StringComparison.OrdinalIgnoreCase) ||
                             (matchingInfo != null && string.Equals(_activeAnimName, matchingInfo.RawName, StringComparison.OrdinalIgnoreCase));

            if (wasActive)
            {
                _animPlayer.Stop();
                if (matchingInfo != null)
                {
                    SelectAnimation(matchingInfo, autoPlay: true);
                }
                else
                {
                    AutoSelectDefaultIdle();
                }
            }
        }

        _currentHeroAnimations.Sort((a, b) =>
        {
            int catOrderA = DeadlockAnimLoader.GetCategoryPriority(a.Category);
            int catOrderB = DeadlockAnimLoader.GetCategoryPriority(b.Category);
            if (catOrderA != catOrderB) return catOrderA.CompareTo(catOrderB);
            return string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
        });

        PopulateAnimationTree();
    }

    private void OnWorkspaceCleared()
    {
        DeadlockAnimLoader.KnownRemappedAnimations.Clear();
        if (_animPlayer != null)
        {
            var animLib = _animPlayer.HasAnimationLibrary("") ? _animPlayer.GetAnimationLibrary("") : null;
            if (animLib != null)
            {
                foreach (var aKey in animLib.GetAnimationList())
                {
                    var anim = animLib.GetAnimation(aKey);
                    if (anim != null && anim.HasMeta("is_retargeted"))
                    {
                        anim.RemoveMeta("is_retargeted");
                        anim.RemoveMeta("decoded_by_anim_loader");
                        animLib.RemoveAnimation(aKey);
                    }
                }
            }
        }
        foreach (var a in _currentHeroAnimations)
        {
            a.Category = DeadlockAnimLoader.ClassifyCategory(a.CleanName);
        }
        PopulateAnimationTree();
    }

    private void InitParticleControls()
    {
        var transport = GetNodeOrNull<Control>("TransportGroup");
        if (transport == null) return;

        _particleControlsGroup = new VBoxContainer
        {
            Name = "ParticleControlsGroup",
            Visible = false
        };
        _particleControlsGroup.AddThemeConstantOverride("separation", 8);

        // Header label: autowraps into two rows if space is tight
        var header = new Label
        {
            Text = "Hero Particle Controls\n(CP1 / EndCap)",
            ThemeTypeVariation = "TitleLabel",
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        _particleControlsGroup.AddChild(header);

        // Charge Slider Group (Label above full-width slider to prevent squishing)
        var chargeGroup = new VBoxContainer();
        chargeGroup.AddThemeConstantOverride("separation", 2);

        _labelCharge = new Label
        {
            Text = "Charge (CP1): 0%",
            ThemeTypeVariation = "HeaderSmall",
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        chargeGroup.AddChild(_labelCharge);

        _sliderCharge = new HSlider
        {
            MinValue = 0.0,
            MaxValue = 1.0,
            Step = 0.01,
            Value = 0.0,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 24)
        };
        _sliderCharge.ValueChanged += (double val) =>
        {
            if (_particleStage != null)
            {
                _particleStage.SetCharge((float)val);
                if (_labelCharge != null)
                {
                    _labelCharge.Text = $"Charge (CP1): {Mathf.RoundToInt(val * 100)}%";
                }
            }
        };
        chargeGroup.AddChild(_sliderCharge);
        _particleControlsGroup.AddChild(chargeGroup);

        // Action Group (Prominent Release button on row 1, Auto-Loop checkbox on row 2)
        var actionGroup = new VBoxContainer();
        actionGroup.AddThemeConstantOverride("separation", 4);

        _btnRelease = new Button
        {
            Text = "Release (EndCap)",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 32)
        };
        _btnRelease.Pressed += () =>
        {
            _particleStage?.TriggerRelease();
        };
        actionGroup.AddChild(_btnRelease);

        _chkAutoLoop = new CheckBox
        {
            Text = "Auto Loop (Charge & Release)",
            ButtonPressed = false,
            TooltipText = "Ramps Charge (CP1) automatically 0->1 and releases EndCap on loop"
        };
        _chkAutoLoop.Toggled += (bool toggled) =>
        {
            if (_particleStage != null)
            {
                _particleStage.SetAutoChargeLoop(toggled);
            }
        };
        actionGroup.AddChild(_chkAutoLoop);
        _particleControlsGroup.AddChild(actionGroup);

        transport.AddChild(_particleControlsGroup);
    }

    public void SetParticleStage(ParticleStage3D stage)
    {
        if (_particleStage != null)
        {
            _particleStage.ParticleLoaded -= OnParticleStageLoaded;
            _particleStage.ParticleUnloaded -= OnParticleStageUnloaded;
        }

        _particleStage = stage;
        if (_particleStage != null)
        {
            _particleStage.ParticleLoaded += OnParticleStageLoaded;
            _particleStage.ParticleUnloaded += OnParticleStageUnloaded;

            if (_particleStage.HasActiveParticle)
            {
                OnParticleStageLoaded(_particleStage.ActiveVpcfPath, _particleStage.ActiveVariantName, _particleStage.ActiveAbilityName);
            }
        }
    }

    private void OnParticleStageLoaded(string vpcfPath, string variantName, string abilityName)
    {
        if (_particleStage == null) return;
        _animPlayer?.Stop();

        // Hide character model animations and pose buttons while particle is active
        var animGroup = GetNodeOrNull<Control>("AnimGroup");
        if (animGroup != null) animGroup.Visible = false;

        var actionButtons = GetNodeOrNull<Control>("TransportGroup/ActionButtons");
        if (actionButtons != null) actionButtons.Visible = false;

        _particleStage.SetLooping(_isLooping);

        if (_timelineSlider != null)
        {
            _timelineSlider.MinValue = 0.0;
            _timelineSlider.MaxValue = _particleStage.Duration;
            _timelineSlider.Step = 0.01;
            _timelineSlider.SetValueNoSignal(0.0);
        }
        UpdateTimeLabel(0.0f, _particleStage.Duration);

        if (_btnPlayPause != null)
        {
            _btnPlayPause.ButtonPressed = true;
            _btnPlayPause.Icon = _iconPause;
        }

        if (_particleControlsGroup != null)
        {
            _particleControlsGroup.Visible = true;
            if (_sliderCharge != null)
            {
                _sliderCharge.SetValueNoSignal(_particleStage.Charge);
            }
            if (_labelCharge != null)
            {
                _labelCharge.Text = $"Charge (CP1): {Mathf.RoundToInt(_particleStage.Charge * 100)}%";
            }
            if (_chkAutoLoop != null)
            {
                _chkAutoLoop.SetPressedNoSignal(_particleStage.AutoChargeLoop);
            }
        }
        AnimationTransportService.Instance.SetParticleMode(true);
        _animStudioDock?.SetTransportEnabled(false);
    }

    private void OnParticleStageUnloaded()
    {
        // Restore character model animations and pose buttons
        var animGroup = GetNodeOrNull<Control>("AnimGroup");
        if (animGroup != null) animGroup.Visible = true;

        var actionButtons = GetNodeOrNull<Control>("TransportGroup/ActionButtons");
        if (actionButtons != null) actionButtons.Visible = true;

        if (_timelineSlider != null)
        {
            _timelineSlider.MinValue = 0.0;
            _timelineSlider.MaxValue = _activeAnimLength;
            _timelineSlider.Step = 0.01;
            _timelineSlider.SetValueNoSignal(0.0);
        }
        AnimationTransportService.Instance.SetParticleMode(false);
        _animStudioDock?.SetTransportEnabled(true);
        UpdateTimeLabel(0.0f, _activeAnimLength);

        if (_particleControlsGroup != null)
        {
            _particleControlsGroup.Visible = false;
        }
    }

    public override void _Process(double delta)
    {
        if (_particleStage != null && _particleStage.HasActiveParticle)
        {
            if (!_isUserDraggingSlider)
            {
                _timelineSlider?.SetValueNoSignal(_particleStage.CurrentTime);
                UpdateTimeLabel(_particleStage.CurrentTime, _particleStage.Duration);
            }

            if (_particleStage.AutoChargeLoop && _sliderCharge != null)
            {
                _sliderCharge.SetValueNoSignal(_particleStage.Charge);
                if (_labelCharge != null)
                {
                    _labelCharge.Text = $"Charge (CP1): {Mathf.RoundToInt(_particleStage.Charge * 100)}%";
                }
            }
            return;
        }

        AnimationTransportService.Instance.Tick(delta);
    }

    public void SetSkeleton(Skeleton3D skeleton)
    {
        _skeleton = (skeleton != null && GodotObject.IsInstanceValid(skeleton) && !skeleton.IsQueuedForDeletion()) ? skeleton : null;
        if (_animStudioDock != null)
        {
            _animStudioDock.SetSkeleton(_skeleton);
            if (_skeleton != null && _animPlayer != null && !string.IsNullOrEmpty(_activeAnimName) && _animPlayer.HasAnimation(_activeAnimName))
            {
                var curAnim = _animPlayer.GetAnimation(_activeAnimName);
                _animStudioDock.BindTarget(_skeleton, _animPlayer, curAnim, _activeAnimName, fps: 30f);
            }
        }
    }

    public void SetAnimationPlayer(AnimationPlayer animPlayer)
    {
        if (_animPlayer != null)
        {
            _animPlayer.AnimationFinished -= OnAnimationFinished;
        }

        _animPlayer = animPlayer;
        if (_animStudioDock != null)
        {
            _animStudioDock.SetAnimationPlayer(_animPlayer);
        }
        if (_animPlayer == null)
        {
            ResetTransportUI();
            _animTree?.Clear();
            _currentHeroAnimations.Clear();
            _activeAnimName = "";
            _activeAnimLength = 0f;
            return;
        }

        // Ensure _skeleton is valid and not pointing to a disposed old skeleton
        if (_skeleton == null || !GodotObject.IsInstanceValid(_skeleton) || _skeleton.IsQueuedForDeletion())
        {
            _skeleton = _animPlayer.GetNodeOrNull<Skeleton3D>(_animPlayer.RootNode)
                     ?? _animPlayer.GetParent()?.FindChild("GeneralSkeleton", true, false) as Skeleton3D
                     ?? _animPlayer.GetParent()?.FindChild("Skeleton3D", true, false) as Skeleton3D;
        }

        _animPlayer.AnimationFinished += OnAnimationFinished;

        PopulateAnimationTreeFromPlayer();
        AutoSelectDefaultIdle();

        if (_animStudioDock != null && _skeleton != null && GodotObject.IsInstanceValid(_skeleton) && !_skeleton.IsQueuedForDeletion() && _animPlayer != null && !string.IsNullOrEmpty(_activeAnimName) && _animPlayer.HasAnimation(_activeAnimName))
        {
            var curAnim = _animPlayer.GetAnimation(_activeAnimName);
            _animStudioDock.BindTarget(_skeleton, _animPlayer, curAnim, _activeAnimName, fps: 30f);
        }
    }

    private void OnAnimationFinished(StringName animName)
    {
        if (!_isLooping && _animPlayer != null)
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
                ProceduralClothSolver.Conform(_skeleton, force: true, _animPlayer);
            }
        }
    }

    private void TrackDecodedAnimationLru(string animName)
    {
        if (string.IsNullOrEmpty(animName)) return;

        _decodedAnimLru.Remove(animName);
        _decodedAnimLru.Add(animName);

        EvictOldDecodedAnimations();
    }

    private void EvictOldDecodedAnimations()
    {
        if (_animPlayer == null || !_animPlayer.HasAnimationLibrary("")) return;
        if (_decodedAnimLru.Count <= MaxCachedDecodedAnims) return;

        var animLib = _animPlayer.GetAnimationLibrary("");
        var defaultIdle = FindDefaultIdleAnim();
        string defaultIdleClean = defaultIdle?.CleanName ?? "";
        string defaultIdleRaw = defaultIdle?.RawName ?? "";

        for (int i = 0; i < _decodedAnimLru.Count && _decodedAnimLru.Count > MaxCachedDecodedAnims;)
        {
            string candidate = _decodedAnimLru[i];

            // Never evict active animation or its underlaid variant
            if (candidate.Equals(_activeAnimName, StringComparison.OrdinalIgnoreCase) ||
                candidate.Equals($"{_activeAnimName}_underlaid", StringComparison.OrdinalIgnoreCase))
            {
                i++;
                continue;
            }

            // Never evict default idle (needed for underlaying additive clips)
            if (candidate.Equals(defaultIdleClean, StringComparison.OrdinalIgnoreCase) ||
                candidate.Equals(defaultIdleRaw, StringComparison.OrdinalIgnoreCase) ||
                candidate.Equals($"{defaultIdleClean}_underlaid", StringComparison.OrdinalIgnoreCase))
            {
                i++;
                continue;
            }

            if (animLib.HasAnimation(candidate))
            {
                var godotAnim = animLib.GetAnimation(candidate);
                // Only evict dynamically decoded animations
                if (godotAnim != null && godotAnim.HasMeta("decoded_by_anim_loader"))
                {
                    animLib.RemoveAnimation(candidate);
                    GD.Print($"[AnimLoader] LRU Cache: Evicted dynamic animation '{candidate}' from memory.");
                }
            }

            _decodedAnimLru.RemoveAt(i);
        }
    }

    public void PopulateAnimationTreeFromPlayer()
    {
        if (_animPlayer == null) return;

        _currentHeroAnimations.Clear();
        FindVpkLoader();

        var seenCleanNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Ingest metadata from VpkLoader if available (lazy loading with zero RAM bloat)
        if (_vpkLoader?.CurrentHeroAnimations != null && _vpkLoader.CurrentHeroAnimations.Count > 0)
        {
            foreach (var vrfAnim in _vpkLoader.CurrentHeroAnimations)
            {
                if (seenCleanNames.Add(vrfAnim.CleanName))
                {
                    _currentHeroAnimations.Add(vrfAnim);
                }
            }
        }

        // 2. Also merge any local / pre-baked / imported / remapped animations already in AnimationPlayer
        var animList = _animPlayer.GetAnimationList();
        foreach (var animName in animList)
        {
            var anim = _animPlayer.GetAnimation(animName);
            string cleanName = DeadlockAnimLoader.SanitizePoseName(animName);

            var existing = _currentHeroAnimations.FirstOrDefault(a =>
                a.RawName.Equals(animName, StringComparison.OrdinalIgnoreCase) ||
                a.CleanName.Equals(cleanName, StringComparison.OrdinalIgnoreCase));

            bool isRetargeted = (anim != null && anim.HasMeta("is_retargeted") && (bool)anim.GetMeta("is_retargeted")) ||
                                DeadlockAnimLoader.KnownRemappedAnimations.Contains(animName) ||
                                DeadlockAnimLoader.KnownRemappedAnimations.Contains(cleanName);

            if (!isRetargeted && DeadlockAnimLoader.IsBlacklisted(animName, false, null))
            {
                continue;
            }

            if (existing != null)
            {
                if (isRetargeted)
                {
                    existing.Category = "Remapped";
                    if (anim != null)
                    {
                        existing.Duration = (float)anim.Length;
                        existing.Fps = 30f;
                        existing.FrameCount = Math.Max(1, (int)Math.Round(anim.Length * 30.0) + 1);
                        existing.IsLooping = anim.LoopMode != Godot.Animation.LoopModeEnum.None;
                    }
                }
                continue;
            }

            bool isAdditive = false;
            if (anim != null)
            {
                DeadlockAnimLoader.StripClothTracks(anim, _skeleton);
                isAdditive = DeadlockAnimLoader.IsAdditiveAnimation(anim, null, _skeleton) ||
                             DeadlockAnimLoader.ShouldStripPositionTracks(anim, _skeleton, animName);
                if (isAdditive)
                {
                    anim.SetMeta("is_additive", true);
                    DeadlockAnimLoader.StripAdditivePositionTracks(anim, _skeleton);
                }
            }
            string displayName = DeadlockAnimLoader.FormatDisplayName(cleanName);
            string category = isRetargeted ? "Remapped" : DeadlockAnimLoader.ClassifyCategory(cleanName);
            float duration = (float)(anim?.Length ?? 0.1f);
            bool isLooping = DeadlockAnimLoader.DetermineLooping(cleanName, category);

            if (seenCleanNames.Add(cleanName))
            {
                _currentHeroAnimations.Add(new DeadlockAnimLoader.AnimSequenceInfo
                {
                    RawName = animName,
                    CleanName = cleanName,
                    DisplayName = displayName,
                    Category = category,
                    Duration = duration,
                    IsLooping = isLooping,
                    IsAdditive = isAdditive
                });
            }
        }

        // 3. Restore any animations staged in active ModWorkspaceSession for this hero
        try
        {
            string heroId = _vpkLoader?.LastLoadedHeroEntry?.InternalCodename ?? "";
            if (!string.IsNullOrEmpty(heroId) &&
                ModWorkspaceSession.Instance.Manifest.Heroes.TryGetValue(heroId, out var heroEntry) &&
                heroEntry.Assets != null)
            {
                var animLib = _animPlayer.HasAnimationLibrary("") ? _animPlayer.GetAnimationLibrary("") : null;
                foreach (var asset in heroEntry.Assets)
                {
                    if (asset.Animations == null || asset.Animations.Count == 0) continue;

                    foreach (var stagedEntry in asset.Animations)
                    {
                        if (string.IsNullOrEmpty(stagedEntry.CleanName)) continue;

                        string animKey = stagedEntry.CleanName;
                        Godot.Animation anim = null;

                        if (animLib != null)
                        {
                            anim = ModWorkspaceSession.Instance.LoadStagedAnimationToGodot(stagedEntry, _skeleton);
                            if (anim != null)
                            {
                                if (animLib.HasAnimation(animKey))
                                {
                                    animLib.RemoveAnimation(animKey);
                                }
                                animLib.AddAnimation(animKey, anim);
                                GD.Print($"[PlayerTabUI] Restored staged animation '{animKey}' into AnimationPlayer for hero '{heroId}'.");
                            }
                        }

                        DeadlockAnimLoader.KnownRemappedAnimations.Add(animKey);
                        DeadlockAnimLoader.KnownRemappedAnimations.Add(stagedEntry.AnimName);

                        var existing = _currentHeroAnimations.FirstOrDefault(a =>
                            a.RawName.Equals(animKey, StringComparison.OrdinalIgnoreCase) ||
                            a.CleanName.Equals(animKey, StringComparison.OrdinalIgnoreCase));

                        if (existing != null)
                        {
                            existing.Category = "Remapped";
                            existing.Duration = stagedEntry.Duration > 0 ? stagedEntry.Duration : (float)(anim?.Length ?? 1.0);
                            existing.IsLooping = stagedEntry.IsLooping;
                            existing.IsAdditive = stagedEntry.IsAdditive;
                            existing.VpkClipPath = stagedEntry.TargetVpkPath;
                        }
                        else
                        {
                            seenCleanNames.Add(animKey);
                            _currentHeroAnimations.Add(new DeadlockAnimLoader.AnimSequenceInfo
                            {
                                RawName = animKey,
                                CleanName = animKey,
                                DisplayName = DeadlockAnimLoader.FormatDisplayName(animKey),
                                Category = "Remapped",
                                Duration = stagedEntry.Duration > 0 ? stagedEntry.Duration : (float)(anim?.Length ?? 1.0),
                                IsLooping = stagedEntry.IsLooping,
                                IsAdditive = stagedEntry.IsAdditive,
                                VpkClipPath = stagedEntry.TargetVpkPath
                            });
                        }
                    }
                }
            }
        }
        catch (Exception wsEx)
        {
            GD.PrintErr($"[PlayerTabUI] Failed to restore workspace staged animations for hero: {wsEx.Message}");
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
            "Remapped",
            "Exported",
            "Idles & Stances",
            "Locomotion",
            "Traversal & Zipline",
            "Combat & Attacks",
            "Abilities & Magic",
            "Reactions & Status",
            "Emotes & Taunts",
            "Additive Layers",
            "Misc / Other"
        };

        foreach (var category in categories)
        {
            var itemsInCategory = _currentHeroAnimations
                .Where(a => a.Category.Equals(category, StringComparison.OrdinalIgnoreCase) ||
                            (category == "Idles & Stances" && a.Category.Equals("Idle", StringComparison.OrdinalIgnoreCase)) ||
                            (category == "Combat & Attacks" && a.Category.Equals("Combat", StringComparison.OrdinalIgnoreCase)) ||
                            (category == "Abilities & Magic" && a.Category.Equals("Abilities", StringComparison.OrdinalIgnoreCase)) ||
                            (category == "Reactions & Status" && a.Category.Equals("Reactions", StringComparison.OrdinalIgnoreCase)) ||
                            (category == "Emotes & Taunts" && a.Category.Equals("Emotes & Expressions", StringComparison.OrdinalIgnoreCase)))
                .Where(a => string.IsNullOrEmpty(filter) ||
                            a.DisplayName.ToLowerInvariant().Contains(filter) ||
                            a.CleanName.ToLowerInvariant().Contains(filter) ||
                            a.RawName.ToLowerInvariant().Contains(filter))
                .ToList();

            if (itemsInCategory.Count == 0)
            {
                if (category == "Remapped" && string.IsNullOrEmpty(filter))
                {
                    TreeItem emptyCategory = _animTree.CreateItem(root);
                    var iconEmpty = GD.Load<Texture2D>("res://assets/at-icons/file_arrow_down.svg") ?? _iconFolder;
                    emptyCategory.SetIcon(0, iconEmpty);
                    emptyCategory.SetIconMaxWidth(0, 16);
                    emptyCategory.SetCustomColor(0, new Color(0.31f, 0.93f, 0.69f, 0.8f));
                    emptyCategory.SetCustomMinimumHeight(28);
                    emptyCategory.SetText(0, "Imported / Remapped (0)");
                    emptyCategory.SetSelectable(0, false);
                    emptyCategory.Collapsed = true;

                    TreeItem emptyPlaceholder = _animTree.CreateItem(emptyCategory);
                    emptyPlaceholder.SetText(0, "(No imported animations yet)");
                    emptyPlaceholder.SetCustomColor(0, new Color(0.6f, 0.6f, 0.6f, 0.7f));
                    emptyPlaceholder.SetSelectable(0, false);
                }
                continue;
            }

            TreeItem categoryItem = _animTree.CreateItem(root);
            var iconCat = (category == "Remapped" || category == "Exported")
                ? (GD.Load<Texture2D>("res://assets/at-icons/file_arrow_down.svg") ?? _iconFolder)
                : _iconFolder;
            categoryItem.SetIcon(0, iconCat);
            categoryItem.SetIconMaxWidth(0, 16);
            Color catColor = category switch
            {
                "Remapped" => new Color(0.31f, 0.93f, 0.69f, 1.0f),
                "Exported" => new Color(0.34f, 0.80f, 0.95f, 1.0f),
                "Idles & Stances" or "Idle" => new Color(0.95f, 0.85f, 0.6f, 1.0f),
                "Locomotion" => new Color(0.45f, 0.85f, 0.95f, 1.0f),
                "Traversal & Zipline" => new Color(0.55f, 0.90f, 0.70f, 1.0f),
                "Combat & Attacks" or "Combat" => new Color(0.95f, 0.55f, 0.45f, 1.0f),
                "Abilities & Magic" or "Abilities" => new Color(0.85f, 0.60f, 0.95f, 1.0f),
                "Reactions & Status" or "Reactions" => new Color(0.95f, 0.75f, 0.45f, 1.0f),
                "Emotes & Taunts" or "Emotes & Expressions" => new Color(0.95f, 0.65f, 0.85f, 1.0f),
                "Additive Layers" => new Color(0.6f, 0.85f, 1.0f, 1.0f),
                _ => new Color(0.9f, 0.9f, 0.9f, 1.0f)
            };
            categoryItem.SetCustomColor(0, catColor);
            categoryItem.SetCustomMinimumHeight(28);
            categoryItem.SetText(0, category == "Remapped" ? $"Imported / Remapped ({itemsInCategory.Count})" : $"{category} ({itemsInCategory.Count})");
            categoryItem.SetSelectable(0, false);

            // Collapsed by default when not searching!
            categoryItem.Collapsed = string.IsNullOrEmpty(filter);

            foreach (var anim in itemsInCategory)
            {
                TreeItem item = _animTree.CreateItem(categoryItem);
                item.SetAutowrapMode(0, TextServer.AutowrapMode.WordSmart);
                item.SetTextOverrunBehavior(0, TextServer.OverrunBehavior.TrimEllipsis);

                string itemText = $"{anim.DisplayName}  [{anim.Duration:F2}s]";
                int charLen = itemText.Length;
                int minHeight = charLen > 40 ? 46 : (charLen > 22 ? 36 : 26);
                item.SetCustomMinimumHeight(minHeight);

                if (!anim.IsCompatible)
                {
                    item.SetText(0, $"⚠ {itemText} [Incompatible]");
                    item.SetTooltipText(0, $"Incompatible prop/skeleton animation: {anim.IncompatibilityReason}\nRaw: {anim.RawName}");
                    item.SetCustomColor(0, new Color(0.55f, 0.55f, 0.55f, 0.6f));
                    item.SetSelectable(0, false);
                    continue;
                }

                item.SetText(0, itemText);
                item.SetTooltipText(0, $"{anim.DisplayName} ({anim.Duration:F2}s)\nRaw: {anim.RawName}");
                item.SetMetadata(0, anim.RawName);
                item.SetSelectable(0, true);

                if (anim.RawName == _activeAnimName || anim.CleanName == _activeAnimName ||
                    _activeAnimName == $"{anim.CleanName}_underlaid" || _activeAnimName == $"{anim.RawName}_underlaid")
                {
                    item.Select(0);
                    categoryItem.Collapsed = false; // Auto-expand active category
                }
            }
        }
    }

   private void OnInPlaceMotionToggled(bool toggled)
    {
        _isInPlaceMotionEnabled = toggled;
        if (_activeAnimInfo == null || _animPlayer == null || !GodotObject.IsInstanceValid(_animPlayer)) return;

        string clean = _activeAnimInfo.CleanName;
        var animLib = _animPlayer.HasAnimationLibrary("") ? _animPlayer.GetAnimationLibrary("") : null;

        // Check if current animation is a retargeted / imported animation (which does NOT exist in the VPK!)
        Godot.Animation existingAnim = _animPlayer.HasAnimation(clean) ? _animPlayer.GetAnimation(clean)
                                     : (_animPlayer.HasAnimation(_activeAnimInfo.RawName) ? _animPlayer.GetAnimation(_activeAnimInfo.RawName) : null);

        bool isRetargeted = _activeAnimInfo.Category == "Remapped" ||
                            (existingAnim != null && existingAnim.HasMeta("is_retargeted") && existingAnim.GetMeta("is_retargeted").AsBool()) ||
                            DeadlockAnimLoader.KnownRemappedAnimations.Contains(clean) ||
                            DeadlockAnimLoader.KnownRemappedAnimations.Contains(_activeAnimInfo.RawName);

        if (isRetargeted && existingAnim != null)
        {
            // Toggle horizontal in-place lock non-destructively on the retargeted pelvis position track!
            ToggleRetargetedAnimInPlace(existingAnim, toggled);
            double pos = _timelineSlider != null ? _timelineSlider.Value : _animPlayer.CurrentAnimationPosition;
            _animPlayer.Seek(pos, update: true);
            _skeleton?.ForceUpdateAllBoneTransforms();
            return;
        }

        if (animLib != null)
        {
            if (animLib.HasAnimation(clean)) animLib.RemoveAnimation(clean);
            if (animLib.HasAnimation($"{clean}_underlaid")) animLib.RemoveAnimation($"{clean}_underlaid");
            if (animLib.HasAnimation(_activeAnimInfo.RawName)) animLib.RemoveAnimation(_activeAnimInfo.RawName);
            if (animLib.HasAnimation($"{_activeAnimInfo.RawName}_underlaid")) animLib.RemoveAnimation($"{_activeAnimInfo.RawName}_underlaid");
        }

        double currentPos = _timelineSlider != null ? _timelineSlider.Value : _animPlayer.CurrentAnimationPosition;
        bool wasPlaying = _animPlayer.IsPlaying();
        SelectAnimation(_activeAnimInfo, autoPlay: wasPlaying);
        if (_animPlayer.HasAnimation(_activeAnimName))
        {
            _animPlayer.Seek(currentPos, update: true);
            if (wasPlaying) _animPlayer.Play(_activeAnimName);
            else _animPlayer.Pause();
        }
        _timelineSlider?.SetValueNoSignal(currentPos);
        UpdateTimeLabel((float)currentPos, _activeAnimLength);
    }

    private void ToggleRetargetedAnimInPlace(Godot.Animation anim, bool lockInPlace)
    {
        if (anim == null) return;
        for (int t = 0; t < anim.GetTrackCount(); t++)
        {
            if (anim.TrackGetType(t) != Godot.Animation.TrackType.Position3D) continue;
            string path = anim.TrackGetPath(t).ToString();
            if (!path.EndsWith(":pelvis", StringComparison.OrdinalIgnoreCase) &&
                !path.EndsWith(":root", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            int keyCount = anim.TrackGetKeyCount(t);
            if (keyCount == 0) continue;

            string metaKey = $"orig_pos_track_{t}";
            if (!anim.HasMeta(metaKey))
            {
                var backup = new Godot.Collections.Array<Vector3>();
                for (int k = 0; k < keyCount; k++)
                {
                    backup.Add(anim.TrackGetKeyValue(t, k).AsVector3());
                }
                anim.SetMeta(metaKey, backup);
            }

            var origKeys = anim.GetMeta(metaKey).AsGodotArray<Vector3>();
            if (origKeys == null || origKeys.Count != keyCount) continue;

            Vector3 anchor = origKeys[0];
            for (int k = 0; k < keyCount; k++)
            {
                Vector3 orig = origKeys[k];
                Vector3 val = lockInPlace ? new Vector3(anchor.X, orig.Y, anchor.Z) : orig;
                anim.TrackSetKeyValue(t, k, val);
            }
        }
    }

    private DeadlockAnimLoader.AnimSequenceInfo FindDefaultIdleAnim()
    {
        if (_currentHeroAnimations.Count == 0) return null;

        static bool IsExcluded(string name)
        {
            string l = name.ToLowerInvariant();
            return l.Contains("ability") ||
                   l.Contains("cast") ||
                   l.Contains("skill") ||
                   l.Contains("ult") ||
                   l.Contains("item") ||
                   l.Contains("sleep") ||
                   l.Contains("corner") ||
                   l.Contains("stun") ||
                   l.Contains("death") ||
                   l.Contains("die") ||
                   l.Contains("hurt") ||
                   l.Contains("hit") ||
                   l.Contains("flinch") ||
                   l.Contains("impact") ||
                   l.Contains("react") ||
                   l.Contains("recoil") ||
                   l.Contains("fire") ||
                   l.Contains("shoot") ||
                   l.Contains("aim") ||
                   l.Contains("intro") ||
                   l.Contains("menu") ||
                   l.Contains("portrait") ||
                   l.Contains("taunt") ||
                   l.Contains("action");
        }

        string[] primaryIdles = [
            "primary_stand_idle",
            "out_of_combat_stand_idle",
            "combat_stand_idle",
            "primary_idle",
            "stand_idle",
            "idle_stand",
            "idle_loadout",
            "idle"
        ];

        // Tier 1: Exact matches for standard combat resting idles
        foreach (var candidate in primaryIdles)
        {
            var match = _currentHeroAnimations.FirstOrDefault(a =>
                a.CleanName.Equals(candidate, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;
        }

        // Tier 2: Animations ending with standard combat resting idles (and not excluded)
        foreach (var candidate in primaryIdles)
        {
            var match = _currentHeroAnimations.FirstOrDefault(a =>
                !IsExcluded(a.CleanName) &&
                a.CleanName.EndsWith(candidate, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;
        }

        // Tier 3: Any Idle-category animation containing "stand_idle" or "idle" without exclusion
        var idleMatch = _currentHeroAnimations.FirstOrDefault(a =>
            (a.Category == "Idles & Stances" || a.Category == "Idle") &&
            !IsExcluded(a.CleanName) &&
            (a.CleanName.Contains("stand_idle", StringComparison.OrdinalIgnoreCase) ||
             a.CleanName.Contains("idle", StringComparison.OrdinalIgnoreCase)));
        if (idleMatch != null) return idleMatch;

        // Tier 4: Any non-additive, non-excluded Idle
        var anyIdle = _currentHeroAnimations.FirstOrDefault(a =>
            (a.Category == "Idles & Stances" || a.Category == "Idle") && !IsExcluded(a.CleanName));
        if (anyIdle != null) return anyIdle;

        // Tier 5: Fallback to any non-additive animation
        var fallback = _currentHeroAnimations.FirstOrDefault(a =>
            a.Category != "Additive Layers" && !a.IsAdditive && !IsExcluded(a.CleanName));
        return fallback ?? _currentHeroAnimations.FirstOrDefault();
    }

    private Godot.Animation GetOrDecodeAnimation(DeadlockAnimLoader.AnimSequenceInfo info)
    {
        if (info == null || _animPlayer == null) return null;

        string targetName = _animPlayer.HasAnimation(info.CleanName) ? info.CleanName :
                           (_animPlayer.HasAnimation(info.RawName) ? info.RawName : null);

        if (targetName != null)
        {
            var anim = _animPlayer.GetAnimation(targetName);
            if (anim != null)
            {
                // CRITICAL: Protect retargeted, imported, or studio-modified animations!
                bool isRetargeted = (anim.HasMeta("is_retargeted") && (bool)anim.GetMeta("is_retargeted")) ||
                                    DeadlockAnimLoader.KnownRemappedAnimations.Contains(targetName) ||
                                    DeadlockAnimLoader.KnownRemappedAnimations.Contains(info.CleanName) ||
                                    DeadlockAnimLoader.KnownRemappedAnimations.Contains(info.RawName);

                if (isRetargeted)
                {
                    return anim;
                }

                bool isDecodedByLoader = anim.HasMeta("decoded_by_anim_loader");
                bool isStaleVersion = isDecodedByLoader
                    ? (!anim.HasMeta("anim_loader_version") || anim.GetMeta("anim_loader_version").AsInt32() < 5)
                    : false;

                // Pre-baked GLTF clips always have root motion; if in-place mode is enabled, evict and decode via loader!
                // For already decoded clips, verify that in_place matches the current user toggle.
                bool isMismatchedInPlace = isDecodedByLoader
                    ? (!anim.HasMeta("in_place") || anim.GetMeta("in_place").AsBool() != _isInPlaceMotionEnabled)
                    : _isInPlaceMotionEnabled;

                bool hasBrokenPrefix = false;
                for (int t = 0; t < anim.GetTrackCount(); t++)
                {
                    if (anim.TrackGetPath(t).ToString().StartsWith("../"))
                    {
                        hasBrokenPrefix = true;
                        break;
                    }
                }

                if (!isStaleVersion && !hasBrokenPrefix && !isMismatchedInPlace)
                {
                    return anim;
                }

                // Stale broken or mismatched animation: remove it so it can be cleanly re-decoded
                var animLib = _animPlayer.HasAnimationLibrary("") ? _animPlayer.GetAnimationLibrary("") : null;
                if (animLib != null && animLib.HasAnimation(targetName))
                {
                    animLib.RemoveAnimation(targetName);
                }
            }
        }

        FindVpkLoader();
        if (_vpkLoader != null && _skeleton != null)
        {
            var defaultIdle = _isIdleUnderlayEnabled ? FindDefaultIdleAnim() : null;
            var decoded = _vpkLoader.DecodeAnimation(info, _skeleton, _animPlayer, idleInfo: defaultIdle, inPlace: _isInPlaceMotionEnabled);
            if (decoded != null) return decoded;
        }

        if (_animPlayer.HasAnimation(info.CleanName))
        {
            return _animPlayer.GetAnimation(info.CleanName);
        }

        return null;
    }

    private void AutoSelectDefaultIdle()
    {
        var bestAnim = FindDefaultIdleAnim();
        if (bestAnim != null)
        {
            SelectAnimation(bestAnim, autoPlay: false);
            HighlightTreeItem(bestAnim.RawName);
        }
    }

    private void HighlightTreeItem(string animKey)
    {
        if (_animTree == null || string.IsNullOrEmpty(animKey)) return;
        TreeItem root = _animTree.GetRoot();
        if (root == null) return;

        foreach (TreeItem cat in root.GetChildren())
        {
            foreach (TreeItem child in cat.GetChildren())
            {
                string itemKey = child.GetMetadata(0).AsString();
                if (itemKey == animKey || itemKey.EndsWith(animKey) ||
                    animKey.StartsWith(itemKey) || animKey.Contains(itemKey))
                {
                    cat.Collapsed = false; // Auto-expand category containing active clip
                    child.Select(0);
                    _animTree.ScrollToItem(child);
                    return;
                }
            }
        }
    }

    public void SelectAnimation(DeadlockAnimLoader.AnimSequenceInfo info, bool autoPlay = false)
    {
        if (info == null || _animPlayer == null || _skeleton == null || !info.IsCompatible) return;

        _activeAnimInfo = info;

        var rawAnim = GetOrDecodeAnimation(info);
        bool isAdditive = (info.Category == "Additive Layers") ||
                          (info.VrfAnimation != null ? info.VrfAnimation.IsAdditive : info.IsAdditive) ||
                          DeadlockAnimLoader.IsDeltaSequence(info);
        if (info.Category == "Idles & Stances" || info.Category == "Idle" || info.Category == "Remapped" ||
            (rawAnim != null && rawAnim.HasMeta("is_retargeted")) ||
            DeadlockAnimLoader.KnownRemappedAnimations.Contains(info.CleanName) ||
            DeadlockAnimLoader.KnownRemappedAnimations.Contains(info.RawName))
        {
            isAdditive = false;
        }

        DeadlockAnimLoader.AnimSequenceInfo idleInfo = null;
        if (isAdditive && _isIdleUnderlayEnabled)
        {
            idleInfo = FindDefaultIdleAnim();
            if (idleInfo == info) idleInfo = null;
        }

        string targetAnim = (isAdditive && _isIdleUnderlayEnabled && idleInfo != null)
            ? $"{info.CleanName}_underlaid"
            : info.CleanName;

        if (isAdditive && _isIdleUnderlayEnabled && idleInfo != null)
        {
            if (!_animPlayer.HasAnimation(targetAnim))
            {
                var idleAnim = GetOrDecodeAnimation(idleInfo);
                if (rawAnim != null && idleAnim != null)
                {
                    var composed = DeadlockAnimLoader.ComposeOverIdle(rawAnim, idleAnim, _skeleton);
                    if (composed != null)
                    {
                        var animLib = _animPlayer.HasAnimationLibrary("")
                            ? _animPlayer.GetAnimationLibrary("")
                            : new AnimationLibrary();
                        if (!_animPlayer.HasAnimationLibrary(""))
                        {
                            _animPlayer.AddAnimationLibrary("", animLib);
                        }
                        if (animLib.HasAnimation(targetAnim))
                        {
                            animLib.RemoveAnimation(targetAnim);
                        }
                        animLib.AddAnimation(targetAnim, composed);
                    }
                }
            }
        }

        if (!_animPlayer.HasAnimation(targetAnim))
        {
            FindVpkLoader();
            if (_vpkLoader != null)
            {
                _vpkLoader.DecodeAnimation(info, _skeleton, _animPlayer, idleInfo, targetAnim, inPlace: _isInPlaceMotionEnabled);
            }
        }

        if (!_animPlayer.HasAnimation(targetAnim))
        {
            if (_animPlayer.HasAnimation(info.CleanName)) targetAnim = info.CleanName;
            else if (_animPlayer.HasAnimation(info.RawName)) targetAnim = info.RawName;
            else return;
        }

        TrackDecodedAnimationLru(targetAnim);

        var godotAnim = _animPlayer.GetAnimation(targetAnim);
        if (godotAnim == null) return;

        // Configure animation playback parameters (preserve authored bone tracks; do not mutate or strip)
        DeadlockAnimLoader.StripClothTracks(godotAnim, _skeleton);

        string playAnimName = targetAnim;
        Godot.Animation playAnim = godotAnim;

        _activeAnimName = playAnimName;
        _activeAnimLength = (float)(playAnim?.Length ?? info.Duration);

        if (playAnim != null)
        {
            playAnim.LoopMode = _isLooping ? Godot.Animation.LoopModeEnum.Linear : Godot.Animation.LoopModeEnum.None;
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

        // Reset all bone poses cleanly to rest/bind pose before evaluating
        for (int i = 0; i < _skeleton.GetBoneCount(); i++)
        {
            _skeleton.ResetBonePose(i);
        }

        AnimationTransportService.Instance.Bind(_animPlayer, _skeleton, playAnimName, (float)(info?.Fps ?? 30f));

        if (autoPlay)
        {
            AnimationTransportService.Instance.Play();
        }
        else
        {
            _animPlayer.Play(playAnimName);
            _animPlayer.Pause();
            _animPlayer.Seek(0.0, update: true);
            _animPlayer.Advance(0.0001);
            _skeleton.ForceUpdateAllBoneTransforms();
            AnimationTransportService.Instance.Pause();
            AnimationTransportService.Instance.Seek(0.0, update: true, conformCloth: true);
        }

        NotifyPoseChanged(_activeAnimName);
        if (_animStudioDock != null && _skeleton != null && GodotObject.IsInstanceValid(_skeleton) && !_skeleton.IsQueuedForDeletion())
        {
            _animStudioDock.BindTarget(_skeleton, _animPlayer, playAnim, playAnimName, fps: (float)(info?.Fps ?? 30f));
        }
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
            if (godotAnim != null && _skeleton != null && GodotObject.IsInstanceValid(_skeleton) && !_skeleton.IsQueuedForDeletion())
            {
                DeadlockAnimLoader.StripClothTracks(godotAnim, _skeleton);
                if (DeadlockAnimLoader.ShouldStripPositionTracks(godotAnim, _skeleton, animName))
                {
                    DeadlockAnimLoader.StripAdditivePositionTracks(godotAnim, _skeleton);
                }
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
            ProceduralClothSolver.Conform(_skeleton, force: true, _animPlayer);
            NotifyPoseChanged(animName);
            _animStudioDock?.BindTarget(_skeleton, _animPlayer, godotAnim, animName, fps: 30f);
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
        if (string.IsNullOrEmpty(newText) && !string.IsNullOrEmpty(_activeAnimName))
        {
            HighlightTreeItem(_activeAnimName);
        }
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
        if (info == null || !info.IsCompatible) return;

        bool wasPlaying = _animPlayer != null && _animPlayer.IsPlaying();
        SelectAnimation(info, autoPlay: wasPlaying);
    }

    private void OnPlayPauseToggled(bool isPlaying)
    {
        if (_particleStage != null && _particleStage.HasActiveParticle)
        {
            if (isPlaying) _particleStage.Play(); else _particleStage.Pause();
            if (_btnPlayPause != null) _btnPlayPause.Icon = isPlaying ? _iconPause : _iconPlay;
            return;
        }

        if (isPlaying)
        {
            AnimationTransportService.Instance.Play();
        }
        else
        {
            AnimationTransportService.Instance.Pause();
        }
    }

    private void OnStopPressed()
    {
        if (_particleStage != null && _particleStage.HasActiveParticle)
        {
            _particleStage.Stop();
            _timelineSlider?.SetValueNoSignal(0.0);
            UpdateTimeLabel(0.0f, _particleStage.Duration);
            if (_btnPlayPause != null)
            {
                _btnPlayPause.SetPressedNoSignal(false);
                _btnPlayPause.Icon = _iconPlay;
            }
            return;
        }

        AnimationTransportService.Instance.Stop();
    }

    private void OnLoopToggled(bool looping)
    {
        _isLooping = looping;
        if (_particleStage != null && _particleStage.HasActiveParticle)
        {
            _particleStage.SetLooping(looping);
            return;
        }

        AnimationTransportService.Instance.SetLoop(looping);
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

        if (_particleStage != null && _particleStage.HasActiveParticle)
        {
            _particleStage.SetSpeed(speed);
            return;
        }

        if (_animPlayer != null)
        {
            _animPlayer.SpeedScale = speed;
        }
    }

    private void OnTimelineSliderValueChanged(double val)
    {
        if (_particleStage != null && _particleStage.HasActiveParticle)
        {
            _particleStage.Seek((float)val);
            UpdateTimeLabel((float)val, _particleStage.Duration);
            return;
        }

        AnimationTransportService.Instance.Seek(val, update: true, conformCloth: true);
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
                ProceduralClothSolver.Conform(_skeleton, force: true, _animPlayer);
                NotifyPoseChanged(_activeAnimName);
            }
        }
    }

    public void ResetAllPoses()
    {
        AnimationTransportService.Instance.Stop();

        if (_skeleton != null)
        {
            for (int i = 0; i < _skeleton.GetBoneCount(); i++)
            {
                _skeleton.ResetBonePose(i);
            }

            _skeleton.ForceUpdateAllBoneTransforms();
            ProceduralClothSolver.Conform(_skeleton, force: true, _animPlayer);
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

    private void OnTransportPlaybackStateChanged(bool isPlaying)
    {
        if (_btnPlayPause != null)
        {
            _btnPlayPause.SetPressedNoSignal(isPlaying);
            _btnPlayPause.Icon = isPlaying ? _iconPause : _iconPlay;
        }
    }

    private void OnTransportTimeChanged(double time)
    {
        if (!_isUserDraggingSlider)
        {
            _timelineSlider?.SetValueNoSignal(time);
            UpdateTimeLabel((float)time, _activeAnimLength);
        }
    }

    private void OnTransportLoopChanged(bool looping)
    {
        _isLooping = looping;
        _btnLoop?.SetPressedNoSignal(looping);
    }
}

[Obsolete("Use PlayerTabUI instead")]
public partial class PoseTabUI : PlayerTabUI
{
}
