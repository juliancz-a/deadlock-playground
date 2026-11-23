using System;
using System.IO;
using System.Linq;
using Godot;
using DeadlockPlayground.Importer;
using DeadlockPlayground.Theming;

namespace DeadlockPlayground.UI
{
    public partial class CustomModelImporterModalUI
    {
        // Phase 5 & 6 Tab 5: Review, Diagnostics, Stat Tiles, Traffic-Light Header & Presets
        private Label _lblReviewSummary;
        private Label _lblReviewBoneLimits;
        private PanelContainer _trafficLightHeader;
        private Label _lblStatVerts;
        private Label _lblStatTris;
        private Label _lblStatBones;
        private Label _lblStatUnweighted;
        private VBoxContainer _boxReviewWarnings;
        private OptionButton _optPresetsList;
        private LineEdit _txtCustomPresetName;

        private Control BuildTab5ReviewAndExport()
        {
            var scroll = new ScrollContainer
            {
                Name = "5. Review & Export",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical = Control.SizeFlags.ExpandFill,
                HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
            };

            var margin = new MarginContainer();
            margin.AddThemeConstantOverride("margin_left", 10);
            margin.AddThemeConstantOverride("margin_top", 10);
            margin.AddThemeConstantOverride("margin_right", 10);
            margin.AddThemeConstantOverride("margin_bottom", 12);
            margin.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            scroll.AddChild(margin);

            var vBox = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            vBox.AddThemeConstantOverride("separation", ImporterStyle.Gap);
            margin.AddChild(vBox);

            // Traffic-Light Header (Bone Budget + Method Status)
            _trafficLightHeader = new PanelContainer
            {
                ThemeTypeVariation = "SubPanelContainer",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
            };
            var tlPad = new MarginContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            tlPad.AddThemeConstantOverride("margin_left", 14);
            tlPad.AddThemeConstantOverride("margin_top", 10);
            tlPad.AddThemeConstantOverride("margin_right", 14);
            tlPad.AddThemeConstantOverride("margin_bottom", 10);
            var tlBox = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            tlBox.AddThemeConstantOverride("separation", 4);

            _lblReviewBoneLimits = new Label
            {
                Text = "✓ Bone count check: Ready",
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
            };
            ImporterStyle.ApplyFontSize(_lblReviewBoneLimits, ImporterStyle.FontBody);
            tlBox.AddChild(_lblReviewBoneLimits);

            _lblReviewSummary = new Label
            {
                Text = "Waiting for rig update...",
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
            };
            ImporterStyle.ApplyHintStyle(_lblReviewSummary);
            _secondaryLabels.Add(_lblReviewSummary);
            tlBox.AddChild(_lblReviewSummary);

            tlPad.AddChild(tlBox);
            _trafficLightHeader.AddChild(tlPad);
            vBox.AddChild(_trafficLightHeader);

            // 4 Stat Tiles (2x2 Grid)
            var statsGrid = new GridContainer { Columns = 2, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            statsGrid.AddThemeConstantOverride("h_separation", 8);
            statsGrid.AddThemeConstantOverride("v_separation", 8);

            Label MakeStatTile(string title)
            {
                var tile = new PanelContainer { ThemeTypeVariation = "SubPanelContainer", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
                var pad = new MarginContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
                pad.AddThemeConstantOverride("margin_left", 12);
                pad.AddThemeConstantOverride("margin_right", 12);
                pad.AddThemeConstantOverride("margin_top", 8);
                pad.AddThemeConstantOverride("margin_bottom", 8);
                var col = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
                col.AddThemeConstantOverride("separation", 2);

                var lblCap = new Label { Text = title };
                ImporterStyle.ApplyHintStyle(lblCap);
                var lblVal = new Label { Text = "--" };
                ImporterStyle.ApplyFontSize(lblVal, ImporterStyle.FontSection);
                lblVal.AddThemeColorOverride("font_color", PlaygroundThemeHelper.Parchment);
                col.AddChild(lblCap);
                col.AddChild(lblVal);
                pad.AddChild(col);
                tile.AddChild(pad);
                statsGrid.AddChild(tile);
                return lblVal;
            }

            _lblStatVerts = MakeStatTile("Verts");
            _lblStatTris = MakeStatTile("Tris");
            _lblStatBones = MakeStatTile("Bones");
            _lblStatUnweighted = MakeStatTile("Unweighted");
            vBox.AddChild(statsGrid);

            // Card 2: Warnings & Diagnostics
            var (warnCard, warnBody) = CreateInspectorCard("Warnings & Diagnostics");
            vBox.AddChild(warnCard);
            _boxReviewWarnings = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _boxReviewWarnings.AddThemeConstantOverride("separation", 6);
            warnBody.AddChild(_boxReviewWarnings);

            // Card 3: Presets (Compact Save / Load Row)
            var (presetCard, presetBody) = CreateInspectorCard("Presets", "Save or load rigging, alignment, and bone settings.");
            vBox.AddChild(presetCard);

            var presetRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            presetRow.AddThemeConstantOverride("separation", 8);

            var btnSaveMenu = new MenuButton
            {
                Text = "Save ▾",
                ThemeTypeVariation = "ImporterSecondaryButton",
                CustomMinimumSize = new Vector2(96, ImporterStyle.ButtonHeight)
            };
            ImporterStyle.ApplyFontSize(btnSaveMenu, ImporterStyle.FontBody);
            var savePop = btnSaveMenu.GetPopup();
            savePop.AddItem("Save for This Model", 0);
            savePop.AddItem("Save for This Hero", 1);
            savePop.AddItem("Save Custom Name (below)", 2);
            savePop.IdPressed += (id) =>
            {
                string key = id switch
                {
                    0 => ImporterPresetStore.MakeModelPresetKey(_filePath),
                    1 => ImporterPresetStore.MakeHeroPresetKey(_heroCodename),
                    _ => ImporterPresetStore.SanitizePresetName(_txtCustomPresetName?.Text)
                };
                if (string.IsNullOrWhiteSpace(key)) return;
                if (ImporterPresetStore.SavePreset(key, GatherRiggingOptions(), _filePath))
                {
                    RefreshPresetsDropdown(key);
                    SetBottomStatus($"Saved preset '{key}'.", StatusTone.Success);
                }
            };
            presetRow.AddChild(btnSaveMenu);

            _optPresetsList = new OptionButton
            {
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(0, ImporterStyle.ButtonHeight)
            };
            ConfigureOptionButton(_optPresetsList);
            presetRow.AddChild(_optPresetsList);

            var btnLoadPreset = new Button
            {
                Text = "Load",
                ThemeTypeVariation = "ImporterSecondaryButton",
                CustomMinimumSize = new Vector2(78, ImporterStyle.ButtonHeight)
            };
            ImporterStyle.ApplyFontSize(btnLoadPreset, ImporterStyle.FontBody);
            btnLoadPreset.Pressed += () =>
            {
                if (_optPresetsList == null || _optPresetsList.Selected < 0) return;
                string key = _optPresetsList.GetItemText(_optPresetsList.Selected);
                var loaded = ImporterPresetStore.LoadPreset(key);
                if (loaded != null)
                {
                    RestoreRiggingOptions(loaded);
                    TriggerRealtimeRigUpdate();
                    SetBottomStatus($"Loaded preset '{key}'.", StatusTone.Success);
                }
            };
            presetRow.AddChild(btnLoadPreset);
            presetBody.AddChild(presetRow);

            _txtCustomPresetName = new LineEdit
            {
                PlaceholderText = "Optional custom preset name...",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
            };
            ImporterStyle.ApplyFontSize(_txtCustomPresetName, ImporterStyle.FontHint);
            presetBody.AddChild(_txtCustomPresetName);

            // Card 4: Standalone 3D Model Export (.glb) (collapsed by default or compact)
            var (exportCard, exportBody) = CreateInspectorCard(
                "Standalone 3D Export (.glb)",
                "Exports the rigged custom meshes, weights, textures, and skeleton to a portable .glb.",
                collapsible: true,
                startExpanded: false);
            vBox.AddChild(exportCard);

            var btnExportGlb = new Button
            {
                Text = "Export Rigged Model (.glb)...",
                ThemeTypeVariation = "ImporterSecondaryButton",
                CustomMinimumSize = new Vector2(0, ImporterStyle.ButtonHeight),
                TooltipText = "Writes a complete .glb containing the currently rigged meshes, materials, and skeleton (with preserved chains)."
            };
            SetButtonIcon(btnExportGlb, "res://assets/at-icons/file_arrow_down.svg", ImporterStyle.IconSize, StatusTone.Info);
            ImporterStyle.ApplyFontSize(btnExportGlb, ImporterStyle.FontBody);
            btnExportGlb.Pressed += OnExportRiggedGlbPressed;
            exportBody.AddChild(btnExportGlb);

            RefreshPresetsDropdown();
            return scroll;
        }

        private void OnExportRiggedGlbPressed()
        {
            if (_previewRiggedContainer == null || !GodotObject.IsInstanceValid(_previewRiggedContainer))
            {
                ExecuteLiveRigUpdate();
            }
            if (_previewHeroClone == null || _previewHeroSkeleton == null)
            {
                SetBottomStatus("Cannot export .glb: preview skeleton not ready.", StatusTone.Warning);
                return;
            }

            var dlg = new FileDialog
            {
                FileMode = FileDialog.FileModeEnum.SaveFile,
                Access = FileDialog.AccessEnum.Filesystem,
                Title = "Export Rigged Model (.glb)",
                UseNativeDialog = true
            };
            dlg.AddFilter("*.glb ; glTF Binary 2.0");
            string baseName = !string.IsNullOrEmpty(_filePath) ? Path.GetFileNameWithoutExtension(_filePath) : "rigged_model";
            dlg.CurrentFile = $"{baseName}_deadlock_rigged.glb";
            dlg.FileSelected += (path) =>
            {
                ExportActiveRigToGlb(path);
                dlg.QueueFree();
            };
            dlg.Canceled += () => dlg.QueueFree();
            AddChild(dlg);
            dlg.PopupCentered(new Vector2I(680, 460));
        }

        private void ExportActiveRigToGlb(string outputPath)
        {
            if (string.IsNullOrWhiteSpace(outputPath) || _previewHeroSkeleton == null) return;
            if (!outputPath.EndsWith(".glb", StringComparison.OrdinalIgnoreCase))
                outputPath += ".glb";

            var exportRoot = new Node3D { Name = "ExportRoot" };
            var skelClone = new Skeleton3D { Name = "Skeleton3D" };
            int bCount = _previewHeroSkeleton.GetBoneCount();
            for (int b = 0; b < bCount; b++)
                skelClone.AddBone(_previewHeroSkeleton.GetBoneName(b));
            for (int b = 0; b < bCount; b++)
            {
                int p = _previewHeroSkeleton.GetBoneParent(b);
                if (p >= 0) skelClone.SetBoneParent(b, p);
                skelClone.SetBoneRest(b, _previewHeroSkeleton.GetBoneRest(b));
            }
            skelClone.ResetBonePoses();
            exportRoot.AddChild(skelClone);

            var customMeshes = (_previewRiggedContainer != null && GodotObject.IsInstanceValid(_previewRiggedContainer))
                ? CustomModelAutoRigger.CollectMeshes(_previewRiggedContainer)
                : new System.Collections.Generic.List<MeshInstance3D>();

            foreach (var mi in customMeshes)
            {
                if (mi?.Mesh == null) continue;
                var miClone = new MeshInstance3D
                {
                    Name = mi.Name,
                    Mesh = (Mesh)mi.Mesh.Duplicate(false),
                    Skin = mi.Skin != null ? (Skin)mi.Skin.Duplicate() : null,
                    Transform = Transform3D.Identity
                };
                skelClone.AddChild(miClone);
                miClone.Skeleton = new NodePath("..");
            }

            if (CustomModelExporter.ExportSceneToGlb(exportRoot, outputPath, out string err))
            {
                SetBottomStatus($"Exported .glb -> {Path.GetFileName(outputPath)}", StatusTone.Success);
                AppendConsoleMessage($"Exported rigged GLB ({customMeshes.Count} submeshes, {bCount} bones) -> {outputPath}");
            }
            else
            {
                SetBottomStatus($"GLB export failed: {err}", StatusTone.Danger);
                AppendConsoleMessage($"GLB export error: {err}");
            }
            exportRoot.QueueFree();
        }

        private void RefreshPresetsDropdown(string selectKey = null)
        {
            if (_optPresetsList == null) return;
            _optPresetsList.Clear();
            var keys = ImporterPresetStore.ListPresetKeys();
            int selIdx = 0;
            for (int i = 0; i < keys.Count; i++)
            {
                _optPresetsList.AddItem(keys[i], i);
                if (!string.IsNullOrEmpty(selectKey) && keys[i].Equals(selectKey, StringComparison.OrdinalIgnoreCase))
                    selIdx = i;
            }
            if (keys.Count > 0) _optPresetsList.Select(selIdx);
        }

        private void RefreshReviewTabFromReport(ImportReport report)
        {
            if (report == null) return;
            var s = report.Stats;

            if (_lblStatVerts != null) _lblStatVerts.Text = $"{s.VertexCount:N0}";
            if (_lblStatTris != null) _lblStatTris.Text = $"{s.TriangleCount:N0}";
            if (_lblStatBones != null) _lblStatBones.Text = $"{s.TotalExportBoneCount}";
            if (_lblStatUnweighted != null) _lblStatUnweighted.Text = $"{s.UnweightedVertexCount}";

            if (_lblReviewSummary != null)
            {
                _lblReviewSummary.Text = $"{report.MethodName} · {report.EffectiveFamily} ({report.FamilyConfidence:P0}) · {s.ElapsedMs:F0} ms";
            }

            if (_lblReviewBoneLimits != null)
            {
                if (s.TotalExportBoneCount > 512)
                {
                    _lblReviewBoneLimits.Text = $"✖ {s.TotalExportBoneCount} bones exceeds 512 Source 2 budget!";
                    _lblReviewBoneLimits.AddThemeColorOverride("font_color", ThemeManager.GetColor(nameof(ThemePaletteData.SemanticDanger)));
                }
                else if (s.TotalExportBoneCount > 256)
                {
                    _lblReviewBoneLimits.Text = $"⚠ {s.TotalExportBoneCount} bones (approaching 256+ Source 2 limit)";
                    _lblReviewBoneLimits.AddThemeColorOverride("font_color", ThemeManager.GetColor(nameof(ThemePaletteData.SemanticWarning)));
                }
                else
                {
                    _lblReviewBoneLimits.Text = $"✓ {s.TotalExportBoneCount} bones within Source 2 limits";
                    _lblReviewBoneLimits.AddThemeColorOverride("font_color", ThemeManager.GetColor(nameof(ThemePaletteData.SemanticSuccess)));
                }
            }

            if (_boxReviewWarnings != null)
            {
                foreach (Node ch in _boxReviewWarnings.GetChildren()) ch.QueueFree();
                if (report.Warnings.Count == 0)
                {
                    var okLbl = new Label { Text = "✓ No warnings — rig and weights look clean." };
                    ImporterStyle.ApplyFontSize(okLbl, ImporterStyle.FontHint);
                    okLbl.AddThemeColorOverride("font_color", ThemeManager.GetColor(nameof(ThemePaletteData.SemanticSuccess)));
                    _boxReviewWarnings.AddChild(okLbl);
                }
                else
                {
                    foreach (string w in report.Warnings)
                    {
                        var wl = new Label { Text = "⚠ " + w, AutowrapMode = TextServer.AutowrapMode.WordSmart };
                        ImporterStyle.ApplyFontSize(wl, ImporterStyle.FontHint);
                        wl.AddThemeColorOverride("font_color", ThemeManager.GetColor(nameof(ThemePaletteData.SemanticWarning)));
                        _boxReviewWarnings.AddChild(wl);
                    }
                }
            }

            // Update pinned footer checklist state
            int activeMeshes = 0;
            foreach (var chk in _importedSubmeshCheckboxes)
                if (chk.ButtonPressed) activeMeshes++;
            string meshStatus = activeMeshes > 0 ? "Meshes ✓" : "Meshes ⚠";
            string warnStatus = report.Warnings.Count == 0 ? "0 warnings" : $"{report.Warnings.Count} warning(s)";
            SetRigChipStatus($"Rig ✓ · {meshStatus} · {warnStatus}",
                report.Warnings.Count == 0 ? StatusTone.Success : StatusTone.Warning);

            AppendConsoleMessage(report.BuildDetailedConsoleText());
        }
    }
}
