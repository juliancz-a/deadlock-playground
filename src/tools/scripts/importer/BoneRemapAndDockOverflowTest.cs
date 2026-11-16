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

                // =========================================================================
                // TEST 6: Apollo (fencer) Orphaned Vanilla Mesh Leak, Material Cleanup & Paint Pose Toggle
                // =========================================================================
                {
                    GD.Print("[TEST 6] Apollo (fencer) Reset -> Re-import Leak, BindPose Basis & PaintTabUI Pose Toggle...");

                    var fencerEntry = DeadlockHeroCatalog.GetByCodename("fencer");
                    Check(fencerEntry != null, "Resolved Apollo (fencer) entry from DeadlockHeroCatalog");

                    Node3D fencerNode = await vpkLoader.LoadHeroModelAsync(fencerEntry);
                    Check(fencerNode != null && vpkLoader.CurrentVrfModel != null,
                        "Loaded Apollo (fencer) from VPK and exposed VpkLoaderTest.CurrentVrfModel");

                    var fencerSkel = CustomModelAutoRigger.FindSkeleton(fencerNode);
                    Check(fencerSkel != null, $"Found Apollo skeleton ({fencerSkel?.GetBoneCount() ?? 0} bones)");

                    // 6A: Verify GetOrAddSkinBindIndex repairs Basis rotation mismatch even when Origin matches
                    var testSkin = new Skin();
                    int armUpperR = fencerSkel.FindBone("arm_upper_R");
                    Transform3D expectedRest = fencerSkel.GetBoneGlobalRest(armUpperR);
                    Transform3D rotatedSameOrigin = new Transform3D(
                        expectedRest.Basis * new Basis(Vector3.Up, Mathf.DegToRad(40f)),
                        expectedRest.Origin);
                    testSkin.AddBind(armUpperR, rotatedSameOrigin.AffineInverse());
                    int repairedIdx = CustomModelAutoRigger.GetOrAddSkinBindIndex(testSkin, fencerSkel, armUpperR);
                    Quaternion qRepaired = testSkin.GetBindPose(repairedIdx).AffineInverse().Basis.Orthonormalized().GetRotationQuaternion();
                    Quaternion qExpected = expectedRest.Basis.Orthonormalized().GetRotationQuaternion();
                    Check(qRepaired.AngleTo(qExpected) < 1e-3f,
                        $"GetOrAddSkinBindIndex repaired 40deg basis mismatch on same-origin bind pose (err={qRepaired.AngleTo(qExpected):E2}rad)");

                    // 6B: Import -> Paint/Hierarchy Scan -> Reset -> Re-import on real Apollo model
                    var dummyCustom = new Node3D { Name = "DummyApolloImport" };
                    var dummyBox = new MeshInstance3D { Name = "CustomChest", Mesh = new BoxMesh { Size = new Vector3(0.3f, 0.5f, 0.2f) } };
                    dummyCustom.AddChild(dummyBox);
                    Root.AddChild(dummyCustom);

                    var impOpts = new CustomModelAutoRigger.RiggingOptions
                    {
                        Method = CustomModelAutoRigger.RiggingMethod.RigidSingleBone,
                        RigidBoneName = "spine_3",
                        Mode = CustomModelAutoRigger.ReplaceMode.FullReplacement
                    };

                    var firstImp = CustomModelAutoRigger.RigAndBind(dummyCustom, fencerNode, impOpts);
                    Check(firstImp.Success, "First RigidSingleBone import onto Apollo succeeded");

                    var hierarchy = new DeadlockPlayground.Painter.HeroMeshHierarchy { Name = "TestHeroMeshHierarchy" };
                    Root.AddChild(hierarchy);
                    hierarchy.ScanHero(fencerNode);

                    // Simulate active MaterialOverlay on a vanilla node prior to reset
                    var origMeshes = CustomModelAutoRigger.CollectOriginalHeroMeshes(fencerNode);
                    var dummyOverlayMat = new StandardMaterial3D { AlbedoColor = Colors.Magenta };
                    foreach (var om in origMeshes) om.MaterialOverlay = dummyOverlayMat;

                    // Reset Custom Model + Clear Paint Hierarchy (what CharacterTabUI.OnResetCustomModelPressed does)
                    CustomModelAutoRigger.RemoveCustomModelAndRestoreHero(fencerNode, includeAccessories: false);
                    hierarchy.ScanHero(fencerNode);
                    hierarchy.ScanHero(null);
                    await ToSignal(this, SignalName.ProcessFrame);

                    int orphanSplitNodes = fencerNode.FindChildren("*", "MeshInstance3D", true, false)
                        .OfType<MeshInstance3D>()
                        .Count(m => GodotObject.IsInstanceValid(m) && m.HasMeta("ParentCompositeMesh"));
                    int staleOverlays = origMeshes.Count(om => GodotObject.IsInstanceValid(om) && om.MaterialOverlay != null);
                    Check(orphanSplitNodes == 0 && staleOverlays == 0 && origMeshes.All(om => !GodotObject.IsInstanceValid(om) || om.Visible),
                        $"Reset to Original Apollo left 0 orphaned _surf_* nodes ({orphanSplitNodes}), 0 stale overlays ({staleOverlays}), and all vanilla meshes visible");

                    // Re-import and re-scan: ensure ZERO vanilla Apollo meshes stay stuck visible!
                    var secondImp = CustomModelAutoRigger.RigAndBind(dummyCustom, fencerNode, impOpts);
                    hierarchy.ScanHero(fencerNode);
                    int visibleVanillaAfterReimport = CustomModelAutoRigger.CollectMeshes(fencerNode)
                        .Count(m => GodotObject.IsInstanceValid(m) &&
                                    m.Visible &&
                                    (!m.HasMeta("IsCustomImportedMesh") || !m.GetMeta("IsCustomImportedMesh").AsBool()) &&
                                    m.GetParent()?.Name != "Custom_Rigged_Character");
                    int visibleCustomAfterReimport = CustomModelAutoRigger.CollectMeshes(fencerNode)
                        .Count(m => GodotObject.IsInstanceValid(m) && m.Visible && m.HasMeta("IsCustomImportedMesh") && m.GetMeta("IsCustomImportedMesh").AsBool());
                    Check(secondImp.Success && visibleVanillaAfterReimport == 0 && visibleCustomAfterReimport >= 1,
                        $"Re-importing after Reset hid all vanilla Apollo geometry (visibleVanilla={visibleVanillaAfterReimport}, visibleCustom={visibleCustomAfterReimport})");

                    // 6C: PaintTabUI Pose Toggle (Keep Current Pose vs Bind A-Pose)
                    var paintTabScene = GD.Load<PackedScene>("res://ui/scenes/tabs/PaintTab.tscn");
                    var paintTab = paintTabScene.Instantiate<PaintTabUI>();
                    Root.AddChild(paintTab);
                    await ToSignal(this, SignalName.ProcessFrame);

                    // Put Apollo arm_upper_R into simulated idle rotation (forward +35 deg)
                    int armUpperL = fencerSkel.FindBone("arm_upper_L");
                    Quaternion idleRotR = new Quaternion(Vector3.Up, Mathf.DegToRad(35f)) * fencerSkel.GetBonePoseRotation(armUpperR);
                    fencerSkel.SetBonePoseRotation(armUpperR, idleRotR);
                    fencerSkel.ForceUpdateAllBoneTransforms();

                    var paintType = typeof(PaintTabUI);
                    const BindingFlags PFlags = BindingFlags.Instance | BindingFlags.NonPublic;
                    paintType.GetField("_currentHero", PFlags)?.SetValue(paintTab, fencerNode);
                    paintTab.OnTabActivated();
                    await ToSignal(this, SignalName.ProcessFrame);

                    Quaternion poseKept = fencerSkel.GetBonePoseRotation(armUpperR);
                    Check(poseKept.AngleTo(idleRotR) < 1e-3f,
                        $"Entering PaintTabUI with default 'Keep Current Pose' preserved active arm rotation (diff={poseKept.AngleTo(idleRotR):E2}rad)");

                    // Switch toggle ON -> Bind A-Pose (Reset)
                    paintTab.SetUseBindPoseInPaint(true);
                    await ToSignal(this, SignalName.ProcessFrame);
                    Quaternion poseBind = fencerSkel.GetBonePoseRotation(armUpperR);
                    Quaternion restRotR = fencerSkel.GetBoneRest(armUpperR).Basis.Orthonormalized().GetRotationQuaternion();
                    Check(poseBind.AngleTo(restRotR) < 1e-3f,
                        $"Toggling 'Bind A-Pose' in PaintTabUI reset arm_upper_R to raw BoneRest (diffFromRest={poseBind.AngleTo(restRotR):E2}rad)");

                    // Switch toggle OFF -> restores cached Current Pose cleanly while still in PaintTabUI
                    paintTab.SetUseBindPoseInPaint(false);
                    await ToSignal(this, SignalName.ProcessFrame);
                    Quaternion poseRestoredInPaint = fencerSkel.GetBonePoseRotation(armUpperR);
                    Check(poseRestoredInPaint.AngleTo(idleRotR) < 1e-3f,
                        $"Toggling back to 'Keep Current Pose' inside PaintTabUI restored idle arm rotation (diff={poseRestoredInPaint.AngleTo(idleRotR):E2}rad)");

                    paintTab.OnTabDeactivated();
                    paintTab.ClearHero();
                    paintTab.QueueFree();
                    hierarchy.QueueFree();
                    dummyCustom.QueueFree();
                }

                // =========================================================================
                // TEST 7: Geometry FlipX (Winding + Normals + Tangents) & Mirror L<->R
                // =========================================================================
                GD.Print("\n=== Test 7: Geometry Flip X/Y/Z, Triangle Winding & Mirror L<->R ===");
                {
                    Check(CustomModelAutoRigger.MirrorBoneNameLtoR("ankle_L") == "ankle_R", "MirrorBoneNameLtoR swaps ankle_L -> ankle_R");
                    Check(CustomModelAutoRigger.MirrorBoneNameLtoR("weapon_hand_R") == "weapon_hand_L", "MirrorBoneNameLtoR swaps weapon_hand_R -> weapon_hand_L");
                    Check(CustomModelAutoRigger.MirrorBoneNameLtoR("head") == null, "MirrorBoneNameLtoR returns null for midline head bone");

                    // Build asymmetric shoe-like tetrahedron around center (0.20, 0.05, 0.00) with distinct X asymmetry
                    Vector3[] shoeVerts =
                    {
                        new Vector3(0.10f, 0.00f, -0.05f),
                        new Vector3(0.30f, 0.00f, -0.05f),
                        new Vector3(0.28f, 0.00f,  0.15f),
                        new Vector3(0.26f, 0.12f,  0.02f)
                    };
                    // Outward-facing CCW triangles for the tetrahedron
                    int[] shoeTris =
                    {
                        0, 2, 1, // bottom (normal -Y)
                        0, 1, 3, // back (normal -Z / +Y)
                        1, 2, 3, // +X outer side
                        2, 0, 3  // -X inner side
                    };
                    var st = new SurfaceTool();
                    st.Begin(Mesh.PrimitiveType.Triangles);
                    for (int t = 0; t < shoeTris.Length; t += 3)
                    {
                        Vector3 v0 = shoeVerts[shoeTris[t]];
                        Vector3 v1 = shoeVerts[shoeTris[t + 1]];
                        Vector3 v2 = shoeVerts[shoeTris[t + 2]];
                        Vector3 n = (v2 - v0).Cross(v1 - v0).Normalized();
                        // Note Godot CCW face normal: Cross(v1 - v0, v2 - v0) vs standard; pick outward relative to centroid
                        Vector3 centroid = (shoeVerts[0] + shoeVerts[1] + shoeVerts[2] + shoeVerts[3]) * 0.25f;
                        Vector3 faceCenter = (v0 + v1 + v2) / 3f;
                        if (n.Dot(faceCenter - centroid) < 0f)
                        {
                            (v1, v2) = (v2, v1);
                            n = -n;
                        }
                        st.SetNormal(n); st.SetUV(new Vector2(0, 0)); st.AddVertex(v0);
                        st.SetNormal(n); st.SetUV(new Vector2(1, 0)); st.AddVertex(v1);
                        st.SetNormal(n); st.SetUV(new Vector2(0, 1)); st.AddVertex(v2);
                    }
                    st.GenerateTangents();
                    var shoeMesh = st.Commit();
                    var shoeScene = new Node3D { Name = "ShoeScene" };
                    var shoeMi = new MeshInstance3D { Name = "LeftSneaker", Mesh = shoeMesh };
                    shoeScene.AddChild(shoeMi);
                    Root.AddChild(shoeScene);

                    var origArrays = shoeMesh.SurfaceGetArrays(0);
                    var origVerts = origArrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                    var origNorms = origArrays[(int)Mesh.ArrayType.Normal].AsVector3Array();
                    var origTangents = origArrays[(int)Mesh.ArrayType.Tangent].AsFloat32Array();
                    float origTw0 = origTangents[3];

                    // Apply FlipX
                    var restore = CustomModelAutoRigger.ApplyGeometryFlipToScene(shoeScene, flipX: true, flipY: false, flipZ: false);
                    Check(restore != null && shoeScene.HasMeta("BakedFlipMask") && shoeScene.GetMeta("BakedFlipMask").AsInt32() == 1,
                        "ApplyGeometryFlipToScene stamped BakedFlipMask=1 for FlipX");

                    var flipArrays = shoeMi.Mesh.SurfaceGetArrays(0);
                    var flipVerts = flipArrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                    var flipNorms = flipArrays[(int)Mesh.ArrayType.Normal].AsVector3Array();
                    var flipTangents = flipArrays[(int)Mesh.ArrayType.Tangent].AsFloat32Array();
                    var flipIdx = flipArrays[(int)Mesh.ArrayType.Index].AsInt32Array();

                    // Verify winding parity matches flipped vertex normals (signed volume parity stays outward!)
                    Vector3 flipCentroid = Vector3.Zero;
                    foreach (var v in flipVerts) flipCentroid += v;
                    flipCentroid /= flipVerts.Length;
                    bool allOutward = true;
                    int triCount = flipIdx != null && flipIdx.Length >= 3 ? flipIdx.Length / 3 : flipVerts.Length / 3;
                    for (int t = 0; t < triCount; t++)
                    {
                        int i0 = flipIdx != null ? flipIdx[t * 3] : t * 3;
                        int i1 = flipIdx != null ? flipIdx[t * 3 + 1] : t * 3 + 1;
                        int i2 = flipIdx != null ? flipIdx[t * 3 + 2] : t * 3 + 2;
                        Vector3 a = flipVerts[i0], b = flipVerts[i1], c = flipVerts[i2];
                        // Must agree with stored flipped vertex normal
                        Vector3 storedN = (flipNorms[i0] + flipNorms[i1] + flipNorms[i2]).Normalized();
                        Vector3 geoN = (b - a).Cross(c - a).Normalized();
                        // Compare with original triangle normal orientation parity
                        Vector3 oa = origVerts[t * 3], ob = origVerts[t * 3 + 1], oc = origVerts[t * 3 + 2];
                        Vector3 oStored = (origNorms[t * 3] + origNorms[t * 3 + 1] + origNorms[t * 3 + 2]).Normalized();
                        Vector3 oGeo = (ob - oa).Cross(oc - oa).Normalized();
                        float origParity = MathF.Sign(oGeo.Dot(oStored));
                        float flipParity = MathF.Sign(geoN.Dot(storedN));
                        if (MathF.Abs(origParity - flipParity) > 0.1f)
                        {
                            allOutward = false;
                            break;
                        }
                    }
                    Check(allOutward, "FlipX reversed triangle winding so flipped face winding parity matches outward normals (zero inside-out culling)");
                    Check(MathF.Abs(flipTangents[3] + origTw0) < 1e-4f, $"FlipX negated tangent binormal handedness W ({origTw0} -> {flipTangents[3]})");

                    // Restore reverts mesh cleanly
                    restore();
                    Check(!shoeScene.HasMeta("BakedFlipMask") && shoeMi.Mesh == shoeMesh,
                        "Restore action reverted ShoeScene back to exact unflipped Mesh and cleared BakedFlipMask");

                    // Test Mirror L<->R accessory options cloning + RigAndBind with FlipX
                    var leftCfg = new DeadlockPlayground.Workspace.WorkspaceCustomModelEntry
                    {
                        AccessoryName = "Cool Sneaker (L)",
                        Method = (int)CustomModelAutoRigger.RiggingMethod.RigidSingleBone,
                        RigidBoneName = "ankle_L",
                        RootPosition = new[] { 0.18f, 0.06f, 0.02f },
                        RootRotationDeg = new[] { 5f, 18f, -12f },
                        RootScale = 1.0f,
                        RootScaleXyz = new[] { 1f, 1f, 1f },
                        FlipX = false,
                        FlipY = false,
                        FlipZ = true
                    };
                    var roundTrip = leftCfg.ToRiggingOptions();
                    Check(!roundTrip.FlipX && !roundTrip.FlipY && roundTrip.FlipZ,
                        "WorkspaceCustomModelEntry round-trips FlipX/FlipY/FlipZ");

                    var mirOpts = CharacterTabUI.BuildMirroredAccessoryOptions("Cool Sneaker (L)", leftCfg, shoeScene, vpkLoader.CurrentHeroNode);
                    Check(mirOpts.FlipX && mirOpts.FlipZ, "BuildMirroredAccessoryOptions toggled FlipX=true while keeping FlipZ=true");
                    Check(mirOpts.AccessoryName == "Cool Sneaker (R)", $"BuildMirroredAccessoryOptions renamed '(L)' -> '{mirOpts.AccessoryName}'");
                    Check(mirOpts.RigidBoneName == "ankle_R", $"BuildMirroredAccessoryOptions swapped ankle_L -> {mirOpts.RigidBoneName}");
                    Check(MathF.Abs(mirOpts.RootRotationDeg.X - 5f) < 1e-3f &&
                          MathF.Abs(mirOpts.RootRotationDeg.Y - (-18f)) < 1e-3f &&
                          MathF.Abs(mirOpts.RootRotationDeg.Z - 12f) < 1e-3f,
                        $"BuildMirroredAccessoryOptions mirrored Euler Yxz to ({mirOpts.RootRotationDeg.X}, {mirOpts.RootRotationDeg.Y}, {mirOpts.RootRotationDeg.Z})");

                    var (_, shoeMaxPre, _) = CustomModelAutoRigger.ComputePristineModelBounds(shoeScene);
                    var (shoeMinPre, _, _) = CustomModelAutoRigger.ComputePristineModelBounds(shoeScene);
                    Vector3 lc = (shoeMinPre + shoeMaxPre) * 0.5f;
                    Vector3 origWc = leftCfg.ToRiggingOptions().AlignedTransform * lc;
                    Vector3 mirWc = mirOpts.AlignedTransform * lc;
                    Check(MathF.Abs(mirWc.X + origWc.X) < 1e-3f && MathF.Abs(mirWc.Y - origWc.Y) < 1e-3f && MathF.Abs(mirWc.Z - origWc.Z) < 1e-3f,
                        $"Mirrored world bounds center landed symmetrically at X=-({origWc.X:F3}) -> {mirWc.X:F3}");

                    var rigMir = CustomModelAutoRigger.RigAndBind(shoeScene, vpkLoader.CurrentHeroNode, mirOpts);
                    Check(rigMir.Success && rigMir.CustomSubmeshes.Count == 1,
                        $"RigAndBind with FlipX+FlipZ succeeded on RigidSingleBone ankle_R (verts={rigMir.TotalVertices})");

                    // 7B: Verify identical textures/materials are reused across BOTH mirrored slots AND two manually imported GLBs,
                    // while distinct textures with generic "Material" names still never collide.
                    static ImageTexture MakeTex(Color c)
                    {
                        var img = Image.CreateEmpty(4, 4, false, Image.Format.Rgba8);
                        img.Fill(c);
                        return ImageTexture.CreateFromImage(img);
                    }
                    var texSameA = MakeTex(new Color(0.8f, 0.2f, 0.1f, 1f));
                    var texSameB = MakeTex(new Color(0.8f, 0.2f, 0.1f, 1f));
                    var texDiffC = MakeTex(new Color(0.1f, 0.7f, 0.9f, 1f));

                    Node3D BuildDummyShoe(string meshName, string matName, Texture2D tex)
                    {
                        var bm = new BoxMesh { Size = new Vector3(0.2f, 0.1f, 0.3f) };
                        bm.SurfaceSetMaterial(0, new StandardMaterial3D { ResourceName = matName, AlbedoTexture = tex });
                        var sc = new Node3D { Name = $"Scene_{meshName}" };
                        var mi = new MeshInstance3D { Name = meshName, Mesh = bm };
                        sc.AddChild(mi);
                        Root.AddChild(sc);
                        return sc;
                    }

                    var shoeA = BuildDummyShoe("LeftSneakerMesh", "Material", texSameA);
                    var shoeB = BuildDummyShoe("RightSneakerMesh", "Material", texSameB);
                    var hatC  = BuildDummyShoe("HatMesh", "Material", texDiffC);

                    var optA = new CustomModelAutoRigger.RiggingOptions { SlotId = "acc_shoe_l", Intent = CustomModelAutoRigger.AccessoryIntent.AddAccessory, Method = CustomModelAutoRigger.RiggingMethod.RigidSingleBone, RigidBoneName = "ankle_L" };
                    var optB = new CustomModelAutoRigger.RiggingOptions { SlotId = "acc_shoe_r", Intent = CustomModelAutoRigger.AccessoryIntent.AddAccessory, Method = CustomModelAutoRigger.RiggingMethod.RigidSingleBone, RigidBoneName = "ankle_R", FlipX = true };
                    var optC = new CustomModelAutoRigger.RiggingOptions { SlotId = "acc_hat_c", Intent = CustomModelAutoRigger.AccessoryIntent.AddAccessory, Method = CustomModelAutoRigger.RiggingMethod.RigidSingleBone, RigidBoneName = "head" };

                    CustomModelAutoRigger.RemoveAccessorySlot(vpkLoader.CurrentHeroNode, mirOpts.SlotId);
                    var resA = CustomModelAutoRigger.RigAndBind(shoeA, vpkLoader.CurrentHeroNode, optA);
                    var resB = CustomModelAutoRigger.RigAndBind(shoeB, vpkLoader.CurrentHeroNode, optB);
                    var resC = CustomModelAutoRigger.RigAndBind(hatC,  vpkLoader.CurrentHeroNode, optC);

                    Material matA = resA.CustomSubmeshes[0].Mesh.SurfaceGetMaterial(0);
                    Material matB = resB.CustomSubmeshes[0].Mesh.SurfaceGetMaterial(0);
                    Material matC = resC.CustomSubmeshes[0].Mesh.SurfaceGetMaterial(0);
                    string sNameA = resA.CustomSubmeshes[0].GetMeta("SourceMaterialName").AsString();
                    string sNameB = resB.CustomSubmeshes[0].GetMeta("SourceMaterialName").AsString();
                    string sNameC = resC.CustomSubmeshes[0].GetMeta("SourceMaterialName").AsString();

                    Check(ReferenceEquals(matA, matB) && sNameA == sNameB,
                        $"Two separate accessory slots sharing identical texture pixels reused exact Material & name ('{sNameA}')");
                    Check(!ReferenceEquals(matA, matC) && !string.Equals(sNameA, sNameC, StringComparison.OrdinalIgnoreCase),
                        $"Distinct texture on generic 'Material' avoided collision ('{sNameA}' vs '{sNameC}')");

                    var hierarchyCheck = new DeadlockPlayground.Painter.HeroMeshHierarchy();
                    Root.AddChild(hierarchyCheck);
                    hierarchyCheck.ScanHero(vpkLoader.CurrentHeroNode);
                    int matchingMatGroups = hierarchyCheck.MaterialGroups.Count(g =>
                        g.Submeshes.Any(sm => sm.Mesh == resA.CustomSubmeshes[0] || sm.Mesh == resB.CustomSubmeshes[0]));
                    Check(matchingMatGroups == 1,
                        $"HeroMeshHierarchy grouped both mirrored/manual sneakers into 1 MaterialGroup (count={matchingMatGroups})");
                    hierarchyCheck.QueueFree();

                    CustomModelAutoRigger.RemoveAccessorySlot(vpkLoader.CurrentHeroNode, "acc_shoe_l");
                    CustomModelAutoRigger.RemoveAccessorySlot(vpkLoader.CurrentHeroNode, "acc_shoe_r");
                    CustomModelAutoRigger.RemoveAccessorySlot(vpkLoader.CurrentHeroNode, "acc_hat_c");
                    shoeA.QueueFree();
                    shoeB.QueueFree();
                    hatC.QueueFree();
                    shoeScene.QueueFree();
                }

                // =========================================================================
                // TEST 8: Parallelized CustomLandmarkWeightSolver Weight Sanity
                // =========================================================================
                GD.Print("\n=== Test 8: CustomLandmarkWeightSolver Geodesic Solve Verification ===");
                {
                    var st = new SurfaceTool();
                    st.Begin(Mesh.PrimitiveType.Triangles);
                    void AddSphere(Vector3 center, float rx, float ry, float rz)
                    {
                        var sp = new SphereMesh { Radius = 1f, Height = 2f, RadialSegments = 12, Rings = 6 };
                        var arr = sp.SurfaceGetArrays(0);
                        var vs = arr[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                        var idx = arr[(int)Mesh.ArrayType.Index].AsInt32Array();
                        for (int i = 0; i < idx.Length; i++)
                        {
                            Vector3 v = vs[idx[i]];
                            st.AddVertex(center + new Vector3(v.X * rx, v.Y * ry, v.Z * rz));
                        }
                    }
                    AddSphere(new Vector3(0f, 1.0f, 0f), 0.18f, 0.40f, 0.12f);  // torso
                    AddSphere(new Vector3(0f, 1.65f, 0f), 0.11f, 0.13f, 0.12f); // head
                    AddSphere(new Vector3(0.35f, 1.15f, 0f), 0.25f, 0.06f, 0.06f); // arm L
                    AddSphere(new Vector3(-0.35f, 1.15f, 0f), 0.25f, 0.06f, 0.06f); // arm R
                    AddSphere(new Vector3(0.10f, 0.45f, 0f), 0.07f, 0.45f, 0.07f); // leg L
                    AddSphere(new Vector3(-0.10f, 0.45f, 0f), 0.07f, 0.45f, 0.07f); // leg R

                    var synthScene = new Node3D { Name = "LandmarkSynthScene" };
                    var synthMi = new MeshInstance3D { Name = "Body", Mesh = st.Commit() };
                    synthScene.AddChild(synthMi);
                    Root.AddChild(synthScene);

                    var hSkel = CustomModelAutoRigger.FindSkeleton(vpkLoader.CurrentHeroNode);
                    var synthOpts = new CustomModelAutoRigger.RiggingOptions
                    {
                        Method = CustomModelAutoRigger.RiggingMethod.LandmarkAutoRig,
                        AlignedTransform = Transform3D.Identity
                    };
                    var synth = CustomLandmarkWeightSolver.BuildSyntheticRig(synthScene, new List<MeshInstance3D> { synthMi }, hSkel, synthOpts);
                    Check(synth.Skeleton.GetBoneCount() == 21 && synth.VertexCount > 100,
                        $"BuildSyntheticRig built 21-bone synthetic skeleton across {synth.VertexCount} vertices");

                    var wArr = synthMi.Mesh.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Weights].AsFloat32Array();
                    bool weightsOk = wArr != null && wArr.Length == synth.VertexCount * 4;
                    if (weightsOk)
                    {
                        for (int v = 0; v < synth.VertexCount; v++)
                        {
                            float sum = wArr[v * 4] + wArr[v * 4 + 1] + wArr[v * 4 + 2] + wArr[v * 4 + 3];
                            if (MathF.Abs(sum - 1f) > 1e-3f) { weightsOk = false; break; }
                        }
                    }
                    Check(weightsOk, "Parallel Dijkstra + double-buffered Laplacian + zero-alloc Top-4 produced normalized weights (sum == 1.0) on all vertices");
                    synth.Restore();
                    Check(synthMi.Skin == null, "BuildSyntheticRig.Restore() cleanly reverted test mesh skin");
                    synthScene.QueueFree();
                }

                // =========================================================================
                // TEST 9: Skinned MeshRaycaster Posed Hit Accuracy (Paint Tab Non-A-Pose Fix)
                // =========================================================================
                GD.Print("\n=== Test 9: Skinned MeshRaycaster Posed Hit Accuracy ===");
                {
                    var posedHero = vpkLoader.CurrentHeroNode;
                    var posedSkel = CustomModelAutoRigger.FindSkeleton(posedHero);
                    MeshInstance3D bodyMi = null;
                    foreach (var m in CustomModelAutoRigger.CollectOriginalHeroMeshes(posedHero))
                    {
                        if (m.Skin != null && m.Name.ToString().Contains("fencer_model", StringComparison.OrdinalIgnoreCase))
                        {
                            bodyMi = m;
                            break;
                        }
                    }
                    Check(posedSkel != null && bodyMi != null && bodyMi.Skin != null,
                        $"Located skinned mesh '{bodyMi?.Name}' on hero for posed raycast test");

                    if (posedSkel != null && bodyMi != null && bodyMi.Skin != null)
                    {
                        int armUpperL = posedSkel.FindBone("arm_upper_L");
                        int handL = posedSkel.FindBone("hand_L");
                        Check(armUpperL >= 0 && handL >= 0, $"Found arm_upper_L ({armUpperL}) and hand_L ({handL})");

                        // Find a vertex strongly weighted to hand_L (bind -> handL) and check 8-bone support
                        var arr = bodyMi.Mesh.SurfaceGetArrays(0);
                        var verts = arr[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                        var bones = arr[(int)Mesh.ArrayType.Bones].AsInt32Array();
                        var weights = arr[(int)Mesh.ArrayType.Weights].AsFloat32Array();
                        int stride = (bones.Length >= verts.Length * 8 && weights.Length >= verts.Length * 8) ? 8 : 4;

                        int handVertIdx = -1;
                        float bestW = 0f;
                        int handBindIdx = -1;
                        for (int bi = 0; bi < bodyMi.Skin.GetBindCount(); bi++)
                        {
                            int sb = bodyMi.Skin.GetBindBone(bi);
                            if (sb < 0) sb = posedSkel.FindBone(bodyMi.Skin.GetBindName(bi));
                            if (sb == handL) { handBindIdx = bi; break; }
                        }
                        for (int v = 0; v < verts.Length && handBindIdx >= 0; v++)
                        {
                            for (int k = 0; k < stride; k++)
                            {
                                if (bones[v * stride + k] == handBindIdx && weights[v * stride + k] > bestW)
                                {
                                    bestW = weights[v * stride + k];
                                    handVertIdx = v;
                                }
                            }
                        }
                        Check(handVertIdx >= 0 && bestW > 0.7f,
                            $"Found strongly weighted hand_L vertex (idx={handVertIdx}, weight={bestW:F2}, stride={stride})");

                        // Build MeshRaycaster on rest pose first, then locate the largest non-degenerate triangle containing handVertIdx
                        posedSkel.ResetBonePoses();
                        await ToSignal(this, SignalName.ProcessFrame);

                        var rc = new DeadlockPlayground.Painter.MeshRaycaster();
                        Check(rc.BuildFromMesh(bodyMi, -1), "MeshRaycaster built from 8-bone skinned hero mesh");

                        var indices = arr.Count > (int)Mesh.ArrayType.Index ? arr[(int)Mesh.ArrayType.Index].AsInt32Array() : null;
                        int handTriIdx = -1;
                        float bestAreaSq = 1e-10f;
                        if (indices != null)
                        {
                            for (int t = 0; t + 2 < indices.Length; t += 3)
                            {
                                if (indices[t] == handVertIdx || indices[t + 1] == handVertIdx || indices[t + 2] == handVertIdx)
                                {
                                    int candidate = t / 3;
                                    var candTri = rc.GetTriangle(candidate);
                                    float areaSq = (candTri.V1 - candTri.V0).Cross(candTri.V2 - candTri.V0).LengthSquared();
                                    if (areaSq > bestAreaSq && candTri.Normal.LengthSquared() > 0.5f)
                                    {
                                        bestAreaSq = areaSq;
                                        handTriIdx = candidate;
                                    }
                                }
                            }
                        }
                        Check(handTriIdx >= 0, $"Located non-degenerate triangle {handTriIdx} containing hand_L vertex (areaSq={bestAreaSq:E2})");

                        var restTri = rc.GetTriangle(handTriIdx);
                        Vector3 restTriWorld = bodyMi.GlobalTransform * ((restTri.V0 + restTri.V1 + restTri.V2) / 3f);
                        Vector3 restNormWorld = (bodyMi.GlobalTransform.Basis * restTri.Normal).Normalized();

                        // Rotate arm_upper_L 75deg upward/forward away from A-Pose
                        Quaternion baseArmRot = posedSkel.GetBoneRest(armUpperL).Basis.Orthonormalized().GetRotationQuaternion();
                        posedSkel.SetBonePoseRotation(armUpperL, Quaternion.FromEuler(new Vector3(0f, Mathf.DegToRad(75f), Mathf.DegToRad(75f))) * baseArmRot);
                        await ToSignal(this, SignalName.ProcessFrame);

                        rc.UpdateSkinningIfNeeded(bodyMi);
                        var posedTri = rc.GetTriangle(handTriIdx);
                        Vector3 posedTriWorld = bodyMi.GlobalTransform * ((posedTri.V0 + posedTri.V1 + posedTri.V2) / 3f);
                        Vector3 posedNormWorld = (bodyMi.GlobalTransform.Basis * posedTri.Normal).Normalized();
                        float moveDist = posedTriWorld.DistanceTo(restTriWorld);
                        Check(moveDist > 0.25f, $"Posed hand_L triangle moved significantly from A-Pose ({moveDist:F3}m)");

                        // Cast a short probe ray from just outside the posed triangle face back into the front face (matching painter cullBackfaces: true)
                        Vector3 rayDir = -posedNormWorld;
                        Vector3 rayOrigin = posedTriWorld + posedNormWorld * 0.02f;
                        var hitPosed = rc.IntersectRay(bodyMi, rayOrigin, rayDir, cullBackfaces: true);
                        Check(hitPosed.Hit && hitPosed.WorldPosition.DistanceTo(posedTriWorld) < 0.01f,
                            $"MeshRaycaster hit posed hand triangle accurately (hit={hitPosed.Hit}, err={hitPosed.WorldPosition.DistanceTo(posedTriWorld):F4}m)");

                        // Empty old A-Pose location should NOT falsely match the old A-Pose triangle
                        var hitOldSpot = rc.IntersectRay(bodyMi, restTriWorld + restNormWorld * 0.02f, -restNormWorld, cullBackfaces: false);
                        bool oldSpotClean = !hitOldSpot.Hit || hitOldSpot.WorldPosition.DistanceTo(restTriWorld) > 0.15f;
                        Check(oldSpotClean, "Old A-Pose location no longer registers phantom hits while hero is posed");

                        // Raycaster UV lookup (with camera context) evaluates to the posed world coordinate
                        var uvPointPosed = rc.FindPointFromUV(bodyMi, hitPosed.HitUV, rayOrigin);
                        Check(uvPointPosed.Hit && uvPointPosed.WorldPosition.DistanceTo(posedTriWorld) < 0.01f,
                            $"FindPointFromUV returned posed hand world position (err={uvPointPosed.WorldPosition.DistanceTo(posedTriWorld):F4}m)");

                        // Reset back to Bind A-Pose without rebuilding MeshRaycaster -> should automatically track rest position
                        posedSkel.ResetBonePoses();
                        await ToSignal(this, SignalName.ProcessFrame);
                        Vector3 restOrigin = restTriWorld + restNormWorld * 0.02f;
                        var hitRest = rc.IntersectRay(bodyMi, restOrigin, -restNormWorld, cullBackfaces: true);
                        Check(hitRest.Hit && hitRest.WorldPosition.DistanceTo(restTriWorld) < 0.01f,
                            $"MeshRaycaster automatically tracked Skeleton3D ResetBonePoses back to A-Pose (err={hitRest.WorldPosition.DistanceTo(restTriWorld):F4}m)");
                    }
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
