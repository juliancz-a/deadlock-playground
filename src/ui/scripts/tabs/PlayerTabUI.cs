#pragma warning disable CS0618
using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using DeadlockPlayground.Materials;
using DeadlockPlayground.Tools;
using DeadlockPlayground.Particles;

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

    private Skeleton3D _skeleton;
    private AnimationPlayer _animPlayer;
    private VpkLoaderTest _vpkLoader;
    private ParticleStage3D _particleStage;
    private VBoxContainer _particleControlsGroup;
    private HSlider _sliderCharge;
    private Label _labelCharge;
    private Button _btnRelease;
    private CheckBox _chkAutoLoop;
    private CheckBox _chkIdleUnderlay;
    private bool _isIdleUnderlayEnabled = true;
    private DeadlockAnimLoader.AnimSequenceInfo _activeAnimInfo;

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
        ProcessPriority = 100;

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

        InitParticleControls();

        var animGroup = GetNodeOrNull<VBoxContainer>("AnimGroup");
        if (animGroup != null)
        {
            _chkIdleUnderlay = new CheckBox
            {
                Name = "ChkIdleUnderlay",
                Text = "Underlay Idle Stance",
                ButtonPressed = _isIdleUnderlayEnabled,
                TooltipText = "Underlays the hero's base combat idle stance for additive and reaction sequences so limbs and weapons remain naturally posed."
            };
            _chkIdleUnderlay.Toggled += OnIdleUnderlayToggled;
            animGroup.AddChild(_chkIdleUnderlay);
            animGroup.MoveChild(_chkIdleUnderlay, 1);
        }

        var stage = GetTree()?.Root?.FindChild("ParticleStage3D", true, false) as ParticleStage3D;
        if (stage != null)
        {
            SetParticleStage(stage);
        }
    }

    public override void _Notification(int what)
    {
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

            if (_skeleton != null)
            {
                ProceduralClothSolver.MarkDirty();
                ProceduralClothSolver.Conform(_skeleton, force: true, _animPlayer);
            }
        }
    }

    public void SetSkeleton(Skeleton3D skeleton)
    {
        _skeleton = skeleton;
    }

    public void SetAnimationPlayer(AnimationPlayer animPlayer)
    {
        if (_animPlayer != null)
        {
            _animPlayer.AnimationFinished -= OnAnimationFinished;
        }

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

        _animPlayer.AnimationFinished += OnAnimationFinished;

        PopulateAnimationTreeFromPlayer();
        AutoSelectDefaultIdle();
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

    private void PopulateAnimationTreeFromPlayer()
    {
        if (_animPlayer == null) return;

        _currentHeroAnimations.Clear();
        var animList = _animPlayer.GetAnimationList();

        foreach (var animName in animList)
        {
            var anim = _animPlayer.GetAnimation(animName);
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
                IsLooping = isLooping,
                IsAdditive = isAdditive
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
            "Additive Layers",
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
            Color catColor = category switch
            {
                "Additive Layers" => new Color(0.6f, 0.85f, 1.0f, 1.0f),
                "Idle" => new Color(0.95f, 0.85f, 0.6f, 1.0f),
                _ => new Color(0.9f, 0.9f, 0.9f, 1.0f)
            };
            categoryItem.SetCustomColor(0, catColor);
            categoryItem.SetCustomMinimumHeight(28);
            categoryItem.SetText(0, $"{category} ({itemsInCategory.Count})");
            categoryItem.SetSelectable(0, false);
            categoryItem.Collapsed = (category == "Additive Layers");

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

                if (anim.RawName == _activeAnimName || anim.CleanName == _activeAnimName ||
                    _activeAnimName == $"{anim.CleanName}_underlaid" || _activeAnimName == $"{anim.RawName}_underlaid")
                {
                    item.Select(0);
                }
            }
        }
    }

    private void OnIdleUnderlayToggled(bool toggled)
    {
        _isIdleUnderlayEnabled = toggled;

        if (_activeAnimInfo != null && _animPlayer != null)
        {
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
            a.Category == "Idle" &&
            !IsExcluded(a.CleanName) &&
            (a.CleanName.Contains("stand_idle", StringComparison.OrdinalIgnoreCase) ||
             a.CleanName.Contains("idle", StringComparison.OrdinalIgnoreCase)));
        if (idleMatch != null) return idleMatch;

        // Tier 4: Any non-additive, non-excluded Idle
        var anyIdle = _currentHeroAnimations.FirstOrDefault(a =>
            a.Category == "Idle" && !IsExcluded(a.CleanName));
        if (anyIdle != null) return anyIdle;

        // Tier 5: Fallback to any non-additive animation
        var fallback = _currentHeroAnimations.FirstOrDefault(a =>
            a.Category != "Additive Layers" && !a.IsAdditive && !IsExcluded(a.CleanName));
        return fallback ?? _currentHeroAnimations.FirstOrDefault();
    }

    private Godot.Animation GetOrDecodeAnimation(DeadlockAnimLoader.AnimSequenceInfo info)
    {
        if (info == null || _animPlayer == null) return null;

        if (_animPlayer.HasAnimation(info.RawName))
        {
            return _animPlayer.GetAnimation(info.RawName);
        }
        if (_animPlayer.HasAnimation(info.CleanName))
        {
            return _animPlayer.GetAnimation(info.CleanName);
        }

        FindVpkLoader();
        if (_vpkLoader != null && _skeleton != null)
        {
            var decoded = _vpkLoader.DecodeAnimation(info, _skeleton, _animPlayer);
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

        _activeAnimInfo = info;

        var rawAnim = GetOrDecodeAnimation(info);
        bool isAdditive = DeadlockAnimLoader.IsAdditiveAnimation(rawAnim, info, _skeleton);
        if (info != null && info.Category == "Idle")
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
                _vpkLoader.DecodeAnimation(info, _skeleton, _animPlayer, idleInfo, targetAnim);
            }
        }

        if (!_animPlayer.HasAnimation(targetAnim))
        {
            if (_animPlayer.HasAnimation(info.CleanName)) targetAnim = info.CleanName;
            else if (_animPlayer.HasAnimation(info.RawName)) targetAnim = info.RawName;
            else return;
        }

        var godotAnim = _animPlayer.GetAnimation(targetAnim);
        if (godotAnim == null) return;

        // Configure animation playback parameters
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
        ProceduralClothSolver.Conform(_skeleton, force: true, _animPlayer);
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
        if (_particleStage != null && _particleStage.HasActiveParticle)
        {
            if (isPlaying) _particleStage.Play(); else _particleStage.Pause();
            if (_btnPlayPause != null) _btnPlayPause.Icon = isPlaying ? _iconPause : _iconPlay;
            return;
        }

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
                ProceduralClothSolver.Conform(_skeleton, force: true, _animPlayer);
            }
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
                _btnPlayPause.ButtonPressed = false;
                _btnPlayPause.Icon = _iconPlay;
            }
            return;
        }

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
            ProceduralClothSolver.Conform(_skeleton, force: true, _animPlayer);
        }
    }

    private void OnLoopToggled(bool looping)
    {
        _isLooping = looping;
        if (_particleStage != null && _particleStage.HasActiveParticle)
        {
            _particleStage.SetLooping(looping);
            return;
        }

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

        if (_animPlayer == null || string.IsNullOrEmpty(_activeAnimName)) return;

        // Immediate seek to evaluate bone matrices in real time
        _animPlayer.Seek(val, update: true);
        UpdateTimeLabel((float)val, _activeAnimLength);

        if (_skeleton != null)
        {
            _skeleton.ForceUpdateAllBoneTransforms();
            ProceduralClothSolver.MarkDirty();
            ProceduralClothSolver.Conform(_skeleton, force: true, _animPlayer);
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
                ProceduralClothSolver.Conform(_skeleton, force: true, _animPlayer);
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
}

[Obsolete("Use PlayerTabUI instead")]
public partial class PoseTabUI : PlayerTabUI
{
}
