using Godot;
using DeadlockPlayground.UI;
using DeadlockPlayground.Tools;

namespace DeadlockPlayground.Animation
{
    /// <summary>
    /// Centralized shortcut dispatcher for the Animation Studio Dock.
    /// Fully integrated with KeybindsManager so users can customize any shortcut in Settings > Keybinds > Animation.
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
            if (KeybindsManager.IsActionTriggered("anim_toggle_dock", keyEvent))
            {
                _dock.ToggleOpen();
                return true;
            }

            // If dock is collapsed, ignore all internal hotkeys
            if (!_dock.IsOpen) return false;

            // 2. Open Shortcuts Cheatsheet Modal (F1 / ?)
            if (KeybindsManager.IsActionTriggered("anim_cheatsheet", keyEvent))
            {
                _dock.OpenShortcutsCheatsheet();
                return true;
            }

            // 3. Transport & Playback Hotkeys
            if (KeybindsManager.IsActionTriggered("anim_play_toggle", keyEvent))
            {
                AnimationTransportService.Instance.TogglePlay();
                return true;
            }

            if (KeybindsManager.IsActionTriggered("anim_seek_start", keyEvent))
            {
                AnimationTransportService.Instance.Seek(0.0);
                return true;
            }

            if (KeybindsManager.IsActionTriggered("anim_seek_end", keyEvent))
            {
                AnimationTransportService.Instance.Seek(AnimationTransportService.Instance.Length);
                return true;
            }

            if (KeybindsManager.IsActionTriggered("anim_step_prev", keyEvent))
            {
                AnimationTransportService.Instance.Step(-1);
                return true;
            }

            if (KeybindsManager.IsActionTriggered("anim_step_next", keyEvent))
            {
                AnimationTransportService.Instance.Step(1);
                return true;
            }

            // 4. Tab Switching
            if (KeybindsManager.IsActionTriggered("anim_tab_dopesheet", keyEvent))
            {
                _dock.SwitchTab(AnimationStudioDock.StudioTab.DopeSheet);
                return true;
            }
            if (KeybindsManager.IsActionTriggered("anim_tab_grapheditor", keyEvent))
            {
                _dock.SwitchTab(AnimationStudioDock.StudioTab.GraphEditor);
                return true;
            }
            if (KeybindsManager.IsActionTriggered("anim_tab_clippose", keyEvent))
            {
                _dock.SwitchTab(AnimationStudioDock.StudioTab.ClipSettings);
                return true;
            }

            // 5. Poses & Clipboard
            if (KeybindsManager.IsActionTriggered("anim_undo", keyEvent))
            {
                _dock.Undo();
                return true;
            }
            if (KeybindsManager.IsActionTriggered("anim_redo", keyEvent))
            {
                _dock.Redo();
                return true;
            }
            if (KeybindsManager.IsActionTriggered("anim_copy_pose", keyEvent))
            {
                _dock.CopyPose();
                return true;
            }
            if (KeybindsManager.IsActionTriggered("anim_paste_mirror_pose", keyEvent))
            {
                _dock.PasteMirrorPose();
                return true;
            }
            if (KeybindsManager.IsActionTriggered("anim_paste_pose", keyEvent))
            {
                _dock.PastePose();
                return true;
            }

            // 6. Keyframe Insert
            if (KeybindsManager.IsActionTriggered("anim_insert_key", keyEvent))
            {
                _dock.InsertKeyframeForSelectedBone();
                return true;
            }

            // 7. Canvas-specific keybinds
            if (_dock.CurrentTab == AnimationStudioDock.StudioTab.DopeSheet)
            {
                var dope = _dock.DopeSheetCanvas;
                if (dope == null) return false;

                if (KeybindsManager.IsActionTriggered("anim_fit_view", keyEvent))
                {
                    dope.FrameAll();
                    return true;
                }
                if (KeybindsManager.IsActionTriggered("anim_fit_selected", keyEvent))
                {
                    dope.FrameSelected();
                    return true;
                }
                if (KeybindsManager.IsActionTriggered("anim_select_all", keyEvent))
                {
                    if (keyEvent.AltPressed) dope.DeselectAllKeys();
                    else dope.SelectAllKeys();
                    return true;
                }
                if (KeybindsManager.IsActionTriggered("anim_delete_keys", keyEvent))
                {
                    dope.DeleteSelectedKeys();
                    return true;
                }
                if (KeybindsManager.IsActionTriggered("anim_duplicate_keys", keyEvent))
                {
                    dope.DuplicateSelectedKeys();
                    return true;
                }
                if (KeybindsManager.IsActionTriggered("anim_transform_grab", keyEvent))
                {
                    dope.StartGrabKeys();
                    return true;
                }
                if (KeybindsManager.IsActionTriggered("anim_transform_scale", keyEvent))
                {
                    dope.StartScaleKeys();
                    return true;
                }
                if (KeybindsManager.IsActionTriggered("anim_box_select", keyEvent))
                {
                    dope.StartBoxSelect();
                    return true;
                }
            }
            else if (_dock.CurrentTab == AnimationStudioDock.StudioTab.GraphEditor)
            {
                var graph = _dock.GraphEditorCanvas;
                if (graph == null) return false;

                if (KeybindsManager.IsActionTriggered("anim_fit_view", keyEvent))
                {
                    graph.FitView();
                    return true;
                }
                if (KeybindsManager.IsActionTriggered("anim_fit_selected", keyEvent))
                {
                    graph.FitSelectedView();
                    return true;
                }
                if (KeybindsManager.IsActionTriggered("anim_select_all", keyEvent))
                {
                    graph.ToggleSelectAll();
                    return true;
                }
                if (KeybindsManager.IsActionTriggered("anim_delete_keys", keyEvent))
                {
                    graph.DeleteSelectedKeys();
                    return true;
                }
                if (KeybindsManager.IsActionTriggered("anim_duplicate_keys", keyEvent))
                {
                    graph.DuplicateSelectedKeys();
                    return true;
                }
                if (KeybindsManager.IsActionTriggered("anim_toggle_modifiers", keyEvent))
                {
                    _dock.ToggleModifiersSidebar();
                    return true;
                }
                if (KeybindsManager.IsActionTriggered("anim_transform_grab", keyEvent))
                {
                    graph.StartModalTransform(GraphEditorCanvas.ModalTransformMode.Grab);
                    return true;
                }
                if (KeybindsManager.IsActionTriggered("anim_transform_scale", keyEvent))
                {
                    graph.StartModalTransform(GraphEditorCanvas.ModalTransformMode.Scale);
                    return true;
                }
                if (KeybindsManager.IsActionTriggered("anim_transform_rotate", keyEvent))
                {
                    graph.StartModalTransform(GraphEditorCanvas.ModalTransformMode.Rotate);
                    return true;
                }
                if (KeybindsManager.IsActionTriggered("anim_box_select", keyEvent))
                {
                    graph.StartBoxSelect();
                    return true;
                }
            }

            return false;
        }
    }
}
