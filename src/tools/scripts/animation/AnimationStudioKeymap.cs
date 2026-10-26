using Godot;
using DeadlockPlayground.UI;

namespace DeadlockPlayground.Animation
{
    /// <summary>
    /// Blender-style hotkeys and centralized shortcut dispatcher for the Animation Studio Dock.
    /// Manages transport (Space, arrows), navigation (Home, Numpad .), modal transforms (G, S, R),
    /// keyframe editing (A, I, X, Del, Shift+D), clipboard (Ctrl+C, Ctrl+V), and tab switching.
    /// </summary>
    public class AnimationStudioKeymap
    {
        private readonly AnimationStudioDock _dock;

        public AnimationStudioKeymap(AnimationStudioDock dock)
        {
            _dock = dock;
        }

        public bool HandleKeyInput(InputEventKey keyEvent)
        {
            if (_dock == null || keyEvent == null || !keyEvent.Pressed) return false;

            // Never intercept typing when a text entry field has focus
            var focusOwner = _dock.GetViewport()?.GuiGetFocusOwner();
            if (focusOwner is LineEdit || focusOwner is TextEdit) return false;

            // 1. Toggle Dock Open / Closed (Global shortcut)
            if (keyEvent.CtrlPressed && keyEvent.Keycode == Key.Space)
            {
                _dock.ToggleOpen();
                return true;
            }

            // If dock is collapsed, ignore all internal hotkeys
            if (!_dock.IsOpen) return false;

            // 2. Transport & Playback Hotkeys
            if (!keyEvent.CtrlPressed && !keyEvent.AltPressed)
            {
                if (keyEvent.Keycode == Key.Space)
                {
                    AnimationTransportService.Instance.TogglePlay();
                    return true;
                }

                if (keyEvent.Keycode == Key.Left)
                {
                    if (keyEvent.ShiftPressed) AnimationTransportService.Instance.Seek(0.0);
                    else AnimationTransportService.Instance.Step(-1);
                    return true;
                }

                if (keyEvent.Keycode == Key.Right)
                {
                    if (keyEvent.ShiftPressed) AnimationTransportService.Instance.Seek(AnimationTransportService.Instance.Length);
                    else AnimationTransportService.Instance.Step(1);
                    return true;
                }

                // Tab Switching via 1, 2, 3
                if (!keyEvent.ShiftPressed)
                {
                    if (keyEvent.Keycode == Key.Key1)
                    {
                        _dock.SwitchTab(AnimationStudioDock.StudioTab.DopeSheet);
                        return true;
                    }
                    if (keyEvent.Keycode == Key.Key2)
                    {
                        _dock.SwitchTab(AnimationStudioDock.StudioTab.GraphEditor);
                        return true;
                    }
                    if (keyEvent.Keycode == Key.Key3)
                    {
                        _dock.SwitchTab(AnimationStudioDock.StudioTab.ClipSettings);
                        return true;
                    }
                }
            }

            // 3. Poses & Clipboard (Ctrl shortcuts)
            if (keyEvent.CtrlPressed)
            {
                if (keyEvent.Keycode == Key.Z)
                {
                    if (keyEvent.ShiftPressed) _dock.Redo();
                    else _dock.Undo();
                    return true;
                }
                if (keyEvent.Keycode == Key.Y)
                {
                    _dock.Redo();
                    return true;
                }
                if (keyEvent.Keycode == Key.C)
                {
                    _dock.CopyPose();
                    return true;
                }
                if (keyEvent.Keycode == Key.V)
                {
                    if (keyEvent.ShiftPressed) _dock.PasteMirrorPose();
                    else _dock.PastePose();
                    return true;
                }
            }

            // 4. Keyframe Insert
            if (keyEvent.Keycode == Key.I && !keyEvent.CtrlPressed && !keyEvent.AltPressed)
            {
                _dock.InsertKeyframeForSelectedBone();
                return true;
            }

            // 5. Canvas-specific keybinds
            if (_dock.CurrentTab == AnimationStudioDock.StudioTab.DopeSheet)
            {
                var dope = _dock.DopeSheetCanvas;
                if (dope == null) return false;

                if (keyEvent.Keycode == Key.Home)
                {
                    dope.FrameAll();
                    return true;
                }
                if (keyEvent.Keycode == Key.KpPeriod || keyEvent.Keycode == Key.Period)
                {
                    dope.FrameSelected();
                    return true;
                }
                if (keyEvent.Keycode == Key.A)
                {
                    if (keyEvent.AltPressed) dope.DeselectAllKeys();
                    else if (keyEvent.CtrlPressed) dope.SelectAllKeys();
                    else dope.SelectAllKeys();
                    return true;
                }
                if (keyEvent.Keycode == Key.Delete || keyEvent.Keycode == Key.X)
                {
                    dope.DeleteSelectedKeys();
                    return true;
                }
                if (keyEvent.Keycode == Key.D && keyEvent.ShiftPressed)
                {
                    dope.DuplicateSelectedKeys();
                    return true;
                }
                if (keyEvent.Keycode == Key.G && !keyEvent.CtrlPressed && !keyEvent.AltPressed)
                {
                    dope.StartGrabKeys();
                    return true;
                }
                if (keyEvent.Keycode == Key.S && !keyEvent.CtrlPressed && !keyEvent.AltPressed)
                {
                    dope.StartScaleKeys();
                    return true;
                }
                if (keyEvent.Keycode == Key.B && !keyEvent.CtrlPressed && !keyEvent.AltPressed)
                {
                    dope.StartBoxSelect();
                    return true;
                }
            }
            else if (_dock.CurrentTab == AnimationStudioDock.StudioTab.GraphEditor)
            {
                var graph = _dock.GraphEditorCanvas;
                if (graph == null) return false;

                if (keyEvent.Keycode == Key.Home)
                {
                    graph.FitView();
                    return true;
                }
                if (keyEvent.Keycode == Key.KpPeriod || keyEvent.Keycode == Key.Period)
                {
                    graph.FitSelectedView();
                    return true;
                }
                if (keyEvent.Keycode == Key.A)
                {
                    graph.ToggleSelectAll();
                    return true;
                }
                if (keyEvent.Keycode == Key.Delete || keyEvent.Keycode == Key.X)
                {
                    graph.DeleteSelectedKeys();
                    return true;
                }
                if (keyEvent.Keycode == Key.D && keyEvent.ShiftPressed)
                {
                    graph.DuplicateSelectedKeys();
                    return true;
                }
                if (keyEvent.Keycode == Key.G && !keyEvent.CtrlPressed && !keyEvent.AltPressed)
                {
                    graph.StartModalTransform(GraphEditorCanvas.ModalTransformMode.Grab);
                    return true;
                }
                if (keyEvent.Keycode == Key.S && !keyEvent.CtrlPressed && !keyEvent.AltPressed)
                {
                    graph.StartModalTransform(GraphEditorCanvas.ModalTransformMode.Scale);
                    return true;
                }
                if (keyEvent.Keycode == Key.R && !keyEvent.CtrlPressed && !keyEvent.AltPressed)
                {
                    graph.StartModalTransform(GraphEditorCanvas.ModalTransformMode.Rotate);
                    return true;
                }
            }

            return false;
        }
    }
}
