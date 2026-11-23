using System;
using System.Collections.Generic;
using Godot;

namespace DeadlockPlayground.UI
{
    public enum WizardStepState
    {
        NotVisited,
        Current,
        Done,
        Attention
    }

    /// <summary>
    /// Horizontal clickable stepper bar for multi-step wizard flows.
    /// Every step chip is clickable at any time (free navigation, no locked steps).
    /// </summary>
    public partial class StepperBar : PanelContainer
    {
        public sealed class StepItem
        {
            public int Index;
            public string Title;
            public WizardStepState State = WizardStepState.NotVisited;
            public bool Applicable = true;
            public Button Button;
        }

        private readonly List<StepItem> _steps = new();
        private HFlowContainer _flow;
        private int _currentIndex = 0;

        public int CurrentIndex => _currentIndex;
        public event Action<int> StepSelected;

        public StepperBar()
        {
            ThemeTypeVariation = "SubPanelContainer";
            SizeFlagsHorizontal = SizeFlags.ExpandFill;

            var pad = new MarginContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            pad.AddThemeConstantOverride("margin_left", 8);
            pad.AddThemeConstantOverride("margin_top", 6);
            pad.AddThemeConstantOverride("margin_right", 8);
            pad.AddThemeConstantOverride("margin_bottom", 6);
            AddChild(pad);

            _flow = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            _flow.AddThemeConstantOverride("h_separation", 6);
            _flow.AddThemeConstantOverride("v_separation", 6);
            pad.AddChild(_flow);
        }

        public void SetSteps(params string[] titles)
        {
            foreach (Node c in _flow.GetChildren()) c.QueueFree();
            _steps.Clear();

            for (int i = 0; i < titles.Length; i++)
            {
                int idx = i;
                var btn = new Button
                {
                    ThemeTypeVariation = "ImporterStepChip",
                    ToggleMode = true,
                    ButtonPressed = i == _currentIndex,
                    CustomMinimumSize = new Vector2(0, 34),
                    MouseDefaultCursorShape = CursorShape.PointingHand
                };
                ImporterStyle.ApplyFontSize(btn, ImporterStyle.FontHint);
                btn.Pressed += () =>
                {
                    _currentIndex = idx;
                    RefreshChips();
                    StepSelected?.Invoke(idx);
                };

                var item = new StepItem
                {
                    Index = idx,
                    Title = titles[i],
                    State = i == _currentIndex ? WizardStepState.Current : WizardStepState.NotVisited,
                    Button = btn
                };
                _steps.Add(item);
                _flow.AddChild(btn);
            }
            RefreshChips();
        }

        public void SetCurrentStep(int index)
        {
            if (index < 0 || index >= _steps.Count) return;
            _currentIndex = index;
            RefreshChips();
        }

        public void SetStepState(int index, WizardStepState state)
        {
            if (index < 0 || index >= _steps.Count) return;
            _steps[index].State = state;
            RefreshChips();
        }

        public void SetStepTitle(int index, string title)
        {
            if (index < 0 || index >= _steps.Count) return;
            _steps[index].Title = title;
            RefreshChips();
        }

        public void SetStepApplicable(int index, bool applicable)
        {
            if (index < 0 || index >= _steps.Count) return;
            _steps[index].Applicable = applicable;
            RefreshChips();
        }

        private void RefreshChips()
        {
            for (int i = 0; i < _steps.Count; i++)
            {
                var s = _steps[i];
                if (s.Button == null) continue;
                s.Button.Visible = s.Applicable;

                bool isCurr = i == _currentIndex;
                s.Button.SetPressedNoSignal(isCurr);

                WizardStepState displayState = isCurr ? WizardStepState.Current : s.State;
                string glyph = displayState switch
                {
                    WizardStepState.Done => "✓",
                    WizardStepState.Current => "●",
                    WizardStepState.Attention => "⚠",
                    _ => "○"
                };
                s.Button.Text = $"{glyph} {i + 1}. {s.Title}";

                Color textColor = displayState switch
                {
                    WizardStepState.Current => PlaygroundThemeHelper.Parchment,
                    WizardStepState.Done => PlaygroundThemeHelper.SageGreen,
                    WizardStepState.Attention => new Color(0.95f, 0.72f, 0.25f),
                    _ => ImporterStyle.HintColor
                };
                s.Button.AddThemeColorOverride("font_color", textColor);
            }
        }
    }
}

