using System;
using System.Collections.Generic;
using Godot;
using DeadlockPlayground.Animation;
using DeadlockPlayground.Tools;

namespace DeadlockPlayground.UI
{
    /// <summary>
    /// Blender 5.2-style F-Curve Modifiers Sidebar Panel.
    /// Non-destructive modifier stack management for the active F-Curve channel.
    /// </summary>
    public partial class GraphModifiersPanelUI : PanelContainer
    {
        [Signal] public delegate void ModifiersChangedEventHandler();
        [Signal] public delegate void PanelCloseRequestedEventHandler();

        private GraphEditorCanvas _canvas;
        private GraphEditorCanvas.ChannelCurve _activeCurve;
        private float _fps = 30f;

        // UI Nodes
        private Label _lblTargetChannel;
        private MenuButton _btnAddModifier;
        private MenuButton _btnDeleteModifiers;
        private Button _btnClose;
        private VBoxContainer _modifiersListVBox;
        private ScrollContainer _scrollContainer;

        public GraphEditorCanvas.ChannelCurve ActiveCurve => _activeCurve;

        public override void _Ready()
        {
            CustomMinimumSize = new Vector2(290, 0);
            SizeFlagsVertical = SizeFlags.ExpandFill;
            BuildUI();
        }

        public void BindCanvas(GraphEditorCanvas canvas)
        {
            _canvas = canvas;
            if (_canvas != null)
            {
                _canvas.ActiveCurveChanged += (name) => BindCurve(_canvas.ActiveCurve);
                _canvas.ModifiersModified += RefreshModifierList;
            }
        }

        public void BindCurve(GraphEditorCanvas.ChannelCurve curve, float fps = 30f)
        {
            _activeCurve = curve;
            _fps = fps > 0 ? fps : 30f;
            UpdateHeader();
            RefreshModifierList();
        }

        private void BuildUI()
        {
            // Panel styling
            var style = new StyleBoxFlat
            {
                BgColor = new Color(0.12f, 0.13f, 0.16f, 0.98f),
                BorderWidthLeft = 1,
                BorderColor = new Color(0.22f, 0.25f, 0.30f, 0.8f),
                ContentMarginLeft = 8,
                ContentMarginTop = 6,
                ContentMarginRight = 8,
                ContentMarginBottom = 6
            };
            AddThemeStyleboxOverride("panel", style);

            var rootVBox = new VBoxContainer
            {
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                SizeFlagsVertical = SizeFlags.ExpandFill
            };
            rootVBox.AddThemeConstantOverride("separation", 6);
            AddChild(rootVBox);

            // 1. Header Row
            var headerHBox = new HBoxContainer
            {
                CustomMinimumSize = new Vector2(0, 26),
                SizeFlagsHorizontal = SizeFlags.ExpandFill
            };
            headerHBox.AddThemeConstantOverride("separation", 6);
            rootVBox.AddChild(headerHBox);

            var iconLayers = new TextureRect
            {
                CustomMinimumSize = new Vector2(16, 16),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                Texture = DockIcons.Load("layers"),
                SizeFlagsVertical = SizeFlags.ShrinkCenter
            };
            headerHBox.AddChild(iconLayers);

            _lblTargetChannel = new Label
            {
                Text = "F-Curve Modifiers",
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis
            };
            _lblTargetChannel.AddThemeFontSizeOverride("font_size", 12);
            headerHBox.AddChild(_lblTargetChannel);

            _btnClose = new Button
            {
                Flat = true,
                CustomMinimumSize = new Vector2(22, 22),
                TooltipText = "Close Modifiers Sidebar (N)"
            };
            DockIcons.Apply(_btnClose, "cross", "Close Modifiers Sidebar (N)");
            _btnClose.Pressed += () => EmitSignal(SignalName.PanelCloseRequested);
            headerHBox.AddChild(_btnClose);

            // 2. Action Bar: Add Modifier & Delete Modifiers
            var actionsHBox = new HBoxContainer
            {
                CustomMinimumSize = new Vector2(0, 28),
                SizeFlagsHorizontal = SizeFlags.ExpandFill
            };
            actionsHBox.AddThemeConstantOverride("separation", 6);
            rootVBox.AddChild(actionsHBox);

            _btnAddModifier = new MenuButton
            {
                Text = "Add Modifier ▼",
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                Flat = false
            };
            _btnAddModifier.AddThemeFontSizeOverride("font_size", 12);
            SetupAddModifierMenu();
            actionsHBox.AddChild(_btnAddModifier);

            _btnDeleteModifiers = new MenuButton
            {
                Text = "Delete ▼",
                SizeFlagsHorizontal = SizeFlags.ShrinkEnd,
                Flat = false
            };
            _btnDeleteModifiers.AddThemeFontSizeOverride("font_size", 12);
            SetupDeleteModifiersMenu();
            actionsHBox.AddChild(_btnDeleteModifiers);

            // Separator
            rootVBox.AddChild(new HSeparator());

            // 3. ScrollContainer for Modifier Cards
            _scrollContainer = new ScrollContainer
            {
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                SizeFlagsVertical = SizeFlags.ExpandFill,
                HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
            };
            rootVBox.AddChild(_scrollContainer);

            _modifiersListVBox = new VBoxContainer
            {
                SizeFlagsHorizontal = SizeFlags.ExpandFill
            };
            _modifiersListVBox.AddThemeConstantOverride("separation", 8);
            _scrollContainer.AddChild(_modifiersListVBox);

            UpdateHeader();
            RefreshModifierList();
        }

        private void SetupAddModifierMenu()
        {
            var popup = _btnAddModifier.GetPopup();
            popup.Clear();
            popup.AddItem("Generator", 0);
            popup.AddItem("Built-in Function", 1);
            popup.AddItem("Envelope", 2);
            popup.AddItem("Cycles", 3);
            popup.AddItem("Noise", 4);
            popup.AddItem("Limits", 5);
            popup.AddItem("Stepped Interpolation", 6);
            popup.AddItem("Smooth (Gaussian)", 7);

            popup.IdPressed += (id) =>
            {
                if (_activeCurve == null || _canvas == null) return;
                FCurveModifier mod = id switch
                {
                    0 => new GeneratorModifier(),
                    1 => new BuiltInFunctionModifier(),
                    2 => new EnvelopeModifier(),
                    3 => new CyclesModifier(),
                    4 => new NoiseModifier(),
                    5 => new LimitsModifier(),
                    6 => new SteppedModifier(),
                    7 => new GaussianSmoothModifier(),
                    _ => null
                };

                if (mod != null)
                {
                    bool ok = _canvas.AddModifier(_activeCurve, mod);
                    if (ok)
                    {
                        RefreshModifierList();
                        EmitSignal(SignalName.ModifiersChanged);
                    }
                }
            };
        }

        private void SetupDeleteModifiersMenu()
        {
            var popup = _btnDeleteModifiers.GetPopup();
            popup.Clear();
            popup.AddItem("Remove All Modifiers", 0);
            popup.AddItem("Remove First Modifier", 1);
            popup.AddSeparator();

            var subMenu = new PopupMenu { Name = "SubMenuRemoveType" };
            subMenu.AddItem("Generator", 10);
            subMenu.AddItem("Built-in Function", 11);
            subMenu.AddItem("Envelope", 12);
            subMenu.AddItem("Cycles", 13);
            subMenu.AddItem("Noise", 14);
            subMenu.AddItem("Limits", 15);
            subMenu.AddItem("Stepped", 16);
            subMenu.AddItem("Smooth (Gaussian)", 17);

            subMenu.IdPressed += (typeId) =>
            {
                if (_activeCurve == null || _canvas == null) return;
                string typeName = typeId switch
                {
                    10 => "Generator",
                    11 => "Built-in Function",
                    12 => "Envelope",
                    13 => "Cycles",
                    14 => "Noise",
                    15 => "Limits",
                    16 => "Stepped",
                    17 => "Smooth (Gaussian)",
                    _ => ""
                };
                _canvas.RemoveModifiers(_activeCurve, ModifierRemoveMode.RemoveType, typeName);
                RefreshModifierList();
                EmitSignal(SignalName.ModifiersChanged);
            };

            popup.AddChild(subMenu);
            popup.AddSubmenuNodeItem("Remove Type...", subMenu, 2);

            popup.IdPressed += (id) =>
            {
                if (_activeCurve == null || _canvas == null) return;
                if (id == 0)
                {
                    _canvas.RemoveModifiers(_activeCurve, ModifierRemoveMode.RemoveAll);
                    RefreshModifierList();
                    EmitSignal(SignalName.ModifiersChanged);
                }
                else if (id == 1)
                {
                    _canvas.RemoveModifiers(_activeCurve, ModifierRemoveMode.RemoveFirst);
                    RefreshModifierList();
                    EmitSignal(SignalName.ModifiersChanged);
                }
            };
        }

        private void UpdateHeader()
        {
            if (_lblTargetChannel == null) return;
            if (_activeCurve != null)
            {
                _lblTargetChannel.Text = $"Modifiers: {_activeCurve.Name}";
                _btnAddModifier.Disabled = false;
                _btnDeleteModifiers.Disabled = false;
            }
            else
            {
                _lblTargetChannel.Text = "No Channel Selected";
                _btnAddModifier.Disabled = true;
                _btnDeleteModifiers.Disabled = true;
            }
        }

        public void RefreshModifierList()
        {
            if (_modifiersListVBox == null) return;

            foreach (var child in _modifiersListVBox.GetChildren())
            {
                child.QueueFree();
            }

            if (_activeCurve == null)
            {
                var lblEmpty = new Label
                {
                    Text = "Select an F-Curve channel on the left to inspect and edit modifiers.",
                    AutowrapMode = TextServer.AutowrapMode.Word,
                    Modulate = new Color(0.7f, 0.7f, 0.7f, 0.8f)
                };
                lblEmpty.AddThemeFontSizeOverride("font_size", 11);
                _modifiersListVBox.AddChild(lblEmpty);
                return;
            }

            if (_activeCurve.Modifiers.Count == 0)
            {
                var lblNone = new Label
                {
                    Text = "No modifiers on this F-Curve.\nClick 'Add Modifier' to add non-destructive curve effects.",
                    AutowrapMode = TextServer.AutowrapMode.Word,
                    Modulate = new Color(0.6f, 0.65f, 0.7f, 0.8f)
                };
                lblNone.AddThemeFontSizeOverride("font_size", 11);
                _modifiersListVBox.AddChild(lblNone);
                return;
            }

            for (int i = 0; i < _activeCurve.Modifiers.Count; i++)
            {
                var card = CreateModifierCard(_activeCurve.Modifiers[i], i);
                _modifiersListVBox.AddChild(card);
            }
        }

        private Control CreateModifierCard(FCurveModifier mod, int index)
        {
            var card = new PanelContainer
            {
                SizeFlagsHorizontal = SizeFlags.ExpandFill
            };
            var cardStyle = new StyleBoxFlat
            {
                BgColor = new Color(0.16f, 0.17f, 0.20f, 0.95f),
                BorderWidthLeft = 1,
                BorderWidthTop = 1,
                BorderWidthRight = 1,
                BorderWidthBottom = 1,
                BorderColor = mod.IsMuted ? new Color(0.3f, 0.3f, 0.3f, 0.5f) : new Color(0.35f, 0.45f, 0.60f, 0.8f),
                CornerRadiusBottomLeft = 4,
                CornerRadiusBottomRight = 4,
                CornerRadiusTopLeft = 4,
                CornerRadiusTopRight = 4,
                ContentMarginLeft = 6,
                ContentMarginTop = 6,
                ContentMarginRight = 6,
                ContentMarginBottom = 6
            };
            card.AddThemeStyleboxOverride("panel", cardStyle);

            var vbox = new VBoxContainer
            {
                SizeFlagsHorizontal = SizeFlags.ExpandFill
            };
            vbox.AddThemeConstantOverride("separation", 6);
            card.AddChild(vbox);

            // 1. Header row
            var head = new HBoxContainer
            {
                SizeFlagsHorizontal = SizeFlags.ExpandFill
            };
            head.AddThemeConstantOverride("separation", 4);
            vbox.AddChild(head);

            // Reorder Up
            var btnUp = new Button
            {
                Text = "▲",
                CustomMinimumSize = new Vector2(20, 20),
                Disabled = index == 0 || mod.RequiresFirst
            };
            btnUp.AddThemeFontSizeOverride("font_size", 10);
            btnUp.Pressed += () =>
            {
                _canvas?.MoveModifier(_activeCurve, index, -1);
                RefreshModifierList();
            };
            head.AddChild(btnUp);

            // Reorder Down
            var btnDown = new Button
            {
                Text = "▼",
                CustomMinimumSize = new Vector2(20, 20),
                Disabled = index == _activeCurve.Modifiers.Count - 1 || mod.RequiresFirst
            };
            btnDown.AddThemeFontSizeOverride("font_size", 10);
            btnDown.Pressed += () =>
            {
                _canvas?.MoveModifier(_activeCurve, index, 1);
                RefreshModifierList();
            };
            head.AddChild(btnDown);

            // Mute Checkbox
            var chkActive = new CheckBox
            {
                ButtonPressed = !mod.IsMuted,
                TooltipText = "Toggle Modifier Active / Mute"
            };
            chkActive.Toggled += (on) =>
            {
                mod.IsMuted = !on;
                _canvas?.NotifyModifiersChanged();
                RefreshModifierList();
            };
            head.AddChild(chkActive);

            // Editable Name LineEdit
            var editName = new LineEdit
            {
                Text = string.IsNullOrEmpty(mod.Name) ? mod.TypeName : mod.Name,
                SizeFlagsHorizontal = SizeFlags.ExpandFill
            };
            editName.AddThemeFontSizeOverride("font_size", 11);
            editName.TextChanged += (txt) =>
            {
                mod.Name = txt;
            };
            head.AddChild(editName);

            // Delete Button [X]
            var btnDelete = new Button
            {
                Text = "✕",
                CustomMinimumSize = new Vector2(22, 20),
                TooltipText = "Delete Modifier"
            };
            btnDelete.AddThemeColorOverride("font_color", new Color(1f, 0.4f, 0.4f));
            btnDelete.Pressed += () =>
            {
                _canvas?.RemoveModifier(_activeCurve, mod);
                RefreshModifierList();
            };
            head.AddChild(btnDelete);

            // If muted, collapse details visually
            if (mod.IsMuted)
            {
                vbox.Modulate = new Color(0.6f, 0.6f, 0.6f, 0.7f);
                return card;
            }

            // 2. Common Properties: Influence Slider
            var infRow = new HBoxContainer
            {
                SizeFlagsHorizontal = SizeFlags.ExpandFill
            };
            infRow.AddThemeConstantOverride("separation", 6);
            vbox.AddChild(infRow);

            var lblInf = new Label { Text = "Influence:", CustomMinimumSize = new Vector2(70, 0) };
            lblInf.AddThemeFontSizeOverride("font_size", 11);
            infRow.AddChild(lblInf);

            var sliderInf = new HSlider
            {
                MinValue = 0.0,
                MaxValue = 1.0,
                Step = 0.01,
                Value = mod.Influence,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                SizeFlagsVertical = SizeFlags.ShrinkCenter
            };
            var spinInf = new SpinBox
            {
                MinValue = 0.0,
                MaxValue = 1.0,
                Step = 0.01,
                Value = mod.Influence,
                CustomMinimumSize = new Vector2(60, 20)
            };
            sliderInf.ValueChanged += (v) =>
            {
                mod.Influence = (float)v;
                spinInf.SetValueNoSignal(v);
                _canvas?.NotifyModifiersChanged();
            };
            spinInf.ValueChanged += (v) =>
            {
                mod.Influence = (float)v;
                sliderInf.SetValueNoSignal(v);
                _canvas?.NotifyModifiersChanged();
            };
            infRow.AddChild(sliderInf);
            infRow.AddChild(spinInf);

            // 3. Common Properties: Restrict Frame Range
            var chkRange = new CheckBox
            {
                Text = "Restrict Frame Range",
                ButtonPressed = mod.RestrictFrameRange
            };
            chkRange.AddThemeFontSizeOverride("font_size", 11);
            vbox.AddChild(chkRange);

            var rangeContainer = new VBoxContainer
            {
                Visible = mod.RestrictFrameRange
            };
            rangeContainer.AddThemeConstantOverride("separation", 4);
            vbox.AddChild(rangeContainer);

            chkRange.Toggled += (on) =>
            {
                mod.RestrictFrameRange = on;
                rangeContainer.Visible = on;
                _canvas?.NotifyModifiersChanged();
            };

            var framesRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            framesRow.AddThemeConstantOverride("separation", 6);
            rangeContainer.AddChild(framesRow);

            framesRow.AddChild(new Label { Text = "Start:" });
            var spinStart = new SpinBox { MinValue = -9999, MaxValue = 9999, Value = mod.StartFrame, SizeFlagsHorizontal = SizeFlags.ExpandFill };
            spinStart.ValueChanged += (v) => { mod.StartFrame = (float)v; _canvas?.NotifyModifiersChanged(); };
            framesRow.AddChild(spinStart);

            framesRow.AddChild(new Label { Text = "End:" });
            var spinEnd = new SpinBox { MinValue = -9999, MaxValue = 9999, Value = mod.EndFrame, SizeFlagsHorizontal = SizeFlags.ExpandFill };
            spinEnd.ValueChanged += (v) => { mod.EndFrame = (float)v; _canvas?.NotifyModifiersChanged(); };
            framesRow.AddChild(spinEnd);

            var blendRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            blendRow.AddThemeConstantOverride("separation", 6);
            rangeContainer.AddChild(blendRow);

            blendRow.AddChild(new Label { Text = "Blend In:" });
            var spinBlendIn = new SpinBox { MinValue = 0, MaxValue = 999, Value = mod.BlendIn, SizeFlagsHorizontal = SizeFlags.ExpandFill };
            spinBlendIn.ValueChanged += (v) => { mod.BlendIn = (float)v; _canvas?.NotifyModifiersChanged(); };
            blendRow.AddChild(spinBlendIn);

            blendRow.AddChild(new Label { Text = "Out:" });
            var spinBlendOut = new SpinBox { MinValue = 0, MaxValue = 999, Value = mod.BlendOut, SizeFlagsHorizontal = SizeFlags.ExpandFill };
            spinBlendOut.ValueChanged += (v) => { mod.BlendOut = (float)v; _canvas?.NotifyModifiersChanged(); };
            blendRow.AddChild(spinBlendOut);

            // Separator before modifier specific controls
            vbox.AddChild(new HSeparator());

            // 4. Modifier Specific Controls
            BuildSpecificModifierControls(vbox, mod);

            return card;
        }

        private void BuildSpecificModifierControls(VBoxContainer container, FCurveModifier mod)
        {
            if (mod is GeneratorModifier gen)
            {
                var optMode = new OptionButton();
                optMode.AddItem("Expanded Polynomial (y = A + Bx + ...)", (int)GeneratorModifier.GeneratorMode.ExpandedPolynomial);
                optMode.AddItem("Factorized Polynomial (y = (Ax+B)(Cx+D))", (int)GeneratorModifier.GeneratorMode.FactorizedPolynomial);
                optMode.Selected = (int)gen.Mode;
                optMode.ItemSelected += (idx) =>
                {
                    gen.Mode = (GeneratorModifier.GeneratorMode)idx;
                    _canvas?.NotifyModifiersChanged();
                    RefreshModifierList();
                };
                container.AddChild(optMode);

                var chkAdd = new CheckBox { Text = "Additive (Add to curve)", ButtonPressed = gen.IsAdditive };
                chkAdd.Toggled += (on) => { gen.IsAdditive = on; _canvas?.NotifyModifiersChanged(); };
                container.AddChild(chkAdd);

                if (gen.Mode == GeneratorModifier.GeneratorMode.ExpandedPolynomial)
                {
                    var orderRow = new HBoxContainer();
                    orderRow.AddChild(new Label { Text = "Order:" });
                    var spinOrder = new SpinBox { MinValue = 0, MaxValue = 6, Value = gen.Order, SizeFlagsHorizontal = SizeFlags.ExpandFill };
                    spinOrder.ValueChanged += (v) =>
                    {
                        gen.Order = (int)v;
                        while (gen.Coefficients.Count <= gen.Order) gen.Coefficients.Add(0f);
                        _canvas?.NotifyModifiersChanged();
                        RefreshModifierList();
                    };
                    orderRow.AddChild(spinOrder);
                    container.AddChild(orderRow);

                    for (int i = 0; i <= gen.Order && i < gen.Coefficients.Count; i++)
                    {
                        int coefIdx = i;
                        var coefRow = new HBoxContainer();
                        coefRow.AddChild(new Label { Text = i == 0 ? "Const A:" : $"x^{i}:", CustomMinimumSize = new Vector2(60, 0) });
                        var spinCoef = new SpinBox { MinValue = -9999, MaxValue = 9999, Step = 0.01, Value = gen.Coefficients[coefIdx], SizeFlagsHorizontal = SizeFlags.ExpandFill };
                        spinCoef.ValueChanged += (v) => { gen.Coefficients[coefIdx] = (float)v; _canvas?.NotifyModifiersChanged(); };
                        coefRow.AddChild(spinCoef);
                        container.AddChild(coefRow);
                    }
                }
                else // Factorized
                {
                    string[] names = { "A:", "B:", "C:", "D:" };
                    for (int i = 0; i < 4; i++)
                    {
                        int coefIdx = i;
                        while (gen.Coefficients.Count <= coefIdx) gen.Coefficients.Add(0f);
                        var row = new HBoxContainer();
                        row.AddChild(new Label { Text = names[i], CustomMinimumSize = new Vector2(30, 0) });
                        var spin = new SpinBox { MinValue = -9999, MaxValue = 9999, Step = 0.01, Value = gen.Coefficients[coefIdx], SizeFlagsHorizontal = SizeFlags.ExpandFill };
                        spin.ValueChanged += (v) => { gen.Coefficients[coefIdx] = (float)v; _canvas?.NotifyModifiersChanged(); };
                        row.AddChild(spin);
                        container.AddChild(row);
                    }
                }
            }
            else if (mod is BuiltInFunctionModifier func)
            {
                var optFunc = new OptionButton();
                optFunc.AddItem("Sine", (int)BuiltInFunctionModifier.FunctionType.Sine);
                optFunc.AddItem("Cosine", (int)BuiltInFunctionModifier.FunctionType.Cosine);
                optFunc.AddItem("Tangent", (int)BuiltInFunctionModifier.FunctionType.Tangent);
                optFunc.AddItem("Square Root", (int)BuiltInFunctionModifier.FunctionType.SquareRoot);
                optFunc.AddItem("Natural Logarithm", (int)BuiltInFunctionModifier.FunctionType.NaturalLogarithm);
                optFunc.AddItem("Normalized Sine: sin(x)/x", (int)BuiltInFunctionModifier.FunctionType.NormalizedSine);
                optFunc.Selected = (int)func.FuncType;
                optFunc.ItemSelected += (idx) => { func.FuncType = (BuiltInFunctionModifier.FunctionType)idx; _canvas?.NotifyModifiersChanged(); };
                container.AddChild(optFunc);

                var chkAdd = new CheckBox { Text = "Additive (Add to curve)", ButtonPressed = func.IsAdditive };
                chkAdd.Toggled += (on) => { func.IsAdditive = on; _canvas?.NotifyModifiersChanged(); };
                container.AddChild(chkAdd);

                AddFloatRow(container, "Amplitude:", func.Amplitude, -999f, 999f, 0.01f, (v) => func.Amplitude = v);
                AddFloatRow(container, "Phase Mult:", func.PhaseMultiplier, -100f, 100f, 0.01f, (v) => func.PhaseMultiplier = v);
                AddFloatRow(container, "Phase Offset:", func.PhaseOffset, -9999f, 9999f, 0.1f, (v) => func.PhaseOffset = v);
                AddFloatRow(container, "Value Offset:", func.ValueOffset, -9999f, 9999f, 0.1f, (v) => func.ValueOffset = v);
            }
            else if (mod is EnvelopeModifier env)
            {
                AddFloatRow(container, "Reference:", env.ReferenceValue, -9999f, 9999f, 0.1f, (v) => env.ReferenceValue = v);
                AddFloatRow(container, "Default Min:", env.DefaultMin, -9999f, 9999f, 0.1f, (v) => env.DefaultMin = v);
                AddFloatRow(container, "Default Max:", env.DefaultMax, -9999f, 9999f, 0.1f, (v) => env.DefaultMax = v);

                var btnAddPoint = new Button { Text = "+ Add Control Point (At Playhead)", SizeFlagsHorizontal = SizeFlags.ExpandFill };
                btnAddPoint.Pressed += () =>
                {
                    float curFrame = (float)(_canvas != null ? _canvas.PlayheadTime * _fps : 0f);
                    env.ControlPoints.Add(new EnvelopeModifier.ControlPoint
                    {
                        Frame = curFrame,
                        Min = env.DefaultMin,
                        Max = env.DefaultMax
                    });
                    _canvas?.NotifyModifiersChanged();
                    RefreshModifierList();
                };
                container.AddChild(btnAddPoint);

                if (env.ControlPoints.Count > 0)
                {
                    container.AddChild(new Label { Text = "Control Points:", Modulate = new Color(0.8f, 0.8f, 0.8f) });
                    for (int i = 0; i < env.ControlPoints.Count; i++)
                    {
                        int ptIdx = i;
                        var pt = env.ControlPoints[ptIdx];
                        var ptRow = new HBoxContainer();
                        ptRow.AddChild(new Label { Text = $"F:{pt.Frame:F0}" });
                        var spinMin = new SpinBox { MinValue = -9999, MaxValue = 9999, Value = pt.Min, SizeFlagsHorizontal = SizeFlags.ExpandFill };
                        spinMin.ValueChanged += (v) => { pt.Min = (float)v; _canvas?.NotifyModifiersChanged(); };
                        ptRow.AddChild(spinMin);

                        var spinMax = new SpinBox { MinValue = -9999, MaxValue = 9999, Value = pt.Max, SizeFlagsHorizontal = SizeFlags.ExpandFill };
                        spinMax.ValueChanged += (v) => { pt.Max = (float)v; _canvas?.NotifyModifiersChanged(); };
                        ptRow.AddChild(spinMax);

                        var btnDelPt = new Button { Text = "✕", CustomMinimumSize = new Vector2(20, 20) };
                        btnDelPt.Pressed += () =>
                        {
                            env.ControlPoints.RemoveAt(ptIdx);
                            _canvas?.NotifyModifiersChanged();
                            RefreshModifierList();
                        };
                        ptRow.AddChild(btnDelPt);
                        container.AddChild(ptRow);
                    }
                }
            }
            else if (mod is CyclesModifier cyc)
            {
                var lblNote = new Label { Text = "Cycles Mode (Must be first in stack):", Modulate = new Color(0.9f, 0.8f, 0.4f) };
                lblNote.AddThemeFontSizeOverride("font_size", 10);
                container.AddChild(lblNote);

                var beforeRow = new HBoxContainer();
                beforeRow.AddChild(new Label { Text = "Before:", CustomMinimumSize = new Vector2(60, 0) });
                var optBefore = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };
                optBefore.AddItem("No Cycles", (int)CyclesModifier.CycleMode.NoCycles);
                optBefore.AddItem("Repeat Motion", (int)CyclesModifier.CycleMode.RepeatMotion);
                optBefore.AddItem("Repeat with Offset", (int)CyclesModifier.CycleMode.RepeatWithOffset);
                optBefore.AddItem("Repeat Mirrored", (int)CyclesModifier.CycleMode.RepeatMirrored);
                optBefore.Selected = (int)cyc.BeforeMode;
                optBefore.ItemSelected += (idx) => { cyc.BeforeMode = (CyclesModifier.CycleMode)idx; _canvas?.NotifyModifiersChanged(); };
                beforeRow.AddChild(optBefore);
                container.AddChild(beforeRow);

                AddFloatRow(container, "Before Count:", cyc.BeforeCycles, 0, 999, 1, (v) => cyc.BeforeCycles = (int)v);

                var afterRow = new HBoxContainer();
                afterRow.AddChild(new Label { Text = "After:", CustomMinimumSize = new Vector2(60, 0) });
                var optAfter = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };
                optAfter.AddItem("No Cycles", (int)CyclesModifier.CycleMode.NoCycles);
                optAfter.AddItem("Repeat Motion", (int)CyclesModifier.CycleMode.RepeatMotion);
                optAfter.AddItem("Repeat with Offset", (int)CyclesModifier.CycleMode.RepeatWithOffset);
                optAfter.AddItem("Repeat Mirrored", (int)CyclesModifier.CycleMode.RepeatMirrored);
                optAfter.Selected = (int)cyc.AfterMode;
                optAfter.ItemSelected += (idx) => { cyc.AfterMode = (CyclesModifier.CycleMode)idx; _canvas?.NotifyModifiersChanged(); };
                afterRow.AddChild(optAfter);
                container.AddChild(afterRow);

                AddFloatRow(container, "After Count:", cyc.AfterCycles, 0, 999, 1, (v) => cyc.AfterCycles = (int)v);
            }
            else if (mod is NoiseModifier noise)
            {
                var blendRow = new HBoxContainer();
                blendRow.AddChild(new Label { Text = "Blend:", CustomMinimumSize = new Vector2(60, 0) });
                var optBlend = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };
                optBlend.AddItem("Add", (int)NoiseModifier.NoiseBlendType.Add);
                optBlend.AddItem("Subtract", (int)NoiseModifier.NoiseBlendType.Subtract);
                optBlend.AddItem("Multiply", (int)NoiseModifier.NoiseBlendType.Multiply);
                optBlend.AddItem("Replace", (int)NoiseModifier.NoiseBlendType.Replace);
                optBlend.Selected = (int)noise.BlendType;
                optBlend.ItemSelected += (idx) => { noise.BlendType = (NoiseModifier.NoiseBlendType)idx; _canvas?.NotifyModifiersChanged(); };
                blendRow.AddChild(optBlend);
                container.AddChild(blendRow);

                AddFloatRow(container, "Scale:", noise.Scale, 0.1f, 500f, 0.5f, (v) => noise.Scale = v);
                AddFloatRow(container, "Strength:", noise.Strength, 0.0f, 100f, 0.05f, (v) => noise.Strength = v);
                AddFloatRow(container, "Offset:", noise.Offset, -9999f, 9999f, 1.0f, (v) => noise.Offset = v);
                AddFloatRow(container, "Phase (Seed):", noise.Phase, 0f, 999f, 1f, (v) => noise.Phase = v);
                AddFloatRow(container, "Depth (Octaves):", noise.Depth, 0, 5, 1, (v) => noise.Depth = (int)v);
                AddFloatRow(container, "Lacunarity:", noise.Lacunarity, 1f, 10f, 0.1f, (v) => noise.Lacunarity = v);
                AddFloatRow(container, "Roughness:", noise.Roughness, 0f, 1f, 0.02f, (v) => noise.Roughness = v);
            }
            else if (mod is LimitsModifier lim)
            {
                AddCheckFloatRow(container, "Minimum X:", lim.UseMinX, lim.MinX, -9999, 9999, (use, v) => { lim.UseMinX = use; lim.MinX = v; _canvas?.NotifyModifiersChanged(); });
                AddCheckFloatRow(container, "Maximum X:", lim.UseMaxX, lim.MaxX, -9999, 9999, (use, v) => { lim.UseMaxX = use; lim.MaxX = v; _canvas?.NotifyModifiersChanged(); });
                AddCheckFloatRow(container, "Minimum Y:", lim.UseMinY, lim.MinY, -9999, 9999, (use, v) => { lim.UseMinY = use; lim.MinY = v; _canvas?.NotifyModifiersChanged(); });
                AddCheckFloatRow(container, "Maximum Y:", lim.UseMaxY, lim.MaxY, -9999, 9999, (use, v) => { lim.UseMaxY = use; lim.MaxY = v; _canvas?.NotifyModifiersChanged(); });
            }
            else if (mod is SteppedModifier step)
            {
                AddFloatRow(container, "Step Size (Frames):", step.StepSize, 1f, 100f, 1f, (v) => step.StepSize = v);
                AddFloatRow(container, "Offset:", step.Offset, -100f, 100f, 1f, (v) => step.Offset = v);
                AddCheckFloatRow(container, "Start Frame:", step.UseFrameRange, step.RangeStart, 0, 9999, (use, v) => { step.UseFrameRange = use; step.RangeStart = v; _canvas?.NotifyModifiersChanged(); });
                AddFloatRow(container, "End Frame:", step.RangeEnd, 0, 9999, 1f, (v) => step.RangeEnd = v);
            }
            else if (mod is GaussianSmoothModifier sm)
            {
                var lblNote = new Label { Text = "Gaussian Smooth (Must be first in stack):", Modulate = new Color(0.9f, 0.8f, 0.4f) };
                lblNote.AddThemeFontSizeOverride("font_size", 10);
                container.AddChild(lblNote);

                AddFloatRow(container, "Sigma:", sm.Sigma, 0.1f, 50f, 0.1f, (v) => sm.Sigma = v);
                AddFloatRow(container, "Filter Width:", sm.FilterWidth, 1, 30, 1, (v) => sm.FilterWidth = (int)v);
            }
        }

        private void AddFloatRow(VBoxContainer parent, string label, float curVal, float min, float max, float step, Action<float> onChanged)
        {
            var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            row.AddThemeConstantOverride("separation", 6);
            parent.AddChild(row);

            var lbl = new Label { Text = label, CustomMinimumSize = new Vector2(90, 0) };
            lbl.AddThemeFontSizeOverride("font_size", 11);
            row.AddChild(lbl);

            var spin = new SpinBox
            {
                MinValue = min,
                MaxValue = max,
                Step = step,
                Value = curVal,
                SizeFlagsHorizontal = SizeFlags.ExpandFill
            };
            spin.ValueChanged += (v) =>
            {
                onChanged((float)v);
                _canvas?.NotifyModifiersChanged();
            };
            row.AddChild(spin);
        }

        private void AddCheckFloatRow(VBoxContainer parent, string label, bool isChecked, float curVal, float min, float max, Action<bool, float> onChanged)
        {
            var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            row.AddThemeConstantOverride("separation", 6);
            parent.AddChild(row);

            var chk = new CheckBox { Text = label, ButtonPressed = isChecked, CustomMinimumSize = new Vector2(100, 0) };
            chk.AddThemeFontSizeOverride("font_size", 11);
            row.AddChild(chk);

            var spin = new SpinBox
            {
                MinValue = min,
                MaxValue = max,
                Step = 0.1,
                Value = curVal,
                Editable = isChecked,
                SizeFlagsHorizontal = SizeFlags.ExpandFill
            };
            chk.Toggled += (on) =>
            {
                spin.Editable = on;
                onChanged(on, (float)spin.Value);
            };
            spin.ValueChanged += (v) =>
            {
                onChanged(chk.ButtonPressed, (float)v);
            };
            row.AddChild(spin);
        }
    }
}
