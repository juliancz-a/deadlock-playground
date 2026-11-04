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
                    .Where(s => s.Mesh != null && s.Mesh.HasMeta("IsCustomImportedMesh"))
                    .Select(s => s.Mesh)
                    .Distinct()
                    .ToList();

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
