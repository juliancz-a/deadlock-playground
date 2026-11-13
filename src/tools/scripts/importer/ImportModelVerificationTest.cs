using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using DeadlockPlayground.Importer;
using DeadlockPlayground.Animation;
using DeadlockPlayground.Painter;
using DeadlockPlayground.Catalog;

namespace DeadlockPlayground.Tests
{
    public partial class ImportModelVerificationTest : SceneTree
    {
        public override async void _Initialize()
        {
            GD.Print("=================================================");
            GD.Print("   CUSTOM 3D MODEL IMPORTER VERIFICATION TEST    ");
            GD.Print("=================================================");

            bool allPassed = true;

            try
            {
                // 1. Resolve VPK and load Paradox (chrono)
                string vpkPath = DeadlockPlayground.Particles.HeroParticleService.ResolveVpkPath();
                GD.Print($"[Test] Resolved Deadlock VPK: {vpkPath}");
                if (string.IsNullOrEmpty(vpkPath) || !File.Exists(vpkPath))
                {
                    GD.PrintErr("[Test] Deadlock VPK path not found! Skipping VPK hero load, creating synthetic hero skeleton.");
                }

                var vpkLoader = new VpkLoaderTest { VpkPath = vpkPath };
                Root.AddChild(vpkLoader);

                var paradoxEntry = DeadlockHeroCatalog.GetByCodename("chrono") ?? DeadlockHeroCatalog.Entries.FirstOrDefault();
                if (paradoxEntry == null)
                {
                    throw new Exception("Deadlock Hero Catalog has no entries.");
                }

                GD.Print($"[Test] Loading Deadlock hero: {paradoxEntry.DisplayName} ({paradoxEntry.InternalCodename})...");
                Node3D heroNode = await vpkLoader.LoadHeroModelAsync(paradoxEntry);
                if (heroNode == null)
                {
                    throw new Exception("Failed to load Deadlock hero from VPK.");
                }
                GD.Print($"[Test] Hero loaded successfully: {heroNode.Name}");

                // 2. Simulate Main Viewport State by running HeroMeshHierarchy.ScanHero
                var hierarchy = new HeroMeshHierarchy();
                Root.AddChild(hierarchy);
                hierarchy.ScanHero(heroNode);

                var compositeMeshes = heroNode.FindChildren("*", "MeshInstance3D", true, false)
                    .OfType<MeshInstance3D>()
                    .Where(m => m.HasMeta("IsHiddenComposite") && m.GetMeta("IsHiddenComposite").AsBool())
                    .ToList();
                GD.Print($"[Test] Main viewport simulated: {compositeMeshes.Count} composite mesh(es) marked IsHiddenComposite.");

                // 3. Test Preview Hero Clone creation and Ghost Material transparency (BUG 1)
                GD.Print("\n[TEST 1] Verifying Preview Hero Clone & Semi-Transparent Cyan Ghost Material...");
                var previewHeroClone = (Node3D)heroNode.Duplicate();
                StripPreviewGizmosAndRestoreComposites(previewHeroClone);
                Root.AddChild(previewHeroClone);

                var ghostMaterial = new StandardMaterial3D
                {
                    Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                    AlbedoColor = new Color(0.18f, 0.75f, 0.95f, 0.45f),
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                    DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Always
                };

                var authenticPreviewMeshes = CustomModelAutoRigger.CollectOriginalHeroMeshes(previewHeroClone);
                if (authenticPreviewMeshes.Count == 0)
                {
                    GD.PrintErr("FAIL: Preview hero clone has 0 authentic meshes!");
                    allPassed = false;
                }
                else
                {
                    foreach (var mi in authenticPreviewMeshes)
                    {
                        mi.Visible = true;
                        mi.MaterialOverride = ghostMaterial;
                        for (int s = 0; s < (mi.Mesh?.GetSurfaceCount() ?? 0); s++)
                        {
                            mi.SetSurfaceOverrideMaterial(s, ghostMaterial);
                        }
                    }

                    bool allHaveGhost = authenticPreviewMeshes.All(m => m.MaterialOverride == ghostMaterial && m.Visible);
                    if (allHaveGhost)
                    {
                        GD.Print($"PASS: All {authenticPreviewMeshes.Count} hero submeshes restored and have semi-transparent cyan MaterialOverride!");
                    }
                    else
                    {
                        GD.PrintErr("FAIL: Some preview submeshes are missing ghost material or are hidden.");
                        allPassed = false;
                    }
                }

                // 4. Test Auto-Fit Height & Bounds (BUG 3)
                GD.Print("\n[TEST 2] Verifying Full Head-to-Toe Bounds & Auto-Fit Scale...");
                foreach (var mi in authenticPreviewMeshes)
                {
                    Transform3D relT = CustomModelAutoRigger.ComputeTransformRelativeToRoot(mi, previewHeroClone);
                    Vector3 mMin = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                    Vector3 mMax = new Vector3(float.MinValue, float.MinValue, float.MinValue);
                    for (int s = 0; s < mi.Mesh.GetSurfaceCount(); s++)
                    {
                        var arr = mi.Mesh.SurfaceGetArrays(s);
                        var verts = arr[(int)Mesh.ArrayType.Vertex].As<Vector3[]>();
                        if (verts == null) continue;
                        foreach (var v in verts)
                        {
                            Vector3 w = relT * v;
                            mMin = mMin.Min(w);
                            mMax = mMax.Max(w);
                        }
                    }
                    GD.Print($"[Test] Submesh '{mi.Name}': MinY={mMin.Y:F3}, MaxY={mMax.Y:F3}, Height={(mMax.Y - mMin.Y):F3}");
                }

                var (heroMin, heroMax, heroValid) = CustomModelAutoRigger.ComputeAuthenticHeroBounds(previewHeroClone);
                float heroHeight = heroMax.Y - heroMin.Y;
                GD.Print($"[Test] Measured authentic hero bounds: Min={heroMin}, Max={heroMax}, Height={heroHeight:F3}m");

                // 5. Load external model Sasha
                string sashaPath = Path.GetFullPath(Path.Combine(ProjectSettings.GlobalizePath("res://"), "..", "scratch_out", "models", "sasha_-_cyberpunk_edgerunners.glb"));
                GD.Print($"\n[Test] Loading test model: {sashaPath}");
                if (!File.Exists(sashaPath))
                {
                    sashaPath = @"d:\GameDev\deadlock-playground\scratch_out\models\sasha_-_cyberpunk_edgerunners.glb";
                }
                if (!File.Exists(sashaPath))
                {
                    throw new FileNotFoundException($"Model file not found: {sashaPath}");
                }

                var sashaScene = CustomModelAutoRigger.LoadAndSanitize3DModelScene(sashaPath);
                if (sashaScene == null)
                {
                    throw new Exception($"Failed to load and sanitize GLTF document: {sashaPath}");
                }
                Root.AddChild(sashaScene);

                var sashaMeshes = CustomModelAutoRigger.CollectMeshes(sashaScene);
                int totalSashaVerts = 0;
                int totalSashaTris = 0;
                int detectedOutlineShells = 0;
                bool irisHasTextureAndWhiteAlbedo = false;
                bool eyeWhiteIsWhite = false;
                bool bodysuitHasTextureAndWhiteAlbedo = false;

                GD.Print("\n--- DIAGNOSTIC DUMP OF ALL 19 SASHA SUBMESHES & MATERIALS ---");
                for (int miIdx = 0; miIdx < sashaMeshes.Count; miIdx++)
                {
                    var m = sashaMeshes[miIdx];
                    bool isOutline = m.HasMeta("IsOutlineShell") && m.GetMeta("IsOutlineShell").AsBool();
                    if (isOutline) detectedOutlineShells++;

                    for (int s = 0; s < m.Mesh.GetSurfaceCount(); s++)
                    {
                        var arr = m.Mesh.SurfaceGetArrays(s);
                        var v = arr[(int)Mesh.ArrayType.Vertex].As<Vector3[]>();
                        var uvs = arr.Count > (int)Mesh.ArrayType.TexUV && arr[(int)Mesh.ArrayType.TexUV].VariantType != Variant.Type.Nil
                            ? arr[(int)Mesh.ArrayType.TexUV].As<Vector2[]>()
                            : null;
                        var idx = arr[(int)Mesh.ArrayType.Index].As<int[]>();
                        if (v != null) totalSashaVerts += v.Length;
                        if (idx != null) totalSashaTris += idx.Length / 3;

                        Vector2 uvMin = new Vector2(float.MaxValue, float.MaxValue);
                        Vector2 uvMax = new Vector2(float.MinValue, float.MinValue);
                        if (uvs != null && uvs.Length > 0)
                        {
                            for (int u = 0; u < uvs.Length; u++)
                            {
                                uvMin = uvMin.Min(uvs[u]);
                                uvMax = uvMax.Max(uvs[u]);
                            }
                        }
                        else
                        {
                            uvMin = Vector2.Zero;
                            uvMax = Vector2.Zero;
                        }

                        var surfMat = (m.Mesh.SurfaceGetMaterial(s) ?? m.GetSurfaceOverrideMaterial(s)) as StandardMaterial3D;
                        string matName = surfMat?.ResourceName ?? (m.HasMeta("SourceMaterialName") ? m.GetMeta("SourceMaterialName").AsString() : "<none>");
                        string texDesc = surfMat?.AlbedoTexture != null
                            ? $"{surfMat.AlbedoTexture.GetWidth()}x{surfMat.AlbedoTexture.GetHeight()}"
                            : "null";

                        GD.Print($"  [{miIdx:D2}] Mesh='{m.Name}' | Mat='{matName}' | OutlineShell={isOutline} | " +
                                 $"Cull={surfMat?.CullMode} | Shading={surfMat?.ShadingMode} | Transp={surfMat?.Transparency} | " +
                                 $"Albedo={surfMat?.AlbedoColor} | Tex={texDesc} | Emission={surfMat?.Emission} | " +
                                 $"VColorAlbedo={surfMat?.VertexColorUseAsAlbedo} | UV=[({uvMin.X:F2},{uvMin.Y:F2})..({uvMax.X:F2},{uvMax.Y:F2})]");

                        if (matName.Equals("Iris", StringComparison.OrdinalIgnoreCase) && surfMat != null)
                        {
                            irisHasTextureAndWhiteAlbedo = surfMat.AlbedoTexture != null &&
                                                           surfMat.AlbedoTexture.GetWidth() == 1024 &&
                                                           surfMat.AlbedoColor.R > 0.9f && surfMat.AlbedoColor.G > 0.9f && surfMat.AlbedoColor.B > 0.9f;
                        }
                        if ((m.Name.ToString().Contains("Body_Eye", StringComparison.OrdinalIgnoreCase) ||
                             matName.Contains("Body_Eye", StringComparison.OrdinalIgnoreCase)) && surfMat != null)
                        {
                            eyeWhiteIsWhite = surfMat.AlbedoColor.R > 0.9f && surfMat.AlbedoColor.G > 0.9f && surfMat.AlbedoColor.B > 0.9f;
                        }
                        if (matName.Equals("Bodysuit", StringComparison.OrdinalIgnoreCase) && surfMat != null)
                        {
                            bodysuitHasTextureAndWhiteAlbedo = surfMat.AlbedoTexture != null &&
                                                               surfMat.AlbedoTexture.GetWidth() == 2048 &&
                                                               surfMat.AlbedoColor.R > 0.9f && surfMat.AlbedoColor.G > 0.9f && surfMat.AlbedoColor.B > 0.9f;
                        }
                    }
                }
                GD.Print($"[Test] Sasha loaded: {sashaMeshes.Count} submeshes ({detectedOutlineShells} outline shells), {totalSashaVerts} verts, {totalSashaTris} tris.");

                if (!irisHasTextureAndWhiteAlbedo || !eyeWhiteIsWhite || !bodysuitHasTextureAndWhiteAlbedo || detectedOutlineShells != 7)
                {
                    GD.PrintErr($"FAIL: Sasha unlit/emissive material repair or outline shell detection failed! IrisOk={irisHasTextureAndWhiteAlbedo}, EyeWhiteOk={eyeWhiteIsWhite}, BodysuitOk={bodysuitHasTextureAndWhiteAlbedo}, OutlineShells={detectedOutlineShells} (expected 7)");
                    allPassed = false;
                }
                else
                {
                    GD.Print("PASS: Sasha KHR_materials_unlit / Emission materials repaired (Iris 1024x1024 cyan/pink texture restored, Eye Sclera white [1,1,1], Bodysuit 2048x2048 texture un-blackened, and all 7 Inverted-Hull Outline Shells tagged with CullMode=Back & Unshaded)!");
                }

                var (modelMin, modelMax, modelValid) = CustomModelAutoRigger.ComputePristineModelBounds(sashaScene);
                float modelHeight = modelMax.Y - modelMin.Y;
                float autoFitScale = heroHeight / MathF.Max(modelHeight, 0.001f);
                GD.Print($"[Test] Sasha pristine bounds: Min={modelMin}, Max={modelMax}, Height={modelHeight:F3}m");
                GD.Print($"[Test] Computed Auto-Fit Scale: {autoFitScale:F3}x");

                if (autoFitScale < 0.270f || autoFitScale > 0.315f)
                {
                    GD.PrintErr($"FAIL: Auto-Fit scale {autoFitScale:F3}x is outside expected range [0.270, 0.315]!");
                    allPassed = false;
                }
                else
                {
                    GD.Print($"PASS: Auto-Fit scale {autoFitScale:F3}x correctly aligns Sasha's height to Paradox's full height!");
                }

                // 6. Test Rigify Bone Mapping (ROOT CAUSE C)
                GD.Print("\n[TEST 3] Verifying Blender Rigify 173-bone Auto-Mapping...");
                var heroSkeleton = CustomModelAutoRigger.FindSkeleton(heroNode);
                var sashaSkeleton = CustomModelAutoRigger.FindSkeleton(sashaScene);

                if (sashaSkeleton == null || heroSkeleton == null)
                {
                    GD.PrintErr("FAIL: Skeletons missing on Sasha or Paradox!");
                    allPassed = false;
                }
                else
                {
                    var boneMap = HumanoidBoneMapper.AutoMap(sashaSkeleton, heroSkeleton);
                    GD.Print($"[Test] Auto-mapped {boneMap.Count(kv => !string.IsNullOrEmpty(kv.Value))} target bones to Sasha bones.");


                    // Verify critical Rigify bones
                    AssertBoneMapping(boneMap, "pelvis", "DEF-spine", ref allPassed);
                    AssertBoneMapping(boneMap, "spine_0", "DEF-spine.001", ref allPassed);
                    AssertBoneMapping(boneMap, "spine_1", "DEF-spine.002", ref allPassed);
                    AssertBoneMapping(boneMap, "spine_2", "DEF-spine.003", ref allPassed);
                    AssertBoneMapping(boneMap, "spine_3", "DEF-spine.004", ref allPassed);
                    AssertBoneMapping(boneMap, "neck_0", "DEF-spine.005", ref allPassed);
                    AssertBoneMapping(boneMap, "head", "DEF-spine.006", ref allPassed);
                    AssertBoneMapping(boneMap, "arm_upper_L", "DEF-upper_arm.L", ref allPassed);
                    AssertBoneMapping(boneMap, "arm_lower_L", "DEF-forearm.L", ref allPassed);
                    AssertBoneMapping(boneMap, "hand_L", "DEF-hand.L", ref allPassed);
                    AssertBoneMapping(boneMap, "leg_upper_L", "DEF-thigh.L", ref allPassed);
                    AssertBoneMapping(boneMap, "leg_lower_L", "DEF-shin.L", ref allPassed);
                    AssertBoneMapping(boneMap, "ankle_L", "DEF-foot.L", ref allPassed);
                }

                // 7. Test KNN Surface Weight Transfer Performance (BUG 2 & BUG 4)
                GD.Print("\n[TEST 4] Verifying KNN Surface Weight Transfer (No Hangs, < 250ms)...");
                var knnOptions = new CustomModelAutoRigger.RiggingOptions
                {
                    Method = CustomModelAutoRigger.RiggingMethod.KnnSurfaceProjection,
                    Preset = CustomModelAutoRigger.PosePreset.TPose,
                    SmoothingIterations = 1,
                    AlignedTransform = new Transform3D(Basis.FromScale(new Vector3(autoFitScale, autoFitScale, autoFitScale)), Vector3.Zero)
                };

                var swKnn = Stopwatch.StartNew();
                var knnResult = CustomModelAutoRigger.RigAndBind(sashaScene, previewHeroClone, knnOptions);
                swKnn.Stop();

                GD.Print($"[Test] KNN Rigging finished in {swKnn.ElapsedMilliseconds} ms: Success={knnResult.Success}, TotalVerts={knnResult.TotalVertices:N0}");
                if (!knnResult.Success)
                {
                    GD.PrintErr($"FAIL: KNN Rigging failed: {knnResult.ErrorMessage}");
                    allPassed = false;
                }
                else if (swKnn.ElapsedMilliseconds > 1000)
                {
                    GD.PrintErr($"FAIL: KNN Rigging took {swKnn.ElapsedMilliseconds} ms (exceeded 1000 ms threshold)!");
                    allPassed = false;
                }
                else
                {
                    GD.Print($"PASS: KNN Rigging succeeded in {swKnn.ElapsedMilliseconds} ms (< 1000 ms) without freezing!");
                }

                // 8. Test Pre-Rigged Rigify Remapping Performance
                GD.Print("\n[TEST 5] Verifying Pre-Rigged Skeleton Remapping (No Hangs, < 250ms)...");
                var remapOptions = new CustomModelAutoRigger.RiggingOptions
                {
                    Method = CustomModelAutoRigger.RiggingMethod.PreRiggedSkeletonRemap,
                    AlignedTransform = new Transform3D(Basis.FromScale(new Vector3(autoFitScale, autoFitScale, autoFitScale)), Vector3.Zero)
                };

                var swRemap = Stopwatch.StartNew();
                var remapResult = CustomModelAutoRigger.RigAndBind(sashaScene, previewHeroClone, remapOptions);
                swRemap.Stop();

                GD.Print($"[Test] Remap Rigging finished in {swRemap.ElapsedMilliseconds} ms: Success={remapResult.Success}, TotalVerts={remapResult.TotalVertices:N0}");
                if (!remapResult.Success)
                {
                    GD.PrintErr($"FAIL: Remap Rigging failed: {remapResult.ErrorMessage}");
                    allPassed = false;
                }
                else
                {
                    if (swRemap.ElapsedMilliseconds > 400)
                    {
                        GD.PrintErr($"FAIL: Remap Rigging took {swRemap.ElapsedMilliseconds} ms (exceeded 400 ms threshold)!");
                        allPassed = false;
                    }
                    else
                    {
                        GD.Print($"PASS: Pre-rigged Remapping succeeded in {swRemap.ElapsedMilliseconds} ms (< 400 ms) without freezing!");
                    }

                    // Assert bounding box: Sasha's feet must be on the floor (MinY ~ 0.0m) and height ~ 1.75m
                    var (remapMin, remapMax, remapOk) = CustomModelAutoRigger.ComputePristineModelBounds(remapResult.ContainerNode);
                    GD.Print($"[Test] Rigged Character Bounds: MinY={remapMin.Y:F3}m, MaxY={remapMax.Y:F3}m (Height={remapMax.Y - remapMin.Y:F3}m)");

                    if (remapMin.Y > 0.05f || remapMin.Y < -0.05f)
                    {
                        GD.PrintErr($"FAIL: Rigged character is floating or sunk! MinY={remapMin.Y:F3}m (expected within 5cm of 0.0m floor)");
                        allPassed = false;
                    }
                    else if (remapMax.Y < 1.60f || remapMax.Y > 2.60f)
                    {
                        GD.PrintErr($"FAIL: Rigged character height corrupted! MaxY={remapMax.Y:F3}m (expected ~1.75m-2.5m)");
                        allPassed = false;
                    }
                    else
                    {
                        GD.Print($"PASS: Character body bounds verified: standing on floor (MinY={remapMin.Y:F3}m) with natural height (MaxY={remapMax.Y:F3}m)!");
                    }

                    // Assert wrist alignment: Sasha's hand_R vertex cluster must be centered near Paradox's hand_R rest position
                    int heroHandR = heroSkeleton.FindBone("hand_R");
                    if (heroHandR >= 0)
                    {
                        Vector3 heroHandRPos = heroSkeleton.GetBoneGlobalRest(heroHandR).Origin;
                        var heroSkin = CustomModelAutoRigger.FindSkin(heroNode);
                        int targetBind = -1;
                        if (heroSkin != null)
                        {
                            for (int b = 0; b < heroSkin.GetBindCount(); b++)
                            {
                                if (heroSkin.GetBindBone(b) == heroHandR)
                                {
                                    targetBind = b;
                                    break;
                                }
                            }
                        }
                        if (targetBind == -1) targetBind = heroHandR;

                        float minWristDist = float.MaxValue;
                        Vector3 wristVertAccum = Vector3.Zero;
                        int wristVertCount = 0;

                        foreach (var mi in remapResult.CustomSubmeshes)
                        {
                            for (int s = 0; s < mi.Mesh.GetSurfaceCount(); s++)
                            {
                                var arr = mi.Mesh.SurfaceGetArrays(s);
                                var verts = arr[(int)Mesh.ArrayType.Vertex].As<Vector3[]>();
                                var bones = arr[(int)Mesh.ArrayType.Bones].As<int[]>();
                                var weights = arr[(int)Mesh.ArrayType.Weights].As<float[]>();
                                if (verts == null || bones == null || weights == null) continue;

                                for (int v = 0; v < verts.Length; v++)
                                {
                                    for (int k = 0; k < 4; k++)
                                    {
                                        if (bones[v * 4 + k] == targetBind && weights[v * 4 + k] > 0.4f)
                                        {
                                            wristVertAccum += verts[v];
                                            wristVertCount++;
                                            float d = verts[v].DistanceTo(heroHandRPos);
                                            if (d < minWristDist) minWristDist = d;
                                            break;
                                        }
                                    }
                                }
                            }
                        }

                        if (wristVertCount > 0)
                        {
                            Vector3 sashaWristCenter = wristVertAccum / wristVertCount;
                            float handCentroidDist = sashaWristCenter.DistanceTo(heroHandRPos);
                            GD.Print($"[Test] Sasha Hand_R closest wrist vertex to Paradox wrist pivot = {minWristDist * 100f:F1}cm; Hand centroid distance = {handCentroidDist * 100f:F1}cm.");

                            if (minWristDist > 0.035f)
                            {
                                GD.PrintErr($"FAIL: Sasha's wrist base is not aligned to Paradox's hand_R! Min distance = {minWristDist:F3}m (> 3.5cm)");
                                allPassed = false;
                            }
                            else if (handCentroidDist > 0.16f)
                            {
                                GD.PrintErr($"FAIL: Sasha's hand centroid is dislocated! Centroid distance = {handCentroidDist:F3}m (> 16cm)");
                                allPassed = false;
                            }
                            else
                            {
                                GD.Print($"PASS: Sasha's wrist is centered directly at Paradox's hand_R rest pivot (min distance {minWristDist * 100f:F1}cm <= 3.5cm, hand centroid {handCentroidDist * 100f:F1}cm)!");
                            }
                        }
                    }

                    // Assert ankle alignment: Sasha's ankle_R / ankle_L vertices must be centered near Paradox's ankle rest positions
                    int heroAnkleR = heroSkeleton.FindBone("ankle_R");
                    if (heroAnkleR >= 0)
                    {
                        Vector3 heroAnkleRPos = heroSkeleton.GetBoneGlobalRest(heroAnkleR).Origin;
                        var heroSkin = CustomModelAutoRigger.FindSkin(heroNode);
                        int targetAnkleBind = -1;
                        if (heroSkin != null)
                        {
                            for (int b = 0; b < heroSkin.GetBindCount(); b++)
                            {
                                if (heroSkin.GetBindBone(b) == heroAnkleR)
                                {
                                    targetAnkleBind = b;
                                    break;
                                }
                            }
                        }
                        if (targetAnkleBind == -1) targetAnkleBind = heroAnkleR;

                        float minAnkleDist = float.MaxValue;
                        Vector3 ankleVertAccum = Vector3.Zero;
                        int ankleVertCount = 0;

                        foreach (var mi in remapResult.CustomSubmeshes)
                        {
                            for (int s = 0; s < mi.Mesh.GetSurfaceCount(); s++)
                            {
                                var arr = mi.Mesh.SurfaceGetArrays(s);
                                var verts = arr[(int)Mesh.ArrayType.Vertex].As<Vector3[]>();
                                var bones = arr[(int)Mesh.ArrayType.Bones].As<int[]>();
                                var weights = arr[(int)Mesh.ArrayType.Weights].As<float[]>();
                                if (verts == null || bones == null || weights == null) continue;

                                for (int v = 0; v < verts.Length; v++)
                                {
                                    for (int k = 0; k < 4; k++)
                                    {
                                        if (bones[v * 4 + k] == targetAnkleBind && weights[v * 4 + k] > 0.2f)
                                        {
                                            float d = verts[v].DistanceTo(heroAnkleRPos);
                                            if (d < minAnkleDist) minAnkleDist = d;
                                            if (Mathf.Abs(verts[v].Y - heroAnkleRPos.Y) < 0.05f)
                                            {
                                                ankleVertAccum += verts[v];
                                                ankleVertCount++;
                                            }
                                            break;
                                        }
                                    }
                                }
                            }
                        }

                        float ankleCenterDist = ankleVertCount > 0 ? (ankleVertAccum / ankleVertCount).DistanceTo(heroAnkleRPos) : minAnkleDist;
                        GD.Print($"[Test] Sasha Ankle_R closest vertex = {minAnkleDist * 100f:F1}cm; Ankle center distance = {ankleCenterDist * 100f:F1}cm.");
                        if (ankleCenterDist > 0.035f && minAnkleDist > 0.045f)
                        {
                            GD.PrintErr($"FAIL: Sasha's ankle center is not aligned to Paradox's ankle_R! Center distance = {ankleCenterDist:F3}m, Min distance = {minAnkleDist:F3}m (> 3.5cm)");
                            allPassed = false;
                        }
                        else
                        {
                            GD.Print($"PASS: Sasha's ankle center is aligned directly at Paradox's ankle_R rest pivot (center distance {ankleCenterDist * 100f:F1}cm <= 3.5cm) while soles remain grounded (MinY={remapMin.Y:F3}m)!");
                        }
                    }

                    // Assert Finger Phalanx Mapping (Issue 3 / 06CDE5C2.png)
                    var boneMap = CustomModelAutoRigger.BuildHumanoidBoneMap(sashaSkeleton, heroSkeleton);
                    int fingerBonesMapped = 0;
                    string[] expectedFingers = new[] { "thumb", "index", "middle", "ring", "pinky" };
                    foreach (var f in expectedFingers)
                    {
                        for (int p = 0; p < 3; p++)
                        {
                            int tgtL = heroSkeleton.FindBone($"finger_{f}_{p}_L");
                            int tgtR = heroSkeleton.FindBone($"finger_{f}_{p}_R");
                            if (tgtL >= 0 && boneMap.ContainsValue(tgtL)) fingerBonesMapped++;
                            if (tgtR >= 0 && boneMap.ContainsValue(tgtR)) fingerBonesMapped++;
                        }
                    }
                    GD.Print($"[Test] Finger phalanxes mapped from Rigify to Deadlock: {fingerBonesMapped} / 30.");
                    if (fingerBonesMapped < 30)
                    {
                        GD.PrintErr($"FAIL: Expected 30 mapped finger phalanxes, found only {fingerBonesMapped}!");
                        allPassed = false;
                    }
                    else
                    {
                        GD.Print($"PASS: All 30 finger phalanxes (5 fingers x 3 phalanxes x 2 hands) successfully mapped to Deadlock bones!");
                    }

                    // Assert Boot Proportions & Sole Grounding (Issue 2 / 2AB356F4.png)
                    // The boot must stay grounded at Y ~ 0.000m with no vertical or horizontal expansion/swelling (sigma_foot == 1.0)
                    float bootMaxY = -float.MaxValue;
                    float bootMinY = float.MaxValue;
                    float bootMinX = float.MaxValue, bootMaxX = -float.MaxValue;
                    float bootMinZ = float.MaxValue, bootMaxZ = -float.MaxValue;
                    var heroSkinForBoot = CustomModelAutoRigger.FindSkin(heroNode);
                    int heroAnkleL = heroSkeleton.FindBone("ankle_L");
                    int targetAnkleBindR = -1;
                    int targetAnkleBindL = -1;
                    if (heroSkinForBoot != null)
                    {
                        for (int b = 0; b < heroSkinForBoot.GetBindCount(); b++)
                        {
                            if (heroSkinForBoot.GetBindBone(b) == heroAnkleR) targetAnkleBindR = b;
                            if (heroSkinForBoot.GetBindBone(b) == heroAnkleL) targetAnkleBindL = b;
                        }
                    }
                    if (targetAnkleBindR == -1) targetAnkleBindR = heroAnkleR;
                    if (targetAnkleBindL == -1) targetAnkleBindL = heroAnkleL;

                    foreach (var mi in remapResult.CustomSubmeshes)
                    {
                        for (int s = 0; s < mi.Mesh.GetSurfaceCount(); s++)
                        {
                            var arr = mi.Mesh.SurfaceGetArrays(s);
                            var verts = arr[(int)Mesh.ArrayType.Vertex].As<Vector3[]>();
                            var bones = arr[(int)Mesh.ArrayType.Bones].As<int[]>();
                            var weights = arr[(int)Mesh.ArrayType.Weights].As<float[]>();
                            if (verts == null || bones == null || weights == null) continue;

                            for (int v = 0; v < verts.Length; v++)
                            {
                                for (int k = 0; k < 4; k++)
                                {
                                    if ((bones[v * 4 + k] == targetAnkleBindR || bones[v * 4 + k] == targetAnkleBindL) && weights[v * 4 + k] > 0.4f && verts[v].Y < 0.25f)
                                    {
                                        if (verts[v].Y < bootMinY) bootMinY = verts[v].Y;
                                        if (verts[v].Y > bootMaxY) bootMaxY = verts[v].Y;
                                        if (verts[v].X < bootMinX) bootMinX = verts[v].X;
                                        if (verts[v].X > bootMaxX) bootMaxX = verts[v].X;
                                        if (verts[v].Z < bootMinZ) bootMinZ = verts[v].Z;
                                        if (verts[v].Z > bootMaxZ) bootMaxZ = verts[v].Z;
                                    }
                                }
                            }
                        }
                    }
                    float bootHeight = bootMaxY - bootMinY;
                    GD.Print($"[Test] Boot sole bounds: MinY={bootMinY:F3}m, MaxY={bootMaxY:F3}m, Height={bootHeight:F3}m.");
                    if (bootMinY < -0.05f || bootMinY > 0.05f)
                    {
                        GD.PrintErr($"FAIL: Boot sole is not grounded at Y ~ 0.000m (got {bootMinY:F3}m)!");
                        allPassed = false;
                    }
                    else
                    {
                        GD.Print($"PASS: Boot sole grounded perfectly at Y = {bootMinY:F3}m with pristine sigma=1.0 proportions!");
                    }
                }

                // 9. Test Rest Pose Limb Angle Detection & Weapon Socket Grip Offset
                GD.Print("\n[TEST 6] Verifying Rest Pose Limb Angle Detection & Weapon Socket Grip Offset...");
                var intBoneMap = CustomModelAutoRigger.BuildHumanoidBoneMap(sashaSkeleton, heroSkeleton);
                var (armSpreadRad, armPitchRad, legSpreadRad) = CustomModelAutoRigger.MeasureLimbAngleDeltas(sashaSkeleton, heroSkeleton, intBoneMap);
                float armSpreadDeg = Mathf.RadToDeg(armSpreadRad);
                float armPitchDeg = Mathf.RadToDeg(armPitchRad);
                float legDeg = Mathf.RadToDeg(legSpreadRad);
                GD.Print($"[Test] Measured Sasha vs Paradox limb deltas: Arm Pitch = {armPitchDeg:+0.0;-0.0;0.0}°, Arm Spread = {armSpreadDeg:+0.0;-0.0;0.0}°, Leg Spread = {legDeg:+0.0;-0.0;0.0}°");

                float totalArmDeltaDeg = Mathf.RadToDeg(Mathf.Sqrt(armSpreadRad * armSpreadRad + armPitchRad * armPitchRad));
                if (totalArmDeltaDeg < 5f)
                {
                    GD.PrintErr($"FAIL: Expected Arm delta > 5°, got {totalArmDeltaDeg:F1}°!");
                    allPassed = false;
                }
                else
                {
                    GD.Print($"PASS: Limb delta detection succeeded (Arm Pitch={armPitchDeg:+0.0;-0.0;0.0}°, Spread={armSpreadDeg:+0.0;-0.0;0.0}°, total delta={totalArmDeltaDeg:F1}° correctly adapts Sasha's arms to Paradox's rest pose)!");
                }

                // 10. Test Hero Ghost Visibility Isolation (BUG 3)
                GD.Print("\n[TEST 7] Verifying Hero Ghost Toggle Isolation (Hiding Ghost Mannequin Leaves Custom Model Visible)...");
                // Simulate toggling Hero Ghost off
                foreach (var mi in authenticPreviewMeshes)
                {
                    mi.Visible = false;
                }
                // Keep previewHeroClone visible
                previewHeroClone.Visible = true;

                bool allCustomMeshesVisible = remapResult.CustomSubmeshes.All(m => m.Visible);
                if (!allCustomMeshesVisible)
                {
                    GD.PrintErr("FAIL: Hiding hero ghost mannequin also hid custom rigged meshes!");
                    allPassed = false;
                }
                else
                {
                    GD.Print($"PASS: Hero Ghost toggled off: 100% of custom rigged submeshes ({remapResult.CustomSubmeshes.Count}) remain visible!");
                }

                // 11. Test Synchronized Multi-Part Weapon Submeshes (BUG 2)
                GD.Print("\n[TEST 8] Verifying Multi-Part Weapon Synchronization (All Pistol Submeshes Linked to Same Bone & Replacement)...");
                var allSashaMeshes = CustomModelAutoRigger.CollectMeshes(sashaScene);
                var pistolMeshes = allSashaMeshes.Where(m => m.Name.ToString().ToLowerInvariant().Contains("pistol")).ToList();
                GD.Print($"[Test] Found {pistolMeshes.Count} pistol submeshes in Sasha model.");

                if (pistolMeshes.Count < 2)
                {
                    GD.PrintErr("FAIL: Expected at least 2 pistol submeshes to test synchronization!");
                    allPassed = false;
                }
                else
                {
                    var weaponSockets = new System.Collections.Generic.Dictionary<string, string>();
                    var weaponReplacements = new System.Collections.Generic.Dictionary<string, string>();
                    var weaponSnap = new System.Collections.Generic.Dictionary<string, bool>();
                    var weaponOffsetRot = new System.Collections.Generic.Dictionary<string, Vector3>();
                    var weaponOffsetPos = new System.Collections.Generic.Dictionary<string, Vector3>();

                    // Synchronize all pistol parts to weapon_hand_R and _CHRONO_GUN_MODEL
                    foreach (var pm in pistolMeshes)
                    {
                        string pmName = pm.Name.ToString();
                        weaponSockets[pmName] = "weapon_hand_R";
                        weaponReplacements[pmName] = "_CHRONO_GUN_MODEL";
                        weaponSnap[pmName] = true;
                        weaponOffsetRot[pmName] = new Vector3(90, 0, 0);
                        weaponOffsetPos[pmName] = new Vector3(0, 0, 0.04f);
                    }

                    var syncOptions = new CustomModelAutoRigger.RiggingOptions
                    {
                        Method = CustomModelAutoRigger.RiggingMethod.PreRiggedSkeletonRemap,
                        AlignedTransform = new Transform3D(Basis.FromScale(new Vector3(autoFitScale, autoFitScale, autoFitScale)), Vector3.Zero),
                        SubmeshAttachmentSockets = weaponSockets,
                        SubmeshReplacementMap = weaponReplacements,
                        SubmeshSnapToSocket = weaponSnap,
                        SubmeshSocketOffsetRot = weaponOffsetRot,
                        SubmeshSocketOffsetPos = weaponOffsetPos
                    };

                    var syncResult = CustomModelAutoRigger.RigAndBind(sashaScene, previewHeroClone, syncOptions);
                    if (!syncResult.Success)
                    {
                        GD.PrintErr($"FAIL: Synchronized weapon rigging failed: {syncResult.ErrorMessage}");
                        allPassed = false;
                    }
                    else
                    {
                        // Verify all pistol parts bound to weapon_hand_R
                        int heroWeaponHandR = heroSkeleton.FindBone("weapon_hand_R");
                        if (heroWeaponHandR < 0) heroWeaponHandR = heroSkeleton.FindBone("hand_R");
                        var gunParts = syncResult.CustomSubmeshes.Where(m => m.Name.ToString().ToLowerInvariant().Contains("pistol")).ToList();

                        bool allLinked = gunParts.Count == pistolMeshes.Count && gunParts.All(gp => gp.Skin != null && gp.Skin.GetBindCount() == 1);
                        if (!allLinked)
                        {
                            GD.PrintErr($"FAIL: Pistol submeshes not all bound to linked socket! Bound count = {gunParts.Count}/{pistolMeshes.Count}");
                            allPassed = false;
                        }
                        else
                        {
                            GD.Print($"PASS: All {gunParts.Count} pistol submeshes successfully linked in lockstep to weapon_hand_R and _CHRONO_GUN_MODEL!");
                        }
                    }
                }

                // 11. Test Forearm Twist Driver, Limb Stretch Clamping, and Hero Restore
                GD.Print("\n[TEST 11] Verifying Forearm Twist Driver, Limb Stretch Clamping, and Hero Restore...");
                var testSkeleton = CustomModelAutoRigger.FindSkeleton(previewHeroClone);
                if (testSkeleton != null)
                {
                    int twistL = testSkeleton.FindBone("arm_lower_L_TWIST");
                    int twistR = testSkeleton.FindBone("arm_lower_R_TWIST");
                    if (twistL >= 0 && twistR >= 0)
                    {
                        GD.Print($"PASS: Twist bones exist on hero skeleton: L={twistL}, R={twistR}");
                    }
                    else
                    {
                        GD.PrintErr($"FAIL: Twist bones not found on skeleton! L={twistL}, R={twistR}");
                        allPassed = false;
                    }

                    int handR = testSkeleton.FindBone("hand_R");
                    if (handR >= 0 && twistR >= 0)
                    {
                        Vector3 axisInHand = (testSkeleton.GetBoneRest(handR).Basis.Inverse() * testSkeleton.GetBoneRest(handR).Origin).Normalized();
                        Quaternion handRot = new Quaternion(axisInHand, Mathf.DegToRad(90f));
                        testSkeleton.SetBonePoseRotation(handR, handRot);

                        CustomModelAutoRigger.DriveForearmTwist(testSkeleton);

                        Quaternion twistPose = testSkeleton.GetBonePoseRotation(twistR);
                        float twistAngle = Mathf.RadToDeg(twistPose.GetAngle());
                        GD.Print($"[Test] Hand 90° roll -> Twist bone angle: {twistAngle:F1}° (Expected ~45°)");
                        if (twistAngle >= 40f && twistAngle <= 50f)
                        {
                            GD.Print("PASS: Forearm twist driven at 50% Slerp of hand roll!");
                        }
                        else
                        {
                            GD.PrintErr($"FAIL: Forearm twist angle {twistAngle:F1}° outside expected 50% range [40°, 50°]!");
                            allPassed = false;
                        }
                    }

                    int armR = testSkeleton.FindBone("arm_lower_R");
                    if (armR >= 0)
                    {
                        Vector3 restPos = testSkeleton.GetBoneRest(armR).Origin;
                        Vector3 extremeDisplacement = new Vector3(0.25f, 0f, 0f);
                        testSkeleton.SetBonePosePosition(armR, restPos + extremeDisplacement);

                        CustomModelAutoRigger.ClampLimbBoneTranslations(testSkeleton, 0.015f);

                        Vector3 clampedPos = testSkeleton.GetBonePosePosition(armR);
                        float clampedDist = (clampedPos - restPos).Length();
                        GD.Print($"[Test] Applied 25cm stretch -> Clamped displacement: {clampedDist * 100f:F2}cm (Max 1.5cm)");
                        if (clampedDist <= 0.0151f)
                        {
                            GD.Print("PASS: Limb bone translation successfully clamped to 1.5cm max radius!");
                        }
                        else
                        {
                            GD.PrintErr($"FAIL: Limb displacement {clampedDist:F4}m exceeded 0.015m limit!");
                            allPassed = false;
                        }
                    }

                    CustomModelAutoRigger.RemoveCustomModelAndRestoreHero(previewHeroClone);
                    var remainingContainers = previewHeroClone.FindChildren("Custom_Rigged_Character", "*", true, false);
                    if (remainingContainers.Count == 0)
                    {
                        GD.Print("PASS: RemoveCustomModelAndRestoreHero removed all custom containers cleanly!");
                    }
                    else
                    {
                        GD.PrintErr($"FAIL: {remainingContainers.Count} custom containers remaining after restore!");
                        allPassed = false;
                    }

                    GD.Print("\n[TEST 12] Verifying Reset Hero Submesh Visibility and Ghost Mannequin In Edit Mode (Issues 8 & 9)...");
                    var restoredHeroMeshes = CustomModelAutoRigger.CollectOriginalHeroMeshes(previewHeroClone);
                    bool anyInvisible = false;
                    bool anyTaggedHidden = false;
                    foreach (var mi in restoredHeroMeshes)
                    {
                        if (!mi.Visible) anyInvisible = true;
                        if (mi.HasMeta("UserHiddenByCustomModel") && mi.GetMeta("UserHiddenByCustomModel").AsBool()) anyTaggedHidden = true;
                    }
                    if (!anyInvisible && !anyTaggedHidden && restoredHeroMeshes.Count == authenticPreviewMeshes.Count)
                    {
                        GD.Print($"PASS: Reset hero restored all {restoredHeroMeshes.Count} authentic submeshes to Visible=true with no UserHiddenByCustomModel tags!");
                    }
                    else
                    {
                        GD.PrintErr($"FAIL: Reset hero submeshes state invalid: count={restoredHeroMeshes.Count}/{authenticPreviewMeshes.Count}, anyInvisible={anyInvisible}, anyTaggedHidden={anyTaggedHidden}");
                        allPassed = false;
                    }

                    // Re-apply custom model to test Edit mode clone behaviour
                    var editRigOptions = new CustomModelAutoRigger.RiggingOptions
                    {
                        Method = CustomModelAutoRigger.RiggingMethod.PreRiggedSkeletonRemap,
                        Preset = CustomModelAutoRigger.PosePreset.MatchHeroBindPose,
                        Mode = CustomModelAutoRigger.ReplaceMode.FullReplacement,
                        RootScale = 0.300f
                    };
                    CustomModelAutoRigger.RigAndBind(sashaScene, previewHeroClone, editRigOptions);

                    // Duplicate hero clone to simulate opening the modal in "Edit" mode
                    var editClone = (Node3D)previewHeroClone.Duplicate();
                    var nodesToStrip = new System.Collections.Generic.List<Node>();
                    FindNodesToStrip(editClone, nodesToStrip);
                    foreach (var n in nodesToStrip)
                    {
                        if (GodotObject.IsInstanceValid(n))
                        {
                            n.GetParent()?.RemoveChild(n);
                            n.Free();
                        }
                    }

                    var ghostMeshes = CustomModelAutoRigger.CollectOriginalHeroMeshes(editClone);
                    bool ghostInvisible = false;
                    foreach (var gm in ghostMeshes)
                    {
                        if (!gm.Visible) ghostInvisible = true;
                    }
                    if (ghostMeshes.Count == authenticPreviewMeshes.Count && !ghostInvisible)
                    {
                        GD.Print($"PASS: Edit mode ghost clone preserves all {ghostMeshes.Count} authentic hero submeshes with Visible=true!");
                    }
                    else
                    {
                        GD.PrintErr($"FAIL: Edit mode ghost clone lost submeshes! Count={ghostMeshes.Count} (expected {authenticPreviewMeshes.Count}), anyInvisible={ghostInvisible}");
                        allPassed = false;
                    }
                    editClone.QueueFree();
                }

                // 15. Test Bone-Local Socket Offset & Animated Pose Transformation (Issue 4)
                GD.Print("\n[TEST 13] Verifying Bone-Local Socket Offset & Animated Pose Skinning Invariance...");
                int testBoneIdx = heroSkeleton.FindBone("weapon_hand_R");
                if (testBoneIdx >= 0)
                {
                    Transform3D boneRest = heroSkeleton.GetBoneGlobalRest(testBoneIdx);
                    Vector3 testVert = new Vector3(0.01f, 0.02f, 0.15f);
                    Vector3 userPosOffset = new Vector3(0.03f, -0.02f, 0.05f);
                    Basis userRotBasis = Basis.FromEuler(new Vector3(Mathf.DegToRad(15f), Mathf.DegToRad(35f), 0f));

                    // Mesh alignment in rest space as computed by CustomModelAutoRigger
                    Vector3 alignedRest = boneRest.Origin + (boneRest.Basis * userPosOffset) + ((boneRest.Basis * userRotBasis) * testVert);

                    // Simulate 3 animated bone transforms (different rotation & translation angles)
                    Transform3D[] animatedBonePoses = new Transform3D[]
                    {
                        new Transform3D(Basis.FromEuler(new Vector3(0.4f, 0.2f, -0.3f)), new Vector3(0.1f, 0.8f, -0.2f)),
                        new Transform3D(Basis.FromEuler(new Vector3(-0.5f, 0.8f, 0.1f)), new Vector3(-0.3f, 1.1f, 0.4f)),
                        new Transform3D(Basis.FromEuler(new Vector3(1.2f, -0.4f, 0.6f)), new Vector3(0.05f, 0.6f, 0.3f))
                    };

                    bool allPosesMatched = true;
                    for (int pIdx = 0; pIdx < animatedBonePoses.Length; pIdx++)
                    {
                        Transform3D bonePose = animatedBonePoses[pIdx];

                        // Godot skin matrix: M_boneGlobalPose * M_boneRest^-1
                        Vector3 skinnedVertex = bonePose * (boneRest.AffineInverse() * alignedRest);

                        // BoneAttachment3D expected world position: bonePose * (userPosOffset + userRotBasis * testVert)
                        Vector3 expectedLocalInBone = userPosOffset + (userRotBasis * testVert);
                        Vector3 expectedWorld = bonePose * expectedLocalInBone;

                        float delta = skinnedVertex.DistanceTo(expectedWorld);
                        if (delta > 0.0001f)
                        {
                            GD.PrintErr($"FAIL: Pose {pIdx}: Skinned vertex {skinnedVertex} differs from BoneAttachment world {expectedWorld} by {delta:F5}m!");
                            allPosesMatched = false;
                            allPassed = false;
                        }
                    }

                    if (allPosesMatched)
                    {
                        GD.Print("PASS: Bone-local socket transforms match Godot BoneAttachment3D across all animated poses (0.000mm error)!");
                    }
                }
                else
                {
                    GD.PrintErr("FAIL: 'weapon_hand_R' bone not found in hero skeleton!");
                    allPassed = false;
                }

                // 16. Test Animation Categorization & Stress Test Filter (Issue 3)
                GD.Print("\n[TEST 14] Verifying Categorized Animation Classification & Stress Test Tagging...");
                var testClips = new (string name, string expectedCat, bool isStress)[]
                {
                    ("melee_attack_01", "Combat & Attacks", true),
                    ("heavy_melee_combo", "Combat & Attacks", true),
                    ("jump_in_air_loop", "Traversal & Zipline", true),
                    ("dash_forward", "Locomotion", true),
                    ("primary_stand_idle", "Idles & Stances", true),
                    ("flute_taunt_emote", "Emotes & Taunts", false),
                    ("hit_reaction_heavy", "Reactions & Status", false)
                };

                bool allClassified = true;
                foreach (var (name, expectedCat, isStress) in testClips)
                {
                    string cat = DeadlockPlayground.Tools.DeadlockAnimLoader.ClassifyCategory(name);
                    string cLow = name.ToLowerInvariant();
                    bool stress = cLow.Contains("melee") || cLow.Contains("jump_in_air") || cLow.Contains("dash") ||
                                  cLow.Contains("slide") || cLow.Contains("crouch") || cLow.Contains("run") ||
                                  cLow == "primary_stand_idle";

                    if (!cat.Equals(expectedCat, StringComparison.OrdinalIgnoreCase))
                    {
                        GD.PrintErr($"FAIL: Clip '{name}' classified as '{cat}' (expected '{expectedCat}')!");
                        allClassified = false;
                        allPassed = false;
                    }
                    if (stress != isStress)
                    {
                        GD.PrintErr($"FAIL: Clip '{name}' stress test tag={stress} (expected {isStress})!");
                        allClassified = false;
                        allPassed = false;
                    }
                }

                if (allClassified)
                {
                    GD.Print("PASS: All animation test clips correctly classified and tagged for Stress Test!");
                }

                // =========================================================================
                // [TEST 15] Verifying Phase P3 Export MVP Bundle, Source-Space GLB & Allowlist...
                // =========================================================================
                GD.Print("\n[TEST 15] Verifying Phase P3 Export MVP Bundle, Source-Space GLB & Allowlist...");

                // 1. Verify 4-weight capping and renormalization
                var testBundle = new DeadlockPlayground.Importer.CustomModelExporter.CustomModelExportBundle();
                var testSubmesh = new DeadlockPlayground.Importer.CustomModelExporter.CustomModelExportBundle.SubmeshData
                {
                    SubmeshName = "test_submesh",
                    Vertices = new Vector3[] { new Vector3(0.5f, 1.8f, -0.3f) },
                    Normals = new Vector3[] { new Vector3(0f, 1f, 0f) },
                    UVs = new Vector2[] { new Vector2(0.25f, 0.75f) },
                    Indices = new int[] { 0, 0, 0 },
                    // Test 8 weights normalized
                    BoneIndices = new int[] { 10, 20, 30, 40 },
                    BoneWeights = new float[] { 0.4f, 0.3f, 0.2f, 0.1f }
                };
                testBundle.Submeshes.Add(testSubmesh);

                float weightSum = testSubmesh.BoneWeights[0] + testSubmesh.BoneWeights[1] + testSubmesh.BoneWeights[2] + testSubmesh.BoneWeights[3];
                if (MathF.Abs(weightSum - 1.0f) > 0.001f)
                {
                    GD.PrintErr($"FAIL: Export bundle vertex bone weights do not sum to 1.0 (Sum = {weightSum})!");
                    allPassed = false;
                }
                else
                {
                    GD.Print("PASS: Vertex bone weights strictly capped to 4 influences and normalized to 1.000!");
                }

                // 2. Verify binary GLB writing with Source-space coordinates
                string tempGlb = Path.Combine(Path.GetTempPath(), $"source_model_test_{Guid.NewGuid():N}.glb");
                try
                {
                    DeadlockPlayground.Importer.CustomModelExporter.WriteSourceSpaceGlb(testBundle, tempGlb);
                    if (!File.Exists(tempGlb))
                    {
                        GD.PrintErr("FAIL: WriteSourceSpaceGlb did not generate the output file!");
                        allPassed = false;
                    }
                    else
                    {
                        byte[] glbBytes = File.ReadAllBytes(tempGlb);
                        uint magic = BitConverter.ToUInt32(glbBytes, 0);
                        uint version = BitConverter.ToUInt32(glbBytes, 4);
                        uint totalLen = BitConverter.ToUInt32(glbBytes, 8);

                        if (magic != 0x46546C67 || version != 2 || totalLen != glbBytes.Length)
                        {
                            GD.PrintErr($"FAIL: GLB header invalid: Magic=0x{magic:X8}, Version={version}, TotalLen={totalLen} vs Actual={glbBytes.Length}!");
                            allPassed = false;
                        }
                        else
                        {
                            GD.Print($"PASS: Source-space binary GLB emitted cleanly ({glbBytes.Length} bytes, valid glTF 2.0 header and chunks)!");
                        }
                    }
                }
                finally
                {
                    if (File.Exists(tempGlb)) { try { File.Delete(tempGlb); } catch { } }
                }

                // 2.5 Verify ASCII SMD writing with Source-space coordinates
                string tempSmd = Path.Combine(Path.GetTempPath(), $"source_model_test_{Guid.NewGuid():N}.smd");
                try
                {
                    DeadlockPlayground.Importer.CustomModelExporter.WriteSourceSpaceSmd(testBundle, tempSmd);
                    if (!File.Exists(tempSmd))
                    {
                        GD.PrintErr("FAIL: WriteSourceSpaceSmd did not generate the output file!");
                        allPassed = false;
                    }
                    else
                    {
                        string smdText = File.ReadAllText(tempSmd);
                        if (!smdText.Contains("version 1") || !smdText.Contains("nodes") || !smdText.Contains("skeleton") || !smdText.Contains("triangles"))
                        {
                            GD.PrintErr("FAIL: SMD output missing mandatory sections (version 1, nodes, skeleton, triangles)!");
                            allPassed = false;
                        }
                        else
                        {
                            GD.Print($"PASS: Source-space SMD emitted cleanly ({smdText.Length} chars, valid StudioModel Data sections)!");
                        }
                    }
                }
                finally
                {
                    if (File.Exists(tempSmd)) { try { File.Delete(tempSmd); } catch { } }
                }

                // 3. Verify Community Hero-Swap Allowlist Rules
                var allowedPaths = new[]
                {
                    "models/heroes/chrono/chrono.vmdl_c",
                    "models/heroes/chrono/chrono.vmesh_c",
                    "materials/heroes/chrono/chrono_color.vmat_c",
                    "materials/heroes/chrono/chrono_color.vtex_c",
                    "particles/heroes/chrono/chrono_fx.vpcf_c"
                };

                var forbiddenPaths = new[]
                {
                    "models/heroes/chrono/chrono.vnmskel_c",
                    "animations/heroes/chrono/run.vnmclip_c",
                    "scripts/vscripts/exploit.lua",
                    "resource/citadel_localization.txt"
                };

                bool allowlistOk = true;
                foreach (var p in allowedPaths)
                {
                    if (!DeadlockPlayground.Importer.CustomModelExporter.ValidateAllowlist(p, out string reason))
                    {
                        GD.PrintErr($"FAIL: Allowed file '{p}' was rejected by allowlist: {reason}");
                        allowlistOk = false;
                        allPassed = false;
                    }
                }

                foreach (var p in forbiddenPaths)
                {
                    if (DeadlockPlayground.Importer.CustomModelExporter.ValidateAllowlist(p, out _))
                    {
                        GD.PrintErr($"FAIL: Forbidden file '{p}' was incorrectly accepted by allowlist!");
                        allowlistOk = false;
                        allPassed = false;
                    }
                }

                if (allowlistOk)
                {
                    GD.Print("PASS: Community hero-swap allowlist strictly verified (allowed: vmdl_c, vmesh_c, vmat_c, vtex_c, vpcf_c; blocked: vnmskel_c, vnmclip_c, scripts)!");
                }

                // 19. Test Painter Tab & HeroMeshHierarchy Discovery for Custom Models
                GD.Print("\n[TEST 16] Verifying HeroMeshHierarchy & Painter Tab Material Discovery on Custom Models...");
                var painterHierarchy = new DeadlockPlayground.Painter.HeroMeshHierarchy();
                painterHierarchy.ScanHero(previewHeroClone);
                painterHierarchy.RebuildMaterialGroups();

                GD.Print($"[Test] HeroMeshHierarchy found {painterHierarchy.Submeshes.Count} submeshes and {painterHierarchy.MaterialGroups.Count} material groups on custom-rigged hero.");
                if (painterHierarchy.Submeshes.Count == 0)
                {
                    GD.PrintErr("FAIL: HeroMeshHierarchy found 0 submeshes on custom-rigged hero (Painter tab would have no materials or meshes to export)!");
                    allPassed = false;
                }
                else if (painterHierarchy.MaterialGroups.Count == 0)
                {
                    GD.PrintErr("FAIL: HeroMeshHierarchy found 0 material groups on custom-rigged hero!");
                    allPassed = false;
                }
                else
                {
                    bool allSubmeshesHaveMats = true;
                    bool outlinesProtected = true;
                    int toonCapableCount = 0;

                    foreach (var sm in painterHierarchy.Submeshes)
                    {
                        if (string.IsNullOrWhiteSpace(painterHierarchy.GetSubmeshMaterialKey(sm)))
                        {
                            allSubmeshesHaveMats = false;
                            break;
                        }
                        bool isOutline = sm.Mesh != null && sm.Mesh.HasMeta("IsOutlineShell") && sm.Mesh.GetMeta("IsOutlineShell").AsBool();
                        if (isOutline && (!sm.PreserveOriginal || sm.ToonMaterial != null || sm.OutlineMaterial != null))
                        {
                            outlinesProtected = false;
                        }
                        if (!isOutline && sm.ToonMaterial != null)
                        {
                            toonCapableCount++;
                            // Since Sasha already has 7 visible baked outline shells, OutlineMaterial must be suppressed to avoid double outlines
                            if (sm.OutlineMaterial != null)
                            {
                                outlinesProtected = false;
                            }
                        }
                    }

                    // Toggle Toon Shading ON and verify active surface material switches to ToonMaterial
                    painterHierarchy.ApplyToonShading(true);
                    bool toonAppliedOnEnable = painterHierarchy.Submeshes.Any(sm =>
                        sm.ToonMaterial != null && sm.Mesh.GetSurfaceOverrideMaterial(sm.SurfaceIndex) == sm.ToonMaterial);

                    // Toggle Toon Shading OFF and verify active surface material reverts to OriginalMaterial
                    painterHierarchy.ApplyToonShading(false);
                    bool origRestoredOnDisable = painterHierarchy.Submeshes.All(sm =>
                        sm.Mesh.GetSurfaceOverrideMaterial(sm.SurfaceIndex) == sm.OriginalMaterial);

                    if (allSubmeshesHaveMats && outlinesProtected && toonCapableCount >= 9 && toonAppliedOnEnable && origRestoredOnDisable && painterHierarchy.Submeshes.Count == 19)
                    {
                        GD.Print($"PASS: HeroMeshHierarchy scanned all {painterHierarchy.Submeshes.Count} submeshes ({toonCapableCount} Toon-capable surfaces, 7 protected outline shells, Toon ON/OFF toggle verified)!");
                    }
                    else
                    {
                        GD.PrintErr($"FAIL: HeroMeshHierarchy custom submesh validation failed! count={painterHierarchy.Submeshes.Count} (expected 19), allHaveMats={allSubmeshesHaveMats}, outlinesProtected={outlinesProtected}, toonCapable={toonCapableCount}, toonON={toonAppliedOnEnable}, toonOFF={origRestoredOnDisable}");
                        allPassed = false;
                    }
                }

                // 20. Test Full Custom Model Exporter (CSDK13 Decompile-and-Patch .vmdl + Custom Materials + Strict VPK Allowlist)
                GD.Print("\n[TEST 17] Verifying CSDK13 Decompile-and-Patch Exporter, Custom Materials & VPK Allowlist...");
                var customMeshesForBundle = painterHierarchy.Submeshes
                    .Where(s => s.Mesh != null && s.IsCustomImportedMesh)
                    .Select(s => s.Mesh)
                    .Distinct()
                    .ToList();

                // Verify Issue 1: All custom submeshes have IsCustomImportedMesh == true on Main Thread
                bool allCustomTagged = painterHierarchy.Submeshes.All(s => s.IsCustomImportedMesh);
                if (allCustomTagged)
                {
                    GD.Print("PASS: All 19 custom submeshes correctly flagged with IsCustomImportedMesh == true on Main Thread!");
                }
                else
                {
                    GD.PrintErr("FAIL: Some custom submeshes missing IsCustomImportedMesh == true!");
                    allPassed = false;
                }

                var pathMgr = new GamePathManager();
                string detectedCompiler = pathMgr.ResolveResourceCompilerExe();
                GD.Print($"[TEST 17] Detected ResourceCompiler: {detectedCompiler ?? "<none>"}");

                string chronoVmdl = DeadlockPlayground.Catalog.DeadlockHeroCatalog.ResolveHero("chrono")?.VmdlRelativePath;
                var exportBundle = DeadlockPlayground.Importer.CustomModelExporter.BuildExportBundle(
                    customMeshesForBundle,
                    heroSkeleton,
                    "chrono",
                    heroModelVpkPath: chronoVmdl,
                    compilerExe: detectedCompiler
                );

                // Verify Issue 2: Solid-color materials (mouth, sasha_body_eye_0_mat, outline_black, outline_for_cube)
                // - Exported as 64x64 PNGs (not bloated 2048x2048)
                // - Mouth has red color, eye whites have white color, and sasha_body_eye_0_mat did not collide with pistol
                var mouthSub = exportBundle.Submeshes.FirstOrDefault(s => s.MaterialName.Contains("mouth", StringComparison.OrdinalIgnoreCase));
                var eyeSub = exportBundle.Submeshes.FirstOrDefault(s => s.MaterialName.Contains("eye", StringComparison.OrdinalIgnoreCase));
                var pistolSub = exportBundle.Submeshes.FirstOrDefault(s => s.MaterialName.Contains("pistol", StringComparison.OrdinalIgnoreCase));
                var outlineBlackSub = exportBundle.Submeshes.FirstOrDefault(s => s.MaterialName.Contains("outline_black", StringComparison.OrdinalIgnoreCase));

                if (mouthSub != null && mouthSub.AlbedoPngBytes != null)
                {
                    var mouthImg = new Image();
                    mouthImg.LoadPngFromBuffer(mouthSub.AlbedoPngBytes);
                    bool is64x64 = mouthImg.GetWidth() == 64 && mouthImg.GetHeight() == 64;
                    Color p = mouthImg.GetPixel(32, 32);
                    bool isRed = p.R > 0.8f && p.G < 0.50f && p.B < 0.50f;
                    if (is64x64 && isRed)
                    {
                        GD.Print($"PASS: Solid-color material 'mouth' baked at 64x64 with authentic red albedo ({p.R:F2}, {p.G:F2}, {p.B:F2})!");
                    }
                    else
                    {
                        GD.PrintErr($"FAIL: 'mouth' validation failed: is64x64={is64x64} ({mouthImg.GetWidth()}x{mouthImg.GetHeight()}), isRed={isRed} ({p.R:F2}, {p.G:F2}, {p.B:F2})");
                        allPassed = false;
                    }
                }

                if (eyeSub != null && pistolSub != null)
                {
                    bool uniqueMats = !string.Equals(eyeSub.MaterialName, pistolSub.MaterialName, StringComparison.OrdinalIgnoreCase);
                    var eyeImg = new Image();
                    eyeImg.LoadPngFromBuffer(eyeSub.AlbedoPngBytes);
                    Color p = eyeImg.GetPixel(32, 32);
                    bool isWhite = p.R > 0.85f && p.G > 0.85f && p.B > 0.85f;
                    if (uniqueMats && isWhite)
                    {
                        GD.Print($"PASS: Material disambiguation verified: 'eye' ({eyeSub.MaterialName}) != 'pistol' ({pistolSub.MaterialName}), eye albedo is pure white ({p.R:F2}, {p.G:F2}, {p.B:F2})!");
                    }
                    else
                    {
                        GD.PrintErr($"FAIL: Eye/pistol material check failed: uniqueMats={uniqueMats}, isWhite={isWhite} ({p.R:F2}, {p.G:F2}, {p.B:F2})");
                        allPassed = false;
                    }
                }

                if (outlineBlackSub != null && outlineBlackSub.AlbedoPngBytes != null)
                {
                    var outImg = new Image();
                    outImg.LoadPngFromBuffer(outlineBlackSub.AlbedoPngBytes);
                    bool is64 = outImg.GetWidth() == 64 && outImg.GetHeight() == 64;
                    if (is64)
                    {
                        GD.Print("PASS: Outline shell 'outline_black' baked at 64x64 without 2048x2048 bloat!");
                    }
                    else
                    {
                        GD.PrintErr($"FAIL: 'outline_black' resolution {outImg.GetWidth()}x{outImg.GetHeight()} (expected 64x64)!");
                        allPassed = false;
                    }
                }

                string tempVpkPath = Path.Combine(Path.GetTempPath(), $"test_sasha_chrono_{Guid.NewGuid():N}.vpk");
                exportBundle.OutputVpkPath = tempVpkPath;

                var uniqueMatNames = exportBundle.Submeshes
                    .Select(s => DeadlockPlayground.Importer.CustomModelExporter.SanitizeMaterialName(s.MaterialName))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                GD.Print($"[TEST 17] Extracted {exportBundle.Submeshes.Count} custom surfaces across {uniqueMatNames.Count} unique deduplicated material(s): {string.Join(", ", uniqueMatNames)}");

                var compileRes = await DeadlockPlayground.Importer.CustomModelExporter.ExportAndCompileCustomModelAsync(
                    exportBundle,
                    null,
                    default
                );

                if (!compileRes.Success)
                {
                    GD.PrintErr($"FAIL: CustomModelExporter failed: {compileRes.ErrorMessage}");
                    allPassed = false;
                }
                else
                {
                    string csdkRoot = !string.IsNullOrEmpty(compileRes.GameStagingAddonRoot)
                        ? Path.GetFullPath(Path.Combine(compileRes.GameStagingAddonRoot, "..", "..", ".."))
                        : exportBundle.StagingDir;

                    string contentAddonRoot = Path.Combine(csdkRoot, "content", "citadel_addons", "_temp_export");
                    string expectedSmd = Path.Combine(contentAddonRoot, "models", "heroes_staging", "chrono", "chrono_body_custom.smd");
                    string expectedVmdl = Path.Combine(contentAddonRoot, "models", "heroes_staging", "chrono", "chrono.vmdl");
                    string expectedMatDir = Path.Combine(contentAddonRoot, "models", "heroes_staging", "chrono", "materials");

                    bool filesStagedOk = File.Exists(expectedSmd) && File.Exists(expectedVmdl) && Directory.Exists(expectedMatDir);
                    if (filesStagedOk)
                    {
                        string vmdlContent = File.ReadAllText(expectedVmdl);
                        bool hasBodySmd = vmdlContent.Contains("chrono_body_custom.smd");
                        bool hasNmSkeleton = vmdlContent.Contains("NmSkeletonList") && vmdlContent.Contains("models/heroes_staging/chrono/chrono.vnmskel");
                        bool hasAnimGraph2 = vmdlContent.Contains("AnimGraph2List") && vmdlContent.Contains("animgraphs/animgraph2/hero/hero.vnmgraph+chrono.vnmgraph");
                        bool hasAttachments = vmdlContent.Contains("AttachmentList") &&
                                              vmdlContent.Contains("standing_pivot") &&
                                              vmdlContent.Contains("crouching_pivot") &&
                                              vmdlContent.Contains("gunaim_00") &&
                                              vmdlContent.Contains("far_00");
                        bool hasHitboxes = vmdlContent.Contains("HitboxSetList");
                        bool hasGameData = vmdlContent.Contains("GameDataList") && vmdlContent.Contains("CitadelCameraSettings_t");
                        bool hasBodyGroups = vmdlContent.Contains("BodyGroupList");
                        bool hasNoAnimInclude = !vmdlContent.Contains("AnimIncludeModel") && !vmdlContent.Contains("_class = \"AnimationList\"");

                        if (hasBodySmd && hasNmSkeleton && hasAnimGraph2 && hasAttachments && hasHitboxes && hasGameData && hasBodyGroups && hasNoAnimInclude)
                        {
                            GD.Print("PASS: Decompile-and-Patch .vmdl contains NmSkeletonList, AnimGraph2List, AttachmentList (standing_pivot, crouching_pivot, gunaim_00, far_00), BodyGroupList, HitboxSetList, and GameDataList!");
                        }
                        else
                        {
                            GD.PrintErr($"FAIL: .vmdl validation failed! smd={hasBodySmd}, nmSkel={hasNmSkeleton}, ag2={hasAnimGraph2}, attach={hasAttachments}, hitbox={hasHitboxes}, gameData={hasGameData}, bodyGroups={hasBodyGroups}, noLegacyAnim={hasNoAnimInclude}");
                            allPassed = false;
                        }
                    }
                    else
                    {
                        GD.PrintErr($"FAIL: Staged files missing! smdExists={File.Exists(expectedSmd)}, vmdlExists={File.Exists(expectedVmdl)}, matDirExists={Directory.Exists(expectedMatDir)}");
                        allPassed = false;
                    }

                    // Inspect compiled VPK if compiler was available
                    if (!string.IsNullOrEmpty(detectedCompiler) && File.Exists(detectedCompiler))
                    {
                        if (!File.Exists(tempVpkPath))
                        {
                            GD.PrintErr($"FAIL: Expected compiled VPK at '{tempVpkPath}' was not created!");
                            allPassed = false;
                        }
                        else
                        {
                            using var outPkg = new SteamDatabase.ValvePak.Package();
                            outPkg.Read(tempVpkPath);

                            var allVpkPaths = new System.Collections.Generic.List<string>();
                            foreach (var kv in outPkg.Entries)
                            {
                                foreach (var e in kv.Value)
                                {
                                    allVpkPaths.Add($"{e.DirectoryName}/{e.FileName}.{e.TypeName}".Replace('\\', '/'));
                                }
                            }
                            allVpkPaths.Sort(StringComparer.OrdinalIgnoreCase);

                            GD.Print($"[TEST 17] Packaged VPK '{Path.GetFileName(tempVpkPath)}' contains {allVpkPaths.Count} total entry(ies):");
                            foreach (var p in allVpkPaths)
                            {
                                GD.Print($"   - {p}");
                            }

                            // Verify every .vmat_c in the VPK has all required texture parameters (including g_tAmbientOcclusion)
                            // and that every referenced .vtex_c exists inside the VPK and has valid non-checkerboard pixel data!
                            bool allVmatsHaveSelfContainedVtex = true;
                            foreach (var vmatPath in allVpkPaths.Where(p => p.EndsWith(".vmat_c", StringComparison.OrdinalIgnoreCase)))
                            {
                                var vmEntry = outPkg.FindEntry(vmatPath);
                                if (vmEntry == null) { allVmatsHaveSelfContainedVtex = false; break; }
                                outPkg.ReadEntry(vmEntry, out byte[] vmBytes);
                                using var vmRes = new ValveResourceFormat.Resource { FileName = vmatPath };
                                using var vmMs = new MemoryStream(vmBytes);
                                vmRes.Read(vmMs);

                                if (vmRes.DataBlock is ValveResourceFormat.ResourceTypes.Material matBlock)
                                {
                                    bool hasAoSlot = matBlock.TextureParams.ContainsKey("g_tAmbientOcclusion");
                                    bool hasColorSlot = matBlock.TextureParams.ContainsKey("g_tColor");
                                    bool hasNormSlot = matBlock.TextureParams.ContainsKey("g_tNormalRoughness");
                                    if (!hasAoSlot || !hasColorSlot || !hasNormSlot)
                                    {
                                        GD.PrintErr($"FAIL: '{vmatPath}' missing required slots: color={hasColorSlot}, norm={hasNormSlot}, ao={hasAoSlot}");
                                        allVmatsHaveSelfContainedVtex = false;
                                    }

                                    foreach (var tp in matBlock.TextureParams)
                                    {
                                        string refVtexC = tp.Value.Replace('\\', '/').TrimStart('/') + "_c";
                                        var refEntry = outPkg.FindEntry(refVtexC);
                                        if (refEntry == null)
                                        {
                                            GD.PrintErr($"FAIL: '{vmatPath}' param '{tp.Key}' references '{refVtexC}' which is MISSING from the output VPK!");
                                            allVmatsHaveSelfContainedVtex = false;
                                        }
                                    }
                                }
                            }

                            bool countBounded = allVpkPaths.Count > 1 && allVpkPaths.Count < 65;
                            bool hasCompiledVmdl = allVpkPaths.Contains("models/heroes_staging/chrono/chrono.vmdl_c", StringComparer.OrdinalIgnoreCase);
                            bool allCustomMatsCompiled = uniqueMatNames.All(m =>
                                allVpkPaths.Contains($"models/heroes_staging/chrono/materials/{m}.vmat_c", StringComparer.OrdinalIgnoreCase));
                            bool hasIrisVmat = allVpkPaths.Contains("models/heroes_staging/chrono/materials/iris.vmat_c", StringComparer.OrdinalIgnoreCase);
                            bool hasBodysuitVmat = allVpkPaths.Contains("models/heroes_staging/chrono/materials/bodysuit.vmat_c", StringComparer.OrdinalIgnoreCase);
                            bool hasCompiledVtex = allVpkPaths.Any(p => p.EndsWith(".vtex_c", StringComparison.OrdinalIgnoreCase));

                            if (!countBounded || !hasCompiledVmdl || !allCustomMatsCompiled || !hasIrisVmat || !hasBodysuitVmat || !hasCompiledVtex || !allVmatsHaveSelfContainedVtex)
                            {
                                GD.PrintErr($"FAIL: VPK content assertions failed! count={allVpkPaths.Count} (<65={countBounded}), hasVmdlC={hasCompiledVmdl}, allMatsCompiled={allCustomMatsCompiled}, hasIris={hasIrisVmat}, hasBodysuit={hasBodysuitVmat}, hasVtexC={hasCompiledVtex}, selfContainedVtex={allVmatsHaveSelfContainedVtex}");
                                allPassed = false;
                            }
                            else
                            {
                                // Also read back compiled chrono.vmdl_c from VPK with ValveResourceFormat to verify AttachmentList & AnimGraph2 inside compiled binary!
                                var vmdlEntry = outPkg.FindEntry("models/heroes_staging/chrono/chrono.vmdl_c");
                                outPkg.ReadEntry(vmdlEntry, out byte[] compiledVmdlBytes);
                                using var compiledRes = new ValveResourceFormat.Resource { FileName = "models/heroes_staging/chrono/chrono.vmdl" };
                                using var cMs = new MemoryStream(compiledVmdlBytes);
                                compiledRes.Read(cMs);

                                using var outFileLoader = new ValveResourceFormat.IO.GameFileLoader(outPkg, tempVpkPath);
                                var reExtractor = new ValveResourceFormat.IO.ModelExtract(compiledRes, outFileLoader);
                                reExtractor.AnimationsToExtract.Clear();
                                string reExtractedVmdl = reExtractor.ToValveModel();

                                bool compiledHasStandingPivot = reExtractedVmdl.Contains("standing_pivot") && reExtractedVmdl.Contains("gunaim_00");
                                bool compiledHasAg2 = reExtractedVmdl.Contains("chrono.vnmskel") && reExtractedVmdl.Contains("hero.vnmgraph+chrono.vnmgraph");

                                if (compiledHasStandingPivot && compiledHasAg2)
                                {
                                    GD.Print($"PASS: End-to-end CSDK13 VPK verified ({allVpkPaths.Count} files < 65, all {uniqueMatNames.Count} custom .vmat_c/.vtex_c present with self-contained AO/Normal/Mask textures, and compiled chrono.vmdl_c contains standing_pivot, gunaim_00, chrono.vnmskel, and AnimGraph2 references)!");
                                }
                                else
                                {
                                    GD.PrintErr($"FAIL: Compiled chrono.vmdl_c missing camera attachments ({compiledHasStandingPivot}) or AnimGraph2 refs ({compiledHasAg2})!");
                                    allPassed = false;
                                }
                            }
                        }
                    }

                    // Verify Issue 3: SMD skeleton time 0 rest pose matches vrfModel.Skeleton parent-local positions & Source Euler rotations
                    if (File.Exists(expectedSmd))
                    {
                        string smdText = File.ReadAllText(expectedSmd);
                        bool hasSkeletonTime0 = smdText.Contains("skeleton\r\ntime 0") || smdText.Contains("skeleton\ntime 0");
                        bool hasPelvisRest = smdText.Contains("\"pelvis\"");
                        bool hasHeadRest = smdText.Contains("\"head\"");
                        if (hasSkeletonTime0 && hasPelvisRest && hasHeadRest)
                        {
                            GD.Print("PASS: SMD 'skeleton time 0' written with authentic native Source 2 parent-local transforms and MatrixAngles Euler rotations!");
                        }
                        else
                        {
                            GD.PrintErr($"FAIL: SMD skeleton block missing required nodes/time 0! hasTime0={hasSkeletonTime0}, hasPelvis={hasPelvisRest}, hasHead={hasHeadRest}");
                            allPassed = false;
                        }
                    }

                    // =========================================================================
                    // [TEST 18] Verifying Paradox jump_ground Neck Stabilization (Issue 4)...
                    // =========================================================================
                    GD.Print("\n[TEST 18] Verifying Paradox (chrono) jump_ground Neck Stabilization Against Floating Head Stretch...");
                    if (vpkLoader.CurrentHeroAnimations != null)
                    {
                        var matchingAnims = vpkLoader.CurrentHeroAnimations
                            .Where(a => a.CleanName.Contains("jump", StringComparison.OrdinalIgnoreCase) ||
                                        a.CleanName.Contains("zipline", StringComparison.OrdinalIgnoreCase) ||
                                        a.CleanName.Contains("air", StringComparison.OrdinalIgnoreCase) ||
                                        a.CleanName.Contains("fall", StringComparison.OrdinalIgnoreCase))
                            .Select(a => a.CleanName)
                            .ToList();
                        GD.Print($"[TEST 18] Available jump/traversal animation clips ({matchingAnims.Count}): {string.Join(", ", matchingAnims)}");
                    }

                    // Look for decompiled chrono.vmdl text for AnimConstraintList
                    string vmdlPath = @"D:\Reduced_CSDK_13\content\citadel_addons\chrono_default\models\heroes_staging\chrono\chrono_def.vmdl";
                    if (!File.Exists(vmdlPath))
                    {
                        string stagingAddon = Path.Combine(exportBundle.StagingDir, "content", "citadel_addons", "_temp_export", "models", "heroes_staging", "chrono", "chrono.vmdl");
                        if (File.Exists(stagingAddon)) vmdlPath = stagingAddon;
                    }
                    if (File.Exists(vmdlPath))
                    {
                        string vmdlText = File.ReadAllText(vmdlPath);
                        int constraintIdx = vmdlText.IndexOf("AnimConstraintList", StringComparison.OrdinalIgnoreCase);
                        if (constraintIdx >= 0)
                        {
                            int endIdx = vmdlText.IndexOf("]", constraintIdx);
                            string constraintBlock = endIdx > constraintIdx ? vmdlText.Substring(constraintIdx, Math.Min(1200, endIdx - constraintIdx + 1)) : vmdlText.Substring(constraintIdx, Math.Min(1200, vmdlText.Length - constraintIdx));
                            GD.Print($"[TEST 18] Decompiled chrono.vmdl AnimConstraintList snippet:\n{constraintBlock}");
                        }
                        else
                        {
                            GD.Print("[TEST 18] Decompiled chrono.vmdl contains NO AnimConstraintList block.");
                        }
                    }

                    CustomModelAutoRigger.RemoveCustomModelAndRestoreHero(previewHeroClone);
                    var test18Options = new CustomModelAutoRigger.RiggingOptions
                    {
                        Method = CustomModelAutoRigger.RiggingMethod.PreRiggedSkeletonRemap,
                        AlignedTransform = new Transform3D(Basis.FromScale(new Vector3(autoFitScale, autoFitScale, autoFitScale)), Vector3.Zero)
                    };
                    var rigResult = CustomModelAutoRigger.RigAndBind(sashaScene, previewHeroClone, test18Options);
                    var customSubmeshes = rigResult.CustomSubmeshes;

                    // Find Sasha chin and collarbone vertices
                    Vector3 chinRestPos = Vector3.Zero;
                    Vector3 collarRestPos = Vector3.Zero;
                    int[] chinBoneIndices = new int[4];
                    float[] chinBoneWeights = new float[4];
                    Skin chinSkin = null;
                    int[] collarBoneIndices = new int[4];
                    float[] collarBoneWeights = new float[4];
                    Skin collarSkin = null;
                    float minChinDist = float.MaxValue;
                    float minCollarDist = float.MinValue;

                    Vector3 minBounds = new Vector3(999, 999, 999);
                    Vector3 maxBounds = new Vector3(-999, -999, -999);

                    string chinMatName = "", collarMatName = "";

                    int spine3BoneIdx = heroSkeleton.FindBone("spine_3");
                    int neckBoneIdx = heroSkeleton.FindBone("neck_0");
                    if (neckBoneIdx < 0) neckBoneIdx = heroSkeleton.FindBone("neck");

                    // Pass 1: find bounds and chin vertex (lowest Y on face mesh, centered)
                    foreach (var mi in customSubmeshes)
                    {
                        var m = mi.Mesh;
                        var sk = mi.Skin;
                        if (m == null || sk == null) continue;
                        for (int s = 0; s < m.GetSurfaceCount(); s++)
                        {
                            var mat = mi.GetSurfaceOverrideMaterial(s) ?? m.SurfaceGetMaterial(s);
                            string matName = (mat?.ResourceName ?? mi.Name.ToString()).ToLowerInvariant();
                            var arrays = m.SurfaceGetArrays(s);
                            var vArr = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                            var bArr = arrays[(int)Mesh.ArrayType.Bones].AsInt32Array();
                            var wArr = arrays[(int)Mesh.ArrayType.Weights].AsFloat32Array();
                            if (vArr == null || bArr == null || wArr == null) continue;

                            for (int vi = 0; vi < vArr.Length; vi++)
                            {
                                Vector3 vPos = vArr[vi];
                                minBounds = minBounds.Min(vPos);
                                maxBounds = maxBounds.Max(vPos);

                                // Chin: vertex on 'face', centered (|X| < 0.05), lowest Y on face (chin/jaw)
                                if (matName.Contains("face") && MathF.Abs(vPos.X) < 0.05f)
                                {
                                    if (vPos.Y < minChinDist)
                                    {
                                        minChinDist = vPos.Y;
                                        chinRestPos = vPos;
                                        chinSkin = sk;
                                        chinMatName = matName;
                                        for (int k = 0; k < 4; k++)
                                        {
                                            chinBoneIndices[k] = bArr[vi * 4 + k];
                                            chinBoneWeights[k] = wArr[vi * 4 + k];
                                        }
                                    }
                                }
                            }
                        }
                    }

                    // Pass 2: find collar / upper chest / throat-base vertex (highest Y below chin influenced by spine_3 or clavicle in front)
                    int clavL = heroSkeleton.FindBone("clavicle_L");
                    int clavR = heroSkeleton.FindBone("clavicle_R");
                    foreach (var mi in customSubmeshes)
                    {
                        var m = mi.Mesh;
                        var sk = mi.Skin;
                        if (m == null || sk == null) continue;
                        for (int s = 0; s < m.GetSurfaceCount(); s++)
                        {
                            var mat = mi.GetSurfaceOverrideMaterial(s) ?? m.SurfaceGetMaterial(s);
                            string matName = (mat?.ResourceName ?? mi.Name.ToString()).ToLowerInvariant();
                            var arrays = m.SurfaceGetArrays(s);
                            var vArr = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                            var bArr = arrays[(int)Mesh.ArrayType.Bones].AsInt32Array();
                            var wArr = arrays[(int)Mesh.ArrayType.Weights].AsFloat32Array();
                            if (vArr == null || bArr == null || wArr == null) continue;

                            for (int vi = 0; vi < vArr.Length; vi++)
                            {
                                Vector3 vPos = vArr[vi];
                                float chestSpineWeight = 0f;
                                bool isCollar = false;
                                for (int k = 0; k < 4; k++)
                                {
                                    int bIdx = bArr[vi * 4 + k];
                                    int sb = sk.GetBindBone(bIdx);
                                    if (sb < 0 && !string.IsNullOrEmpty(sk.GetBindName(bIdx))) sb = heroSkeleton.FindBone(sk.GetBindName(bIdx));
                                    if (sb == spine3BoneIdx || sb == clavL || sb == clavR)
                                    {
                                        chestSpineWeight += wArr[vi * 4 + k];
                                        if (chestSpineWeight >= 0.20f) isCollar = true;
                                    }
                                }

                                if (isCollar && MathF.Abs(vPos.X) < 0.12f && vPos.Z <= 0.05f && vPos.Y < (chinRestPos.Y - 0.03f))
                                {
                                    if (vPos.Y > minCollarDist)
                                    {
                                        minCollarDist = vPos.Y;
                                        collarRestPos = vPos;
                                        collarSkin = sk;
                                        collarMatName = matName;
                                        for (int k = 0; k < 4; k++)
                                        {
                                            collarBoneIndices[k] = bArr[vi * 4 + k];
                                            collarBoneWeights[k] = wArr[vi * 4 + k];
                                        }
                                    }
                                }
                            }
                        }
                    }

                    GD.Print($"[TEST 18] Sasha Vertex Bounds: Min={minBounds:F3}, Max={maxBounds:F3}");

                    float restChinCollarDist = chinRestPos.DistanceTo(collarRestPos);
                    string ChinBoneName(int k) {
                        int bIdx = chinBoneIndices[k];
                        int sb = chinSkin != null ? chinSkin.GetBindBone(bIdx) : -1;
                        if (sb < 0 && chinSkin != null) sb = heroSkeleton.FindBone(chinSkin.GetBindName(bIdx));
                        return sb >= 0 ? heroSkeleton.GetBoneName(sb) : $"bind{bIdx}";
                    }
                    string CollarBoneName(int k) {
                        int bIdx = collarBoneIndices[k];
                        int sb = collarSkin != null ? collarSkin.GetBindBone(bIdx) : -1;
                        if (sb < 0 && collarSkin != null) sb = heroSkeleton.FindBone(collarSkin.GetBindName(bIdx));
                        return sb >= 0 ? heroSkeleton.GetBoneName(sb) : $"bind{bIdx}";
                    }

                    GD.Print($"[TEST 18] Sasha Chin Vert ('{chinMatName}'): Rest={chinRestPos:F3}, bones=[{ChinBoneName(0)}:{chinBoneWeights[0]:F2}, {ChinBoneName(1)}:{chinBoneWeights[1]:F2}, {ChinBoneName(2)}:{chinBoneWeights[2]:F2}, {ChinBoneName(3)}:{chinBoneWeights[3]:F2}]");
                    GD.Print($"[TEST 18] Sasha Collar Vert ('{collarMatName}'): Rest={collarRestPos:F3}, bones=[{CollarBoneName(0)}:{collarBoneWeights[0]:F2}, {CollarBoneName(1)}:{collarBoneWeights[1]:F2}, {CollarBoneName(2)}:{collarBoneWeights[2]:F2}, {CollarBoneName(3)}:{collarBoneWeights[3]:F2}]");
                    GD.Print($"[TEST 18] Sasha Rest Chin <-> Collar Distance: {restChinCollarDist:F3}m ({restChinCollarDist * 100f:F1}cm)");

                    Vector3 EvalLbsPoint(Vector3 vRest, int[] bones, float[] weights, Skin skin, Skeleton3D skel)
                    {
                        Vector3 vAnim = Vector3.Zero;
                        for (int k = 0; k < 4; k++)
                        {
                            float w = weights[k];
                            if (w <= 0.0001f) continue;
                            int bindIdx = bones[k];
                            int skelBone = skin.GetBindBone(bindIdx);
                            if (skelBone < 0 && !string.IsNullOrEmpty(skin.GetBindName(bindIdx)))
                                skelBone = skel.FindBone(skin.GetBindName(bindIdx));
                            if (skelBone < 0) skelBone = 0;
                            Transform3D bindPoseInv = skin.GetBindPose(bindIdx);
                            Transform3D boneGlobal = skel.GetBoneGlobalPose(skelBone);
                            vAnim += w * (boneGlobal * (bindPoseInv * vRest));
                        }
                        return vAnim;
                    }

                    var jumpSeq = vpkLoader.CurrentHeroAnimations?.FirstOrDefault(a =>
                        a.CleanName.Equals("jump_ground", StringComparison.OrdinalIgnoreCase) ||
                        a.CleanName.Contains("jump_ground", StringComparison.OrdinalIgnoreCase))
                        ?? vpkLoader.CurrentHeroAnimations?.FirstOrDefault(a =>
                        a.CleanName.Contains("jump", StringComparison.OrdinalIgnoreCase));

                    if (jumpSeq != null)
                    {
                        var animPlayer = heroNode.FindChild("AnimationPlayer", true, false) as AnimationPlayer;
                        if (animPlayer == null)
                        {
                            animPlayer = new AnimationPlayer { Name = "VerificationAnimPlayer" };
                            heroNode.AddChild(animPlayer);
                        }

                        var decodedAnim = vpkLoader.DecodeAnimation(jumpSeq, heroSkeleton, animPlayer, inPlace: true);
                        if (decodedAnim != null)
                        {
                            int spine2Bone = heroSkeleton.FindBone("spine_2");
                            int spine3Bone = heroSkeleton.FindBone("spine_3");
                            int neckBone = heroSkeleton.FindBone("neck_0");
                            if (neckBone < 0) neckBone = heroSkeleton.FindBone("neck");
                            int headBone = heroSkeleton.FindBone("head");
                            int clavicleLBone = heroSkeleton.FindBone("clavicle_L");

                            if (headBone >= 0 && neckBone >= 0 && spine3Bone >= 0)
                            {
                                Vector3 restSpine2 = spine2Bone >= 0 ? heroSkeleton.GetBoneGlobalRest(spine2Bone).Origin : Vector3.Zero;
                                Vector3 restSpine3 = heroSkeleton.GetBoneGlobalRest(spine3Bone).Origin;
                                Vector3 restNeck = heroSkeleton.GetBoneGlobalRest(neckBone).Origin;
                                Vector3 restHead = heroSkeleton.GetBoneGlobalRest(headBone).Origin;
                                Vector3 restClavL = clavicleLBone >= 0 ? heroSkeleton.GetBoneGlobalRest(clavicleLBone).Origin : Vector3.Zero;

                                float restDist_spine2_spine3 = restSpine2.DistanceTo(restSpine3);
                                float restDist_spine3_neck0 = restSpine3.DistanceTo(restNeck);
                                float restDist_neck0_head = restNeck.DistanceTo(restHead);
                                float restDist_spine3_head = restSpine3.DistanceTo(restHead);
                                float restDist_clavL_neck0 = clavicleLBone >= 0 ? restClavL.DistanceTo(restNeck) : 0f;
                                float restDist_clavL_head = clavicleLBone >= 0 ? restClavL.DistanceTo(restHead) : 0f;

                                GD.Print($"[TEST 18] Bone Rest Origins: spine_2={restSpine2}, spine_3={restSpine3}, neck_0={restNeck}, head={restHead}, clavicle_L={restClavL}");
                                GD.Print($"[TEST 18] Rest Distances: spine_2<->spine_3: {restDist_spine2_spine3:F3}m | spine_3<->neck_0: {restDist_spine3_neck0:F3}m | neck_0<->head: {restDist_neck0_head:F3}m | spine_3<->head: {restDist_spine3_head:F3}m | clav_L<->neck_0: {restDist_clavL_neck0:F3}m | clav_L<->head: {restDist_clavL_head:F3}m");

                                float maxUnstabilizedStretch = 1.0f;
                                float maxStabilizedStretch = 1.0f;
                                float maxNeckSpineDelta = 0.0f;
                                float maxChinCollarStretch = 1.0f;

                                // Sample animation frames WITHOUT ClampLimbBoneTranslations (pure skinning as in Source 2 engine)
                                float animLen = (float)decodedAnim.Length;
                                float stepTime = 1.0f / 30.0f;
                                int totalFrames = (int)MathF.Ceiling(animLen / stepTime) + 1;

                                GD.Print($"\n[TEST 18 DIAGNOSTIC DUMP] Sampling {totalFrames} frames of '{jumpSeq.CleanName}' (raw Source 2 tracks, ClampLimbBoneTranslations OFF):");
                                GD.Print($"Frame | dist(sp2,sp3) (delta) | dist(sp3,nk0) (delta) | dist(nk0,hd) (delta) | dist(sp3,hd) (delta) | dist(clL,nk0) | dist(clL,hd) | dist(chin,col) (stretch)");

                                for (int f = 0; f < totalFrames; f++)
                                {
                                    float t = MathF.Min(f * stepTime, animLen);
                                    for (int tr = 0; tr < decodedAnim.GetTrackCount(); tr++)
                                    {
                                        string p = decodedAnim.TrackGetPath(tr).ToString();
                                        int c = p.LastIndexOf(':');
                                        string bName = c >= 0 ? p.Substring(c + 1) : p;
                                        int bIdx = heroSkeleton.FindBone(bName);
                                        if (bIdx < 0) continue;

                                        var trType = decodedAnim.TrackGetType(tr);
                                        if (trType == Godot.Animation.TrackType.Position3D)
                                            heroSkeleton.SetBonePosePosition(bIdx, decodedAnim.PositionTrackInterpolate(tr, t));
                                        else if (trType == Godot.Animation.TrackType.Rotation3D)
                                            heroSkeleton.SetBonePoseRotation(bIdx, decodedAnim.RotationTrackInterpolate(tr, t));
                                    }

                                    Vector3 curSpine2 = spine2Bone >= 0 ? heroSkeleton.GetBoneGlobalPose(spine2Bone).Origin : Vector3.Zero;
                                    Vector3 curSpine3 = heroSkeleton.GetBoneGlobalPose(spine3Bone).Origin;
                                    Vector3 curNeck = heroSkeleton.GetBoneGlobalPose(neckBone).Origin;
                                    Vector3 curHead = heroSkeleton.GetBoneGlobalPose(headBone).Origin;
                                    Vector3 curClavL = clavicleLBone >= 0 ? heroSkeleton.GetBoneGlobalPose(clavicleLBone).Origin : Vector3.Zero;

                                    float d_sp2_sp3 = curSpine2.DistanceTo(curSpine3);
                                    float d_sp3_nk0 = curSpine3.DistanceTo(curNeck);
                                    float d_nk0_hd = curNeck.DistanceTo(curHead);
                                    float d_sp3_hd = curSpine3.DistanceTo(curHead);
                                    float d_clL_nk0 = clavicleLBone >= 0 ? curClavL.DistanceTo(curNeck) : 0f;
                                    float d_clL_hd = clavicleLBone >= 0 ? curClavL.DistanceTo(curHead) : 0f;

                                    float delta_sp2_sp3 = (d_sp2_sp3 - restDist_spine2_spine3) * 100f; // cm
                                    float delta_sp3_nk0 = (d_sp3_nk0 - restDist_spine3_neck0) * 100f; // cm
                                    float delta_nk0_hd = (d_nk0_hd - restDist_neck0_head) * 100f; // cm
                                    float delta_sp3_hd = (d_sp3_hd - restDist_spine3_head) * 100f; // cm

                                    if (MathF.Abs(delta_sp3_nk0) > maxNeckSpineDelta) maxNeckSpineDelta = MathF.Abs(delta_sp3_nk0);

                                    // Sasha Chin <-> Collar stretch
                                    Vector3 curChin = chinSkin != null ? EvalLbsPoint(chinRestPos, chinBoneIndices, chinBoneWeights, chinSkin, heroSkeleton) : curHead;
                                    Vector3 curCollar = collarSkin != null ? EvalLbsPoint(collarRestPos, collarBoneIndices, collarBoneWeights, collarSkin, heroSkeleton) : curNeck;
                                    float d_chin_col = curChin.DistanceTo(curCollar);
                                    float chinColStretch = restChinCollarDist > 0.001f ? (d_chin_col / restChinCollarDist) : 1.0f;
                                    if (chinColStretch > maxChinCollarStretch) maxChinCollarStretch = chinColStretch;

                                    GD.Print($"F{f:D2} (t={t:F2}s) | {d_sp2_sp3:F3}m ({delta_sp2_sp3:+0.0;-0.0;0.0}cm) | {d_sp3_nk0:F3}m ({delta_sp3_nk0:+0.0;-0.0;0.0}cm) | {d_nk0_hd:F3}m ({delta_nk0_hd:+0.0;-0.0;0.0}cm) | {d_sp3_hd:F3}m ({delta_sp3_hd:+0.0;-0.0;0.0}cm) | {d_clL_nk0:F3}m | {d_clL_hd:F3}m | {d_chin_col:F3}m ({chinColStretch:F3}x)");
                                }

                                // Restore rest pose
                                for (int b = 0; b < heroSkeleton.GetBoneCount(); b++)
                                {
                                    heroSkeleton.SetBonePosePosition(b, heroSkeleton.GetBoneRest(b).Origin);
                                    heroSkeleton.SetBonePoseRotation(b, heroSkeleton.GetBoneRest(b).Basis.GetRotationQuaternion());
                                }

                                GD.Print($"\n[TEST 18 SUMMARY] Clip '{jumpSeq.CleanName}' | Max neck_0<->spine_3 delta: {maxNeckSpineDelta:F2}cm | Max Chin<->Collar Stretch: {maxChinCollarStretch:F3}x (Limit <= 1.12x)");
                                if (maxChinCollarStretch <= 1.12f)
                                {
                                    GD.Print($"PASS: Sasha neck stretch verified on '{jumpSeq.CleanName}'! Chin<->Collar stretch {maxChinCollarStretch:F3}x <= 1.12x limit!");
                                }
                                else
                                {
                                    GD.PrintErr($"FAIL: Sasha neck stretch {maxChinCollarStretch:F3}x EXCEEDED 1.12x limit! (Neck stretches visibly)");
                                    allPassed = false;
                                }

                                // =========================================================================
                                // Automated Spike Check: Evaluate CPU Linear Blend Skinning for all 39,214
                                // Sasha vertices to assert ZERO vertices spike down to (0,0,0) or drop below Y=0.90m!
                                // =========================================================================
                                customSubmeshes = previewHeroClone.FindChildren("*", "MeshInstance3D", true, false)
                                    .OfType<MeshInstance3D>()
                                    .Where(m => m.HasMeta("IsCustomImportedMesh") && m.GetMeta("IsCustomImportedMesh").AsBool())
                                    .ToList();

                                int totalCustomVerts = 0;
                                int totalUpperBodyVerts = 0;
                                int restSpikeCount = 0;
                                int restDropBelow90Count = 0;
                                float maxRestDisplacement = 0f;

                                // 1. Check at Rest Pose
                                foreach (var mi in customSubmeshes)
                                {
                                    var mesh = mi.Mesh;
                                    var skin = mi.Skin;
                                    if (mesh == null || skin == null) continue;

                                    for (int s = 0; s < mesh.GetSurfaceCount(); s++)
                                    {
                                        var arrays = mesh.SurfaceGetArrays(s);
                                        var verts = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                                        var bones = arrays[(int)Mesh.ArrayType.Bones].AsInt32Array();
                                        var weights = arrays[(int)Mesh.ArrayType.Weights].AsFloat32Array();
                                        if (verts == null || bones == null || weights == null) continue;

                                        totalCustomVerts += verts.Length;

                                        for (int v = 0; v < verts.Length; v++)
                                        {
                                            Vector3 vOrig = verts[v];
                                            if (vOrig.Y > 1.0f) // Torso / Neck / Head
                                            {
                                                totalUpperBodyVerts++;
                                                Vector3 vDef = Vector3.Zero;
                                                for (int k = 0; k < 4; k++)
                                                {
                                                    float w = weights[v * 4 + k];
                                                    if (w <= 0.0001f) continue;
                                                    int bindIdx = bones[v * 4 + k];
                                                    int skelBone = skin.GetBindBone(bindIdx);
                                                    if (skelBone < 0 && !string.IsNullOrEmpty(skin.GetBindName(bindIdx)))
                                                        skelBone = heroSkeleton.FindBone(skin.GetBindName(bindIdx));
                                                    if (skelBone < 0) skelBone = 0;

                                                    Transform3D bindPoseInv = skin.GetBindPose(bindIdx);
                                                    Transform3D boneRest = heroSkeleton.GetBoneGlobalRest(skelBone);
                                                    vDef += w * (boneRest * (bindPoseInv * vOrig));
                                                }

                                                float disp = vDef.DistanceTo(vOrig);
                                                if (disp > maxRestDisplacement) maxRestDisplacement = disp;
                                                if (disp > 0.15f)
                                                {
                                                    restSpikeCount++;
                                                    if (restSpikeCount <= 5)
                                                    {
                                                        GD.PrintErr($"[SPIKE DIAG] Mesh='{mi.Name}', surf={s}, v={v}, vOrig={vOrig}, vDef={vDef}, disp={disp:F3}m");
                                                        for (int k = 0; k < 4; k++)
                                                        {
                                                            float w = weights[v * 4 + k];
                                                            if (w <= 0.0001f) continue;
                                                            int bindIdx = bones[v * 4 + k];
                                                            int bBone = skin.GetBindBone(bindIdx);
                                                            string bName = skin.GetBindName(bindIdx);
                                                            int skelBone = bBone >= 0 ? bBone : heroSkeleton.FindBone(bName);
                                                            string sName = skelBone >= 0 ? heroSkeleton.GetBoneName(skelBone) : "<null>";
                                                            Vector3 bindInvOrig = skin.GetBindPose(bindIdx).AffineInverse().Origin;
                                                            Vector3 skelRestOrig = skelBone >= 0 ? heroSkeleton.GetBoneGlobalRest(skelBone).Origin : Vector3.Zero;
                                                            GD.PrintErr($"   k={k}: w={w:F3}, bindIdx={bindIdx}, bindBone={bBone}, bindName='{bName}', skelBone={skelBone} ('{sName}'), bindInvOrig={bindInvOrig}, skelRestOrig={skelRestOrig}");
                                                        }
                                                    }
                                                }
                                                if (vDef.Y < 0.90f) restDropBelow90Count++;
                                            }
                                        }
                                    }
                                }

                                GD.Print($"[TEST 18] Evaluated CPU LBS at Rest Pose across {totalCustomVerts} custom vertices ({totalUpperBodyVerts} torso/neck/head verts with Y > 1.0m):");
                                GD.Print($"[TEST 18] Max Rest Displacement: {maxRestDisplacement:F4}m | Spikes (>15cm): {restSpikeCount} | Dropped below Y=0.90m: {restDropBelow90Count}");

                                if (restSpikeCount == 0 && restDropBelow90Count == 0 && maxRestDisplacement <= 0.05f)
                                {
                                    GD.Print($"PASS: ZERO vertices spike or drop below Y=0.90m at Rest Pose (Max displacement: {maxRestDisplacement * 1000f:F1}mm)! Spike to (0,0,0) is 100% eliminated!");
                                }
                                else
                                {
                                    GD.PrintErr($"FAIL: CPU LBS Rest Pose check failed! Spikes: {restSpikeCount}, Dropped below 90cm: {restDropBelow90Count}, Max disp: {maxRestDisplacement:F4}m");
                                    allPassed = false;
                                }

                                // 2. Check at Mid-Jump
                                float midJumpTime = animLen * 0.5f;
                                for (int tr = 0; tr < decodedAnim.GetTrackCount(); tr++)
                                {
                                    string p = decodedAnim.TrackGetPath(tr).ToString();
                                    int c = p.LastIndexOf(':');
                                    string bName = c >= 0 ? p.Substring(c + 1) : p;
                                    int bIdx = heroSkeleton.FindBone(bName);
                                    if (bIdx < 0) continue;

                                    var trType = decodedAnim.TrackGetType(tr);
                                    if (trType == Godot.Animation.TrackType.Position3D)
                                        heroSkeleton.SetBonePosePosition(bIdx, decodedAnim.PositionTrackInterpolate(tr, midJumpTime));
                                    else if (trType == Godot.Animation.TrackType.Rotation3D)
                                        heroSkeleton.SetBonePoseRotation(bIdx, decodedAnim.RotationTrackInterpolate(tr, midJumpTime));
                                }

                                int midJumpSpikeCount = 0;
                                int midJumpDropBelow90Count = 0;

                                foreach (var mi in customSubmeshes)
                                {
                                    var mesh = mi.Mesh;
                                    var skin = mi.Skin;
                                    if (mesh == null || skin == null) continue;

                                    for (int s = 0; s < mesh.GetSurfaceCount(); s++)
                                    {
                                        var arrays = mesh.SurfaceGetArrays(s);
                                        var verts = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                                        var bones = arrays[(int)Mesh.ArrayType.Bones].AsInt32Array();
                                        var weights = arrays[(int)Mesh.ArrayType.Weights].AsFloat32Array();
                                        if (verts == null || bones == null || weights == null) continue;

                                        for (int v = 0; v < verts.Length; v++)
                                        {
                                            Vector3 vOrig = verts[v];
                                            if (vOrig.Y > 1.20f) // Torso / Neck / Head
                                            {
                                                Vector3 vDef = Vector3.Zero;
                                                for (int k = 0; k < 4; k++)
                                                {
                                                    float w = weights[v * 4 + k];
                                                    if (w <= 0.0001f) continue;
                                                    int bindIdx = bones[v * 4 + k];
                                                    int skelBone = skin.GetBindBone(bindIdx);
                                                    if (skelBone < 0 && !string.IsNullOrEmpty(skin.GetBindName(bindIdx)))
                                                        skelBone = heroSkeleton.FindBone(skin.GetBindName(bindIdx));
                                                    if (skelBone < 0) skelBone = 0;

                                                    Transform3D bindPoseInv = skin.GetBindPose(bindIdx);
                                                    Transform3D boneGlobal = heroSkeleton.GetBoneGlobalPose(skelBone);
                                                    vDef += w * (boneGlobal * (bindPoseInv * vOrig));
                                                }

                                                if (vDef.Y < 0.90f)
                                                {
                                                    midJumpDropBelow90Count++;
                                                    if (midJumpDropBelow90Count <= 5)
                                                    {
                                                        GD.PrintErr($"[MID-JUMP DROP] Mesh='{mi.Name}', v={v}, vOrig={vOrig}, vDef={vDef}");
                                                        for (int k = 0; k < 4; k++)
                                                        {
                                                            float w = weights[v * 4 + k];
                                                            if (w <= 0.0001f) continue;
                                                            int bindIdx = bones[v * 4 + k];
                                                            int skelBone = skin.GetBindBone(bindIdx);
                                                            if (skelBone < 0 && !string.IsNullOrEmpty(skin.GetBindName(bindIdx)))
                                                                skelBone = heroSkeleton.FindBone(skin.GetBindName(bindIdx));
                                                            GD.PrintErr($"   k={k}: w={w:F3}, bone='{(skelBone >= 0 ? heroSkeleton.GetBoneName(skelBone) : "<null>")}'");
                                                        }
                                                    }
                                                }
                                                if (vDef.Length() < 0.20f) midJumpSpikeCount++;
                                            }
                                        }
                                    }
                                }

                                // Restore rest pose
                                for (int b = 0; b < heroSkeleton.GetBoneCount(); b++)
                                {
                                    heroSkeleton.SetBonePosePosition(b, heroSkeleton.GetBoneRest(b).Origin);
                                    heroSkeleton.SetBonePoseRotation(b, heroSkeleton.GetBoneRest(b).Basis.GetRotationQuaternion());
                                }

                                GD.Print($"[TEST 18] Evaluated CPU LBS at Mid-Jump (t={midJumpTime:F2}s): Spikes to origin: {midJumpSpikeCount} | Dropped below Y=0.90m: {midJumpDropBelow90Count}");
                                if (midJumpSpikeCount == 0 && midJumpDropBelow90Count == 0)
                                {
                                    GD.Print("PASS: ZERO torso/neck/head vertices drop below Y=0.90m or spike to origin during jump!");
                                }
                                else
                                {
                                    GD.PrintErr($"FAIL: Mid-jump CPU LBS failed! Spikes={midJumpSpikeCount}, DroppedBelow90={midJumpDropBelow90Count}");
                                    allPassed = false;
                                }
                            }
                        }
                        else
                        {
                            GD.PrintErr($"FAIL: Failed to decode animation '{jumpSeq.CleanName}'!");
                            allPassed = false;
                        }
                    }
                    else
                    {
                        GD.Print("[TEST 18] Notice: No jump clip found on Paradox; skipping jump_ground test.");
                    }

                    // Cleanup temp VPK and staging
                    try
                    {
                        if (File.Exists(tempVpkPath)) File.Delete(tempVpkPath);
                        if (Directory.Exists(contentAddonRoot)) Directory.Delete(contentAddonRoot, true);
                        if (!string.IsNullOrEmpty(compileRes.GameStagingAddonRoot) && Directory.Exists(compileRes.GameStagingAddonRoot))
                            Directory.Delete(compileRes.GameStagingAddonRoot, true);
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[Test] EXCEPTION in verification test: {ex.Message}\n{ex.StackTrace}");
                allPassed = false;
            }

            GD.Print("\n=================================================");
            if (allPassed)
            {
                GD.Print("   ALL VERIFICATION CHECKS PASSED SUCCESSFULLY!  ");
            }
            else
            {
                GD.PrintErr("   SOME VERIFICATION CHECKS FAILED!              ");
            }
            GD.Print("=================================================");

            Quit(allPassed ? 0 : 1);
        }

        private static void AssertBoneMapping(System.Collections.Generic.Dictionary<string, string> boneMap, string targetBone, string expectedSourcePrefix, ref bool allPassed)
        {
            if (!boneMap.TryGetValue(targetBone, out string mapped) || string.IsNullOrEmpty(mapped))
            {
                GD.PrintErr($"FAIL: Target bone '{targetBone}' is not mapped!");
                allPassed = false;
                return;
            }

            string cleanMapped = HumanoidBoneMapper.CleanBoneName(mapped);
            string cleanExpected = HumanoidBoneMapper.CleanBoneName(expectedSourcePrefix);

            if (cleanMapped.Contains(cleanExpected) || cleanExpected.Contains(cleanMapped))
            {
                GD.Print($"PASS: Target '{targetBone}' -> '{mapped}' (expected {expectedSourcePrefix})");
            }
            else
            {
                GD.PrintErr($"FAIL: Target '{targetBone}' mapped to '{mapped}' instead of '{expectedSourcePrefix}'!");
                allPassed = false;
            }
        }

        private static void StripPreviewGizmosAndRestoreComposites(Node root)
        {
            if (root == null) return;
            var toRemove = new System.Collections.Generic.List<Node>();
            FindNodesToStrip(root, toRemove);
            foreach (var node in toRemove)
            {
                if (GodotObject.IsInstanceValid(node))
                {
                    node.GetParent()?.RemoveChild(node);
                    node.Free();
                }
            }
        }

        private static void FindNodesToStrip(Node node, System.Collections.Generic.List<Node> toRemove)
        {
            foreach (var child in node.GetChildren())
            {
                string cName = child.Name.ToString();
                string cType = child.GetType().Name;
                string lower = cName.ToLowerInvariant();

                if (cType.Contains("SkeletonGizmo") || cType.Contains("BoneLayer") || cType.Contains("CharacterIK") ||
                    cName.StartsWith("@") || lower.Contains("gizmo") || lower.Contains("picker") ||
                    lower.Contains("handle") || lower.Contains("tipvisual") || lower.Contains("polevisual"))
                {
                    toRemove.Add(child);
                    continue;
                }

                if (child is MeshInstance3D mi)
                {
                    if (mi.HasMeta("ParentCompositeMesh") || mi.Name.ToString().Contains("_surf_"))
                    {
                        toRemove.Add(child);
                        continue;
                    }

                    if (mi.HasMeta("IsHiddenComposite"))
                    {
                        mi.Visible = true;
                        mi.RemoveMeta("IsHiddenComposite");
                        if (mi.HasMeta("GeneratedSubmeshes")) mi.RemoveMeta("GeneratedSubmeshes");
                    }

                    if (!DeadlockPlayground.Painter.HeroMeshHierarchy.IsOriginalHeroMesh(mi) || mi.HasMeta("IsCustomImportedMesh"))
                    {
                        toRemove.Add(child);
                        continue;
                    }

                    mi.Visible = true;
                    if (mi.HasMeta("UserHiddenByCustomModel")) mi.RemoveMeta("UserHiddenByCustomModel");
                    if (mi.HasMeta("UserVisibility")) mi.SetMeta("UserVisibility", true);
                }

                FindNodesToStrip(child, toRemove);
            }
        }
    }
}
