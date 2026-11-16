using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using DeadlockPlayground.Animation;
using DeadlockPlayground.Catalog;
using DeadlockPlayground.Importer;
using DeadlockPlayground.Particles;
using DeadlockPlayground.Tools;
using DeadlockPlayground.UI;
using Godot;

namespace DeadlockPlayground.Tests
{
    /// <summary>
    /// Verifies:
    /// 1) ValveBiped.Bip01_Head1_06 auto-mapping on ambush.glb + Bebop without matching _end nubs.
    /// 2) Manual bone dropdown selection in CustomModelImporterModalUI surviving live preview Skeleton3D recreation.
    /// 3) AnimationStudioDock header toolbar zero-overflow HFlowContainer layout + Global/Local space toggle sync.
    /// </summary>
    public partial class BoneRemapAndDockOverflowTest : SceneTree
    {
        private int _passed;
        private int _failed;

        private void Check(bool cond, string label)
        {
            if (cond)
            {
                _passed++;
                GD.Print($"  [PASS] {label}");
            }
            else
            {
                _failed++;
                GD.PrintErr($"  [FAIL] {label}");
            }
        }

        public override void _Initialize()
        {
            _ = RunAsync();
        }

        private async Task RunAsync()
        {
            await ToSignal(this, SignalName.ProcessFrame);

            bool origLocal = GizmoDisplaySettings.GizmoUseLocalSpace;
            try
            {
                GD.Print("=== Test 1: HumanoidBoneMapper ValveBiped + ambush.glb ===");
                Check(HumanoidBoneMapper.CleanBoneName("ValveBiped.Bip01_Head1_06") == "head1",
                    "CleanBoneName('ValveBiped.Bip01_Head1_06') == 'head1'");
                Check(HumanoidBoneMapper.CleanBoneName("ValveBiped.Bip01_Head1_end_023") == "head1_end",
                    "CleanBoneName('ValveBiped.Bip01_Head1_end_023') == 'head1_end'");

                string glbPath = Path.GetFullPath(Path.Combine(OS.GetExecutablePath(), "..", "scratch_out/models/ambush.glb"));
                if (!File.Exists(glbPath))
                {
                    glbPath = Path.GetFullPath("scratch_out/models/ambush.glb");
                }
                if (!File.Exists(glbPath))
                {
                    glbPath = @"d:\GameDev\deadlock-playground\scratch_out\models\ambush.glb";
                }
                Check(File.Exists(glbPath), $"Located ambush.glb at {glbPath}");

                var gltfDoc = new GltfDocument();
                var gltfState = new GltfState();
                var err = gltfDoc.AppendFromFile(glbPath, gltfState);
                Check(err == Error.Ok, $"Parsed ambush.glb cleanly ({err})");

                var ambushRoot = gltfDoc.GenerateScene(gltfState) as Node3D;
                Check(ambushRoot != null, "Generated ambush.glb scene root");
                var ambushSkel = CustomModelAutoRigger.FindSkeleton(ambushRoot);
                Check(ambushSkel != null && ambushSkel.GetBoneCount() > 0,
                    $"Found ambush skeleton ({ambushSkel?.GetBoneCount() ?? 0} bones)");

                string vpkPath = HeroParticleService.ResolveVpkPath();
                var vpkLoader = new VpkLoaderTest { Name = "TestVpkLoader", VpkPath = vpkPath };
                Root.AddChild(vpkLoader);

                var bebopEntry = DeadlockHeroCatalog.GetByCodename("bebop");
                Check(bebopEntry != null, "Resolved Bebop entry from DeadlockHeroCatalog");

                Node3D bebopNode = await vpkLoader.LoadHeroModelAsync(bebopEntry);
                Check(bebopNode != null, "Loaded Bebop model from VPK");
                var bebopSkel = CustomModelAutoRigger.FindSkeleton(bebopNode);
                Check(bebopSkel != null, $"Found Bebop skeleton ({bebopSkel?.GetBoneCount() ?? 0} bones)");

                var autoMap = HumanoidBoneMapper.AutoMap(ambushSkel, bebopSkel);
                string mappedHeadTarget = HumanoidBoneMapper.ResolveTargetBone(
                    bebopSkel, HumanoidBoneMapper.Definitions.First(d => d.CanonicalName == "head"));
                Check(!string.IsNullOrEmpty(mappedHeadTarget), $"Bebop head target resolved to '{mappedHeadTarget}'");

                autoMap.TryGetValue(mappedHeadTarget, out string autoHeadSource);
                Check(string.Equals(autoHeadSource, "ValveBiped.Bip01_Head1_06", StringComparison.OrdinalIgnoreCase),
                    $"AutoMap mapped Bebop '{mappedHeadTarget}' -> '{autoHeadSource}' (expected 'ValveBiped.Bip01_Head1_06')");

                string[] coreBones =
                {
                    "pelvis", "spine_0", "neck_0", "head",
                    "arm_upper_L", "arm_lower_L", "hand_L",
                    "arm_upper_R", "arm_lower_R", "hand_R",
                    "leg_upper_L", "leg_lower_L", "ankle_L",
                    "leg_upper_R", "leg_lower_R", "ankle_R"
                };
                int missingCore = 0;
                foreach (string c in coreBones)
                {
                    var def = HumanoidBoneMapper.Definitions.First(d => d.CanonicalName == c);
                    string tgt = HumanoidBoneMapper.ResolveTargetBone(bebopSkel, def);
                    if (string.IsNullOrEmpty(tgt) || !autoMap.TryGetValue(tgt, out string src) || string.IsNullOrEmpty(src))
                    {
                        missingCore++;
                        GD.PrintErr($"    Unmapped core bone: {c} (target={tgt})");
                    }
                }
                Check(missingCore == 0, $"Zero unmapped core bones on ambush.glb -> Bebop (missing={missingCore})");

                // Test manual BuildHumanoidBoneMap override (even if explicit dictionary uses canonical 'head')
                var manualMap = CustomModelAutoRigger.BuildHumanoidBoneMap(ambushSkel, bebopSkel, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["head"] = "ValveBiped.Bip01_Head1_06"
                });
                int ambushHeadIdx = ambushSkel.FindBone("ValveBiped.Bip01_Head1_06");
                int bebopHeadIdx = bebopSkel.FindBone(mappedHeadTarget);
                Check(manualMap.TryGetValue(ambushHeadIdx, out int mappedIdx) && mappedIdx == bebopHeadIdx,
                    "BuildHumanoidBoneMap honors manual 'head' -> 'ValveBiped.Bip01_Head1_06'");

                GD.Print("=== Test 2: CustomModelImporterModalUI Manual Bone Selection After Live Rig Update ===");
                var modal = new CustomModelImporterModalUI();
                Root.AddChild(modal);
                await ToSignal(this, SignalName.ProcessFrame);

                // Inject preview state and simulate initial Analyze + ExecuteLiveRigUpdate (which frees the original _previewModelInstance!)
                var modalType = typeof(CustomModelImporterModalUI);
                const BindingFlags Priv = BindingFlags.Instance | BindingFlags.NonPublic;
                modalType.GetField("_liveImportedScene", Priv)?.SetValue(modal, ambushRoot);
                modalType.GetField("_previewHeroSkeleton", Priv)?.SetValue(modal, bebopSkel);
                modalType.GetField("_previewHeroClone", Priv)?.SetValue(modal, bebopNode);

                var previewRoot = new Node3D { Name = "TestPreviewModelRoot" };
                Root.AddChild(previewRoot);
                var previewModelInstance = (Node3D)ambushRoot.Duplicate();
                previewRoot.AddChild(previewModelInstance);
                modalType.GetField("_previewModelRoot", Priv)?.SetValue(modal, previewRoot);
                modalType.GetField("_previewModelInstance", Priv)?.SetValue(modal, previewModelInstance);

                modalType.GetMethod("AnalyzeModelRigStatus", Priv)?.Invoke(modal, null);

                // Simulate what ExecuteLiveRigUpdate does: QueueFree old _previewModelInstance and duplicate a fresh one
                previewModelInstance.QueueFree();
                var replacementInstance = (Node3D)ambushRoot.Duplicate();
                previewRoot.AddChild(replacementInstance);
                modalType.GetField("_previewModelInstance", Priv)?.SetValue(modal, replacementInstance);
                await ToSignal(this, SignalName.ProcessFrame);
                await ToSignal(this, SignalName.ProcessFrame);

                var userRemap = modalType.GetField("_userBoneRemap", Priv)?.GetValue(modal) as Dictionary<string, string>;
                var rowsByCanonical = modalType.GetField("_boneRowsByCanonical", Priv)?.GetValue(modal);
                var headRowObj = rowsByCanonical?.GetType().GetProperty("Item")?.GetValue(rowsByCanonical, new object[] { "head" });
                var optHead = headRowObj?.GetType().GetField("BoneDropdown")?.GetValue(headRowObj) as OptionButton;
                var lblBadge = headRowObj?.GetType().GetField("StatusBadge")?.GetValue(headRowObj) as Label;

                Check(userRemap != null && optHead != null && lblBadge != null, "Resolved Head dropdown row in CustomModelImporterModalUI");

                // Simulate user picking "[ None / Auto-Collapse ]" then picking "ValveBiped.Bip01_Head1_06" on the live dropdown
                // AFTER the original _previewModelInstance/Skeleton3D was freed!
                optHead.Select(0);
                optHead.EmitSignal(OptionButton.SignalName.ItemSelected, 0);
                await ToSignal(this, SignalName.ProcessFrame);
                Check(!userRemap.ContainsKey("head") && lblBadge.Text == "!",
                    $"Clearing Head dropdown after Skeleton3D free updated _userBoneRemap and badge ('{lblBadge.Text}')");

                int targetItemIdx = -1;
                for (int i = 0; i < optHead.ItemCount; i++)
                {
                    if (optHead.GetItemId(i) == ambushHeadIdx + 1)
                    {
                        targetItemIdx = i;
                        break;
                    }
                }
                Check(targetItemIdx > 0, $"Found ValveBiped.Bip01_Head1_06 item index ({targetItemIdx}) in Head dropdown");

                optHead.Select(targetItemIdx);
                optHead.EmitSignal(OptionButton.SignalName.ItemSelected, targetItemIdx);
                await ToSignal(this, SignalName.ProcessFrame);
                Check(userRemap.TryGetValue("head", out string updatedHead) && updatedHead == "ValveBiped.Bip01_Head1_06",
                    $"Manual Head selection updated _userBoneRemap -> '{updatedHead}' after Skeleton3D free");
                Check(lblBadge.Text == "OK",
                    $"Manual Head selection updated badge to '{lblBadge.Text}' (expected 'OK')");

                modal.QueueFree();
                previewRoot.QueueFree();

                GD.Print("=== Test 3: AnimationStudioDock Top Bar Zero Overflow & Global/Local Sync ===");
                var hostViewport = new Control { Name = "ViewportArea", Size = new Vector2(1440, 800) };
                Root.AddChild(hostViewport);
                var dockScene = GD.Load<PackedScene>("res://ui/scenes/components/AnimationStudioDock.tscn");
                Check(dockScene != null, "Loaded res://ui/scenes/components/AnimationStudioDock.tscn");

                var dock = dockScene.Instantiate<AnimationStudioDock>();
                hostViewport.AddChild(dock);
                dock.Visible = true;
                await ToSignal(this, SignalName.ProcessFrame);
                await ToSignal(this, SignalName.ProcessFrame);

                var btnToolbarSpace = dock.GetNodeOrNull<Button>("%BtnGizmoSpace");
                var btnBonePanelSpace = dock.GetNodeOrNull<Button>("%BtnBoneGizmoSpace");
                var toolbar = dock.GetNodeOrNull<Container>("%Toolbar");
                Check(toolbar is HFlowContainer, $"Toolbar is HFlowContainer ({toolbar?.GetType().Name})");
                Check(btnToolbarSpace != null && btnBonePanelSpace != null,
                    "Both %BtnGizmoSpace and %BtnBoneGizmoSpace exist inside AnimationStudioDock");

                foreach (float testW in new[] { 1440f, 1180f, 850f })
                {
                    hostViewport.Size = new Vector2(testW, 800f);
                    dock.RefreshLayout();
                    await ToSignal(this, SignalName.ProcessFrame);
                    await ToSignal(this, SignalName.ProcessFrame);

                    float dockMinX = dock.GetCombinedMinimumSize().X;
                    float dockActualW = dock.Size.X;
                    Check(dockMinX <= testW && dockActualW <= testW + 1f,
                        $"At ViewportWidth={testW}px: DockMinX={dockMinX:F1}px, DockActualW={dockActualW:F1}px (no horizontal overflow), DockH={dock.Size.Y:F1}px");
                }

                // Test Global/Local toggle sync between Toolbar, BoneQuickControlPanel, and GizmoDisplaySettings
                GizmoDisplaySettings.GizmoUseLocalSpace = false;
                btnToolbarSpace.ButtonPressed = true;
                Check(GizmoDisplaySettings.GizmoUseLocalSpace && btnToolbarSpace.Text == "Local" && btnBonePanelSpace.ButtonPressed && btnBonePanelSpace.Text == "Space: Local",
                    "Toggling Toolbar Global/Local synced GizmoDisplaySettings and BoneQuickControlPanel to Local");

                btnBonePanelSpace.ButtonPressed = false;
                Check(!GizmoDisplaySettings.GizmoUseLocalSpace && btnToolbarSpace.Text == "Global" && !btnToolbarSpace.ButtonPressed && btnBonePanelSpace.Text == "Space: Global",
                    "Toggling BoneQuickControlPanel Global/Local synced GizmoDisplaySettings and Toolbar back to Global");

                hostViewport.QueueFree();
                ambushRoot.QueueFree();

                GD.Print("=== Test 4: Auto-Rig Submesh Exclusion & Landmark Select All Bones QOL ===");
                {
                    var swordScene = new Node3D { Name = "HeroWithSword" };
                    Root.AddChild(swordScene);

                    // Build a simple symmetric humanoid silhouette ("Body") + far-out "SideSword"
                    ArrayMesh BuildBoxMesh(Vector3 center, Vector3 size)
                    {
                        var st = new SurfaceTool();
                        st.Begin(Mesh.PrimitiveType.Triangles);
                        var bm = new BoxMesh { Size = size };
                        var arrs = bm.GetMeshArrays();
                        var verts = arrs[(int)Mesh.ArrayType.Vertex].As<Vector3[]>();
                        var idx = arrs[(int)Mesh.ArrayType.Index].As<int[]>();
                        for (int i = 0; i < idx.Length; i++)
                        {
                            st.AddVertex(verts[idx[i]] + center);
                        }
                        return st.Commit();
                    }

                    // Composite humanoid body (X in [-0.45, 0.45], Y in [0.0, 1.8])
                    var bodySt = new SurfaceTool();
                    bodySt.Begin(Mesh.PrimitiveType.Triangles);
                    void AppendBox(Vector3 c, Vector3 sz)
                    {
                        var bm = new BoxMesh { Size = sz };
                        var arrs = bm.GetMeshArrays();
                        var verts = arrs[(int)Mesh.ArrayType.Vertex].As<Vector3[]>();
                        var idx = arrs[(int)Mesh.ArrayType.Index].As<int[]>();
                        for (int i = 0; i < idx.Length; i++) bodySt.AddVertex(verts[idx[i]] + c);
                    }
                    AppendBox(new Vector3(0f, 1.65f, 0f), new Vector3(0.22f, 0.25f, 0.22f)); // Head
                    AppendBox(new Vector3(0f, 1.15f, 0f), new Vector3(0.36f, 0.60f, 0.22f)); // Torso
                    AppendBox(new Vector3(-0.32f, 1.15f, 0f), new Vector3(0.24f, 0.55f, 0.14f)); // Arm R
                    AppendBox(new Vector3(0.32f, 1.15f, 0f), new Vector3(0.24f, 0.55f, 0.14f));  // Arm L
                    AppendBox(new Vector3(-0.11f, 0.42f, 0f), new Vector3(0.14f, 0.84f, 0.16f)); // Leg R
                    AppendBox(new Vector3(0.11f, 0.42f, 0f), new Vector3(0.14f, 0.84f, 0.16f));  // Leg L

                    var miBody = new MeshInstance3D { Name = "Body", Mesh = bodySt.Commit() };
                    var miSword = new MeshInstance3D { Name = "SideSword", Mesh = BuildBoxMesh(new Vector3(2.2f, 1.25f, 0f), new Vector3(1.6f, 2.5f, 0.15f)) };
                    swordScene.AddChild(miBody);
                    swordScene.AddChild(miSword);

                    var (fullMin, fullMax, fullOk) = CustomModelAutoRigger.ComputePristineModelBounds(swordScene);
                    var (bodyMin, bodyMax, bodyOk) = CustomModelAutoRigger.ComputePristineModelBounds(swordScene, new[] { "Body" });
                    Check(fullOk && fullMax.X > 2.5f, $"Full bounds include SideSword (Max.X={fullMax.X:F2})");
                    Check(bodyOk && bodyMax.X < 0.55f && bodyMax.Y <= 1.82f,
                        $"Filtered bounds exclude SideSword (Max.X={bodyMax.X:F2}, Max.Y={bodyMax.Y:F2})");

                    var qolModal = new CustomModelImporterModalUI();
                    Root.AddChild(qolModal);
                    await ToSignal(this, SignalName.ProcessFrame);

                    var qType = typeof(CustomModelImporterModalUI);
                    const BindingFlags PrivFlags = BindingFlags.Instance | BindingFlags.NonPublic;
                    qType.GetField("_liveHeroNode", PrivFlags)?.SetValue(qolModal, bebopNode);
                    qType.GetField("_liveImportedScene", PrivFlags)?.SetValue(qolModal, swordScene);
                    qType.GetField("_previewHeroClone", PrivFlags)?.SetValue(qolModal, bebopNode);
                    qType.GetField("_previewHeroSkeleton", PrivFlags)?.SetValue(qolModal, bebopSkel);

                    var qPreviewRoot = new Node3D { Name = "QolPreviewRoot" };
                    Root.AddChild(qPreviewRoot);
                    var qPreviewInstance = (Node3D)swordScene.Duplicate();
                    qPreviewRoot.AddChild(qPreviewInstance);
                    qType.GetField("_previewModelRoot", PrivFlags)?.SetValue(qolModal, qPreviewRoot);
                    qType.GetField("_previewModelInstance", PrivFlags)?.SetValue(qolModal, qPreviewInstance);

                    qType.GetMethod("PopulateSubmeshChecklists", PrivFlags)?.Invoke(qolModal, null);
                    qType.GetMethod("AutoFitToHero", PrivFlags)?.Invoke(qolModal, null);
                    qType.GetMethod("EnsureLandmarkPins", PrivFlags)?.Invoke(qolModal, new object[] { true });

                    var pinsBefore = qType.GetField("_landmarkPins", PrivFlags)?.GetValue(qolModal) as Dictionary<string, Vector3>;
                    float headYWithSword = pinsBefore != null && pinsBefore.TryGetValue("Head", out var hb) ? hb.Y : 0f;
                    var spinScaleBefore = qType.GetField("_spinScale", PrivFlags)?.GetValue(qolModal) as SpinBox;
                    float scaleWithSword = (float)(spinScaleBefore?.Value ?? 0.0);

                    // Simulate replacing _previewModelInstance during ExecuteLiveRigUpdate, then disabling SideSword in Tab 3
                    qPreviewInstance.QueueFree();
                    var qPreviewReplaced = (Node3D)swordScene.Duplicate();
                    qPreviewRoot.AddChild(qPreviewReplaced);
                    qType.GetField("_previewModelInstance", PrivFlags)?.SetValue(qolModal, qPreviewReplaced);
                    await ToSignal(this, SignalName.ProcessFrame);

                    var subChks = qType.GetField("_importedSubmeshCheckboxes", PrivFlags)?.GetValue(qolModal) as List<CheckBox>;
                    var swordChk = subChks?.FirstOrDefault(c => c.HasMeta("SubmeshName") && c.GetMeta("SubmeshName").AsString() == "SideSword");
                    Check(swordChk != null, "Found SideSword submesh checkbox in Tab 3");
                    swordChk.ButtonPressed = false;
                    await ToSignal(this, SignalName.ProcessFrame);
                    await ToSignal(this, SignalName.ProcessFrame);

                    var currentPreview = qType.GetField("_previewModelInstance", PrivFlags)?.GetValue(qolModal) as Node3D;
                    var swordPreviewNode = currentPreview?.GetNodeOrNull<MeshInstance3D>("SideSword");
                    var bodyPreviewNode = currentPreview?.GetNodeOrNull<MeshInstance3D>("Body");
                    Check(swordPreviewNode != null && !swordPreviewNode.Visible && bodyPreviewNode != null && bodyPreviewNode.Visible,
                        "Unchecking SideSword hid SideSword on active _previewModelInstance while keeping Body visible");

                    var pinsAfter = qType.GetField("_landmarkPins", PrivFlags)?.GetValue(qolModal) as Dictionary<string, Vector3>;
                    float headYAfter = pinsAfter != null && pinsAfter.TryGetValue("Head", out var ha) ? ha.Y : 0f;
                    var boundsCenterAfter = (Vector3)(qType.GetField("_accLocalBoundsCenter", PrivFlags)?.GetValue(qolModal) ?? Vector3.Zero);
                    var spinScaleAfter = qType.GetField("_spinScale", PrivFlags)?.GetValue(qolModal) as SpinBox;
                    float scaleWithoutSword = (float)(spinScaleAfter?.Value ?? 0.0);

                    Check(Math.Abs(boundsCenterAfter.X) < 0.05f,
                        $"Disabling SideSword re-centered _accLocalBoundsCenter.X to {boundsCenterAfter.X:F3}");
                    Check(headYAfter < headYWithSword - 0.3f && headYAfter <= 1.85f,
                        $"Disabling SideSword adjusted Landmark Head.Y from {headYWithSword:F3} to {headYAfter:F3}");
                    Check(scaleWithoutSword > scaleWithSword * 1.15f,
                        $"Disabling taller SideSword auto-adjusted AutoFit scale from {scaleWithSword:F3} to {scaleWithoutSword:F3}");

                    // Test 4B: Select All Bones button & Shift+Click multi-select helpers
                    var btnSelectAll = qType.GetField("_btnSelectAllPins", PrivFlags)?.GetValue(qolModal) as Button;
                    var selectedPins = qType.GetField("_selectedPins", PrivFlags)?.GetValue(qolModal) as HashSet<string>;
                    var activeBadge = qType.GetField("_lblActivePinBadge", PrivFlags)?.GetValue(qolModal) as Label;
                    Check(btnSelectAll != null && selectedPins != null && activeBadge != null,
                        "Located _btnSelectAllPins, _selectedPins, and _lblActivePinBadge in Landmark panel");

                    qType.GetMethod("ToggleSelectAllPins", PrivFlags)?.Invoke(qolModal, null);
                    Check(selectedPins.Count == CustomLandmarkWeightSolver.PinNames.Length && btnSelectAll.Text.Contains("Deselect") && activeBadge.Text.Contains("ALL 16"),
                        $"Select All Bones selected {selectedPins.Count}/16 pins (btn='{btnSelectAll.Text}', badge='{activeBadge.Text}')");

                    qType.GetMethod("ToggleSelectAllPins", PrivFlags)?.Invoke(qolModal, null);
                    Check(selectedPins.Count == 0 && btnSelectAll.Text == "Select All Bones",
                        $"Second toggle cleared selection cleanly ({selectedPins.Count} pins, btn='{btnSelectAll.Text}')");

                    qType.GetMethod("TogglePinInSelection", PrivFlags)?.Invoke(qolModal, new object[] { "ElbowL", true });
                    Check(selectedPins.Count == 2 && selectedPins.Contains("ElbowL") && selectedPins.Contains("ElbowR"),
                        "Shift+Click toggle with mirror selected both ElbowL and ElbowR");

                    qolModal.QueueFree();
                    qPreviewRoot.QueueFree();
                    swordScene.QueueFree();
                }

                // =========================================================================
                // TEST 5: Phase 1 (Kept Hero Part/Weapon Transform) & Phase 2 (Palm Roll + Hand Offsets + Persistence)
                // =========================================================================
                {
                    GD.Print("[TEST 5] Phase 1 Kept Hero Part Transform + Phase 2 Hand Roll/Offset + .dpp Round-Trip...");

                    Skeleton3D CreateHandHeroSkel(string name, float palmRollDeg)
                    {
                        var sk = new Skeleton3D { Name = name };
                        int bPelvis = sk.AddBone("pelvis");
                        sk.SetBoneRest(bPelvis, new Transform3D(Basis.Identity, new Vector3(0f, 0.95f, 0f)));

                        int bUpper = sk.AddBone("arm_upper_R");
                        sk.SetBoneParent(bUpper, bPelvis);
                        sk.SetBoneRest(bUpper, new Transform3D(Basis.Identity, new Vector3(-0.20f, 0.45f, 0f)));

                        int bLower = sk.AddBone("arm_lower_R");
                        sk.SetBoneParent(bLower, bUpper);
                        sk.SetBoneRest(bLower, new Transform3D(Basis.Identity, new Vector3(-0.28f, 0f, 0f)));

                        int bHand = sk.AddBone("hand_R");
                        sk.SetBoneParent(bHand, bLower);
                        sk.SetBoneRest(bHand, new Transform3D(Basis.Identity, new Vector3(-0.26f, 0f, 0f)));

                        float rollRad = Mathf.DegToRad(palmRollDeg);
                        Basis qRoll = new Basis(Vector3.Left, rollRad);
                        int bIdx = sk.AddBone("finger_index_0_R");
                        sk.SetBoneParent(bIdx, bHand);
                        sk.SetBoneRest(bIdx, new Transform3D(Basis.Identity, qRoll * new Vector3(-0.08f, 0.01f, 0.03f)));

                        int bPky = sk.AddBone("finger_pinky_0_R");
                        sk.SetBoneParent(bPky, bHand);
                        sk.SetBoneRest(bPky, new Transform3D(Basis.Identity, qRoll * new Vector3(-0.08f, -0.01f, -0.03f)));

                        int bWpn = sk.AddBone("weapon_hand_R");
                        sk.SetBoneParent(bWpn, bHand);
                        sk.SetBoneRest(bWpn, new Transform3D(new Basis(Vector3.Up, Mathf.DegToRad(30f)), new Vector3(-0.04f, -0.02f, 0.02f)));

                        return sk;
                    }

                    // 5A: Phase 1 Kept Hero Weapon Transform & Restoration
                    var keptHero = new Node3D { Name = "fencer" };
                    var keptSkel = CreateHandHeroSkel("HeroSkel", 0f);
                    keptHero.AddChild(keptSkel);

                    int wpnIdx = keptSkel.FindBone("weapon_hand_R");
                    int pelvisIdx = keptSkel.FindBone("pelvis");
                    Transform3D wpnRest = keptSkel.GetBoneGlobalRest(wpnIdx);

                    var swordMesh = new ArrayMesh();
                    Vector3 bladeTipOrig = wpnRest * new Vector3(0f, 0f, 0.80f);
                    Vector3 bladeBaseOrig = wpnRest.Origin;
                    Vector3 gemVertOrig = new Vector3(0f, 1.0f, 0.1f);

                    // Surface 0: sword bound to weapon_hand_R
                    var surf0Verts = new[] { bladeBaseOrig, wpnRest * new Vector3(0.05f, 0f, 0.4f), bladeTipOrig };
                    var surf0Norms = new[] { Vector3.Up, Vector3.Up, Vector3.Up };
                    var surf0Indices = new[] { 0, 1, 2 };
                    var surf0Bones = new[] { wpnIdx, 0, 0, 0, wpnIdx, 0, 0, 0, wpnIdx, 0, 0, 0 };
                    var surf0Weights = new[] { 1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f };
                    var s0Arr = new Godot.Collections.Array();
                    s0Arr.Resize((int)Mesh.ArrayType.Max);
                    s0Arr[(int)Mesh.ArrayType.Vertex] = surf0Verts;
                    s0Arr[(int)Mesh.ArrayType.Normal] = surf0Norms;
                    s0Arr[(int)Mesh.ArrayType.Index] = surf0Indices;
                    s0Arr[(int)Mesh.ArrayType.Bones] = surf0Bones;
                    s0Arr[(int)Mesh.ArrayType.Weights] = surf0Weights;
                    swordMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, s0Arr);

                    // Surface 1: control gem bound to pelvis
                    var surf1Verts = new[] { gemVertOrig, gemVertOrig + new Vector3(0.05f, 0f, 0f), gemVertOrig + new Vector3(0f, 0.05f, 0f) };
                    var surf1Norms = new[] { Vector3.Up, Vector3.Up, Vector3.Up };
                    var surf1Bones = new[] { pelvisIdx, 0, 0, 0, pelvisIdx, 0, 0, 0, pelvisIdx, 0, 0, 0 };
                    var s1Arr = new Godot.Collections.Array();
                    s1Arr.Resize((int)Mesh.ArrayType.Max);
                    s1Arr[(int)Mesh.ArrayType.Vertex] = surf1Verts;
                    s1Arr[(int)Mesh.ArrayType.Normal] = surf1Norms;
                    s1Arr[(int)Mesh.ArrayType.Index] = surf0Indices;
                    s1Arr[(int)Mesh.ArrayType.Bones] = surf1Bones;
                    s1Arr[(int)Mesh.ArrayType.Weights] = surf0Weights;
                    swordMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, s1Arr);

                    var swordMi = new MeshInstance3D { Name = "_sword_model", Mesh = swordMesh, Skeleton = new NodePath("..") };
                    keptSkel.AddChild(swordMi);
                    Root.AddChild(keptHero);

                    bool foundDomBone = CustomModelAutoRigger.FindDominantBoneForHeroKeepKey(
                        keptHero, "_sword_model::surf::0", out string domBone, out Transform3D domPivot);
                    Check(foundDomBone && domBone == "weapon_hand_R" && domPivot.Origin.DistanceTo(wpnRest.Origin) < 1e-4f,
                        $"Kept sword dominant bone resolved to '{domBone}' at weapon_hand_R pivot");

                    var keptOpts = new CustomModelAutoRigger.RiggingOptions
                    {
                        KeptOriginalPartTransforms = new Dictionary<string, CustomModelAutoRigger.KeptHeroPartTransform>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["_sword_model::surf::0"] = new CustomModelAutoRigger.KeptHeroPartTransform
                            {
                                PositionOffset = new Vector3(0.05f, 0f, 0f),
                                RotationDeg = new Vector3(0f, 0f, 45f),
                                Scale = 1.25f
                            }
                        }
                    };
                    CustomModelAutoRigger.RecomputeHeroDeformations(keptHero, keptOpts);
                    Check(swordMi.HasMeta("IsDeformedOriginalMesh") && swordMi.GetMeta("IsDeformedOriginalMesh").AsBool(),
                        "RecomputeHeroDeformations stamped IsDeformedOriginalMesh=true on repositioned kept sword");

                    var deformedS0Verts = swordMi.Mesh.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                    var deformedS0Norms = swordMi.Mesh.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Normal].AsVector3Array();
                    var untouchedS1Verts = swordMi.Mesh.SurfaceGetArrays(1)[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                    float swordTipLenBefore = (bladeTipOrig - bladeBaseOrig).Length();
                    float swordTipLenAfter = (deformedS0Verts[2] - deformedS0Verts[0]).Length();
                    Check(MathF.Abs(swordTipLenAfter - swordTipLenBefore * 1.25f) < 1e-3f &&
                          MathF.Abs(deformedS0Norms[0].Normalized().Z) <= 1f &&
                          deformedS0Norms[0].DistanceTo(Vector3.Up) > 0.2f &&
                          untouchedS1Verts[0].DistanceTo(gemVertOrig) < 1e-5f,
                        $"Kept sword scaled ({swordTipLenBefore:F3}m -> {swordTipLenAfter:F3}m), rotated normal, and left surf 1 untouched");

                    CustomModelAutoRigger.RecomputeHeroDeformations(keptHero, new CustomModelAutoRigger.RiggingOptions());
                    var restoredS0Verts = swordMi.Mesh.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                    Check(!swordMi.HasMeta("IsDeformedOriginalMesh") && restoredS0Verts[2].DistanceTo(bladeTipOrig) < 1e-5f,
                        "Resetting KeptOriginalPartTransforms restored exact original sword vertices and cleared IsDeformedOriginalMesh");

                    // 5B: Phase 2 Auto Palm Roll (Finger + PCA) & Manual Hand Offset / Forearm Smooth Twist
                    var pcaPts = new List<Vector3>();
                    for (int xi = -3; xi <= 3; xi++)
                        for (int yi = -1; yi <= 1; yi++)
                            pcaPts.Add(new Vector3(xi * 0.015f, yi * 0.005f, 0f));
                    bool pcaOk = CustomModelAutoRigger.TryComputePcaPalmWidthAxis(pcaPts, Vector3.Forward, out Vector3 pcaWidth);
                    Check(pcaOk && MathF.Abs(pcaWidth.X) > 0.99f && MathF.Abs(pcaWidth.Y) < 0.05f,
                        $"PCA palm width detector recovered lateral knuckle axis ({pcaWidth.X:F3}, {pcaWidth.Y:F3}, {pcaWidth.Z:F3})");

                    var srcRolledScene = new Node3D { Name = "CustomHandModel" };
                    var srcSkel = CreateHandHeroSkel("SrcSkel", 60f);
                    srcRolledScene.AddChild(srcSkel);
                    int srcLower = srcSkel.FindBone("arm_lower_R");
                    int srcHand = srcSkel.FindBone("hand_R");
                    Transform3D srcLowerRest = srcSkel.GetBoneGlobalRest(srcLower);
                    Transform3D srcHandRest = srcSkel.GetBoneGlobalRest(srcHand);
                    Vector3 srcPinkyPt = srcHandRest * (new Basis(Vector3.Left, Mathf.DegToRad(60f)) * new Vector3(-0.04f, 0f, -0.04f));
                    Vector3 srcElbowEdge = srcLowerRest.Origin + new Vector3(0f, 0.03f, 0.04f);

                    var handMesh = new ArrayMesh();
                    var hmVerts = new[] { srcElbowEdge, srcHandRest.Origin, srcPinkyPt };
                    var hmNorms = new[] { Vector3.Up, Vector3.Up, Vector3.Up };
                    var hmIndices = new[] { 0, 1, 2 };
                    var hmBones = new[] { srcLower, 0, 0, 0, srcHand, 0, 0, 0, srcHand, 0, 0, 0 };
                    var hmArr = new Godot.Collections.Array();
                    hmArr.Resize((int)Mesh.ArrayType.Max);
                    hmArr[(int)Mesh.ArrayType.Vertex] = hmVerts;
                    hmArr[(int)Mesh.ArrayType.Normal] = hmNorms;
                    hmArr[(int)Mesh.ArrayType.Index] = hmIndices;
                    hmArr[(int)Mesh.ArrayType.Bones] = hmBones;
                    hmArr[(int)Mesh.ArrayType.Weights] = surf0Weights;
                    handMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, hmArr);
                    var handMi = new MeshInstance3D { Name = "CustomHandMesh", Mesh = handMesh, Skeleton = new NodePath("..") };
                    srcSkel.AddChild(handMi);
                    Root.AddChild(srcRolledScene);

                    var customMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    for (int b = 0; b < srcSkel.GetBoneCount(); b++)
                    {
                        string bn = srcSkel.GetBoneName(b);
                        customMap[bn] = bn;
                    }

                    var offAutoRoll = new CustomModelAutoRigger.RiggingOptions
                    {
                        Method = CustomModelAutoRigger.RiggingMethod.PreRiggedSkeletonRemap,
                        AutoSnapArmsToHero = true,
                        AutoSnapLegsToHero = false,
                        AutoSnapHeadToHero = false,
                        AutoAlignHandOrientation = false,
                        CustomBoneRemap = customMap
                    };
                    var onAutoRoll = new CustomModelAutoRigger.RiggingOptions
                    {
                        Method = CustomModelAutoRigger.RiggingMethod.PreRiggedSkeletonRemap,
                        AutoSnapArmsToHero = true,
                        AutoSnapLegsToHero = false,
                        AutoSnapHeadToHero = false,
                        AutoAlignHandOrientation = true,
                        RightHandSnapOffsetDeg = new Vector3(0f, 0f, 30f),
                        RightHandSnapScale = 1.20f,
                        CustomBoneRemap = customMap
                    };

                    var resNoRoll = CustomModelAutoRigger.RigAndBind(srcRolledScene, keptHero, offAutoRoll);
                    Vector3 pinkyNoRoll = resNoRoll.CustomSubmeshes[0].Mesh.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex].AsVector3Array()[2];
                    Vector3 elbowNoRoll = resNoRoll.CustomSubmeshes[0].Mesh.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex].AsVector3Array()[0];

                    var resWithRoll = CustomModelAutoRigger.RigAndBind(srcRolledScene, keptHero, onAutoRoll);
                    Vector3 pinkyWithRoll = resWithRoll.CustomSubmeshes[0].Mesh.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex].AsVector3Array()[2];
                    Vector3 wristWithRoll = resWithRoll.CustomSubmeshes[0].Mesh.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex].AsVector3Array()[1];
                    Vector3 elbowWithRoll = resWithRoll.CustomSubmeshes[0].Mesh.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex].AsVector3Array()[0];

                    float radialBefore = (pinkyNoRoll - keptSkel.GetBoneGlobalRest(keptSkel.FindBone("hand_R")).Origin).Length();
                    float radialAfter = (pinkyWithRoll - wristWithRoll).Length();
                    Check(resNoRoll.Success && resWithRoll.Success &&
                          pinkyNoRoll.DistanceTo(pinkyWithRoll) > 0.02f &&
                          MathF.Abs(radialAfter - radialBefore * 1.20f) < 1e-3f &&
                          elbowNoRoll.DistanceTo(elbowWithRoll) < 1e-4f,
                        $"Auto palm roll + 30deg manual roll & 1.20x scale transformed hand ({radialBefore:F4}m -> {radialAfter:F4}m) while locking elbow ({elbowNoRoll.DistanceTo(elbowWithRoll):E2}m)");

                    // 5C: .dpp WorkspaceCustomModelEntry Persistence Round-Trip
                    var roundEntry = DeadlockPlayground.Workspace.WorkspaceCustomModelEntry.FromRiggingOptions("models/test.glb", "test.glb", new CustomModelAutoRigger.RiggingOptions
                    {
                        AutoAlignHandOrientation = false,
                        LeftHandSnapOffsetDeg = new Vector3(12f, -8f, 25f),
                        RightHandSnapOffsetDeg = new Vector3(-15f, 10f, -35f),
                        LeftHandSnapScale = 1.12f,
                        RightHandSnapScale = 0.88f,
                        KeptOriginalPartTransforms = keptOpts.KeptOriginalPartTransforms
                    });
                    string roundJson = System.Text.Json.JsonSerializer.Serialize(roundEntry);
                    var restoredEntry = System.Text.Json.JsonSerializer.Deserialize<DeadlockPlayground.Workspace.WorkspaceCustomModelEntry>(roundJson);
                    var restoredOpts = restoredEntry.ToRiggingOptions();
                    Check(!restoredOpts.AutoAlignHandOrientation &&
                          restoredOpts.LeftHandSnapOffsetDeg.DistanceTo(new Vector3(12f, -8f, 25f)) < 1e-4f &&
                          restoredOpts.RightHandSnapOffsetDeg.DistanceTo(new Vector3(-15f, 10f, -35f)) < 1e-4f &&
                          MathF.Abs(restoredOpts.LeftHandSnapScale - 1.12f) < 1e-4f &&
                          MathF.Abs(restoredOpts.RightHandSnapScale - 0.88f) < 1e-4f &&
                          restoredOpts.KeptOriginalPartTransforms != null &&
                          restoredOpts.KeptOriginalPartTransforms.TryGetValue("_sword_model::surf::0", out var rSwordXf) &&
                          MathF.Abs(rSwordXf.RotationDeg.Z - 45f) < 1e-4f &&
                          MathF.Abs(rSwordXf.Scale - 1.25f) < 1e-4f,
                        ".dpp WorkspaceCustomModelEntry JSON round-trip preserved hand snap offsets, scales, and kept-part transforms");

                    srcRolledScene.QueueFree();
                    keptHero.QueueFree();
                }
            }
            catch (Exception ex)
            {
                _failed++;
                GD.PrintErr($"[EXCEPTION] {ex}");
            }
            finally
            {
                GizmoDisplaySettings.GizmoUseLocalSpace = origLocal;
                GizmoDisplaySettings.SaveConfig();
            }

            GD.Print($"SUMMARY: {_passed} passed, {_failed} failed.");
            Quit(_failed == 0 ? 0 : 1);
        }
    }
}
