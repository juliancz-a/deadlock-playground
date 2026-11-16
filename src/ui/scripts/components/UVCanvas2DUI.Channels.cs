using System;
using System.Collections.Generic;
using Godot;
using DeadlockPlayground.Materials;
using DeadlockPlayground.Painter;

namespace DeadlockPlayground.UI
{
    public partial class UVCanvas2DUI : PanelContainer
    {
        private PanelContainer _bottomChannelBar = null;
        private HFlowContainer _channelPillsFlow = null;
        private HFlowContainer _channelActionsFlow = null;
        private Button _btnSoloChannel = null;
        private Button _btnImportChannelMap = null;
        private Button _btnResetChannelMap = null;
        private OptionButton _optColorVariant = null;
        private OptionButton _optExtraChannels = null;
        private FileDialog _channelImportDialog = null;
        private bool _subscribedChannelEvents = false;

        private readonly List<MaterialTextureChannel> _extraChannelMapIds = new();

        private void SetupBottomChannelBar()
        {
            _bottomChannelBar = GetNodeOrNull<PanelContainer>("VBoxContainer/BottomChannelBar")
                             ?? FindChild("BottomChannelBar", true, false) as PanelContainer;
            _channelPillsFlow = GetNodeOrNull<HFlowContainer>("VBoxContainer/BottomChannelBar/Margin/ChannelBarVBox/ChannelPillsFlow")
                             ?? FindChild("ChannelPillsFlow", true, false) as HFlowContainer;
            _channelActionsFlow = GetNodeOrNull<HFlowContainer>("VBoxContainer/BottomChannelBar/Margin/ChannelBarVBox/ChannelActionsFlow")
                               ?? FindChild("ChannelActionsFlow", true, false) as HFlowContainer;

            if (_bottomChannelBar != null)
            {
                _bottomChannelBar.CustomMinimumSize = new Vector2(0, 56);
            }

            RefreshChannelBarFromLayerManager();
        }

        private void EnsureChannelEventsSubscribed()
        {
            if (_subscribedChannelEvents) return;
            _layerManager ??= SkinLayerManager.Instance;
            if (_layerManager == null) return;

            _subscribedChannelEvents = true;
            _layerManager.ActiveChannelChanged += (_) =>
            {
                RefreshChannelBarFromLayerManager();
                UpdateResolutionLabel();
                QueueCanvasRedraw();
            };
            _layerManager.ChannelSetUpdated += (_) =>
            {
                RefreshChannelBarFromLayerManager();
                QueueCanvasRedraw();
            };
        }

        public void RefreshChannelBarFromLayerManager()
        {
            EnsureChannelEventsSubscribed();
            if (_channelPillsFlow == null || _channelActionsFlow == null) return;

            // Don't display material channels during particle standalone editing
            bool isParticle = !string.IsNullOrEmpty(_activeParticleTexturePath);
            if (_bottomChannelBar != null)
            {
                _bottomChannelBar.Visible = !isParticle;
            }
            if (isParticle) return;

            foreach (Node child in _channelPillsFlow.GetChildren()) child.QueueFree();
            foreach (Node child in _channelActionsFlow.GetChildren()) child.QueueFree();

            var lm = _layerManager ?? SkinLayerManager.Instance;
            var chSet = lm?.GetActiveChannelSet();
            var activeChannel = lm?.ActiveTextureChannel ?? MaterialTextureChannel.Color;

            MaterialTextureChannel[] coreChannels =
            {
                MaterialTextureChannel.Color,
                MaterialTextureChannel.AmbientOcclusion,
                MaterialTextureChannel.Normal,
                MaterialTextureChannel.Roughness,
                MaterialTextureChannel.Shadow
            };

            var pillGroup = new ButtonGroup { AllowUnpress = false };
            foreach (var ch in coreChannels)
            {
                var entry = chSet?.GetChannel(ch);
                bool isReal = entry != null && entry.IsPresent && !entry.IsDefaultFallback;
                bool isDirty = entry != null && entry.IsDirty;
                bool isSelected = ch == activeChannel;

                // Hide non-Color channel buttons (AO, Normal, Roughness, Shadow) when this material has no map for them
                if (ch != MaterialTextureChannel.Color && !isReal && !isDirty && !isSelected)
                {
                    continue;
                }

                bool isEnabled = entry?.IsEnabled ?? true;

                var pillBox = new HBoxContainer();
                pillBox.AddThemeConstantOverride("separation", 2);

                var eyeCheck = new CheckBox
                {
                    ButtonPressed = isEnabled,
                    TooltipText = $"Toggle {MaterialChannelSet.GetDisplayLabel(ch)} on 3D shader",
                    CustomMinimumSize = new Vector2(20, 22),
                    Disabled = chSet == null
                };
                var localCh = ch;
                eyeCheck.Toggled += (pressed) =>
                {
                    (_layerManager ?? SkinLayerManager.Instance)?.SetChannelEnabled(localCh, pressed);
                };

                string shortName = ch switch
                {
                    MaterialTextureChannel.Color => "COLOR",
                    MaterialTextureChannel.AmbientOcclusion => "AO",
                    MaterialTextureChannel.Normal => "NORMAL",
                    MaterialTextureChannel.Roughness => "ROUGH",
                    MaterialTextureChannel.Shadow => "SHADOW",
                    _ => ch.ToString().ToUpperInvariant()
                };
                string suffix = (entry != null && entry.IsDefaultFallback && !isDirty) ? " (Def)" : "";

                var pillBtn = new Button
                {
                    Text = shortName + suffix,
                    ToggleMode = true,
                    ButtonGroup = pillGroup,
                    ButtonPressed = isSelected,
                    CustomMinimumSize = new Vector2(0, 22),
                    Disabled = chSet == null || isSelected,
                    TooltipText = isSelected
                        ? $"{MaterialChannelSet.GetDisplayLabel(ch)} (Currently Active)"
                        : (entry != null
                            ? $"{MaterialChannelSet.GetDisplayLabel(ch)}: {entry.SourcePath}\nClick to inspect & edit in 2D UV Canvas"
                            : $"{MaterialChannelSet.GetDisplayLabel(ch)}")
                };
                pillBtn.AddThemeFontSizeOverride("font_size", 11);

                if (isSelected)
                {
                    var gold = new Color(1.0f, 0.88f, 0.48f, 1.0f);
                    pillBtn.Modulate = gold;
                    pillBtn.AddThemeColorOverride("font_disabled_color", gold);
                }
                else if (isReal)
                {
                    pillBtn.Modulate = new Color(0.85f, 0.94f, 1.0f, 0.95f);
                }
                else
                {
                    pillBtn.Modulate = new Color(0.68f, 0.72f, 0.78f, 0.75f);
                }

                pillBtn.Pressed += () =>
                {
                    var targetLm = _layerManager ?? SkinLayerManager.Instance;
                    if (targetLm == null || targetLm.ActiveTextureChannel == localCh) return;
                    targetLm.SetActiveTextureChannel(localCh);
                };

                pillBox.AddChild(eyeCheck);
                pillBox.AddChild(pillBtn);
                _channelPillsFlow.AddChild(pillBox);
            }

            // Masks / Extra Channels dropdown when extra maps exist
            _extraChannelMapIds.Clear();
            MaterialTextureChannel[] extras =
            {
                MaterialTextureChannel.SelfIllum,
                MaterialTextureChannel.Metalness,
                MaterialTextureChannel.TintMask,
                MaterialTextureChannel.RimMask,
                MaterialTextureChannel.OutlineMask
            };
            if (chSet != null)
            {
                foreach (var ex in extras)
                {
                    if (chSet.Channels.TryGetValue(ex, out var exEntry) && exEntry != null &&
                        ((exEntry.IsPresent && !exEntry.IsDefaultFallback) || exEntry.IsDirty || ex == activeChannel))
                    {
                        _extraChannelMapIds.Add(ex);
                    }
                }
            }

            if (_extraChannelMapIds.Count > 0)
            {
                _optExtraChannels = new OptionButton
                {
                    CustomMinimumSize = new Vector2(90, 22),
                    TooltipText = "Inspect or edit extra mask maps (Self-Illum, Metalness, Tint, Rim, Outline)"
                };
                _optExtraChannels.AddThemeFontSizeOverride("font_size", 11);
                _optExtraChannels.AddItem("MASKS...", -1);
                int selExtraIdx = 0;
                for (int i = 0; i < _extraChannelMapIds.Count; i++)
                {
                    var ex = _extraChannelMapIds[i];
                    _optExtraChannels.AddItem(MaterialChannelSet.GetDisplayLabel(ex), (int)ex);
                    if (ex == activeChannel) selExtraIdx = i + 1;
                }
                _optExtraChannels.Select(selExtraIdx);
                _optExtraChannels.ItemSelected += (idx) =>
                {
                    int id = _optExtraChannels.GetItemId((int)idx);
                    if (id >= 0)
                    {
                        var targetLm = _layerManager ?? SkinLayerManager.Instance;
                        if (targetLm == null || targetLm.ActiveTextureChannel == (MaterialTextureChannel)id) return;
                        targetLm.SetActiveTextureChannel((MaterialTextureChannel)id);
                    }
                };
                _channelPillsFlow.AddChild(_optExtraChannels);
            }

            // Action row: Color Variant selector, Solo 3D, Import Map..., Reset Channel
            var colorEntry = chSet?.GetChannel(MaterialTextureChannel.Color);
            if (colorEntry != null && colorEntry.VariantPaths.Count > 1)
            {
                _optColorVariant = new OptionButton
                {
                    CustomMinimumSize = new Vector2(105, 22),
                    TooltipText = "Switch base Color variant discovered in hero VPK folder"
                };
                _optColorVariant.AddThemeFontSizeOverride("font_size", 11);
                for (int v = 0; v < colorEntry.VariantPaths.Count; v++)
                {
                    string lbl = v < colorEntry.VariantLabels.Count ? colorEntry.VariantLabels[v] : $"Color {v + 1}";
                    _optColorVariant.AddItem(lbl, v);
                }
                _optColorVariant.Select(Mathf.Clamp(colorEntry.ActiveVariantIndex, 0, colorEntry.VariantPaths.Count - 1));
                _optColorVariant.ItemSelected += (idx) =>
                {
                    if ((int)idx == colorEntry.ActiveVariantIndex) return;
                    (_layerManager ?? SkinLayerManager.Instance)?.SelectColorVariant((int)idx);
                };
                _channelActionsFlow.AddChild(_optColorVariant);
            }

            bool isSolo = chSet?.SoloPreviewChannel != null;
            _btnSoloChannel = new Button
            {
                Text = isSolo ? "Solo 3D: ON" : "Solo 3D",
                ToggleMode = true,
                ButtonPressed = isSolo,
                CustomMinimumSize = new Vector2(72, 22),
                Disabled = chSet == null,
                TooltipText = "Preview ONLY the selected channel unlit on the 3D hero model"
            };
            _btnSoloChannel.AddThemeFontSizeOverride("font_size", 11);
            if (isSolo) _btnSoloChannel.Modulate = new Color(1.0f, 0.85f, 0.40f, 1.0f);
            _btnSoloChannel.Toggled += (pressed) =>
            {
                (_layerManager ?? SkinLayerManager.Instance)?.SetSoloChannelPreview(pressed);
            };
            _channelActionsFlow.AddChild(_btnSoloChannel);

            _btnImportChannelMap = new Button
            {
                Text = "Import Map...",
                CustomMinimumSize = new Vector2(84, 22),
                Disabled = chSet == null,
                TooltipText = $"Import an external .png/.tga/.jpg into the active {MaterialChannelSet.GetDisplayLabel(activeChannel)} channel"
            };
            _btnImportChannelMap.AddThemeFontSizeOverride("font_size", 11);
            _btnImportChannelMap.Pressed += OnImportChannelMapPressed;
            _channelActionsFlow.AddChild(_btnImportChannelMap);

            _btnResetChannelMap = new Button
            {
                Text = "Reset",
                CustomMinimumSize = new Vector2(48, 22),
                Disabled = chSet == null,
                TooltipText = $"Reset the active {MaterialChannelSet.GetDisplayLabel(activeChannel)} channel back to original"
            };
            _btnResetChannelMap.AddThemeFontSizeOverride("font_size", 11);
            _btnResetChannelMap.Pressed += () =>
            {
                (_layerManager ?? SkinLayerManager.Instance)?.ResetActiveChannel();
            };
            _channelActionsFlow.AddChild(_btnResetChannelMap);
        }

        private void OnImportChannelMapPressed()
        {
            if (_channelImportDialog == null)
            {
                _channelImportDialog = new FileDialog
                {
                    FileMode = FileDialog.FileModeEnum.OpenFile,
                    Access = FileDialog.AccessEnum.Filesystem,
                    UseNativeDialog = true,
                    Title = "Import External Texture to Active Channel",
                    Filters = new[] { "*.png, *.tga, *.jpg, *.jpeg, *.webp ; Image Files" }
                };
                _channelImportDialog.FileSelected += (path) =>
                {
                    var img = new Image();
                    if (img.Load(path) == Error.Ok)
                    {
                        (_layerManager ?? SkinLayerManager.Instance)?.ImportExternalImageToActiveChannel(img, path);
                        UpdateResolutionLabel();
                        QueueCanvasRedraw();
                    }
                    else
                    {
                        GD.PrintErr($"[UVCanvas2DUI] Failed to load external channel map: {path}");
                    }
                };
                AddChild(_channelImportDialog);
            }
            _channelImportDialog.PopupCentered(new Vector2I(820, 540));
        }
    }
}

