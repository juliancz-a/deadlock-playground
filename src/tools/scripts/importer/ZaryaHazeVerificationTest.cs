using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using DeadlockPlayground.Importer;
using DeadlockPlayground.Animation;
using DeadlockPlayground.Painter;
using DeadlockPlayground.Catalog;
using DeadlockPlayground.Theming;
using DeadlockPlayground.UI;

namespace DeadlockPlayground.Tests
{
    public partial class ZaryaHazeVerificationTest : SceneTree
    {
        public override async void _Initialize()
        {
            GD.Print("=================================================");
            GD.Print("   ZARYA -> HAZE DIAGNOSTIC & VERIFICATION TEST  ");
            GD.Print("=================================================");

            try
            {
                string vpkPath = DeadlockPlayground.Particles.HeroParticleService.ResolveVpkPath();
                GD.Print($"[Diag] VPK Path: {vpkPath}");
                var vpkLoader = new VpkLoaderTest { Name = "VpkLoaderTest", VpkPath = vpkPath };
                Root.AddChild(vpkLoader);

                var hazeEntry = DeadlockHeroCatalog.GetByCodename("haze");
                if (hazeEntry == null) throw new Exception("Haze not found in DeadlockHeroCatalog");

                Node3D heroNode = await vpkLoader.LoadHeroModelAsync(hazeEntry);
                if (heroNode == null) throw new Exception("Failed to load Haze from VPK");

                var hierarchy = new HeroMeshHierarchy();
                Root.AddChild(hierarchy);
                hierarchy.ScanHero(heroNode);

                var heroSkeleton = CustomModelAutoRigger.FindSkeleton(heroNode);
                heroSkeleton?.ResetBonePoses();

                GD.Print("\n--- HAZE AUTHENTIC MESHES & BOUNDS ---");
                var heroMeshes = CustomModelAutoRigger.CollectOriginalHeroMeshes(heroNode);
                foreach (var mi in heroMeshes)
                {
                    Transform3D relT = CustomModelAutoRigger.ComputeTransformRelativeToRoot(mi, heroNode);
                    var (mMin, mMax) = ComputeMeshBounds(mi, relT);
                    GD.Print($"  Haze Mesh '{mi.Name}': RelOrigin={relT.Origin}, Min={mMin}, Max={mMax}, H={(mMax.Y - mMin.Y):F3}m");
                }
                var (hMin, hMax, hValid) = CustomModelAutoRigger.ComputeAuthenticHeroBounds(heroNode);
                GD.Print($"[Diag] Haze ComputeAuthenticHeroBounds: Valid={hValid}, Min={hMin}, Max={hMax}, Height={(hMax.Y - hMin.Y):F3}m");

                GD.Print($"\n--- HAZE SKELETON BONES ({heroSkeleton.GetBoneCount()}) ---");
                string[] interestingHeroBones = {
                    "root_motion", "pelvis", "spine_0", "spine_1", "spine_2", "spine_3", "neck_0", "head",
                    "clavicle_L", "arm_upper_L", "arm_lower_L", "hand_L",
                    "clavicle_R", "arm_upper_R", "arm_lower_R", "hand_R",
                    "leg_upper_L", "leg_lower_L", "ankle_L", "ball_L",
                    "leg_upper_R", "leg_lower_R", "ankle_R", "ball_R",
                    "weapon_hand_L", "weapon_hand_R"
                };
                foreach (var bName in interestingHeroBones)
                {
                    int idx = heroSkeleton.FindBone(bName);
                    if (idx >= 0)
                    {
                        Vector3 pos = heroSkeleton.GetBoneGlobalRest(idx).Origin;
                        int p = heroSkeleton.GetBoneParent(idx);
                        string pName = p >= 0 ? heroSkeleton.GetBoneName(p) : "<root>";
                        GD.Print($"  [{idx:D3}] {bName,-18} parent={pName,-14} restOrigin={pos}");
                    }
                    else
                    {
                        GD.Print($"  [---] {bName,-18} MISSING");
                    }
                }

                // Load zarya_from_stalker.glb
                string zaryaPath = Path.GetFullPath(Path.Combine(ProjectSettings.GlobalizePath("res://"), "..", "scratch_out", "models", "zarya_from_stalker.glb"));
                if (!File.Exists(zaryaPath)) zaryaPath = @"d:\GameDev\deadlock-playground\scratch_out\models\zarya_from_stalker.glb";
                GD.Print($"\n[Diag] Loading: {zaryaPath}");
                var zaryaScene = CustomModelAutoRigger.LoadAndSanitize3DModelScene(zaryaPath);
                if (zaryaScene == null) throw new Exception("Failed to load zarya_from_stalker.glb");
                Root.AddChild(zaryaScene);

                GD.Print("\n--- ZARYA NODE TREE & TRANSFORMS ---");
                DumpNodeTree(zaryaScene, zaryaScene, 0);

                var (zMin, zMax, zValid) = CustomModelAutoRigger.ComputePristineModelBounds(zaryaScene);
                GD.Print($"\n[Diag] Zarya ComputePristineModelBounds: Valid={zValid}, Min={zMin}, Max={zMax}, Size={zMax - zMin}");

                var zaryaSkel = CustomModelAutoRigger.FindSkeleton(zaryaScene);
                var zaryaMeshes = CustomModelAutoRigger.CollectMeshes(zaryaScene);
                foreach (var zm in zaryaMeshes)
                {
                    GD.Print($"[Diag] ZaryaMesh '{zm.Name}': parent='{zm.GetParent()?.Name}', SkeletonPath='{zm.Skeleton}', SkinBinds={zm.Skin?.GetBindCount() ?? 0}");
                    if (zm.Skin != null && zaryaSkel != null && zm.Skin.GetBindCount() > 0)
                    {
                        Transform3D skelRel = CustomModelAutoRigger.ComputeTransformRelativeToRoot(zaryaSkel, zaryaScene);
                        for (int bi = 0; bi < Math.Min(4, zm.Skin.GetBindCount()); bi++)
                        {
                            int boneIdx = zm.Skin.GetBindBone(bi);
                            string bNm = boneIdx >= 0 ? zaryaSkel.GetBoneName(boneIdx) : zm.Skin.GetBindName(bi);
                            Transform3D ibm = zm.Skin.GetBindPose(bi);
                            Transform3D gRest = boneIdx >= 0 ? zaryaSkel.GetBoneGlobalRest(boneIdx) : Transform3D.Identity;
                            Transform3D skinInScene = skelRel * gRest * ibm;
                            GD.Print($"  Bind[{bi}] bone={boneIdx}({bNm}): skinInScene.BasisEulerDeg={skinInScene.Basis.GetEuler() * (180f / MathF.PI)}, Origin={skinInScene.Origin}");
                        }
                    }
                }
                GD.Print($"\n--- ZARYA SKELETON BONES ({zaryaSkel?.GetBoneCount() ?? 0}) ---");
                if (zaryaSkel != null)
                {
                    Transform3D skelToRoot = CustomModelAutoRigger.ComputeTransformRelativeToRoot(zaryaSkel, zaryaScene);
                    GD.Print($"  Skeleton relative transform to scene root: Origin={skelToRoot.Origin}, BasisEulerDeg={skelToRoot.Basis.GetEuler() * (180f / MathF.PI)}, Scale={skelToRoot.Basis.Scale}");
                    for (int b = 0; b < zaryaSkel.GetBoneCount(); b++)
                    {
                        string bName = zaryaSkel.GetBoneName(b);
                        int p = zaryaSkel.GetBoneParent(b);
                        string pName = p >= 0 ? zaryaSkel.GetBoneName(p) : "<root>";
                        Vector3 restLocal = zaryaSkel.GetBoneGlobalRest(b).Origin;
                        Vector3 restInScene = skelToRoot * restLocal;
                        GD.Print($"  [{b:D2}] {bName,-28} parent={pName,-26} skelRest={restLocal} | sceneRest={restInScene}");
                    }
                }

                GD.Print("\n--- HUMANOID BONE MAPPER AUTOMAP (Zarya -> Haze) ---");
                var autoMap = HumanoidBoneMapper.AutoMap(zaryaSkel, heroSkeleton);
                foreach (var def in HumanoidBoneMapper.Definitions)
                {
                    string resolved = HumanoidBoneMapper.ResolveSourceBone(zaryaSkel, def);
                    string target = HumanoidBoneMapper.ResolveTargetBone(heroSkeleton, def);
                    autoMap.TryGetValue(target ?? def.CanonicalName, out string amVal);
                    GD.Print($"  {def.CanonicalName,-22} | target={target ?? "<none>",-22} | ResolveSource={resolved ?? "<null>",-28} | AutoMap={amVal ?? "<none>"}");
                }

                GD.Print("\n--- TEST RIGPRE-RIGGED WITH AUTOSNAP=ON vs OFF ---");
                var modelSize = zMax - zMin;
                var heroHeight = hMax.Y - Math.Max(0f, hMin.Y);
                float scale = heroHeight / Math.Max(modelSize.Y, 0.001f);
                Vector3 heroCenter = (hMin + hMax) * 0.5f;
                Vector3 modelCenter = (zMin + zMax) * 0.5f;
                float posX = heroCenter.X - (-modelCenter.X * scale);
                float posZ = heroCenter.Z - (-modelCenter.Z * scale);
                float posY = 0.0f - (zMin.Y * scale);
                Transform3D autoFitTransform = new Transform3D(
                    Basis.FromEuler(new Vector3(0, Mathf.Pi, 0), EulerOrder.Yxz).Scaled(new Vector3(scale, scale, scale)),
                    new Vector3(posX, posY, posZ));
                GD.Print($"[Diag] AutoFit: scale={scale:F4}, pos=({posX:F3}, {posY:F3}, {posZ:F3}), modelH={modelSize.Y:F3}m, heroH={heroHeight:F3}m");

                var userRemap = new Dictionary<string, string>(autoMap);
                var optOn = new CustomModelAutoRigger.RiggingOptions
                {
                    Method = CustomModelAutoRigger.RiggingMethod.PreRiggedSkeletonRemap,
                    CustomBoneRemap = userRemap,
                    AutoSnapArmsToHero = true,
                    AutoSnapLegsToHero = true,
                    AutoSnapHeadToHero = true,
                    AlignedTransform = autoFitTransform
                };
                var resOn = CustomModelAutoRigger.RigAndBind((Node3D)zaryaScene.Duplicate(), heroNode, optOn);
                var (rOnMin, rOnMax, _) = CustomModelAutoRigger.ComputePristineModelBounds(resOn.ContainerNode);
                GD.Print($"[Diag] PreRigged (AutoFit, AutoSnap=ON): Success={resOn.Success}, Verts={resOn.TotalVertices}, OutMin={rOnMin}, OutMax={rOnMax}, OutSize={rOnMax - rOnMin}");

                var optOff = new CustomModelAutoRigger.RiggingOptions
                {
                    Method = CustomModelAutoRigger.RiggingMethod.PreRiggedSkeletonRemap,
                    CustomBoneRemap = userRemap,
                    AutoSnapArmsToHero = false,
                    AutoSnapLegsToHero = false,
                    AutoSnapHeadToHero = false,
                    AlignedTransform = autoFitTransform
                };
                var resOff = CustomModelAutoRigger.RigAndBind((Node3D)zaryaScene.Duplicate(), heroNode, optOff);
                var (rOffMin, rOffMax, _) = CustomModelAutoRigger.ComputePristineModelBounds(resOff.ContainerNode);
                GD.Print($"[Diag] PreRigged (AutoFit, AutoSnap=OFF): Success={resOff.Success}, Verts={resOff.TotalVertices}, OutMin={rOffMin}, OutMax={rOffMax}, OutSize={rOffMax - rOffMin}");

                // === FORMAL ASSERTIONS: AREAS 1 & 2 ===
                Assert(zValid, "Zarya ComputePristineModelBounds should succeed");
                Assert(modelSize.Y > 1.70f && modelSize.Y < 2.20f, $"Zarya upright height expected ~1.91m, got {modelSize.Y:F3}m");
                Assert(modelSize.Y > modelSize.Z * 2.0f, $"Zarya height ({modelSize.Y:F3}) must exceed Z depth ({modelSize.Z:F3})");
                Assert(heroHeight > 2.20f && heroHeight < 2.60f, $"Haze authentic height expected ~2.40m, got {heroHeight:F3}m");
                Assert(scale > 1.0f && scale < 1.5f, $"AutoFit scale expected ~1.25x, got {scale:F3}x");

                AssertEqual(autoMap["pelvis"], "bip01_pelvis_02", "pelvis mapping");
                AssertEqual(autoMap["spine_0"], "bip01_spine_011", "spine_0 mapping");
                AssertEqual(autoMap["spine_1"], "bip01_spine1_012", "spine_1 mapping");
                AssertEqual(autoMap["spine_2"], "", "spine_2 collapsed for 3-spine Biped");
                AssertEqual(autoMap["spine_3"], "bip01_spine2_013", "spine_3 top chest shift");
                AssertEqual(autoMap["neck_0"], "bip01_neck_014", "neck_0 mapping");
                AssertEqual(autoMap["head"], "bip01_head_015", "head mapping");
                AssertEqual(autoMap["arm_lower_L"], "bip01_l_forearm_022", "arm_lower_L mapping");
                AssertEqual(autoMap["arm_lower_R"], "bip01_r_forearm_035", "arm_lower_R mapping");
                AssertEqual(autoMap["ball_L"], "bip01_l_toe0_06", "ball_L mapping");
                AssertEqual(autoMap["ball_R"], "bip01_r_toe0_010", "ball_R mapping");

                var nonEmptyMapped = autoMap.Values.Where(v => !string.IsNullOrEmpty(v)).ToList();
                Assert(nonEmptyMapped.Count >= 46, $"Expected >= 46 mapped bones, got {nonEmptyMapped.Count}");
                Assert(nonEmptyMapped.Distinct(StringComparer.OrdinalIgnoreCase).Count() == nonEmptyMapped.Count,
                    "AutoMap must never assign the same source bone to multiple target slots");

                Assert(resOn.Success && resOn.TotalVertices == 2905, "PreRigged AutoSnap=ON should succeed with 2905 verts");
                Vector3 onSize = rOnMax - rOnMin;
                Assert(Math.Abs(rOnMin.Y) < 0.05f, $"AutoSnap=ON MinY should sit on floor (~0), got {rOnMin.Y:F3}");
                Assert(onSize.Y > 2.20f && onSize.Y < 2.60f, $"AutoSnap=ON upright height expected ~2.39m, got {onSize.Y:F3}m");
                Assert(onSize.X < 1.30f, $"AutoSnap=ON width should match Hero A-pose (<1.30m), got {onSize.X:F3}m");

                GD.Print("\n--- ZARYA + HAZE VISIBILITY & TOON SHADER REGRESSION CHECKS ---");
                // Re-scan hierarchy right after RigAndBind (exact 1st Confirm flow from CharacterTabUI)
                hierarchy.ScanHero(heroNode);
                hierarchy.ApplyToonShading(true);

                // Verify Haze's original _body remains hidden after ScanHero() + CleanupGeneratedSubmeshes()
                int checkedOrigHeroBodies = 0;
                foreach (var origHeroMesh in CustomModelAutoRigger.CollectOriginalHeroMeshes(heroNode))
                {
                    if (origHeroMesh.Name.ToString().Equals("_body", StringComparison.OrdinalIgnoreCase))
                    {
                        checkedOrigHeroBodies++;
                        Assert(!origHeroMesh.Visible, "Haze original '_body' must stay hidden after 1st Confirm + ScanHero");
                        Assert(origHeroMesh.HasMeta("UserHiddenByCustomModel"), "'_body' must retain UserHiddenByCustomModel metadata");
                    }
                }
                Assert(checkedOrigHeroBodies >= 1, "Expected at least one '_body' mesh on Haze");

                // Verify no orphaned visible _body_surf_* nodes remain in the scene tree
                var bodySurfNodes = new List<MeshInstance3D>();
                CollectNodesOfType(heroNode, bodySurfNodes);
                foreach (var m in bodySurfNodes)
                {
                    if (m.Name.ToString().Contains("_body_surf_", StringComparison.OrdinalIgnoreCase))
                    {
                        Assert(!m.Visible, $"Split submesh '{m.Name}' must not remain visible when parent '_body' is hidden");
                    }
                }

                // Verify custom model Toon materials bind has_metallic_texture & shader never treats metallic=1.0 as floor
                int verifiedCustomSubs = 0;
                foreach (var sub in hierarchy.Submeshes)
                {
                    if (!sub.IsCustomImportedMesh) continue;
                    verifiedCustomSubs++;
                    Assert(sub.ToonMaterial != null, $"Custom submesh '{sub.RawName}' must have a ToonMaterial");
                    if (sub.OriginalMaterial is StandardMaterial3D origStd && origStd.MetallicTexture != null)
                    {
                        bool hasMetalTex = sub.ToonMaterial.GetShaderParameter("has_metallic_texture").AsBool();
                        var boundMetalTex = sub.ToonMaterial.GetShaderParameter("metallic_texture").AsGodotObject();
                        Assert(hasMetalTex && boundMetalTex != null,
                            $"Custom submesh '{sub.RawName}' with MetallicTexture must bind metallic_texture and has_metallic_texture=true");
                    }
                }
                Assert(verifiedCustomSubs >= 2, $"Expected >= 2 custom Zarya submeshes scanned, got {verifiedCustomSubs}");

                string toonShaderCode = File.ReadAllText(ProjectSettings.GlobalizePath("res://assets/shaders/toon_shader.gdshader"));
                Assert(toonShaderCode.Contains("has_metallic_texture ? (metallic * metal_tex_val) : metallic", StringComparison.Ordinal),
                    "toon_shader.gdshader must multiply metallic factor by metallic_texture instead of max()");
                Assert(!toonShaderCode.Contains("METALLIC = max(metallic, metal_tex_val);", StringComparison.Ordinal),
                    "toon_shader.gdshader must not contain legacy max(metallic, metal_tex_val)");

                // === FORMAL ASSERTIONS: CHARACTER TAB LAYOUT ===
                var charTabScene = GD.Load<PackedScene>("res://ui/scenes/tabs/CharacterTab.tscn");
                Assert(charTabScene != null, "CharacterTab.tscn must load");
                var charTab = charTabScene.Instantiate();
                var actionsRow = charTab.FindChild("CustomModelActionsRow", true, false);
                Assert(actionsRow is VBoxContainer, "CustomModelActionsRow must be a VBoxContainer so Edit/Reset stack on separate lines");
                var btnEditCustom = charTab.FindChild("BtnEditCustomModel", true, false) as Button;
                var btnResetCustom = charTab.FindChild("BtnResetCustomModel", true, false) as Button;
                Assert(btnEditCustom != null && !btnEditCustom.ClipText, "BtnEditCustomModel must not clip text");
                Assert(btnResetCustom != null && !btnResetCustom.ClipText, "BtnResetCustomModel must not clip text");
                charTab.QueueFree();

                // === FORMAL ASSERTIONS: AREAS 3 & 4 (UI MODAL LAYOUT, ACCORDIONS, THEMING) ===
                GD.Print("\n--- MODAL UI LAYOUT, TAB 2 ACCORDIONS & THEME CONTRAST CHECKS ---");
                var themeMgr = new ThemeManager();
                Root.AddChild(themeMgr);
                themeMgr.ApplyProfile(ThemeProfile.CreateLightPreset(), saveAsActive: false);

                var modal = new CustomModelImporterModalUI();
                Root.AddChild(modal);
                await ToSignal(this, SceneTree.SignalName.ProcessFrame);
                modal.Open(heroNode, zaryaScene, zaryaPath);
                await ToSignal(this, SceneTree.SignalName.ProcessFrame);

                var modalWin = modal.FindChild("ModalWindow", true, false) as PanelContainer;
                Assert(modalWin != null, "ModalWindow must exist");
                Vector2 minSize = modalWin.GetCombinedMinimumSize();
                GD.Print($"[Diag] ModalWindow CombinedMinimumSize = {minSize}");
                Assert(minSize.X <= 1280f, $"Modal min width ({minSize.X}px) must fit <= 1280px (and 1920px) with zero horizontal overflow!");

                // Verify ALL OptionButtons inside modal clip text properly
                var allOptButtons = new List<OptionButton>();
                CollectNodesOfType(modal, allOptButtons);
                Assert(allOptButtons.Count > 20, $"Expected >20 OptionButtons, got {allOptButtons.Count}");
                foreach (var opt in allOptButtons)
                {
                    Assert(!opt.FitToLongestItem && opt.ClipText,
                        $"OptionButton '{opt.Name}' ({opt.Text}) must have FitToLongestItem=false and ClipText=true");
                }

                // Verify Tab 2 categorized accordions & finger collapse/search behavior
                var leftFingersTitle = FindLabelByText(modal, "Left Fingers");
                Assert(leftFingersTitle != null, "Left Fingers accordion section must exist in Tab 2");
                var leftFingersBody = FindSiblingBodyBox(leftFingersTitle);
                Assert(leftFingersBody != null && !leftFingersBody.Visible, "Left Fingers section should be collapsed by default");

                var searchEdit = FindFirstNodeOfType<LineEdit>(modal, l => l.PlaceholderText.Contains("Search"));
                Assert(searchEdit != null, "Tab 2 Bone Search LineEdit must exist");
                searchEdit.Text = "finger_thumb_0_L";
                searchEdit.EmitSignal(LineEdit.SignalName.TextChanged, searchEdit.Text);
                await ToSignal(this, SceneTree.SignalName.ProcessFrame);
                Assert(leftFingersBody.Visible, "Searching for a finger bone must expand Left Fingers accordion automatically");

                searchEdit.Text = "";
                searchEdit.EmitSignal(LineEdit.SignalName.TextChanged, "");
                await ToSignal(this, SceneTree.SignalName.ProcessFrame);
                Assert(!leftFingersBody.Visible, "Clearing search should restore Left Fingers collapsed state");

                // Verify AutoMap / Mirror / Clear buttons
                var btnAutoMap = FindButtonByText(modal, "Auto-Map Bones");
                var btnMirror = FindButtonByText(modal, "Mirror L <-> R");
                var btnClear = FindButtonByText(modal, "Clear Unmatched");
                Assert(btnAutoMap != null && btnMirror != null && btnClear != null, "Tab 2 convenience buttons must exist");
                btnAutoMap.EmitSignal(BaseButton.SignalName.Pressed);
                btnMirror.EmitSignal(BaseButton.SignalName.Pressed);
                btnClear.EmitSignal(BaseButton.SignalName.Pressed);

                // Verify Light, Default, and Night theme contrast & styling
                var btnConfirm = FindButtonByText(modal, "Confirm & Apply to Hero");
                var btnBones = FindButtonByText(modal, "Bones");
                var lblBadge = FindLabelByTextContains(modal, "Pre-Rigged Skeleton");
                Assert(btnConfirm != null && btnBones != null && lblBadge != null, "Confirm, Bones, and RigStatusBadge must exist");

                foreach (var profile in new[] { ThemeProfile.CreateLightPreset(), ThemeProfile.CreateDefaultPreset(), ThemeProfile.CreateNightPreset() })
                {
                    themeMgr.ApplyProfile(profile, saveAsActive: false);
                    await ToSignal(this, SceneTree.SignalName.ProcessFrame);

                    var mTheme = modalWin.Theme;
                    Assert(mTheme != null, $"Modal theme must be assigned under {profile.Name}");
                    var panelBg = (mTheme.GetStylebox("panel", "ModalPanel") as StyleBoxFlat)?.BgColor ?? Colors.Black;
                    Color badgeCol = lblBadge.GetThemeColor("font_color");
                    double badgeContrast = ThemeProfile.ComputeContrastRatio(badgeCol, panelBg);
                    GD.Print($"[Diag] Theme='{profile.Name}' | IconMaxW(Confirm)={btnConfirm.GetThemeConstant("icon_max_width")} | BadgeContrast={badgeContrast:F2}:1");
                    Assert(badgeContrast >= 3.5, $"RigStatusBadge contrast too low in {profile.Name}: {badgeContrast:F2}:1");

                    var confirmBg = (mTheme.GetStylebox("normal", "PrimaryActionButton") as StyleBoxFlat)?.BgColor ?? Colors.Black;
                    var confirmFg = mTheme.GetColor("font_color", "PrimaryActionButton");
                    double confirmContrast = ThemeProfile.ComputeContrastRatio(confirmFg, confirmBg);
                    Assert(confirmContrast >= 4.2, $"PrimaryActionButton contrast too low in {profile.Name}: {confirmContrast:F2}:1");

                    var tbBg = (mTheme.GetStylebox("normal", "ToolbarButton") as StyleBoxFlat)?.BgColor ?? Colors.Black;
                    var tbFg = mTheme.GetColor("font_color", "ToolbarButton");
                    double tbContrast = ThemeProfile.ComputeContrastRatio(tbFg, tbBg);
                    Assert(tbContrast >= 4.5, $"ToolbarButton contrast too low in {profile.Name}: {tbContrast:F2}:1");

                    Assert(btnConfirm.GetThemeConstant("icon_max_width") >= 22, "Button icons must scale >= 22px");
                }

                // === FORMAL ASSERTIONS: EXPORT MOD DIALOG & PROGRESS DIALOG THEMING ===
                GD.Print("\n--- EXPORT MOD & PROGRESS DIALOG THEME CHECKS ---");
                var exportDialogScene = GD.Load<PackedScene>("res://ui/scenes/modals/ExportModDialog.tscn");
                var exportProgressScene = GD.Load<PackedScene>("res://ui/scenes/modals/ExportProgressDialog.tscn");
                Assert(exportDialogScene != null && exportProgressScene != null, "Export dialog scenes must load");

                var exportDlg = exportDialogScene.Instantiate<ExportModDialog>();
                var progressDlg = exportProgressScene.Instantiate<ExportProgressDialog>();
                exportDlg.Visible = false;
                progressDlg.Visible = false;
                Root.AddChild(exportDlg);
                Root.AddChild(progressDlg);
                await ToSignal(this, SceneTree.SignalName.ProcessFrame);

                var expPanel = exportDlg.FindChild("DialogPanel", true, false) as PanelContainer;
                var progPanel = progressDlg.FindChild("DialogPanel", true, false) as PanelContainer;
                var expVpkLbl = exportDlg.FindChild("LblTargetVpkInfo", true, false) as Label;
                var expCompLbl = exportDlg.FindChild("LblCompilerStatus", true, false) as Label;
                var progStepLbl = progressDlg.FindChild("LblStepStatus", true, false) as Label;
                var progConsole = progressDlg.FindChild("LogConsole", true, false) as RichTextLabel;
                Assert(expPanel != null && progPanel != null && expVpkLbl != null && expCompLbl != null && progStepLbl != null && progConsole != null,
                    "Export modal child nodes must exist");

                foreach (var profile in new[] { ThemeProfile.CreateLightPreset(), ThemeProfile.CreateDefaultPreset(), ThemeProfile.CreateNightPreset() })
                {
                    themeMgr.ApplyProfile(profile, saveAsActive: false);
                    await ToSignal(this, SceneTree.SignalName.ProcessFrame);

                    var expBg = (expPanel.Theme?.GetStylebox("panel", "ModalPanel") as StyleBoxFlat)?.BgColor ?? Colors.Black;
                    var progBg = (progPanel.Theme?.GetStylebox("panel", "ModalPanel") as StyleBoxFlat)?.BgColor ?? Colors.Black;
                    if (profile.PresetId == ThemeProfile.PresetLight)
                    {
                        Assert(ThemeProfile.ComputeRelativeLuminance(expBg) > 0.6, "ExportModDialog panel must be bright in Light theme");
                        Assert(ThemeProfile.ComputeRelativeLuminance(progBg) > 0.6, "ExportProgressDialog panel must be bright in Light theme");
                    }

                    double vpkContrast = ThemeProfile.ComputeContrastRatio(expVpkLbl.GetThemeColor("font_color"), expBg);
                    double compContrast = ThemeProfile.ComputeContrastRatio(expCompLbl.GetThemeColor("font_color"), expBg);
                    double stepContrast = ThemeProfile.ComputeContrastRatio(progStepLbl.GetThemeColor("font_color"), progBg);
                    var consoleBox = progConsole.GetThemeStylebox("normal") as StyleBoxFlat;
                    Color consoleBg = consoleBox?.BgColor ?? progBg;
                    double consoleContrast = ThemeProfile.ComputeContrastRatio(progConsole.GetThemeColor("default_color"), consoleBg);

                    GD.Print($"[Diag] ExportModal '{profile.Name}' contrasts: vpk={vpkContrast:F2}, comp={compContrast:F2}, step={stepContrast:F2}, console={consoleContrast:F2}");
                    Assert(vpkContrast >= 3.0 && compContrast >= 3.0 && stepContrast >= 3.5 && consoleContrast >= 4.5,
                        $"Export dialog text contrast too low in {profile.Name}");
                }
                exportDlg.QueueFree();
                progressDlg.QueueFree();

                // === FORMAL ASSERTIONS: SKIN EXPORTER EMPTY SOURCE MOD PATH GUARD ===
                string skinExporterCode = File.ReadAllText(ProjectSettings.GlobalizePath("res://tools/scripts/painter/SkinExporter.cs"));
                Assert(skinExporterCode.Contains("!string.IsNullOrWhiteSpace(config.SourceModVpkPath) &&", StringComparison.Ordinal) &&
                       !skinExporterCode.Contains("Path.GetFullPath(config.SourceModVpkPath ?? \"\")", StringComparison.Ordinal),
                    "SkinExporter.cs must guard null/empty SourceModVpkPath before calling Path.GetFullPath");

                // === FORMAL ASSERTIONS: SUBMESH CHECKLIST DISPOSAL SAFETY (ISSUE #6) ===
                var submeshChks = new List<CheckBox>();
                CollectNodesOfType(modal.FindChild("BoxImportedSubmeshes", true, false) ?? modal, submeshChks);
                var outlineChks = submeshChks.Where(c => c.Text == "Outline Hull").ToList();
                Assert(outlineChks.Count > 0, "Expected Outline Hull checkboxes in Tab 3");
                // Dispose the preview meshes inside the modal, then toggle Outline Hull -> must NOT throw ObjectDisposedException
                var previewSubViewport = modal.FindChild("SubViewport", true, false);
                var previewMeshes = new List<MeshInstance3D>();
                if (previewSubViewport != null) CollectNodesOfType(previewSubViewport, previewMeshes);
                foreach (var pm in previewMeshes)
                {
                    if (pm.Name.ToString().Contains("Object_67", StringComparison.OrdinalIgnoreCase) ||
                        pm.Name.ToString().Contains("Object_69", StringComparison.OrdinalIgnoreCase))
                    {
                        pm.QueueFree();
                    }
                }
                await ToSignal(this, SceneTree.SignalName.ProcessFrame);
                foreach (var ochk in outlineChks)
                {
                    ochk.ButtonPressed = !ochk.ButtonPressed;
                }
                await ToSignal(this, SceneTree.SignalName.ProcessFrame);

                GD.Print("\n[PASS] ALL ZARYA -> HAZE & MODAL UI VERIFICATION CHECKS PASSED!");
                modal.QueueFree();
                Quit(0);
                return;
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[FAIL] VERIFICATION FAILED: {ex}");
                Quit(1);
                return;
            }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new Exception($"Assertion failed: {message}");
        }

        private static void AssertEqual(string actual, string expected, string label)
        {
            if (!string.Equals(actual ?? "", expected ?? "", StringComparison.OrdinalIgnoreCase))
            {
                throw new Exception($"Assertion failed [{label}]: expected '{expected}', got '{actual}'");
            }
        }

        private static void CollectNodesOfType<T>(Node root, List<T> results) where T : Node
        {
            if (root is T match) results.Add(match);
            foreach (Node ch in root.GetChildren()) CollectNodesOfType(ch, results);
        }

        private static T FindFirstNodeOfType<T>(Node root, Func<T, bool> predicate) where T : Node
        {
            if (root is T match && predicate(match)) return match;
            foreach (Node ch in root.GetChildren())
            {
                var found = FindFirstNodeOfType(ch, predicate);
                if (found != null) return found;
            }
            return null;
        }

        private static Label FindLabelByText(Node root, string exactText) =>
            FindFirstNodeOfType<Label>(root, l => string.Equals(l.Text, exactText, StringComparison.Ordinal));

        private static Label FindLabelByTextContains(Node root, string substr) =>
            FindFirstNodeOfType<Label>(root, l => l.Text != null && l.Text.Contains(substr, StringComparison.OrdinalIgnoreCase));

        private static Button FindButtonByText(Node root, string exactText) =>
            FindFirstNodeOfType<Button>(root, b => string.Equals(b.Text, exactText, StringComparison.Ordinal));

        private static VBoxContainer FindSiblingBodyBox(Label titleLabel)
        {
            Node p = titleLabel?.GetParent()?.GetParent()?.GetParent(); // HeaderBtn -> CardVBox
            Node cardVBox = p?.GetParent();
            if (cardVBox == null) return null;
            foreach (Node ch in cardVBox.GetChildren())
            {
                if (ch is VBoxContainer vb) return vb;
            }
            return null;
        }

        private static (Vector3 Min, Vector3 Max) ComputeMeshBounds(MeshInstance3D mi, Transform3D relT)
        {
            Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            if (mi.Mesh == null) return (Vector3.Zero, Vector3.Zero);
            for (int s = 0; s < mi.Mesh.GetSurfaceCount(); s++)
            {
                var arr = mi.Mesh.SurfaceGetArrays(s);
                var verts = arr[(int)Mesh.ArrayType.Vertex].As<Vector3[]>();
                if (verts == null) continue;
                foreach (var v in verts)
                {
                    Vector3 w = relT * v;
                    min = min.Min(w);
                    max = max.Max(w);
                }
            }
            return (min, max);
        }

        private static void DumpNodeTree(Node node, Node3D root, int depth)
        {
            string indent = new string(' ', depth * 2);
            if (node is Node3D n3d)
            {
                Transform3D rel = CustomModelAutoRigger.ComputeTransformRelativeToRoot(n3d, root);
                Vector3 eulerDeg = n3d.Transform.Basis.GetEuler() * (180f / MathF.PI);
                Vector3 relEulerDeg = rel.Basis.GetEuler() * (180f / MathF.PI);
                string extra = "";
                if (node is MeshInstance3D mi && mi.Mesh != null)
                {
                    var (mMin, mMax) = ComputeMeshBounds(mi, Transform3D.Identity);
                    var (wMin, wMax) = ComputeMeshBounds(mi, rel);
                    extra = $" | MeshRaw=[{mMin}..{mMax}] | RelBounds=[{wMin}..{wMax}] | Skin={(mi.Skin != null ? mi.Skin.GetBindCount().ToString() : "null")} | SkelPath={mi.Skeleton}";
                }
                GD.Print($"{indent}- {node.Name} ({node.GetType().Name}) LocalT={n3d.Transform.Origin} LocalRot={eulerDeg} LocalS={n3d.Transform.Basis.Scale} | RelRot={relEulerDeg} RelS={rel.Basis.Scale}{extra}");
            }
            else
            {
                GD.Print($"{indent}- {node.Name} ({node.GetType().Name})");
            }
            if (node is Skeleton3D skel)
            {
                foreach (Node ch in node.GetChildren())
                    DumpNodeTree(ch, root, depth + 1);
                return;
            }
            foreach (Node ch in node.GetChildren())
            {
                DumpNodeTree(ch, root, depth + 1);
            }
        }
    }
}
