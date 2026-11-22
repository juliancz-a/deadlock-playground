using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using DeadlockPlayground.Importer;
using DeadlockPlayground.Theming;

namespace DeadlockPlayground.UI
{
    public partial class CustomModelImporterModalUI
    {
        // Phase 1, 5, 6: Memory Footer, Light Mode Manual Rig Update, Console, Camera Presets, Bone Legend & Viewport Right-Click Disable
        private Label _lblMemoryStatus;
        private Button _btnManualRigUpdate;
        private Button _btnToggleConsole;
        private PanelContainer _consolePanel;
        private TextEdit _txtLogConsole;
        private HFlowContainer _boneLegendRow;
        private double _memPollTimer;
        private Vector2 _rightClickStartPos;
        private Vector2 _leftClickStartPos;
        private string _selectedViewportBoneName = "";
        private PanelContainer _boxSelectedBoneBadge;
        private Label _lblSelectedBoneName;
        private Label _lblSelectedBoneRole;
        private Button _btnSelBonePhysics;
        private Button _btnSelBoneDisable;
        private Button _btnSelBonePaint;
        private Button _btnSelBoneRemap;
        private Button _btnToggleBonesOnTop;
        private bool _bonesOnTop = true;
        private MultiMeshInstance3D _bonesJointPointsMesh;
        private CheckBox _chkBoneFilterCore;
        private CheckBox _chkBoneFilterFingers;
        private CheckBox _chkBoneFilterPhysics;
        private CheckBox _chkBoneFilterAccessory;
        private CheckBox _chkBoneFilterDisabled;
        private CheckBox _chkBoneFilterVanilla;
        private readonly HashSet<string> _activeCustomWeightedBoneNames = new(StringComparer.OrdinalIgnoreCase);

        private void RebuildActiveCustomWeightedBoneSet()
        {
            _activeCustomWeightedBoneNames.Clear();
            if (_previewRiggedContainer == null || !GodotObject.IsInstanceValid(_previewRiggedContainer) || _previewHeroSkeleton == null)
                return;
            int bCount = _previewHeroSkeleton.GetBoneCount();
            foreach (var mi in CustomModelAutoRigger.CollectMeshes(_previewRiggedContainer))
            {
                if (mi?.Mesh == null) continue;
                var skin = mi.Skin;
                for (int s = 0; s < mi.Mesh.GetSurfaceCount(); s++)
                {
                    var arrays = mi.Mesh.SurfaceGetArrays(s);
                    if (arrays == null || arrays.Count <= (int)Mesh.ArrayType.Weights) continue;
                    var bones = CustomModelAutoRigger.GetInt32Array(arrays[(int)Mesh.ArrayType.Bones]);
                    var weights = CustomModelAutoRigger.GetFloat32Array(arrays[(int)Mesh.ArrayType.Weights]);
                    if (bones == null || weights == null) continue;
                    int n = Math.Min(bones.Length, weights.Length);
                    for (int i = 0; i < n; i++)
                    {
                        if (weights[i] <= 0.01f) continue;
                        int bindIdx = bones[i];
                        int skelIdx = bindIdx;
                        if (skin != null && bindIdx >= 0 && bindIdx < skin.GetBindCount())
                        {
                            string bn = skin.GetBindName(bindIdx);
                            if (!string.IsNullOrEmpty(bn))
                                skelIdx = _previewHeroSkeleton.FindBone(bn);
                            else
                                skelIdx = skin.GetBindBone(bindIdx);
                        }
                        if (skelIdx >= 0 && skelIdx < bCount)
                        {
                            _activeCustomWeightedBoneNames.Add(_previewHeroSkeleton.GetBoneName(skelIdx));
                        }
                    }
                }
            }
        }

        private void FocusCameraOnReferenceHand(bool left)
        {
            if (_previewCamera == null) return;
            EnsureReferenceRigInitialized(false);
            string side = left ? "L" : "R";
            if (_referenceRig == null || !_referenceRig.Joints.TryGetValue("Wrist" + side, out var wristLocal))
                return;
            Transform3D xf = BuildCurrentAlignedTransform();
            Vector3 targetWorld = xf * wristLocal;
            if (_referenceRig.Joints.TryGetValue("Elbow" + side, out var elbowLocal))
            {
                Vector3 dir = (targetWorld - (xf * elbowLocal));
                if (dir.LengthSquared() > 1e-6f) targetWorld += dir.Normalized() * 0.06f;
            }
            _orbitTarget = targetWorld;
            _orbitDist = 0.40f;
            _orbitPitch = 0.35f;
            _orbitYaw = left ? 0.35f : -0.35f;
            UpdateCamera();
        }

        private void SetCameraPreset(string preset)
        {
            var (heroMin, heroMax, heroOk) = _previewHeroClone != null
                ? CustomModelAutoRigger.ComputeAuthenticHeroBounds(_previewHeroClone)
                : (Vector3.Zero, new Vector3(0, 2.0f, 0), false);
            float h = heroOk ? MathF.Max(1.0f, heroMax.Y - MathF.Min(0f, heroMin.Y)) : 2.2f;
            _orbitTarget = new Vector3(0f, heroOk ? (heroMin.Y + heroMax.Y) * 0.5f : 1.0f, 0f);
            _orbitDist = Mathf.Max(h * 1.55f, 2.2f);

            switch (preset)
            {
                case "Front":
                    _orbitYaw = 0f;
                    _orbitPitch = 0.08f;
                    break;
                case "Side":
                    _orbitYaw = Mathf.Pi * 0.5f;
                    _orbitPitch = 0.08f;
                    break;
                case "3/4":
                    _orbitYaw = Mathf.DegToRad(35f);
                    _orbitPitch = Mathf.DegToRad(14f);
                    break;
                case "Frame":
                    _orbitPitch = 0.12f;
                    break;
            }
            UpdateCamera();
        }

        private void UpdateFooterMemoryIndicator()
        {
            if (_lblMemoryStatus != null && GodotObject.IsInstanceValid(_lblMemoryStatus))
            {
                _lblMemoryStatus.Text = ImporterMemoryGuard.FormatFooterMemoryText();
            }
        }

        private void AppendConsoleMessage(string text)
        {
            if (_txtLogConsole == null || !GodotObject.IsInstanceValid(_txtLogConsole) || string.IsNullOrWhiteSpace(text))
                return;
            string ts = DateTime.Now.ToString("HH:mm:ss");
            _txtLogConsole.Text = string.IsNullOrEmpty(_txtLogConsole.Text)
                ? $"[{ts}] {text}"
                : $"{_txtLogConsole.Text}\n[{ts}] {text}";
            _txtLogConsole.ScrollVertical = _txtLogConsole.GetLineCount();
        }

        private void BuildSelectedBoneBadgeOverlay(Control viewportPanel)
        {
            if (viewportPanel == null) return;
            var overlayMargin = new MarginContainer
            {
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical = Control.SizeFlags.ExpandFill,
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            overlayMargin.AddThemeConstantOverride("margin_left", 10);
            overlayMargin.AddThemeConstantOverride("margin_right", 10);
            overlayMargin.AddThemeConstantOverride("margin_top", 10);
            overlayMargin.AddThemeConstantOverride("margin_bottom", 10);
            viewportPanel.AddChild(overlayMargin);

            _boxSelectedBoneBadge = new PanelContainer
            {
                ThemeTypeVariation = "InspectorSectionPanel",
                SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin,
                SizeFlagsVertical = Control.SizeFlags.ShrinkEnd,
                CustomMinimumSize = new Vector2(280, 0),
                Visible = false,
                MouseFilter = Control.MouseFilterEnum.Stop
            };
            overlayMargin.AddChild(_boxSelectedBoneBadge);

            var vBox = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            vBox.AddThemeConstantOverride("separation", 5);
            _boxSelectedBoneBadge.AddChild(vBox);

            var hdr = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            hdr.AddThemeConstantOverride("separation", 6);
            _lblSelectedBoneName = new Label
            {
                Text = "Bone",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                ClipText = true
            };
            ImporterStyle.ApplyFontSize(_lblSelectedBoneName, ImporterStyle.FontBody);
            _lblSelectedBoneName.AddThemeColorOverride("font_color", Colors.White);
            hdr.AddChild(_lblSelectedBoneName);

            var btnClose = new Button
            {
                Text = "×",
                ThemeTypeVariation = "ToolbarButton",
                CustomMinimumSize = new Vector2(24, 24),
                TooltipText = "Clear bone selection"
            };
            btnClose.Pressed += () =>
            {
                _selectedViewportBoneName = "";
                UpdateSelectedBoneBadge();
                if (_showBones) RedrawBoneWireframe();
            };
            hdr.AddChild(btnClose);
            vBox.AddChild(hdr);

            _lblSelectedBoneRole = new Label
            {
                Text = "Role",
                AutowrapMode = TextServer.AutowrapMode.WordSmart
            };
            ImporterStyle.ApplyHintStyle(_lblSelectedBoneRole);
            _secondaryLabels.Add(_lblSelectedBoneRole);
            vBox.AddChild(_lblSelectedBoneRole);

            var actionRow = new HFlowContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            actionRow.AddThemeConstantOverride("h_separation", 6);
            actionRow.AddThemeConstantOverride("v_separation", 4);

            _btnSelBonePhysics = new Button
            {
                Text = "Physics",
                ThemeTypeVariation = "ToolbarButton",
                CustomMinimumSize = new Vector2(68, 26),
                TooltipText = "Toggle Jiggle / Rigid and open in Physics tree"
            };
            _btnSelBonePhysics.Pressed += OnSelBonePhysicsPressed;
            actionRow.AddChild(_btnSelBonePhysics);

            _btnSelBoneDisable = new Button
            {
                Text = "Disable",
                ThemeTypeVariation = "ToolbarButton",
                CustomMinimumSize = new Vector2(68, 26),
                TooltipText = "Disable or Re-Enable this bone (collapses weights to parent)"
            };
            _btnSelBoneDisable.Pressed += OnSelBoneDisablePressed;
            actionRow.AddChild(_btnSelBoneDisable);

            _btnSelBonePaint = new Button
            {
                Text = "Paint Weights",
                ThemeTypeVariation = "ToolbarButton",
                CustomMinimumSize = new Vector2(92, 26),
                TooltipText = "Switch to Bone Weight Editor with this bone selected"
            };
            _btnSelBonePaint.Pressed += OnSelBonePaintPressed;
            actionRow.AddChild(_btnSelBonePaint);

            _btnSelBoneRemap = new Button
            {
                Text = "Remap",
                ThemeTypeVariation = "ToolbarButton",
                CustomMinimumSize = new Vector2(60, 26),
                TooltipText = "Jump to Bones -> Remap tab"
            };
            _btnSelBoneRemap.Pressed += OnSelBoneRemapPressed;
            actionRow.AddChild(_btnSelBoneRemap);

            vBox.AddChild(actionRow);
        }

        private BoneChainDef FindChainForViewportBone(string bName)
        {
            if (string.IsNullOrEmpty(bName) || _boneChains == null) return null;
            return _boneChains.FirstOrDefault(c =>
                c.RootSourceBone.Equals(bName, StringComparison.OrdinalIgnoreCase) ||
                c.AppendedHeroBoneNames.Any(a => a.Equals(bName, StringComparison.OrdinalIgnoreCase)) ||
                c.SourceBoneNames.Any(s => s.Equals(bName, StringComparison.OrdinalIgnoreCase)));
        }

        private static bool IsRigidHubChain(BoneChainDef ch) =>
            ch != null && (ch.Name.Contains("[Rigid]", StringComparison.OrdinalIgnoreCase) ||
                           ch.Category.Contains("Hub", StringComparison.OrdinalIgnoreCase));

        private void UpdateSelectedBoneBadge()
        {
            if (_boxSelectedBoneBadge == null) return;
            if (string.IsNullOrEmpty(_selectedViewportBoneName) || _previewHeroSkeleton == null ||
                _previewHeroSkeleton.FindBone(_selectedViewportBoneName) < 0)
            {
                _boxSelectedBoneBadge.Visible = false;
                return;
            }

            string bName = _selectedViewportBoneName;
            bool isDisabled = _disabledSourceBones.Contains(bName);
            var chain = FindChainForViewportBone(bName);
            bool isHub = IsRigidHubChain(chain);
            bool isFinger = bName.Contains("finger_", StringComparison.OrdinalIgnoreCase) ||
                            bName.Contains("thumb", StringComparison.OrdinalIgnoreCase) ||
                            bName.Contains("index", StringComparison.OrdinalIgnoreCase) ||
                            bName.Contains("middle", StringComparison.OrdinalIgnoreCase) ||
                            bName.Contains("ring", StringComparison.OrdinalIgnoreCase) ||
                            bName.Contains("pinky", StringComparison.OrdinalIgnoreCase);

            _lblSelectedBoneName.Text = $"Bone: {bName}";
            if (isDisabled)
                _lblSelectedBoneRole.Text = "Disabled (weights collapsed onto parent)";
            else if (chain != null)
                _lblSelectedBoneRole.Text = isHub
                    ? $"Rigid Hub: {chain.Name} • Anchor: {chain.AnchorHeroBone}"
                    : $"Chain: {chain.Name} • {(chain.Policy == ChainPolicy.PreservePhysicsChain ? "Jiggle Physics" : "Rigid")}";
            else if (isFinger)
                _lblSelectedBoneRole.Text = "Finger Bone";
            else
                _lblSelectedBoneRole.Text = "Core / Skeleton Bone";

            if (_btnSelBoneDisable != null)
                _btnSelBoneDisable.Text = isDisabled ? "Re-Enable" : "Disable";

            if (_btnSelBonePhysics != null)
            {
                _btnSelBonePhysics.Visible = chain != null;
                if (chain != null)
                {
                    bool isJiggle = chain.Policy == ChainPolicy.PreservePhysicsChain;
                    _btnSelBonePhysics.Text = isHub ? "View Chain" : (isJiggle ? "Set Rigid" : "Set Jiggle");
                }
            }

            _boxSelectedBoneBadge.Visible = true;
        }

        private void OnSelBonePhysicsPressed()
        {
            var chain = FindChainForViewportBone(_selectedViewportBoneName);
            if (chain == null) return;
            if (!IsRigidHubChain(chain))
            {
                chain.Policy = chain.Policy == ChainPolicy.PreservePhysicsChain
                    ? ChainPolicy.CollapseToNearestCore
                    : ChainPolicy.PreservePhysicsChain;
            }
            _selectedChain = chain;
            if (_rightTabs != null && _rightTabs.GetTabCount() > 2)
                _rightTabs.CurrentTab = 2; // 3_ Bones
            SetBonesSubView(2); // Physics
            MarkRemapStaleAndRefresh();
            RefreshClothingChainsPanel();
            UpdateSelectedBoneBadge();
            if (_showBones) RedrawBoneWireframe();
        }

        private void OnSelBoneDisablePressed()
        {
            if (string.IsNullOrEmpty(_selectedViewportBoneName)) return;
            ToggleBoneDisabled(_selectedViewportBoneName);
            UpdateSelectedBoneBadge();
        }

        private void OnSelBonePaintPressed()
        {
            InspectWeightsForBone(_selectedViewportBoneName);
        }

        private void InspectWeightsForBone(string boneName)
        {
            if (string.IsNullOrEmpty(boneName)) return;
            bool inBonesTab = _rightTabs != null && _rightTabs.CurrentTab == 2;
            if (inBonesTab)
            {
                SetBonesSubView(1); // Dedicated Weights workspace
                HostWeightPaintSectionIn(_boxTab3WeightPaintHost);
            }
            else
            {
                if (_rightTabs != null && _rightTabs.GetTabCount() > 1)
                    _rightTabs.CurrentTab = 1; // 2_ Rig
                SetRigWizardStep(2); // Weights step
                HostWeightPaintSectionIn(_boxTab2WeightPaintHost);
            }
            PopulatePaintBoneDropdown();
            if (_btnWeightPaintMode != null && !_btnWeightPaintMode.ButtonPressed)
                _btnWeightPaintMode.ButtonPressed = true;
            SelectPaintBoneByName(boneName);
            RefreshChainPaintQuickBones();
        }

        private Button _btnStressTestPopover;
        private PanelContainer _boxStressTestPopover;

        private void BuildStressTestViewportPopover(Control viewportPanel)
        {
            if (viewportPanel == null) return;

            var overlayMargin = new MarginContainer
            {
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical = Control.SizeFlags.ExpandFill,
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            overlayMargin.AddThemeConstantOverride("margin_left", 10);
            overlayMargin.AddThemeConstantOverride("margin_right", 10);
            overlayMargin.AddThemeConstantOverride("margin_top", 10);
            overlayMargin.AddThemeConstantOverride("margin_bottom", 10);
            viewportPanel.AddChild(overlayMargin);

            _boxStressTestPopover = new PanelContainer
            {
                ThemeTypeVariation = "InspectorSectionPanel",
                SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin,
                SizeFlagsVertical = Control.SizeFlags.ShrinkBegin,
                CustomMinimumSize = new Vector2(310, 0),
                Visible = false,
                MouseFilter = Control.MouseFilterEnum.Stop
            };
            overlayMargin.AddChild(_boxStressTestPopover);

            var pad = new MarginContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            pad.AddThemeConstantOverride("margin_left", 10);
            pad.AddThemeConstantOverride("margin_right", 10);
            pad.AddThemeConstantOverride("margin_top", 8);
            pad.AddThemeConstantOverride("margin_bottom", 10);
            _boxStressTestPopover.AddChild(pad);

            var stressBody = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            stressBody.AddThemeConstantOverride("separation", 6);
            pad.AddChild(stressBody);

            var hdr = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            hdr.AddThemeConstantOverride("separation", 6);
            var lblTitle = new Label
            {
                Text = "Physics Stress Test",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
            };
            ImporterStyle.ApplyFontSize(lblTitle, ImporterStyle.FontBody);
            _accentLabels.Add(lblTitle);
            hdr.AddChild(lblTitle);

            var btnClose = new Button
            {
                Text = "×",
                ThemeTypeVariation = "ToolbarButton",
                CustomMinimumSize = new Vector2(26, 26),
                TooltipText = "Close stress test popover"
            };
            btnClose.Pressed += () =>
            {
                if (_btnStressTestPopover != null) _btnStressTestPopover.ButtonPressed = false;
                else if (_boxStressTestPopover != null) _boxStressTestPopover.Visible = false;
            };
            hdr.AddChild(btnClose);
            stressBody.AddChild(hdr);

            var stressPresetRow = new HFlowContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            stressPresetRow.AddThemeConstantOverride("h_separation", 6);
            stressPresetRow.AddThemeConstantOverride("v_separation", 6);

            var btnStressReset = new Button { Text = "Normal", CustomMinimumSize = new Vector2(68, 28), ThemeTypeVariation = "ToolbarButton" };
            btnStressReset.Pressed += () => SetPhysicsStressTestPose(0f, 0f, false);

            var btnStressLookUp = new Button { Text = "Look Up (+65°)", CustomMinimumSize = new Vector2(110, 28), ThemeTypeVariation = "ToolbarButton" };
            btnStressLookUp.Pressed += () => SetPhysicsStressTestPose(65f, 0f, _stressWalkBackShake);

            var btnStressCrouchUp = new Button { Text = "Crouch + Up", CustomMinimumSize = new Vector2(102, 28), ThemeTypeVariation = "ToolbarButton" };
            btnStressCrouchUp.Pressed += () => SetPhysicsStressTestPose(65f, 38f, _stressWalkBackShake);

            var btnStressLookDown = new Button { Text = "Look Down (-45°)", CustomMinimumSize = new Vector2(118, 28), ThemeTypeVariation = "ToolbarButton" };
            btnStressLookDown.Pressed += () => SetPhysicsStressTestPose(-45f, 0f, _stressWalkBackShake);

            _btnStressWalkShake = new Button
            {
                Text = "Walk Back / Shake",
                ToggleMode = true,
                ButtonPressed = false,
                CustomMinimumSize = new Vector2(128, 28),
                ThemeTypeVariation = "ToolbarButton",
                TooltipText = "Oscillates the hero forward & backward to preview hair/cloth inertia and torso collision live."
            };
            _btnStressWalkShake.Toggled += (on) =>
            {
                _stressWalkBackShake = on;
                if (!on && _stressHasSavedHeroPos && _previewHeroClone != null && GodotObject.IsInstanceValid(_previewHeroClone))
                {
                    _previewHeroClone.Position = _stressSavedHeroPos;
                    _stressHasSavedHeroPos = false;
                }
            };

            stressPresetRow.AddChild(btnStressReset);
            stressPresetRow.AddChild(btnStressLookUp);
            stressPresetRow.AddChild(btnStressCrouchUp);
            stressPresetRow.AddChild(btnStressLookDown);
            stressPresetRow.AddChild(_btnStressWalkShake);
            stressBody.AddChild(stressPresetRow);

            (_sliderStressHeadPitch, _lblValStressHeadPitch) = AddExplainedSlider(
                stressBody,
                "Head Pitch (Down ↔ Up)",
                "Pitches head (70%) + neck_0 (30%) on top of Bind Pose or playing animation.",
                -60.0, 75.0, 1.0, "F0",
                (v) =>
                {
                    _stressHeadPitchDeg = (float)v;
                    if (_lblValStressHeadPitch != null) _lblValStressHeadPitch.Text = $"{v:+0;-0;0}°";
                });
            _sliderStressHeadPitch.SetValueNoSignal(0.0);
            if (_lblValStressHeadPitch != null) _lblValStressHeadPitch.Text = "0°";

            (_sliderStressCrouchLean, _lblValStressCrouchLean) = AddExplainedSlider(
                stressBody,
                "Crouch Forward Lean",
                "Leans spine_1..3 forward to test extreme ~100° crouch + look-up folds.",
                0.0, 50.0, 1.0, "F0",
                (v) =>
                {
                    _stressCrouchLeanDeg = (float)v;
                    if (_lblValStressCrouchLean != null) _lblValStressCrouchLean.Text = $"{v:F0}°";
                });
            _sliderStressCrouchLean.SetValueNoSignal(0.0);
            if (_lblValStressCrouchLean != null) _lblValStressCrouchLean.Text = "0°";
        }

        private void SelectPaintBoneByName(string boneName)
        {
            if (_optPaintBone == null || string.IsNullOrEmpty(boneName)) return;
            for (int i = 0; i < _optPaintBone.ItemCount; i++)
            {
                if (_optPaintBone.GetItemText(i).Equals(boneName, StringComparison.OrdinalIgnoreCase))
                {
                    _optPaintBone.Select(i);
                    UpdateResetBoneButtonLabel();
                    RefreshWeightPaintHeatmap();
                    break;
                }
            }
        }

        private void OnSelBoneRemapPressed()
        {
            if (_rightTabs != null && _rightTabs.GetTabCount() > 2)
                _rightTabs.CurrentTab = 2; // 3_ Bones
            SetBonesSubView(0); // Remap
            if (!string.IsNullOrEmpty(_selectedViewportBoneName) &&
                _boneRowsByCanonical.TryGetValue(_selectedViewportBoneName, out var entry))
            {
                entry.BoneDropdown?.GrabFocus();
            }
        }

        private int PickNearestViewportBone(Vector2 clickPos, float maxPixelDist = 14.0f)
        {
            if (_previewCamera == null || _previewHeroSkeleton == null) return -1;
            int bestBone = -1;
            float bestDist = maxPixelDist;
            Transform3D skelGlobal = _previewHeroSkeleton.IsInsideTree() ? _previewHeroSkeleton.GlobalTransform : Transform3D.Identity;
            int bCount = _previewHeroSkeleton.GetBoneCount();
            int origCount = _previewHeroSkeleton.HasMeta("OriginalHeroBoneCount")
                ? _previewHeroSkeleton.GetMeta("OriginalHeroBoneCount").AsInt32()
                : bCount;
            var appendedKinds = BuildAppendedBoneColorMap();

            for (int b = 0; b < bCount; b++)
            {
                string bName = _previewHeroSkeleton.GetBoneName(b);
                if (!ClassifyBoneOverlay(bName, b, origCount, appendedKinds).Visible)
                    continue;

                Vector3 wp = skelGlobal * _previewHeroSkeleton.GetBoneGlobalPose(b).Origin;
                if (!_previewCamera.IsPositionBehind(wp))
                {
                    Vector2 sp = _previewCamera.UnprojectPosition(wp);
                    float dJoint = sp.DistanceTo(clickPos);
                    if (dJoint < bestDist)
                    {
                        bestDist = dJoint;
                        bestBone = b;
                    }
                }

                int p = _previewHeroSkeleton.GetBoneParent(b);
                if (p >= 0 && b > 0)
                {
                    Vector3 wpParent = skelGlobal * _previewHeroSkeleton.GetBoneGlobalPose(p).Origin;
                    if (!_previewCamera.IsPositionBehind(wp) && !_previewCamera.IsPositionBehind(wpParent))
                    {
                        Vector2 spChild = _previewCamera.UnprojectPosition(wp);
                        Vector2 spParent = _previewCamera.UnprojectPosition(wpParent);
                        Vector2 closestSeg = Geometry2D.GetClosestPointToSegment(clickPos, spParent, spChild);
                        float dSeg = closestSeg.DistanceTo(clickPos);
                        if (dSeg < bestDist)
                        {
                            bestDist = dSeg;
                            bestBone = b;
                        }
                    }
                }
            }
            return bestBone;
        }

        private bool TryHandleViewportBoneLeftClick(InputEvent @event)
        {
            if ((!_showBones && !_showColliders) || _previewCamera == null || _previewHeroSkeleton == null) return false;
            if (IsWeightPaintActive() || IsDeformBrushActive()) return false;
            if (@event is not InputEventMouseButton mb || mb.ButtonIndex != MouseButton.Left) return false;

            if (mb.Pressed)
            {
                _leftClickStartPos = mb.Position;
                return false;
            }

            if (mb.Position.DistanceTo(_leftClickStartPos) > 5.0f) return false;

            if (_showBones)
            {
                int bestBone = PickNearestViewportBone(mb.Position, 14.0f);
                if (bestBone >= 0)
                {
                    string bName = _previewHeroSkeleton.GetBoneName(bestBone);
                    _selectedViewportBoneName = bName;
                    var ch = FindChainForViewportBone(bName);
                    if (ch != null)
                    {
                        _selectedChain = ch;
                        RefreshClothingChainsPanel();
                    }
                    UpdateSelectedBoneBadge();
                    RedrawBoneWireframe();
                    SetBottomStatus($"Selected bone '{bName}'.", StatusTone.Info);
                    return true;
                }
            }

            if (_showColliders)
            {
                var pickedCol = PickNearestViewportCollider(mb.Position, 20.0f);
                if (pickedCol != null)
                {
                    _selectedCollider = pickedCol;
                    SetPhysicsSubView(1);
                    RefreshBodyCollidersPanel();
                    RedrawColliderWireframe();
                    SetBottomStatus($"Selected collider '{pickedCol.DisplayName}' ({pickedCol.BoneName}).", StatusTone.Info);
                    return true;
                }
            }
            return false;
        }

        private StandardMaterial3D _collidersWireMat;

        private BodyCollisionCapsuleDef PickNearestViewportCollider(Vector2 clickPos, float maxPixelDist = 20.0f)
        {
            if (_previewCamera == null || _previewHeroSkeleton == null || _bodyCollisionCapsules == null || _bodyCollisionCapsules.Count == 0)
                return null;

            Transform3D skelGlobal = _previewHeroSkeleton.IsInsideTree() ? _previewHeroSkeleton.GlobalTransform : Transform3D.Identity;
            BodyCollisionCapsuleDef best = null;
            float bestDist = maxPixelDist;

            foreach (var cap in _bodyCollisionCapsules)
            {
                if (cap == null || !cap.Enabled) continue;
                int bIdx = _previewHeroSkeleton.FindBone(cap.BoneName);
                if (bIdx < 0) continue;

                Transform3D boneWorld = skelGlobal * _previewHeroSkeleton.GetBoneGlobalPose(bIdx);
                Vector3 wp0 = boneWorld * cap.EffectivePoint0;
                Vector3 wp1 = boneWorld * cap.EffectivePoint1;
                if (_previewCamera.IsPositionBehind(wp0) && _previewCamera.IsPositionBehind(wp1)) continue;

                Vector2 sp0 = _previewCamera.UnprojectPosition(wp0);
                Vector2 sp1 = _previewCamera.UnprojectPosition(wp1);
                float d = DistancePointToSegment2D(clickPos, sp0, sp1);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = cap;
                }
            }
            return best;
        }

        private static float DistancePointToSegment2D(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float lenSq = ab.LengthSquared();
            if (lenSq < 1e-5f) return p.DistanceTo(a);
            float t = Mathf.Clamp((p - a).Dot(ab) / lenSq, 0.0f, 1.0f);
            return p.DistanceTo(a + ab * t);
        }

        private void RedrawColliderWireframe()
        {
            _collidersDirty = false;
            if (_collidersDebugMesh == null || !GodotObject.IsInstanceValid(_collidersDebugMesh)) return;
            if (!_showColliders || _previewHeroSkeleton == null || _bodyCollisionCapsules == null || _bodyCollisionCapsules.Count == 0)
            {
                _collidersDebugMesh.Mesh = null;
                return;
            }

            if (_collidersWireMat == null)
            {
                _collidersWireMat = new StandardMaterial3D
                {
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    VertexColorUseAsAlbedo = true,
                    Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                    NoDepthTest = true,
                    RenderPriority = 90
                };
            }

            Transform3D skelGlobal = _previewHeroSkeleton.IsInsideTree() ? _previewHeroSkeleton.GlobalTransform : Transform3D.Identity;
            var im = new ImmediateMesh();
            bool hasSurface = false;

            const int RingSegs = 16;
            const int ArcSegs = 8;

            foreach (var cap in _bodyCollisionCapsules)
            {
                if (cap == null) continue;
                int bIdx = _previewHeroSkeleton.FindBone(cap.BoneName);
                if (bIdx < 0) continue;

                if (!hasSurface)
                {
                    im.SurfaceBegin(Mesh.PrimitiveType.Lines);
                    hasSurface = true;
                }

                bool isSel = ReferenceEquals(cap, _selectedCollider);
                Color col = !cap.Enabled
                    ? new Color(0.48f, 0.52f, 0.58f, 0.25f)
                    : (isSel
                        ? new Color(1.0f, 0.85f, 0.22f, 0.96f)
                        : (cap.Shape == ColliderShapeKind.Box
                            ? new Color(0.98f, 0.58f, 0.20f, 0.78f)
                            : new Color(0.20f, 0.90f, 0.96f, 0.65f)));

                Transform3D boneWorld = skelGlobal * _previewHeroSkeleton.GetBoneGlobalPose(bIdx);

                void EmitLine(Vector3 a, Vector3 b)
                {
                    im.SurfaceSetColor(col);
                    im.SurfaceAddVertex(a);
                    im.SurfaceSetColor(col);
                    im.SurfaceAddVertex(b);
                }

                if (cap.Shape == ColliderShapeKind.Box)
                {
                    Basis rotLocal = Basis.FromEuler(new Vector3(
                        Mathf.DegToRad(cap.LocalRotationDeg.X),
                        Mathf.DegToRad(cap.LocalRotationDeg.Y),
                        Mathf.DegToRad(cap.LocalRotationDeg.Z)));
                    Basis boxBasis = (boneWorld.Basis.Orthonormalized() * rotLocal).Orthonormalized();
                    Vector3 boxCenter = boneWorld * cap.LocalOffsetMeters;
                    float worldScale = (boneWorld.Basis.X.Length() + boneWorld.Basis.Y.Length() + boneWorld.Basis.Z.Length()) / 3f;
                    if (worldScale < 0.01f) worldScale = 1.0f;
                    Vector3 h = cap.EffectiveBoxHalfExtents * worldScale;

                    Vector3 hx = boxBasis.X * h.X;
                    Vector3 hy = boxBasis.Y * h.Y;
                    Vector3 hz = boxBasis.Z * h.Z;

                    Vector3 c000 = boxCenter - hx - hy - hz;
                    Vector3 c100 = boxCenter + hx - hy - hz;
                    Vector3 c010 = boxCenter - hx + hy - hz;
                    Vector3 c110 = boxCenter + hx + hy - hz;
                    Vector3 c001 = boxCenter - hx - hy + hz;
                    Vector3 c101 = boxCenter + hx - hy + hz;
                    Vector3 c011 = boxCenter - hx + hy + hz;
                    Vector3 c111 = boxCenter + hx + hy + hz;

                    // 12 edges of oriented box
                    EmitLine(c000, c100); EmitLine(c010, c110); EmitLine(c001, c101); EmitLine(c011, c111);
                    EmitLine(c000, c010); EmitLine(c100, c110); EmitLine(c001, c011); EmitLine(c101, c111);
                    EmitLine(c000, c001); EmitLine(c100, c101); EmitLine(c010, c011); EmitLine(c110, c111);
                    // Diagonal X on back face (-Z) when PlanarizeBackOnly is enabled
                    if (cap.PlanarizeBackOnly)
                    {
                        EmitLine(c000, c110);
                        EmitLine(c100, c010);
                    }
                    continue;
                }

                Vector3 wp0 = boneWorld * cap.EffectivePoint0;
                Vector3 wp1 = boneWorld * cap.EffectivePoint1;
                float r0 = cap.EffectiveRadius0;
                float r1 = cap.EffectiveRadius1;

                Vector3 delta = wp1 - wp0;
                float axisLen = delta.Length();
                Vector3 axis = axisLen > 1e-4f ? delta / axisLen : Vector3.Up;
                Vector3 refVec = Mathf.Abs(axis.Dot(Vector3.Up)) < 0.92f ? Vector3.Up : Vector3.Right;
                Vector3 tangent = axis.Cross(refVec).Normalized();
                Vector3 binormal = axis.Cross(tangent).Normalized();

                void EmitRing(Vector3 center, float radius)
                {
                    for (int i = 0; i < RingSegs; i++)
                    {
                        float a0 = (i / (float)RingSegs) * Mathf.Tau;
                        float a1 = ((i + 1) / (float)RingSegs) * Mathf.Tau;
                        Vector3 pA = center + (tangent * Mathf.Cos(a0) + binormal * Mathf.Sin(a0)) * radius;
                        Vector3 pB = center + (tangent * Mathf.Cos(a1) + binormal * Mathf.Sin(a1)) * radius;
                        EmitLine(pA, pB);
                    }
                }

                // End rings & midpoint ring
                EmitRing(wp0, r0);
                if (axisLen > 1e-4f)
                {
                    EmitRing((wp0 + wp1) * 0.5f, (r0 + r1) * 0.5f);
                    EmitRing(wp1, r1);
                }

                // 6 longitudinal ribs
                for (int r = 0; r < 6; r++)
                {
                    float ang = (r / 6.0f) * Mathf.Tau;
                    Vector3 radial = tangent * Mathf.Cos(ang) + binormal * Mathf.Sin(ang);
                    EmitLine(wp0 + radial * r0, wp1 + radial * r1);
                }

                // 2 orthogonal dome meridian arcs per end cap (shows spherical rounded ends cleanly)
                for (int m = 0; m < 2; m++)
                {
                    Vector3 dir = m == 0 ? tangent : binormal;
                    for (int s = 0; s < ArcSegs; s++)
                    {
                        float t0 = (s / (float)ArcSegs) * Mathf.Pi;
                        float t1 = ((s + 1) / (float)ArcSegs) * Mathf.Pi;
                        // Cap 0 dome faces -axis
                        Vector3 c0A = wp0 + (dir * Mathf.Cos(t0) - axis * Mathf.Sin(t0)) * r0;
                        Vector3 c0B = wp0 + (dir * Mathf.Cos(t1) - axis * Mathf.Sin(t1)) * r0;
                        EmitLine(c0A, c0B);
                        // Cap 1 dome faces +axis
                        Vector3 c1A = wp1 + (dir * Mathf.Cos(t0) + axis * Mathf.Sin(t0)) * r1;
                        Vector3 c1B = wp1 + (dir * Mathf.Cos(t1) + axis * Mathf.Sin(t1)) * r1;
                        EmitLine(c1A, c1B);
                    }
                }
            }

            if (hasSurface)
            {
                im.SurfaceEnd();
                _collidersDebugMesh.Mesh = im;
                _collidersDebugMesh.Layers = 1u;
                _collidersDebugMesh.MaterialOverride = _collidersWireMat;
                _collidersDebugMesh.Visible = _showColliders;
            }
            else
            {
                _collidersDebugMesh.Mesh = null;
            }
        }

        private bool TryHandleViewportBoneRightClick(InputEvent @event)
        {
            if (!_showBones || _previewCamera == null || _previewHeroSkeleton == null) return false;
            if (@event is not InputEventMouseButton mb || mb.ButtonIndex != MouseButton.Right) return false;

            if (mb.Pressed)
            {
                _rightClickStartPos = mb.Position;
                return false;
            }

            // Only treat as bone click if mouse barely moved (not a pan drag)
            if (mb.Position.DistanceTo(_rightClickStartPos) > 5.0f) return false;

            int bestBone = PickNearestViewportBone(mb.Position, 14.0f);
            if (bestBone >= 0)
            {
                string bName = _previewHeroSkeleton.GetBoneName(bestBone);
                ToggleBoneDisabled(bName);
                UpdateSelectedBoneBadge();
                SetBottomStatus($"Toggled disabled state for bone '{bName}' (right-click).", StatusTone.Info);
                AppendConsoleMessage($"Viewport right-click toggled bone '{bName}' -> disabled={_disabledSourceBones.Contains(bName)}");
                return true;
            }
            return false;
        }

        private Dictionary<string, Color> BuildAppendedBoneColorMap()
        {
            var appendedByKind = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase);
            foreach (var ch in _boneChains)
            {
                if (ch == null) continue;
                bool isAcc = ch.Category == "Accessory" || !string.IsNullOrEmpty(ch.SlotId);
                Color c = isAcc
                    ? new Color(0.95f, 0.35f, 0.90f, 0.95f) // Magenta: Accessory chain
                    : new Color(1.0f, 0.82f, 0.18f, 0.95f); // Gold: Clothing / Jiggle chain
                foreach (string bn in ch.AppendedHeroBoneNames)
                    appendedByKind[bn] = c;
                if (!string.IsNullOrEmpty(ch.RootSourceBone) && !appendedByKind.ContainsKey(ch.RootSourceBone))
                    appendedByKind[ch.RootSourceBone] = c;
            }
            return appendedByKind;
        }

        private (Color Color, float Scale, bool Visible) ClassifyBoneOverlay(
            string bName, int boneIdx, int origCount, Dictionary<string, Color> appendedByKind)
        {
            string lower = (bName ?? "").ToLowerInvariant();
            bool isSelected = !string.IsNullOrEmpty(_selectedViewportBoneName) &&
                              bName.Equals(_selectedViewportBoneName, StringComparison.OrdinalIgnoreCase);

            if (_disabledSourceBones.Contains(bName))
                return (new Color(0.95f, 0.25f, 0.25f, 0.98f), isSelected ? 1.85f : 1.35f, _chkBoneFilterDisabled?.ButtonPressed ?? true);

            if (appendedByKind.TryGetValue(bName, out Color chCol))
            {
                bool isAccessory = chCol.R > 0.9f && chCol.B > 0.8f;
                bool vis = isAccessory ? (_chkBoneFilterAccessory?.ButtonPressed ?? true) : (_chkBoneFilterPhysics?.ButtonPressed ?? true);
                return (isSelected ? Colors.White : chCol, isSelected ? 1.85f : 1.30f, vis || isSelected);
            }

            if (boneIdx >= origCount)
                return (isSelected ? Colors.White : new Color(1.0f, 0.82f, 0.18f, 0.98f), isSelected ? 1.85f : 1.25f, (_chkBoneFilterPhysics?.ButtonPressed ?? true) || isSelected);

            // Hide vanilla hero bones not used by the custom character unless "Show Vanilla Hero" is explicitly enabled!
            bool hasCustomSet = _activeCustomWeightedBoneNames.Count > 0;
            bool isUsedByCustom = !hasCustomSet || _activeCustomWeightedBoneNames.Contains(bName);
            if (!isUsedByCustom)
            {
                bool showVanilla = _chkBoneFilterVanilla?.ButtonPressed ?? false;
                return (isSelected ? Colors.White : new Color(0.55f, 0.68f, 0.72f, 0.28f), isSelected ? 1.50f : 0.62f, showVanilla || isSelected);
            }

            if (lower.Contains("finger_") || lower.Contains("thumb") || lower.Contains("index") || lower.Contains("middle") || lower.Contains("ring") || lower.Contains("pinky"))
                return (isSelected ? Colors.White : new Color(1.0f, 0.80f, 0.25f, 0.96f), isSelected ? 1.85f : 0.78f, (_chkBoneFilterFingers?.ButtonPressed ?? true) || isSelected);

            return (isSelected ? Colors.White : new Color(0.25f, 0.88f, 0.98f, 0.96f), isSelected ? 1.85f : 1.08f, (_chkBoneFilterCore?.ButtonPressed ?? true) || isSelected);
        }

        private void RedrawBoneWireframeColorCoded()
        {
            if (_previewHeroSkeleton == null || _bonesDebugMesh == null) return;

            var im = new ImmediateMesh();
            im.SurfaceBegin(Mesh.PrimitiveType.Lines);

            int origCount = _previewHeroSkeleton.HasMeta("OriginalHeroBoneCount")
                ? _previewHeroSkeleton.GetMeta("OriginalHeroBoneCount").AsInt32()
                : _previewHeroSkeleton.GetBoneCount();

            var appendedByKind = BuildAppendedBoneColorMap();

            int bCount = _previewHeroSkeleton.GetBoneCount();
            for (int b = 1; b < bCount; b++)
            {
                int p = _previewHeroSkeleton.GetBoneParent(b);
                if (p < 0) continue;

                string bName = _previewHeroSkeleton.GetBoneName(b);
                var (col, _, vis) = ClassifyBoneOverlay(bName, b, origCount, appendedByKind);
                if (!vis) continue;

                Vector3 p0 = _previewHeroSkeleton.GetBoneGlobalPose(b).Origin;
                Vector3 p1 = _previewHeroSkeleton.GetBoneGlobalPose(p).Origin;
                im.SurfaceSetColor(col);
                im.SurfaceAddVertex(p0);
                im.SurfaceAddVertex(p1);
            }

            im.SurfaceEnd();
            if (_bonesDebugMesh.MaterialOverride is StandardMaterial3D existingMat)
            {
                existingMat.NoDepthTest = _bonesOnTop;
                existingMat.RenderPriority = _bonesOnTop ? 95 : 10;
            }
            else
            {
                _bonesDebugMesh.MaterialOverride = new StandardMaterial3D
                {
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    VertexColorUseAsAlbedo = true,
                    Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                    NoDepthTest = _bonesOnTop,
                    RenderPriority = _bonesOnTop ? 95 : 10
                };
            }
            _bonesDebugMesh.Mesh = im;

            // Render visible color-coded joint spheres at every bone origin
            if (_bonesJointPointsMesh == null || !GodotObject.IsInstanceValid(_bonesJointPointsMesh))
            {
                var dotMat = new StandardMaterial3D
                {
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    VertexColorUseAsAlbedo = true,
                    Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                    NoDepthTest = _bonesOnTop,
                    RenderPriority = _bonesOnTop ? 96 : 11
                };
                var sphere = new SphereMesh
                {
                    Radius = 0.011f,
                    Height = 0.022f,
                    RadialSegments = 8,
                    Rings = 4,
                    Material = dotMat
                };
                _bonesJointPointsMesh = new MultiMeshInstance3D
                {
                    Name = "BoneJointPointsOverlay",
                    Layers = 1u,
                    Multimesh = new MultiMesh
                    {
                        TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                        UseColors = true,
                        Mesh = sphere
                    }
                };
                _previewHeroSkeleton.AddChild(_bonesJointPointsMesh);
            }
            else if (_bonesJointPointsMesh.Multimesh?.Mesh is SphereMesh sm && sm.Material is StandardMaterial3D smMat)
            {
                smMat.NoDepthTest = _bonesOnTop;
                smMat.RenderPriority = _bonesOnTop ? 96 : 11;
            }

            if (_bonesJointPointsMesh?.Multimesh != null)
            {
                var mm = _bonesJointPointsMesh.Multimesh;
                int visCount = 0;
                for (int b = 0; b < bCount; b++)
                {
                    string bName = _previewHeroSkeleton.GetBoneName(b);
                    var (col, scale, vis) = ClassifyBoneOverlay(bName, b, origCount, appendedByKind);
                    if (!vis) continue;
                    visCount++;
                }
                mm.InstanceCount = Math.Max(visCount, 1);
                mm.VisibleInstanceCount = visCount;
                int writeIdx = 0;
                for (int b = 0; b < bCount; b++)
                {
                    string bName = _previewHeroSkeleton.GetBoneName(b);
                    var (col, scale, vis) = ClassifyBoneOverlay(bName, b, origCount, appendedByKind);
                    if (!vis) continue;
                    Vector3 origin = _previewHeroSkeleton.GetBoneGlobalPose(b).Origin;
                    Transform3D xf = new Transform3D(Basis.Identity.Scaled(new Vector3(scale, scale, scale)), origin);
                    mm.SetInstanceTransform(writeIdx, xf);
                    mm.SetInstanceColor(writeIdx, col);
                    writeIdx++;
                }
                _bonesJointPointsMesh.Visible = _showBones;
            }
        }

        private void CleanupPreviewResources()
        {
            _liveRigDebounceTimer?.Stop();
            _paintSurfaces.Clear();
            _baselineSkinOverrides.Clear();
            _deformSurfaceCaches.Clear();
            _selectedViewportBoneName = "";
            UpdateSelectedBoneBadge();

            if (_previewRiggedContainer != null && GodotObject.IsInstanceValid(_previewRiggedContainer))
            {
                _previewRiggedContainer.QueueFree();
                _previewRiggedContainer = null;
            }
            if (_bonesJointPointsMesh != null && GodotObject.IsInstanceValid(_bonesJointPointsMesh))
            {
                _bonesJointPointsMesh.QueueFree();
                _bonesJointPointsMesh = null;
            }
            if (_collidersDebugMesh != null && GodotObject.IsInstanceValid(_collidersDebugMesh))
            {
                _collidersDebugMesh.Mesh = null;
            }
            if (_previewModelInstance != null && GodotObject.IsInstanceValid(_previewModelInstance))
            {
                _previewModelInstance.QueueFree();
                _previewModelInstance = null;
            }
            if (_previewHeroClone != null && GodotObject.IsInstanceValid(_previewHeroClone))
            {
                _previewHeroClone.QueueFree();
                _previewHeroClone = null;
            }
            _previewHeroSkeleton = null;
            _previewConstraintDriver = null;
            _previewAnimPlayer = null;
            _ghostMaterial = null;
            _cachedSourceSkeleton = null;

            GC.Collect();
        }
    }
}

