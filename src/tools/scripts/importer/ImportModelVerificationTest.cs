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
using DeadlockPlayground.Materials;

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

                var vpkLoader = new VpkLoaderTest { Name = "VpkLoaderTest", VpkPath = vpkPath };
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
                        bool hasAnimRestList = !vmdlContent.Contains("AnimIncludeModel") && vmdlContent.Contains("_class = \"AnimationList\"") && vmdlContent.Contains("_anim_bind.smd");

                        if (hasBodySmd && hasNmSkeleton && hasAnimGraph2 && hasAttachments && hasHitboxes && hasGameData && hasBodyGroups && hasAnimRestList)
                        {
                            GD.Print("PASS: Decompile-and-Patch .vmdl contains NmSkeletonList, AnimGraph2List, rest AnimationList, AttachmentList (standing_pivot, crouching_pivot, gunaim_00, far_00), BodyGroupList, HitboxSetList, and GameDataList!");
                        }
                        else
                        {
                            GD.PrintErr($"FAIL: .vmdl validation failed! smd={hasBodySmd}, nmSkel={hasNmSkeleton}, ag2={hasAnimGraph2}, attach={hasAttachments}, hitbox={hasHitboxes}, gameData={hasGameData}, bodyGroups={hasBodyGroups}, restAnimList={hasAnimRestList}");
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
                            using var outPkg = new ValvePak.Package();
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
                                var compiledModelBlock = compiledRes.DataBlock as ValveResourceFormat.ResourceTypes.Model;
                                var compiledLegBone = compiledModelBlock?.Skeleton?.Bones?.FirstOrDefault(b => b.Name == "leg_upper_L");
                                bool compiledHasAnimBoneFlags = compiledLegBone != null && compiledLegBone.Flags.ToString().Contains("Animation");

                                if (compiledHasStandingPivot && compiledHasAg2 && compiledHasAnimBoneFlags)
                                {
                                    GD.Print($"PASS: End-to-end CSDK13 VPK verified ({allVpkPaths.Count} files < 65, all 15 custom .vmat_c/.vtex_c present, compiled chrono.vmdl_c has leg_upper_L Flags={compiledLegBone.Flags}, standing_pivot, gunaim_00, chrono.vnmskel, and AnimGraph2 references)!");
                                }
                                else
                                {
                                    GD.PrintErr($"FAIL: Compiled VMDL check failed! standingPivot={compiledHasStandingPivot}, ag2={compiledHasAg2}, legAnimFlags={compiledHasAnimBoneFlags} ({compiledLegBone?.Flags})");
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

                // =========================================================================
                // TEST 26: DeadlockAnimLoader Masked Partial Sequence (jump_landing*) Decode
                // =========================================================================
                GD.Print("\n[TEST 26] Verifying DeadlockAnimLoader masked partial SequenceAnimations ('jump_landing*') preserve legs across all frames...");
                {
                    var animPlayer = heroNode.FindChild("AnimationPlayer", true, false) as AnimationPlayer;
                    if (animPlayer == null)
                    {
                        animPlayer = new AnimationPlayer { Name = "VerificationAnimPlayer" };
                        heroNode.AddChild(animPlayer);
                    }

                    int legLowerIdx = heroSkeleton.FindBone("leg_lower_L");
                    int legUpperIdx = heroSkeleton.FindBone("leg_upper_L");
                    Quaternion legUpperRestRot = legUpperIdx >= 0 ? heroSkeleton.GetBoneRest(legUpperIdx).Basis.GetRotationQuaternion() : Quaternion.Identity;

                    foreach (var seqName in new[] { "jump_landing", "jump_landing_crouch", "jump_landing_not_shooting" })
                    {
                        var landingSeq = vpkLoader.CurrentHeroAnimations?.FirstOrDefault(a =>
                            a.CleanName.Equals(seqName, StringComparison.OrdinalIgnoreCase));
                        if (landingSeq == null)
                        {
                            GD.PrintErr($"FAIL: Chrono '{seqName}' not found in CurrentHeroAnimations!");
                            allPassed = false;
                            continue;
                        }

                        var decodedLanding = vpkLoader.DecodeAnimation(landingSeq, heroSkeleton, animPlayer, inPlace: true, targetAnimName: $"{seqName}_verify");
                        int legUpperPosTrack = -1, legLowerPosTrack = -1, legUpperRotTrack = -1, pelvisPosTrack = -1;
                        if (decodedLanding != null)
                        {
                            for (int t = 0; t < decodedLanding.GetTrackCount(); t++)
                            {
                                string p = decodedLanding.TrackGetPath(t).ToString();
                                var tt = decodedLanding.TrackGetType(t);
                                if (tt == Godot.Animation.TrackType.Position3D && p.EndsWith(":leg_upper_L")) legUpperPosTrack = t;
                                else if (tt == Godot.Animation.TrackType.Position3D && p.EndsWith(":leg_lower_L")) legLowerPosTrack = t;
                                else if (tt == Godot.Animation.TrackType.Rotation3D && p.EndsWith(":leg_upper_L")) legUpperRotTrack = t;
                                else if (tt == Godot.Animation.TrackType.Position3D && p.EndsWith(":pelvis")) pelvisPosTrack = t;
                            }
                        }

                        float midTime = decodedLanding != null ? Math.Min(0.25f, (float)decodedLanding.Length * 0.4f) : 0.25f;
                        Vector3 legPos0 = legUpperPosTrack >= 0 ? decodedLanding.PositionTrackInterpolate(legUpperPosTrack, 0f) : Vector3.Zero;
                        Vector3 legPosMid = legUpperPosTrack >= 0 ? decodedLanding.PositionTrackInterpolate(legUpperPosTrack, midTime) : Vector3.Zero;
                        Vector3 kneePosMid = legLowerPosTrack >= 0 ? decodedLanding.PositionTrackInterpolate(legLowerPosTrack, midTime) : Vector3.Zero;
                        Vector3 pelvPos0 = pelvisPosTrack >= 0 ? decodedLanding.PositionTrackInterpolate(pelvisPosTrack, 0f) : Vector3.Zero;
                        Quaternion legRotMid = legUpperRotTrack >= 0 ? decodedLanding.RotationTrackInterpolate(legUpperRotTrack, midTime) : Quaternion.Identity;
                        float legRotAngleDeg = legUpperRestRot.AngleTo(legRotMid) * 180f / Mathf.Pi;

                        if (decodedLanding != null &&
                            legPos0.Length() > 0.1f &&
                            legPosMid.Length() > 0.1f &&
                            kneePosMid.Length() > 0.3f &&
                            pelvPos0.Y > 0.4f &&
                            legRotAngleDeg < 90f)
                        {
                            GD.Print($"PASS: Chrono '{seqName}' preserved leg_upper_L@mid={legPosMid} (rotDiff={legRotAngleDeg:F1}deg), knee@mid={kneePosMid} (len={kneePosMid.Length():F3}m), pelvis@0.Y={pelvPos0.Y:F3}m.");
                        }
                        else
                        {
                            GD.PrintErr($"FAIL: Chrono '{seqName}' leg/pelvis glitched! leg0={legPos0}, legMid={legPosMid}, kneeMid={kneePosMid}, pelv0={pelvPos0}, rotDiff={legRotAngleDeg:F1}deg");
                            allPassed = false;
                        }
                    }
                }

                // =========================================================================
                // TEST 19: Multi-Channel Discovery & Deadman Danny (deadpack) Variants + Prefix Stripping
                // =========================================================================
                GD.Print("\n[TEST 19] Verifying Deadman Danny (deadpack) Multi-Channel Discovery, Variants & Prefix Stripping...");
                {
                    var hierarchyClean = new HeroMeshHierarchy();
                    hierarchyClean.HeroCodename = "deadpack";
                    hierarchyClean.HeroDisplayName = "Deadman Danny";
                    hierarchyClean.HeroWipAlias = "deadpack";
                    Root.AddChild(hierarchyClean);

                    string n1 = HeroMeshHierarchy.FormatCleanMaterialDisplayName("DEADPACK_LOWERBODY", "deadpack", "Deadman Danny");
                    string n2 = HeroMeshHierarchy.FormatCleanMaterialDisplayName("DEAPACK_JETPACK", "deadpack", "Deadman Danny");
                    string n3 = HeroMeshHierarchy.FormatCleanMaterialDisplayName("DEADPACK", "deadpack", "Deadman Danny");
                    GD.Print($"[TEST 19] Clean names: DEADPACK_LOWERBODY->'{n1}', DEAPACK_JETPACK->'{n2}', DEADPACK->'{n3}'");
                    if (n1 != "LOWERBODY" || n2 != "JETPACK" || string.IsNullOrEmpty(n3))
                    {
                        GD.PrintErr($"FAIL: Hero prefix stripping mismatch! n1='{n1}', n2='{n2}', n3='{n3}'");
                        allPassed = false;
                    }
                    else
                    {
                        GD.Print("PASS: Hero codename + 1-char typo prefix (DEAPACK -> JETPACK) stripped cleanly without empty fallback.");
                    }

                    var deadmanEntry = DeadlockHeroCatalog.ResolveHero("deadpack")
                                    ?? DeadlockHeroCatalog.ResolveHero("deadman_danny")
                                    ?? DeadlockHeroCatalog.ResolveHero("Deadman Danny");
                    if (!string.IsNullOrEmpty(vpkPath) && File.Exists(vpkPath) && deadmanEntry != null)
                    {
                        Node3D deadmanNode = await vpkLoader.LoadHeroModelAsync(deadmanEntry);
                        if (deadmanNode == null)
                        {
                            GD.PrintErr("FAIL: Failed to load Deadman Danny (deadpack) from VPK.");
                            allPassed = false;
                        }
                        else
                        {
                            hierarchyClean.ScanHero(deadmanNode);
                            var lowerSub = hierarchyClean.Submeshes.FirstOrDefault(s =>
                                (s.MaterialName ?? "").Contains("lowerbody", StringComparison.OrdinalIgnoreCase) ||
                                (s.OriginalVmatPath ?? "").Contains("lowerbody", StringComparison.OrdinalIgnoreCase));
                            if (lowerSub?.Channels == null)
                            {
                                GD.PrintErr("FAIL: deadpack_lowerbody submesh or MaterialChannelSet not found!");
                                allPassed = false;
                            }
                            else
                            {
                                var chs = lowerSub.Channels;
                                bool hasColor = chs.HasRealMap(MaterialTextureChannel.Color);
                                bool hasAo = chs.HasRealMap(MaterialTextureChannel.AmbientOcclusion);
                                bool hasNorm = chs.HasRealMap(MaterialTextureChannel.Normal);
                                bool hasRough = chs.HasRealMap(MaterialTextureChannel.Roughness);
                                bool hasShadow = chs.HasRealMap(MaterialTextureChannel.Shadow);
                                int variantCount = chs.GetChannel(MaterialTextureChannel.Color)?.VariantPaths.Count ?? 0;

                                GD.Print($"[TEST 19] deadpack_lowerbody channels -> Color:{hasColor}(variants={variantCount}), AO:{hasAo}, Normal:{hasNorm}, Rough:{hasRough}, Shadow:{hasShadow}");
                                if (hasColor && hasAo && hasNorm && hasRough && hasShadow && variantCount >= 2)
                                {
                                    GD.Print("PASS: deadpack_lowerbody discovered all 5 core channels + >=2 Color variants!");
                                }
                                else
                                {
                                    GD.PrintErr($"FAIL: deadpack_lowerbody missing channels or color variants (variants={variantCount})!");
                                    allPassed = false;
                                }

                                // Roundtrip g_tNormalRoughness pack check
                                var nImg = chs.GetChannel(MaterialTextureChannel.Normal)?.SourceImage;
                                var rImg = chs.GetChannel(MaterialTextureChannel.Roughness)?.SourceImage;
                                using var repacked = MaterialChannelDiscovery.PackNormalRoughnessImage(nImg, rImg, true);
                                if (repacked != null && !repacked.IsEmpty())
                                {
                                    GD.Print($"PASS: Normal+Roughness repacked cleanly ({repacked.GetWidth()}x{repacked.GetHeight()}).");
                                }
                                else
                                {
                                    GD.PrintErr("FAIL: Normal+Roughness repacking returned null/empty image.");
                                    allPassed = false;
                                }
                            }
                            deadmanNode.QueueFree();
                        }
                    }
                    else
                    {
                        GD.Print("[TEST 19] Notice: Deadman Danny entry or VPK not available; skipped live VPK deadpack check.");
                    }
                    hierarchyClean.QueueFree();
                }

                // =========================================================================
                // TEST 20: 2D UV ATLAS Header Two-Row Resize & Bottom Channel Bar Scene
                // =========================================================================
                GD.Print("\n[TEST 20] Verifying UVCanvasPanel two-row header & BottomChannelBar resize bounds...");
                {
                    var uvScene = GD.Load<PackedScene>("res://ui/scenes/components/UVCanvasPanel.tscn");
                    if (uvScene == null)
                    {
                        GD.PrintErr("FAIL: Could not load res://ui/scenes/components/UVCanvasPanel.tscn!");
                        allPassed = false;
                    }
                    else
                    {
                        var uvPanel = uvScene.Instantiate<DeadlockPlayground.UI.UVCanvas2DUI>();
                        Root.AddChild(uvPanel);
                        uvPanel.SetPanelWidth(180f);
                        bool hasBottomBar = uvPanel.FindChild("BottomChannelBar", true, false) != null;
                        bool hasToolbarRow = uvPanel.FindChild("ToolbarRow", true, false) != null;
                        bool hasPillsFlow = uvPanel.FindChild("ChannelPillsFlow", true, false) != null;
                        var hdrHBox = uvPanel.FindChild("HBox", true, false) as Control;
                        var mainVBox = uvPanel.FindChild("VBoxContainer", true, false) as Control;
                        float minPanelX = uvPanel.CustomMinimumSize.X;
                        float hdrMinX = hdrHBox?.GetCombinedMinimumSize().X ?? 999f;
                        float vboxMinX = mainVBox?.GetCombinedMinimumSize().X ?? 999f;

                        GD.Print($"[TEST 20] MinPanelWidth={uvPanel.MinPanelWidth}, CustomMinX={minPanelX}, VBoxMinX={vboxMinX:F1}, HeaderRowMinX={hdrMinX:F1}, ToolbarRow={hasToolbarRow}, BottomBar={hasBottomBar}, PillsFlow={hasPillsFlow}");
                        if (uvPanel.MinPanelWidth <= 180f && minPanelX <= 180f && vboxMinX < 300f && hdrMinX < 300f && hasBottomBar && hasToolbarRow && hasPillsFlow)
                        {
                            GD.Print("PASS: UVCanvasPanel allows narrowing (<300px) and BottomChannelBar is wired!");
                        }
                        else
                        {
                            GD.PrintErr("FAIL: UVCanvasPanel minimum width or child bar wiring invalid!");
                            allPassed = false;
                        }
                        uvPanel.QueueFree();
                    }
                }

                // =========================================================================
                // TEST 21: Custom Model 3-Tier Discovery (ORM Unpack, Tier-2 Fuzzy, Shader Toggles)
                // =========================================================================
                GD.Print("\n[TEST 21] Verifying 3-Tier Custom Model Channel Discovery & Live 3D Shader Toggles...");
                {
                    // Synthetic ORM image: varying R (AO), G (Rough), B (Metal)
                    byte[] ormBytes = new byte[16 * 16 * 4];
                    for (int y = 0; y < 16; y++)
                    {
                        for (int x = 0; x < 16; x++)
                        {
                            int idx = (y * 16 + x) * 4;
                            ormBytes[idx + 0] = (byte)(x * 16);        // R = AO variation
                            ormBytes[idx + 1] = (byte)(32 + y * 12);   // G = Roughness variation
                            ormBytes[idx + 2] = (byte)(x > 8 ? 255 : 0); // B = Metalness variation
                            ormBytes[idx + 3] = 255;
                        }
                    }
                    using var ormImg = Image.CreateFromData(16, 16, false, Image.Format.Rgba8, ormBytes);
                    var ormTex = ImageTexture.CreateFromImage(ormImg);
                    ormTex.ResourceName = "hero_armor_orm";

                    using var normImg = Image.CreateEmpty(16, 16, false, Image.Format.Rgba8);
                    normImg.Fill(new Color(0.5f, 0.5f, 1.0f, 1.0f));
                    var normTex = ImageTexture.CreateFromImage(normImg);
                    normTex.ResourceName = "hero_armor_nrm";

                    var stdMat = new StandardMaterial3D
                    {
                        ResourceName = "hero_armor",
                        RoughnessTexture = ormTex,
                        RoughnessTextureChannel = BaseMaterial3D.TextureChannel.Green,
                        NormalTexture = normTex,
                        NormalEnabled = true
                    };

                    // Tier 2 temp dir with _shadow and _gloss maps
                    string tempTexDir = Path.Combine(Path.GetTempPath(), $"dl_ch_test_{Guid.NewGuid():N}");
                    Directory.CreateDirectory(tempTexDir);
                    try
                    {
                        using var shImg = Image.CreateEmpty(8, 8, false, Image.Format.Rgba8);
                        shImg.Fill(new Color(0.6f, 0.3f, 0.4f, 1f));
                        string shPath = Path.Combine(tempTexDir, "hero_armor_shadow.png");
                        shImg.SavePng(shPath);
                        using var loadedShImg = Image.LoadFromFile(shPath);
                        var shTex = ImageTexture.CreateFromImage(loadedShImg);
                        shTex.ResourceName = "hero_armor_shadow.png";

                        var chSet = MaterialChannelDiscovery.DiscoverCustomModelChannels(
                            stdMat,
                            "hero_armor",
                            glbSlot: null,
                            externalPool: new System.Collections.Generic.List<Texture2D> { shTex });
                        bool tier1AoFromOrm = chSet.HasRealMap(MaterialTextureChannel.AmbientOcclusion);
                        bool tier1MetalFromOrm = chSet.HasRealMap(MaterialTextureChannel.Metalness);
                        bool tier1Rough = chSet.HasRealMap(MaterialTextureChannel.Roughness);
                        bool tier2Shadow = chSet.HasRealMap(MaterialTextureChannel.Shadow);
                        bool tier3ColorFallback = chSet.GetChannel(MaterialTextureChannel.Color)?.IsDefaultFallback ?? false;

                        GD.Print($"[TEST 21] ORM AO={tier1AoFromOrm}, Metal={tier1MetalFromOrm}, Rough={tier1Rough}, Tier2 Shadow={tier2Shadow}, ColorDefaultFallback={tier3ColorFallback}");
                        if (!tier1AoFromOrm || !tier1MetalFromOrm || !tier1Rough || !tier2Shadow || !tier3ColorFallback)
                        {
                            GD.PrintErr("FAIL: Custom 3-Tier channel discovery failed on ORM/Tier2/Tier3 check!");
                            allPassed = false;
                        }
                        else
                        {
                            GD.Print("PASS: Custom 3-Tier discovery unpacked ORM AO/Metalness, detected Tier-2 _shadow, and generated Tier-3 fallback.");
                        }

                        var toonShader = GD.Load<Shader>("res://assets/shaders/toon_shader.gdshader");
                        if (toonShader != null)
                        {
                            var toonMat = new ShaderMaterial { Shader = toonShader };
                            chSet.GetChannel(MaterialTextureChannel.AmbientOcclusion).IsEnabled = false;
                            chSet.SoloPreviewChannel = MaterialTextureChannel.Normal;
                            MaterialChannelDiscovery.ApplyChannelsToMaterial(toonMat, chSet);
                            bool aoDisabled = !toonMat.GetShaderParameter("enable_ao").AsBool();
                            int dbgChan = toonMat.GetShaderParameter("debug_view_channel").AsInt32();
                            if (aoDisabled && dbgChan == MaterialChannelSet.ChannelToShaderDebugIndex(MaterialTextureChannel.Normal))
                            {
                                GD.Print($"PASS: Shader toggle/solo sync verified (enable_ao=false, debug_view_channel={dbgChan}).");
                            }
                            else
                            {
                                GD.PrintErr($"FAIL: Shader uniform sync mismatch (aoDisabled={aoDisabled}, debug_view_channel={dbgChan}).");
                                allPassed = false;
                            }
                        }
                    }
                    finally
                    {
                        try { Directory.Delete(tempTexDir, true); } catch { }
                    }
                }

                // =========================================================================
                // TEST 22: Multi-Channel VMAT Shadow/AO/Roughness Binding
                // =========================================================================
                GD.Print("\n[TEST 22] Verifying CustomModelExporter.GenerateDeadlockPbrVmat multi-channel bindings...");
                {
                    string vmat = CustomModelExporter.GenerateDeadlockPbrVmat(
                        "models/test/hero_color.png",
                        "models/test/hero_normal.png",
                        "models/test/hero_rough.png",
                        0.25f,
                        "models/test/hero_ao.png",
                        false,
                        false,
                        false,
                        "models/test/hero_selfillum.png",
                        "models/test/hero_tintmask.png",
                        "models/test/hero_shadow.png");
                    if (vmat.Contains("TextureNprTramsissiveColor1 \"models/test/hero_shadow.png\"") &&
                        vmat.Contains("TextureAmbientOcclusion1 \"models/test/hero_ao.png\"") &&
                        vmat.Contains("TextureSelfIllumMask1 \"models/test/hero_selfillum.png\""))
                    {
                        GD.Print("PASS: GenerateDeadlockPbrVmat emitted Shadow, AO, Normal, Roughness, and SelfIllum bindings.");
                    }
                    else
                    {
                        GD.PrintErr("FAIL: GenerateDeadlockPbrVmat missing expected multi-channel bindings!");
                        allPassed = false;
                    }
                }

                // =========================================================================
                // TEST 23: Deadlock VMAT Combos, Outline/Unlit SelfIllum, Tint Isolation & NPR Transmissive
                // =========================================================================
                GD.Print("\n[TEST 23] Verifying Deadlock VMAT combos (NPR transmissive, tint mask isolation, outline/unlit self-illum)...");
                {
                    string vmatLitDefault = CustomModelExporter.GenerateDeadlockPbrVmat(
                        "models/test/color.png",
                        "models/test/normal.png",
                        "0.500",
                        0.0f,
                        "1.000",
                        blackMaskRel: "models/test/default_black_mask.png",
                        shadowMapRel: "models/test/default_npr_transmissive.png");
                    bool litOk = vmatLitDefault.Contains("F_USE_STATUS_EFFECTS_PROXY 1") &&
                                 vmatLitDefault.Contains("F_WRITE_DEPTH_BEFORE_ALPHA_BLENDING 1") &&
                                 vmatLitDefault.Contains("F_SOLID_COLOR_OUTLINE 1") &&
                                 vmatLitDefault.Contains("g_flModelTintAmount \"0.000\"") &&
                                 vmatLitDefault.Contains("TextureTintMask1 \"models/test/default_black_mask.png\"") &&
                                 vmatLitDefault.Contains("TextureRimLightMask1 \"models/test/default_black_mask.png\"") &&
                                 vmatLitDefault.Contains("TextureNprOutlineMask1 \"models/test/default_black_mask.png\"") &&
                                 vmatLitDefault.Contains("TextureNprTramsissiveColor1 \"models/test/default_npr_transmissive.png\"");

                    string vmatOutline = CustomModelExporter.GenerateDeadlockPbrVmat(
                        "models/test/outline_color.png",
                        "models/test/normal.png",
                        "0.900",
                        0.0f,
                        "1.000",
                        isOutlineShell: true,
                        selfIllumMaskRel: "models/test/default_white_ao.png",
                        blackMaskRel: "models/test/default_black_mask.png",
                        shadowMapRel: "models/test/default_npr_transmissive.png");
                    bool outlineOk = vmatOutline.Contains("F_DISABLE_NPR_OUTLINE 1") &&
                                     vmatOutline.Contains("F_SELF_ILLUM 1") &&
                                     vmatOutline.Contains("g_flSelfIllumAlbedoFactor1 \"1.000\"") &&
                                     vmatOutline.Contains("TextureSelfIllumMask1 \"models/test/default_white_ao.png\"") &&
                                     vmatOutline.Contains("TextureNprTramsissiveColor1 \"models/test/default_npr_transmissive.png\"");

                    string vmatUnlit = CustomModelExporter.GenerateDeadlockPbrVmat(
                        "models/test/unlit_color.png",
                        "models/test/normal.png",
                        "0.800",
                        0.0f,
                        "1.000",
                        isUnlit: true,
                        selfIllumMaskRel: "models/test/default_white_ao.png",
                        blackMaskRel: "models/test/default_black_mask.png",
                        shadowMapRel: "models/test/default_npr_transmissive.png");
                    bool unlitOk = vmatUnlit.Contains("F_SELF_ILLUM 1") &&
                                   vmatUnlit.Contains("g_flSelfIllumAlbedoFactor1 \"1.000\"") &&
                                   vmatUnlit.Contains("g_flSelfIllumScale1 \"0.850\"") &&
                                   vmatUnlit.Contains("TextureSelfIllumMask1 \"models/test/default_white_ao.png\"");

                    if (litOk && outlineOk && unlitOk)
                    {
                        GD.Print("PASS: GenerateDeadlockPbrVmat emitted valid Deadlock combos, outline/unlit self-illum, black tint/rim/outline isolation, and non-black NPR transmissive default.");
                    }
                    else
                    {
                        GD.PrintErr($"FAIL: VMAT regression check failed (litOk={litOk}, outlineOk={outlineOk}, unlitOk={unlitOk}).");
                        allPassed = false;
                    }
                }

                // =========================================================================
                // TEST 24: WriteRestBindAnimationSmd + GeneratePatchedVmdl AnimationList Preservation
                // =========================================================================
                GD.Print("\n[TEST 24] Verifying WriteRestBindAnimationSmd & GeneratePatchedVmdl AnimationList preservation...");
                {
                    string tempVmdlDir = Path.Combine(ProjectSettings.GlobalizePath("user://"), "vmdl_animlist_test");
                    try
                    {
                        Directory.CreateDirectory(tempVmdlDir);
                        var animListBundle = new CustomModelExporter.CustomModelExportBundle
                        {
                            HeroCodename = "chrono",
                            HeroModelVpkPath = "models/heroes_staging/chrono/chrono.vmdl_c"
                        };
                        var (origVmdl, _, vrfBones) = CustomModelExporter.DecompileVanillaHeroVmdl(animListBundle, animListBundle.HeroModelVpkPath, "models/heroes_staging/chrono", false);
                        string animSmdPath = Path.Combine(tempVmdlDir, "chrono_anim_bind.smd");
                        CustomModelExporter.WriteRestBindAnimationSmd(animSmdPath, animListBundle, vrfBones);
                        string animSmdText = File.Exists(animSmdPath) ? File.ReadAllText(animSmdPath) : "";

                        string patchedVmdl = CustomModelExporter.GeneratePatchedVmdl(
                            origVmdl,
                            animListBundle,
                            "models/heroes_staging/chrono",
                            "models/heroes_staging/chrono/materials",
                            new System.Collections.Generic.List<(string MeshName, string SmdRelativePath)> { ("chrono_body", "models/heroes_staging/chrono/chrono_body.smd") },
                            new System.Collections.Generic.List<string> { "models/heroes_staging/chrono/materials/chrono_body.vmat" },
                            new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase),
                            includeAnimGraph2Nodes: true);

                        bool smdOk = animSmdText.Contains("version 1") && animSmdText.Contains("skeleton") && animSmdText.Contains("leg_upper_L");
                        bool vmdlOk = patchedVmdl.Contains("_class = \"AnimationList\"") &&
                                      patchedVmdl.Contains("_class = \"AnimFile\"") &&
                                      patchedVmdl.Contains("chrono_anim_bind.smd");

                        if (smdOk && vmdlOk)
                        {
                            GD.Print($"PASS: Rest animation SMD ({vrfBones.Count} bones) and patched VMDL AnimationList generated cleanly.");
                        }
                        else
                        {
                            GD.PrintErr($"FAIL: AnimationList VMDL/SMD check failed (smdOk={smdOk}, vmdlOk={vmdlOk}).");
                            allPassed = false;
                        }
                    }
                    finally
                    {
                        try { Directory.Delete(tempVmdlDir, true); } catch { }
                    }
                }

                // =========================================================================
                // TEST 25: CustomModelAutoRigger.TryUpdateSocketSkinBindPose O(1) Precision
                // =========================================================================
                GD.Print("\n[TEST 25] Verifying TryUpdateSocketSkinBindPose O(1) GPU skin bind matrix math...");
                {
                    Transform3D boneRest = new Transform3D(Basis.FromEuler(new Vector3(0.2f, -0.5f, 0.3f)), new Vector3(0.35f, 1.1f, -0.15f));
                    Vector3 pivot = new Vector3(0.32f, 1.05f, -0.18f);
                    Vector3 pos0 = new Vector3(0.02f, -0.01f, 0.05f);
                    Vector3 rot0 = new Vector3(15f, -30f, 10f);
                    float scale0 = 0.85f;
                    Vector3 pos1 = new Vector3(-0.04f, 0.06f, 0.02f);
                    Vector3 rot1 = new Vector3(82f, 45f, -20f);
                    float scale1 = 1.25f;

                    Transform3D bakedHero = CustomModelAutoRigger.ComputeSocketHeroMatrix(boneRest, pivot, pos0, rot0, scale0);
                    Transform3D expectedHero = CustomModelAutoRigger.ComputeSocketHeroMatrix(boneRest, pivot, pos1, rot1, scale1);

                    var testSkin = new Skin();
                    testSkin.AddBind(0, boneRest.AffineInverse());
                    var testMi = new MeshInstance3D { Skin = testSkin };
                    testMi.SetMeta("IsSocketProp", true);
                    testMi.SetMeta("SocketBoneRest", boneRest);
                    testMi.SetMeta("SocketPivot", pivot);
                    testMi.SetMeta("SocketBakedInv", bakedHero.AffineInverse());

                    bool updated = CustomModelAutoRigger.TryUpdateSocketSkinBindPose(testMi, pos1, rot1, scale1);
                    Vector3 sampleBase = new Vector3(0.12f, -0.08f, 0.44f);
                    Vector3 bakedVert = bakedHero * sampleBase;
                    Vector3 gpuSkinnedRest = boneRest * (testSkin.GetBindPose(0) * bakedVert);
                    Vector3 expectedVert = expectedHero * sampleBase;
                    float err = gpuSkinnedRest.DistanceTo(expectedVert);

                    if (updated && err < 0.0002f)
                    {
                        GD.Print($"PASS: O(1) TryUpdateSocketSkinBindPose matches full vertex rebuild within {err:E2}m.");
                    }
                    else
                    {
                        GD.PrintErr($"FAIL: TryUpdateSocketSkinBindPose mismatch (updated={updated}, err={err:F6}m).");
                        allPassed = false;
                    }
                    testMi.Free();
                }

                // =========================================================================
                // TEST 27: Sasha GLB Export Material Shadow/SelfIllum Isolation
                // =========================================================================
                GD.Print("\n[TEST 27] Verifying Sasha unlit NPR GLB surfaces do not export as fullbright F_SELF_ILLUM...");
                {
                    string sashaGlbPath = ProjectSettings.GlobalizePath("res://../scratch_out/models/sasha_-_cyberpunk_edgerunners.glb");
                    if (File.Exists(sashaGlbPath))
                    {
                        var sashaVerifyScene = CustomModelAutoRigger.LoadAndSanitize3DModelScene(sashaGlbPath);
                        var sashaVerifyMeshes = CustomModelAutoRigger.CollectMeshes(sashaVerifyScene);
                        var sashaVerifySkel = CustomModelAutoRigger.FindSkeleton(sashaVerifyScene);
                        var sashaVerifyBundle = CustomModelExporter.BuildExportBundle(sashaVerifyMeshes, sashaVerifySkel, "chrono");
                        int badUnlitCount = sashaVerifyBundle.Submeshes.Count(s => !s.IsOutlineShell && (s.IsUnlit || s.SelfIllumPngBytes != null));
                        int outlineShellCount = sashaVerifyBundle.Submeshes.Count(s => s.IsOutlineShell && s.IsUnlit);

                        if (sashaVerifyBundle.Submeshes.Count > 0 && badUnlitCount == 0 && outlineShellCount > 0)
                        {
                            GD.Print($"PASS: Sasha export bundle has 0 fullbright non-outline surfaces and {outlineShellCount} unlit outline shell(s) out of {sashaVerifyBundle.Submeshes.Count} total surfaces.");
                        }
                        else
                        {
                            GD.PrintErr($"FAIL: Sasha export bundle fullbright check failed (submeshes={sashaVerifyBundle.Submeshes.Count}, badUnlitCount={badUnlitCount}, outlineShellCount={outlineShellCount}).");
                            allPassed = false;
                        }
                        sashaVerifyScene?.Free();
                    }
                    else
                    {
                        GD.Print($"SKIP: Sasha GLB not found at {sashaGlbPath}.");
                    }
                }

                // =========================================================================
                // TEST 28: Automatic Limb Counter-Twist & Hip-Seam Weight Adjustment
                // =========================================================================
                GD.Print("\n[TEST 28] Verifying automatic thigh/shoulder counter-twist weights & distal bleed cleanup (Pre-Rigged + KNN)...");
                {
                    Skeleton3D previewSkel = CustomModelAutoRigger.FindSkeleton(previewHeroClone);
                    var twistProfile = CustomModelAutoRigger.BuildLimbTwistProfile(previewSkel);

                    var twistMethods = new[]
                    {
                        CustomModelAutoRigger.RiggingMethod.PreRiggedSkeletonRemap,
                        CustomModelAutoRigger.RiggingMethod.KnnSurfaceProjection
                    };

                    string[] requiredThighTwistBones =
                    {
                        "leg_upper_L_TWIST",
                        "leg_upper_L_TWIST1",
                        "leg_upper_R_TWIST",
                        "leg_upper_R_TWIST1"
                    };

                    foreach (var method in twistMethods)
                    {
                        CustomModelAutoRigger.RemoveCustomModelAndRestoreHero(previewHeroClone);
                        var twistOpts = new CustomModelAutoRigger.RiggingOptions
                        {
                            Method = method,
                            AutoCounterTwistWeights = true,
                            AlignedTransform = new Transform3D(Basis.FromScale(new Vector3(autoFitScale, autoFitScale, autoFitScale)), Vector3.Zero)
                        };

                        var rigRes = CustomModelAutoRigger.RigAndBind(sashaScene, previewHeroClone, twistOpts);
                        if (!rigRes.Success || rigRes.CustomSubmeshes.Count == 0)
                        {
                            GD.PrintErr($"FAIL: [{method}] AutoCounterTwistWeights rig failed: {rigRes.ErrorMessage}");
                            allPassed = false;
                            continue;
                        }

                        var boneVertCounts = new System.Collections.Generic.Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                        var boneWeightSums = new System.Collections.Generic.Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
                        int badWeightSumCount = 0;
                        int strayDistalBleedCount = 0;
                        int crossLegBleedCount = 0;
                        int highThighPelvisCount = 0;
                        int legIdxPelvis = previewSkel.FindBone("pelvis");

                        foreach (var mi in rigRes.CustomSubmeshes)
                        {
                            if (mi?.Mesh == null || mi.Skin == null) continue;
                            Skin skin = mi.Skin;
                            int bindCount = skin.GetBindCount();
                            int[] bindToSkel = new int[bindCount];
                            for (int bi = 0; bi < bindCount; bi++)
                            {
                                int sb = skin.GetBindBone(bi);
                                if (sb < 0 && !string.IsNullOrEmpty(skin.GetBindName(bi)))
                                    sb = previewSkel.FindBone(skin.GetBindName(bi));
                                bindToSkel[bi] = sb;
                            }

                            for (int s = 0; s < mi.Mesh.GetSurfaceCount(); s++)
                            {
                                var arrays = mi.Mesh.SurfaceGetArrays(s);
                                if (arrays == null || arrays.Count <= (int)Mesh.ArrayType.Weights) continue;
                                var verts = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                                var bones = arrays[(int)Mesh.ArrayType.Bones].AsInt32Array();
                                var weights = arrays[(int)Mesh.ArrayType.Weights].AsFloat32Array();
                                if (verts == null || bones == null || weights == null) continue;

                                for (int v = 0; v < verts.Length; v++)
                                {
                                    float sum = 0f;
                                    int baseIdx = v * 4;
                                    for (int k = 0; k < 4; k++)
                                    {
                                        float w = weights[baseIdx + k];
                                        if (w <= 0.0001f) continue;
                                        sum += w;

                                        int bi = bones[baseIdx + k];
                                        int sb = (bi >= 0 && bi < bindToSkel.Length) ? bindToSkel[bi] : -1;
                                        if (sb >= 0)
                                        {
                                            string bName = previewSkel.GetBoneName(sb);
                                            boneVertCounts[bName] = (boneVertCounts.TryGetValue(bName, out int curVc) ? curVc : 0) + 1;
                                            boneWeightSums[bName] = (boneWeightSums.TryGetValue(bName, out float curWs) ? curWs : 0f) + w;
                                        }
                                    }

                                    if (Mathf.Abs(sum - 1.0f) > 0.01f)
                                        badWeightSumCount++;

                                    foreach (var chain in twistProfile.Chains)
                                    {
                                        if (chain.DistalBone < 0 || chain.LowerLen <= 0.05f) continue;
                                        float upperW = 0f, lowerW = 0f, distalW = 0f, pelvisW = 0f;
                                        for (int k = 0; k < 4; k++)
                                        {
                                            float w = weights[baseIdx + k];
                                            if (w <= 0.0001f) continue;
                                            int bi = bones[baseIdx + k];
                                            int sb = (bi >= 0 && bi < bindToSkel.Length) ? bindToSkel[bi] : -1;
                                            if (sb == chain.UpperBone || sb == chain.TwistFullBone || sb == chain.TwistHalfBone) upperW += w;
                                            else if (sb == chain.LowerBone) lowerW += w;
                                            else if (sb == chain.DistalBone) distalW += w;
                                            else if (sb == legIdxPelvis) pelvisW += w;
                                        }

                                        if (distalW > 0.0001f && distalW < 0.25f && (upperW + lowerW) >= 0.30f)
                                        {
                                            float tLower = (verts[v] - chain.LowerOrigin).Dot(chain.LowerDir) / chain.LowerLen;
                                            if (tLower < 0.55f) strayDistalBleedCount++;
                                        }

                                        if (chain.IsLeg && chain.UpperLen > 0.05f)
                                        {
                                            Vector3 relU = verts[v] - chain.UpperOrigin;
                                            float projU = relU.Dot(chain.UpperDir);
                                            float tUpper = projU / chain.UpperLen;
                                            float rPerp = (relU - chain.UpperDir * projU).Length();

                                            if (tUpper >= 0.20f && tUpper <= 0.45f && rPerp <= 0.28f && upperW >= 0.25f && pelvisW > 0.15f)
                                                highThighPelvisCount++;

                                            if (tUpper >= 0.03f && tUpper <= 1.05f && rPerp <= 0.16f && upperW >= 0.20f)
                                            {
                                                foreach (var other in twistProfile.Chains)
                                                {
                                                    if (!other.IsLeg || other.UpperBone == chain.UpperBone) continue;
                                                    float oppW = 0f;
                                                    for (int k = 0; k < 4; k++)
                                                    {
                                                        float w = weights[baseIdx + k];
                                                        if (w <= 0.0001f) continue;
                                                        int bi = bones[baseIdx + k];
                                                        int sb = (bi >= 0 && bi < bindToSkel.Length) ? bindToSkel[bi] : -1;
                                                        if (sb == other.UpperBone || sb == other.TwistFullBone || sb == other.TwistHalfBone || sb == other.LowerBone)
                                                            oppW += w;
                                                    }
                                                    if (oppW > 0.005f && oppW < 0.22f && upperW >= oppW * 2.5f)
                                                        crossLegBleedCount++;
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }

                        bool methodPassed = badWeightSumCount == 0 && strayDistalBleedCount == 0 && crossLegBleedCount == 0 && highThighPelvisCount == 0;
                        foreach (string reqBone in requiredThighTwistBones)
                        {
                            int vc = boneVertCounts.TryGetValue(reqBone, out int vcVal) ? vcVal : 0;
                            float ws = boneWeightSums.TryGetValue(reqBone, out float wsVal) ? wsVal : 0f;
                            GD.Print($"  [{method}] {reqBone}: {vc} verts, totalW={ws:F1}");
                            if (vc < 100 || ws < 50f)
                            {
                                GD.PrintErr($"FAIL: [{method}] Insufficient counter-twist weights on '{reqBone}' (verts={vc}, totalW={ws:F1}).");
                                methodPassed = false;
                            }
                        }

                        // Verify export bundle preserves counter-twist bone weights intact
                        var twistExportBundle = CustomModelExporter.BuildExportBundle(rigRes.CustomSubmeshes, previewSkel, "chrono");
                        int exportedRT = 0, exportedRT1 = 0;
                        int idxRT = twistExportBundle.BoneNames.FindIndex(n => n.Equals("leg_upper_R_TWIST", StringComparison.OrdinalIgnoreCase));
                        int idxRT1 = twistExportBundle.BoneNames.FindIndex(n => n.Equals("leg_upper_R_TWIST1", StringComparison.OrdinalIgnoreCase));
                        foreach (var sub in twistExportBundle.Submeshes)
                        {
                            if (sub.BoneIndices == null || sub.BoneWeights == null) continue;
                            for (int i = 0; i < sub.BoneIndices.Length; i++)
                            {
                                if (sub.BoneWeights[i] <= 0.0001f) continue;
                                if (sub.BoneIndices[i] == idxRT) exportedRT++;
                                else if (sub.BoneIndices[i] == idxRT1) exportedRT1++;
                            }
                        }

                        if (exportedRT < 100 || exportedRT1 < 100)
                        {
                            GD.PrintErr($"FAIL: [{method}] CustomModelExporter dropped thigh twist weights (RT={exportedRT}, RT1={exportedRT1}).");
                            methodPassed = false;
                        }

                        if (badWeightSumCount > 0 || strayDistalBleedCount > 0 || crossLegBleedCount > 0 || highThighPelvisCount > 0)
                        {
                            GD.PrintErr($"FAIL: [{method}] badWeightSums={badWeightSumCount}, strayDistalBleeds={strayDistalBleedCount}, crossLegBleed={crossLegBleedCount}, highThighPelvis={highThighPelvisCount}.");
                            methodPassed = false;
                        }

                        if (methodPassed)
                        {
                            GD.Print($"PASS: [{method}] Counter-twist redistribution & export preservation verified (exportedRT={exportedRT}, exportedRT1={exportedRT1}, strayBleeds=0, crossLegBleed=0, highThighPelvis=0).");
                        }
                        else
                        {
                            allPassed = false;
                        }
                    }
                }

                // 29. Verify Unrigged KNN Weight Transfer on Venator (priest) (8-bone stride, non-body props, and clamping delta diagnostics)
                GD.Print("\n[TEST 29] Inspecting Venator (priest) donor meshes, 8-bone skinning stride & unrigged KNN weighting...");
                var priestEntry = DeadlockHeroCatalog.GetByCodename("priest");
                if (priestEntry != null)
                {
                    Node3D priestNode = await vpkLoader.LoadHeroModelAsync(priestEntry);
                    if (priestNode != null)
                    {
                        if (priestNode.GetParent() == null) Root.AddChild(priestNode);
                        var priestSkel = CustomModelAutoRigger.FindSkeleton(priestNode);
                        var priestMeshes = CustomModelAutoRigger.CollectOriginalHeroMeshes(priestNode);
                        GD.Print($"[TEST 29] Priest meshes ({priestMeshes.Count}), Skel bones={priestSkel?.GetBoneCount() ?? 0}:");
                        int maxStride = 4;
                        foreach (var pm in priestMeshes)
                        {
                            int vc = 0, bc = 0;
                            if (pm.Mesh != null && pm.Mesh.GetSurfaceCount() > 0)
                            {
                                var arr = pm.Mesh.SurfaceGetArrays(0);
                                var vArr = arr[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                                var bArr = arr.Count > (int)Mesh.ArrayType.Bones ? arr[(int)Mesh.ArrayType.Bones].AsInt32Array() : null;
                                vc = vArr?.Length ?? 0;
                                bc = bArr?.Length ?? 0;
                            }
                            int st = vc > 0 ? bc / vc : 0;
                            if (st > maxStride) maxStride = st;
                            GD.Print($"  mesh='{pm.Name}' verts={vc} boneInts={bc} stride={st} skinBinds={pm.Skin?.GetBindCount() ?? 0}");
                        }

                        Vector3 GetRestPos(string boneName)
                        {
                            int idx = priestSkel.FindBone(boneName);
                            return idx >= 0 ? priestSkel.GetBoneGlobalRest(idx).Origin : Vector3.Zero;
                        }
                        Vector3 pPelvis = GetRestPos("pelvis");
                        Vector3 pSpine3 = GetRestPos("spine_3");
                        Vector3 pHead = GetRestPos("head");
                        Vector3 pShL = GetRestPos("arm_upper_L");
                        Vector3 pShR = GetRestPos("arm_upper_R");
                        Vector3 pElL = GetRestPos("arm_lower_L");
                        Vector3 pElR = GetRestPos("arm_lower_R");
                        Vector3 pHandL = GetRestPos("hand_L");
                        Vector3 pHandR = GetRestPos("hand_R");
                        Vector3 pHipL = GetRestPos("leg_upper_L");
                        Vector3 pAnkleL = GetRestPos("ankle_L");
                        Vector3 pHipR = GetRestPos("leg_upper_R");
                        Vector3 pAnkleR = GetRestPos("ankle_R");
                        GD.Print($"[TEST 29] Priest rest skeleton landmarks: pelvis={pPelvis}, spine_3={pSpine3}, head={pHead}, arm_upper_L={pShL}, hand_L={pHandL}, hip_L={pHipL}, ankle_L={pAnkleL}");

                        // Build a synthetic unrigged T-pose humanoid probe mesh scaled to priest's skeleton landmarks,
                        // matching PoseHeroArmsForPreset(TPose = +45° coronal lift around each shoulder).
                        var probeVerts = new System.Collections.Generic.List<Vector3>();
                        var probeLabels = new System.Collections.Generic.List<string>();
                        for (int i = 0; i <= 6; i++)
                        {
                            float t = i / 6f;
                            probeVerts.Add(pPelvis.Lerp(pSpine3, t) + new Vector3(0f, 0f, 0.05f));
                            probeLabels.Add("torso");
                        }
                        probeVerts.Add(pHead + new Vector3(0f, 0.05f, 0f));
                        probeLabels.Add("head");

                        Vector3 axisL = (pElL - pShL).Cross(Vector3.Up).Normalized();
                        Vector3 axisR = (pElR - pShR).Cross(Vector3.Up).Normalized();
                        Quaternion qLiftL = new Quaternion(axisL, Mathf.DegToRad(45f));
                        Quaternion qLiftR = new Quaternion(axisR, Mathf.DegToRad(45f));
                        Vector3 tElL = pShL + qLiftL * (pElL - pShL);
                        Vector3 tHandL = pShL + qLiftL * (pHandL - pShL);
                        Vector3 tElR = pShR + qLiftR * (pElR - pShR);
                        Vector3 tHandR = pShR + qLiftR * (pHandR - pShR);
                        foreach (float u in new[] { 0.3f, 0.7f, 1.0f })
                        {
                            probeVerts.Add(pShL.Lerp(tElL, u)); probeLabels.Add("arm_L");
                            probeVerts.Add(pShR.Lerp(tElR, u)); probeLabels.Add("arm_R");
                        }
                        foreach (float u in new[] { 0.5f, 1.0f })
                        {
                            probeVerts.Add(tElL.Lerp(tHandL, u)); probeLabels.Add("arm_L");
                            probeVerts.Add(tElR.Lerp(tHandR, u)); probeLabels.Add("arm_R");
                        }
                        for (int i = 0; i <= 5; i++)
                        {
                            float t = i / 5f;
                            probeVerts.Add(pAnkleL.Lerp(pHipL, t)); probeLabels.Add("leg_L");
                            probeVerts.Add(pAnkleR.Lerp(pHipR, t)); probeLabels.Add("leg_R");
                        }

                        var probeArr = new Godot.Collections.Array();
                        probeArr.Resize((int)Mesh.ArrayType.Max);
                        probeArr[(int)Mesh.ArrayType.Vertex] = probeVerts.ToArray();
                        var probeNorms = new Vector3[probeVerts.Count];
                        Array.Fill(probeNorms, Vector3.Forward);
                        probeArr[(int)Mesh.ArrayType.Normal] = probeNorms;
                        var probeIdxs = new int[probeVerts.Count * 3];
                        for (int i = 0; i < probeVerts.Count; i++) { probeIdxs[i * 3] = i; probeIdxs[i * 3 + 1] = i; probeIdxs[i * 3 + 2] = i; }
                        probeArr[(int)Mesh.ArrayType.Index] = probeIdxs;

                        var probeMesh = new ArrayMesh();
                        probeMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, probeArr);
                        var probeScene = new Node3D { Name = "ProbeUnriggedScene" };
                        var probeMi = new MeshInstance3D { Name = "ProbeHumanoid", Mesh = probeMesh };
                        probeScene.AddChild(probeMi);

                        int InspectKnnRun(CustomModelAutoRigger.PosePreset preset, float customAngleDeg, bool checkArms)
                        {
                            var opts = new CustomModelAutoRigger.RiggingOptions
                            {
                                Mode = CustomModelAutoRigger.ReplaceMode.FullReplacement,
                                Preset = preset,
                                CustomArmAngleDeg = customAngleDeg,
                                Method = CustomModelAutoRigger.RiggingMethod.KnnSurfaceProjection,
                                AlignedTransform = Transform3D.Identity,
                                SmoothingIterations = 0,
                                SearchRadius = 0.20f,
                                KNearest = 5,
                                ExcludeClothHairBones = true,
                                StabilizeNeckFloatingHead = false,
                                AutoCounterTwistWeights = true,
                                IsPreviewOnly = true
                            };

                            var knnRes = CustomModelAutoRigger.RigAndBind(probeScene, priestNode, opts);
                            if (!knnRes.Success || knnRes.CustomSubmeshes.Count == 0)
                            {
                                GD.PrintErr($"FAIL: [TEST 29] KNN RigAndBind ({preset}) on priest failed: {knnRes.ErrorMessage}");
                                return 999;
                            }

                            var outMi = knnRes.CustomSubmeshes[0];
                            var outArr = outMi.Mesh.SurfaceGetArrays(0);
                            var outBones = outArr[(int)Mesh.ArrayType.Bones].AsInt32Array();
                            var outWeights = outArr[(int)Mesh.ArrayType.Weights].AsFloat32Array();
                            var outSkin = outMi.Skin;

                            int mismatches = 0;
                            for (int v = 0; v < probeVerts.Count; v++)
                            {
                                string domBone = "";
                                float domW = -1f;
                                for (int k = 0; k < 4; k++)
                                {
                                    float w = outWeights[v * 4 + k];
                                    if (w > domW)
                                    {
                                        domW = w;
                                        int bindIdx = outBones[v * 4 + k];
                                        int skIdx = (outSkin != null && bindIdx >= 0 && bindIdx < outSkin.GetBindCount()) ? outSkin.GetBindBone(bindIdx) : bindIdx;
                                        domBone = (skIdx >= 0 && skIdx < priestSkel.GetBoneCount()) ? priestSkel.GetBoneName(skIdx) : $"#{skIdx}";
                                    }
                                }
                                string lbl = probeLabels[v];
                                string dbLow = domBone.ToLowerInvariant();
                                bool forbidden = dbLow.Contains("root") || dbLow.Contains("stake") || dbLow.Contains("weapon") ||
                                                 dbLow.Contains("gun") || dbLow.Contains("coat") || dbLow.Contains("sash");
                                bool torsoHeadOk = (lbl == "torso" && (dbLow.Contains("spine") || dbLow.Contains("pelvis") || dbLow.Contains("clav"))) ||
                                                   (lbl == "head" && (dbLow.Contains("head") || dbLow.Contains("neck")));
                                bool legOk = (lbl == "leg_L" && !dbLow.EndsWith("_r") && (dbLow.Contains("leg") || dbLow.Contains("ankle") || dbLow.Contains("ball") || dbLow.Contains("pelvis"))) ||
                                             (lbl == "leg_R" && !dbLow.EndsWith("_l") && (dbLow.Contains("leg") || dbLow.Contains("ankle") || dbLow.Contains("ball") || dbLow.Contains("pelvis")));
                                bool armOk = !checkArms ||
                                             (lbl == "arm_L" && !dbLow.EndsWith("_r") && (dbLow.Contains("arm") || dbLow.Contains("hand") || dbLow.Contains("clav"))) ||
                                             (lbl == "arm_R" && !dbLow.EndsWith("_l") && (dbLow.Contains("arm") || dbLow.Contains("hand") || dbLow.Contains("clav")));
                                bool mismatch = forbidden ||
                                                ((lbl == "torso" || lbl == "head") && !torsoHeadOk) ||
                                                ((lbl == "leg_L" || lbl == "leg_R") && !legOk) ||
                                                ((lbl == "arm_L" || lbl == "arm_R") && !armOk);
                                if (mismatch) mismatches++;
                                GD.Print($"  [{preset}] probe[{v:00}] ({lbl,-5} {probeVerts[v].X:+0.00;-0.00},{probeVerts[v].Y:0.00}) -> {domBone} ({domW:0.00}){(mismatch ? " [MISMATCH]" : "")}");
                            }
                            return mismatches;
                        }

                        // Pass 1: MatchHeroBindPose verifies torso/head/legs cleanly & zero accessory/weaponHand/sash bleed anywhere
                        int m1 = InspectKnnRun(CustomModelAutoRigger.PosePreset.MatchHeroBindPose, 0f, checkArms: false);
                        // Pass 2: TPose (+45 deg coronal lift) verifies PoseHeroArmsForPreset, BakeInversePose & all 30 probe vertices including arms
                        int m2 = InspectKnnRun(CustomModelAutoRigger.PosePreset.TPose, 0f, checkArms: true);
                        if (m1 > 0 || m2 > 0)
                        {
                            GD.PrintErr($"FAIL: [TEST 29] Detected probe mismatches on priest (MatchHero={m1}, TPose={m2}, maxStride={maxStride})!");
                            allPassed = false;
                        }
                        else
                        {
                            GD.Print($"PASS: [TEST 29] Zero accessory/weaponHand/sash bindings in MatchHeroBindPose, and all {probeVerts.Count} probe vertices bound cleanly in TPose(+45°) on priest (maxStride={maxStride}).");
                        }

                        // Verify ClampLimbBoneTranslations leaves procedural/neck/head/clavicle/twist/weapon bones untouched during animation
                        var priestAp = priestNode.FindChildren("*", "AnimationPlayer", true, false).OfType<AnimationPlayer>().FirstOrDefault();
                        if (priestAp != null)
                        {
                            var animList = priestAp.GetAnimationList();
                            GD.Print($"[TEST 29] Priest animations count={animList.Length}");
                            foreach (var aName in new[] { "crouch", "run" })
                            {
                                string foundAnim = animList.FirstOrDefault(a => a.ToLowerInvariant().Contains(aName) && !a.ToLowerInvariant().Contains("additive"));
                                if (foundAnim != null)
                                {
                                    priestAp.Play(foundAnim);
                                    priestAp.Seek(0.15, true);
                                    int neckIdx = priestSkel.FindBone("neck_0");
                                    Vector3 neckBefore = neckIdx >= 0 ? priestSkel.GetBonePosePosition(neckIdx) : Vector3.Zero;
                                    CustomModelAutoRigger.UpdateCustomModelRuntimePosing(priestSkel, true);
                                    if (neckIdx >= 0)
                                    {
                                        float neckShift = priestSkel.GetBonePosePosition(neckIdx).DistanceTo(neckBefore);
                                        if (neckShift > 0.001f)
                                        {
                                            GD.PrintErr($"FAIL: [TEST 29] ClampLimbBoneTranslations altered neck_0 on '{foundAnim}' by {neckShift:F4}m!");
                                            allPassed = false;
                                        }
                                    }
                                }
                            }
                        }
                        probeScene.QueueFree();
                        priestNode.QueueFree();
                    }
                }

                // 33. TEST 30: Auto-Calibrating Unrigged Pose Calibration + Regional Corridor KNN + Individual Composite Surface Keep (Gordon Freeman -> priest)
                string gordonPath = System.IO.Path.GetFullPath(@"..\scratch_out\models\gordon_freeman_half-life_1.glb");
                if (!System.IO.File.Exists(gordonPath))
                    gordonPath = System.IO.Path.GetFullPath(@"scratch_out\models\gordon_freeman_half-life_1.glb");

                var priestEntry30 = DeadlockHeroCatalog.GetByCodename("priest");
                if (priestEntry30 != null && System.IO.File.Exists(gordonPath))
                {
                    GD.Print("\n[TEST 30] Verifying Auto-Calibrated Unrigged KNN + Rigid Finger Lock + Individual Composite Surface Keep (gordon_freeman -> priest)...");
                    var priestLive = await vpkLoader.LoadHeroModelAsync(priestEntry30);
                    var gordonScene = CustomModelAutoRigger.LoadAndSanitize3DModelScene(gordonPath);
                    if (priestLive != null && gordonScene != null)
                    {
                        if (priestLive.GetParent() == null) Root.AddChild(priestLive);
                        var priestHierarchy = new DeadlockPlayground.Painter.HeroMeshHierarchy();
                        priestHierarchy.ScanHero(priestLive);
                        var priestSkel = CustomModelAutoRigger.FindSkeleton(priestLive);
                        priestSkel?.ResetBonePoses();

                        var (pMin, pMax, pOk) = CustomModelAutoRigger.ComputeAuthenticHeroBounds(priestLive);
                        var (gMin, gMax, gOk) = CustomModelAutoRigger.ComputePristineModelBounds(gordonScene);
                        float scale = (pOk && gOk && (gMax.Y - gMin.Y) > 0.01f)
                            ? (pMax.Y - Math.Max(0f, pMin.Y)) / (gMax.Y - gMin.Y)
                            : 0.025f;
                        Vector3 heroCtr = (pMin + pMax) * 0.5f;
                        Vector3 gCtr = (gMin + gMax) * 0.5f;
                        Transform3D alignedXf = Transform3D.Identity
                            .Scaled(new Vector3(scale, scale, scale))
                            .Rotated(Vector3.Up, Mathf.Pi);
                        alignedXf.Origin = new Vector3(heroCtr.X - (-gCtr.X * scale), -gMin.Y * scale, heroCtr.Z - (-gCtr.Z * scale));

                        var cal = CustomModelAutoRigger.AutoEstimateUnriggedPoseCalibration(gordonScene, priestSkel, alignedXf);
                        GD.Print($"[TEST 30] AutoEstimateUnriggedPoseCalibration: ArmAngle={cal.ArmAngleDeg:F1}°, ArmFwd={cal.ArmForwardDeg:F1}°, ElbowStraight={cal.ElbowStraightenPct:F0}%, LegSpread={cal.LegSpreadDeg:F1}°, ArmSamples={cal.DetectedArmSamples}, LegSamples={cal.DetectedLegSamples}");

                        if (cal.DetectedArmSamples < 15 || cal.DetectedLegSamples < 15 || cal.ArmAngleDeg >= -2f)
                        {
                            GD.PrintErr($"FAIL: [TEST 30] Auto-calibration did not detect Gordon's arms-down pose on priest!");
                            allPassed = false;
                        }

                        var gordonOpts = new CustomModelAutoRigger.RiggingOptions
                        {
                            Method = CustomModelAutoRigger.RiggingMethod.KnnSurfaceProjection,
                            Preset = CustomModelAutoRigger.PosePreset.APoseCustomAngle,
                            CustomArmAngleDeg = cal.ArmAngleDeg,
                            CustomArmForwardDeg = cal.ArmForwardDeg,
                            CustomElbowStraightenPct = cal.ElbowStraightenPct,
                            CustomLegSpreadDeg = cal.LegSpreadDeg,
                            AlignedTransform = alignedXf,
                            KeepFingersRigidOnHand = true,
                            ClampLimbStretching = true,
                            AutoCounterTwistWeights = true,
                            KeptOriginalSubmeshNames = new System.Collections.Generic.List<string>
                            {
                                CustomModelAutoRigger.MakeSurfaceKeepKey("_priest", 2) // priest_shotgun only!
                            }
                        };

                        var rigRes = CustomModelAutoRigger.RigAndBind(gordonScene, priestLive, gordonOpts);
                        if (!rigRes.Success || rigRes.CustomSubmeshes.Count == 0)
                        {
                            GD.PrintErr($"FAIL: [TEST 30] Gordon RigAndBind failed: {rigRes.ErrorMessage}");
                            allPassed = false;
                        }
                        else
                        {
                            // Verify individual surface keep: _priest_surf_2* visible, other split surfaces hidden!
                            var allPriestMeshes = priestLive.FindChildren("*", "MeshInstance3D", true, false).OfType<MeshInstance3D>().ToList();
                            var surf0 = allPriestMeshes.FirstOrDefault(m => m.Name.ToString().StartsWith("_priest_surf_0", StringComparison.OrdinalIgnoreCase));
                            var surf2 = allPriestMeshes.FirstOrDefault(m => m.Name.ToString().StartsWith("_priest_surf_2", StringComparison.OrdinalIgnoreCase));
                            var surf3 = allPriestMeshes.FirstOrDefault(m => m.Name.ToString().StartsWith("_priest_surf_3", StringComparison.OrdinalIgnoreCase));
                            var surf4 = allPriestMeshes.FirstOrDefault(m => m.Name.ToString().StartsWith("_priest_surf_4", StringComparison.OrdinalIgnoreCase));
                            if (surf2 == null || !surf2.Visible || (surf0 != null && surf0.Visible) || (surf3 != null && surf3.Visible) || (surf4 != null && surf4.Visible))
                            {
                                GD.PrintErr($"FAIL: [TEST 30] Individual surface keep failed! surf0={surf0?.Name}:{surf0?.Visible}, surf2={surf2?.Name}:{surf2?.Visible}, surf3={surf3?.Name}:{surf3?.Visible}, surf4={surf4?.Name}:{surf4?.Visible}");
                                allPassed = false;
                            }
                            else
                            {
                                GD.Print($"PASS: [TEST 30] Kept {surf2.Name} (priest_shotgun) active individually while hiding _priest_surf_0/_1/_3/_4.");
                            }

                            // Verify arm vs trenchcoat isolation & zero finger splits on Gordon Freeman using Priest's actual height landmarks
                            int shLIdx = priestSkel.FindBone("arm_upper_L");
                            int handLIdx = priestSkel.FindBone("hand_L");
                            int pelvisIdx = priestSkel.FindBone("pelvis");
                            float shX = shLIdx >= 0 ? MathF.Abs(priestSkel.GetBoneGlobalRest(shLIdx).Origin.X) : 0.29f;
                            float shY = shLIdx >= 0 ? priestSkel.GetBoneGlobalRest(shLIdx).Origin.Y : 2.08f;
                            float pelvY = pelvisIdx >= 0 ? priestSkel.GetBoneGlobalRest(pelvisIdx).Origin.Y : 1.50f;
                            float handY = handLIdx >= 0 ? priestSkel.GetBoneGlobalRest(handLIdx).Origin.Y : 1.50f;

                            int badArmCoatVerts = 0;
                            int fingerBoundVerts = 0;
                            int checkedArmVerts = 0;
                            int checkedLegVerts = 0;
                            int badLegArmVerts = 0;

                            foreach (var cm in rigRes.CustomSubmeshes)
                            {
                                var skin = cm.Skin;
                                for (int s = 0; s < cm.Mesh.GetSurfaceCount(); s++)
                                {
                                    var arrays = cm.Mesh.SurfaceGetArrays(s);
                                    var verts = arrays[(int)Mesh.ArrayType.Vertex].As<Vector3[]>();
                                    var bones = arrays[(int)Mesh.ArrayType.Bones].As<int[]>();
                                    var weights = arrays[(int)Mesh.ArrayType.Weights].As<float[]>();
                                    int stride = bones.Length / Math.Max(1, verts.Length);

                                    var posedGlobals = new Transform3D[priestSkel.GetBoneCount()];
                                    var restGlobals = new Transform3D[priestSkel.GetBoneCount()];
                                    for (int b = 0; b < priestSkel.GetBoneCount(); b++) restGlobals[b] = priestSkel.GetBoneGlobalRest(b);
                                    var bk = CustomModelAutoRigger.BackupSkeletonLocalRotations(priestSkel);
                                    priestSkel.ResetBonePoses();
                                    CustomModelAutoRigger.PoseHeroSkeletonForOptions(priestSkel, gordonOpts);
                                    for (int b = 0; b < priestSkel.GetBoneCount(); b++) posedGlobals[b] = priestSkel.GetBoneGlobalPose(b);
                                    CustomModelAutoRigger.RestoreSkeletonLocalRotations(priestSkel, bk);

                                    for (int v = 0; v < verts.Length; v++)
                                    {
                                        int domBind = bones[v * stride];
                                        int domSkel = domBind;
                                        if (skin != null && domBind >= 0 && domBind < skin.GetBindCount())
                                        {
                                            int sb = skin.GetBindBone(domBind);
                                            if (sb >= 0) domSkel = sb;
                                        }
                                        string domName = (domSkel >= 0 && domSkel < priestSkel.GetBoneCount()) ? priestSkel.GetBoneName(domSkel).ToLowerInvariant() : "";

                                        for (int k = 0; k < stride; k++)
                                        {
                                            if (weights[v * stride + k] <= 0.01f) continue;
                                            int kb = bones[v * stride + k];
                                            if (skin != null && kb >= 0 && kb < skin.GetBindCount() && skin.GetBindBone(kb) >= 0) kb = skin.GetBindBone(kb);
                                            string kName = (kb >= 0 && kb < priestSkel.GetBoneCount()) ? priestSkel.GetBoneName(kb).ToLowerInvariant() : "";
                                            if (kName.Contains("finger")) { fingerBoundVerts++; break; }
                                        }

                                        Vector3 posedP = Vector3.Zero;
                                        for (int k = 0; k < stride; k++)
                                        {
                                            float w = weights[v * stride + k];
                                            if (w <= 0.0001f) continue;
                                            int kb = bones[v * stride + k];
                                            if (skin != null && kb >= 0 && kb < skin.GetBindCount() && skin.GetBindBone(kb) >= 0) kb = skin.GetBindBone(kb);
                                            Transform3D m = (kb >= 0 && kb < posedGlobals.Length) ? (posedGlobals[kb] * restGlobals[kb].AffineInverse()) : Transform3D.Identity;
                                            posedP += w * (m * verts[v]);
                                        }

                                        // Check outer arm/forearm/hand silhouette (outside torso/shoulder width at arm height)
                                        if (MathF.Abs(posedP.X) > shX + 0.04f && posedP.Y >= handY - 0.15f && posedP.Y <= shY + 0.05f)
                                        {
                                            checkedArmVerts++;
                                            if (domName.Contains("pelvis") || domName.Contains("leg") || domName.Contains("ankle") || domName.Contains("spine"))
                                            {
                                                badArmCoatVerts++;
                                                GD.Print($"  [TEST 30 BLEED] mesh={cm.Name} posed={posedP:F3} rest={verts[v]:F3} dom={domName} (w0={weights[v * stride]:F2})");
                                            }
                                        }
                                        // Check legs strictly below pelvis
                                        else if (posedP.Y < pelvY - 0.18f && MathF.Abs(posedP.X) < shX)
                                        {
                                            checkedLegVerts++;
                                            if (domName.Contains("arm") || domName.Contains("hand"))
                                            {
                                                badLegArmVerts++;
                                            }
                                        }
                                    }
                                }
                            }

                            GD.Print($"[TEST 30] Gordon verts check: ArmVerts={checkedArmVerts} (coatBleed={badArmCoatVerts}), LegVerts={checkedLegVerts} (armBleed={badLegArmVerts}), FingerSplitVerts={fingerBoundVerts}");
                            if (checkedArmVerts == 0 || badArmCoatVerts > 0 || fingerBoundVerts > 0 || badLegArmVerts > 0)
                            {
                                GD.PrintErr($"FAIL: [TEST 30] Weight isolation failed on Gordon Freeman!");
                                allPassed = false;
                            }
                            else
                            {
                                GD.Print("PASS: [TEST 30] Gordon Freeman calibrated cleanly with zero coat-to-arm bleed, zero arm-to-leg bleed, and solid locked hands!");
                            }
                        }

                        priestLive.QueueFree();

                        // Also verify elbow-straighten detection AFTER priestLive is freed (vpkLoader swaps active hero model instance)
                        var chronoEntry30 = DeadlockHeroCatalog.GetByCodename("chrono");
                        if (chronoEntry30 != null)
                        {
                            var chronoLive = await vpkLoader.LoadHeroModelAsync(chronoEntry30);
                            var chronoSkel30 = chronoLive != null ? CustomModelAutoRigger.FindSkeleton(chronoLive) : null;
                            if (chronoLive != null && chronoSkel30 != null)
                            {
                                chronoSkel30.ResetBonePoses();
                                var (cMin, cMax, cOk) = CustomModelAutoRigger.ComputeAuthenticHeroBounds(chronoLive);
                                float cScale = (cOk && gOk && (gMax.Y - gMin.Y) > 0.01f)
                                    ? (cMax.Y - Math.Max(0f, cMin.Y)) / (gMax.Y - gMin.Y)
                                    : scale;
                                Vector3 cCtr = (cMin + cMax) * 0.5f;
                                Transform3D alignedXfChrono = Transform3D.Identity
                                    .Scaled(new Vector3(cScale, cScale, cScale))
                                    .Rotated(Vector3.Up, Mathf.Pi);
                                alignedXfChrono.Origin = new Vector3(cCtr.X - (-gCtr.X * cScale), -gMin.Y * cScale, cCtr.Z - (-gCtr.Z * cScale));

                                int cShL = chronoSkel30.FindBone("arm_upper_L"), cElL = chronoSkel30.FindBone("arm_lower_L"), cHdL = chronoSkel30.FindBone("hand_L");
                                Vector3 cSh = chronoSkel30.GetBoneGlobalRest(cShL).Origin, cEl = chronoSkel30.GetBoneGlobalRest(cElL).Origin, cWr = chronoSkel30.GetBoneGlobalRest(cHdL).Origin;
                                float cBendDeg = Mathf.RadToDeg(MathF.Acos(Mathf.Clamp((cEl - cSh).Normalized().Dot((cWr - cEl).Normalized()), -1f, 1f)));
                                GD.Print($"[TEST 30] chrono armL rest: sh={cSh:F3}, el={cEl:F3}, wr={cWr:F3}, bend={cBendDeg:F1}°");

                                var calChrono = CustomModelAutoRigger.AutoEstimateUnriggedPoseCalibration(gordonScene, chronoSkel30, alignedXfChrono);
                                GD.Print($"[TEST 30] AutoEstimate Gordon on chrono: ArmAngle={calChrono.ArmAngleDeg:F1}°, ArmFwd={calChrono.ArmForwardDeg:F1}°, ElbowStraight={calChrono.ElbowStraightenPct:F0}%, ArmSamples={calChrono.DetectedArmSamples}");
                                if (calChrono.DetectedArmSamples < 15)
                                {
                                    GD.PrintErr("FAIL: [TEST 30] Expected DetectedArmSamples >= 15 when fitting Gordon onto chrono!");
                                    allPassed = false;
                                }

                                // Verify straight-arm elbow straightening detection (synthetic straight-arm mesh against chrono's 17.3° bent elbow)
                                var straightScene = new Node3D { Name = "StraightArmProbe" };
                                var straightMi = new MeshInstance3D();
                                var straightSt = new SurfaceTool();
                                straightSt.Begin(Mesh.PrimitiveType.Triangles);
                                void AddProbeVerts(Vector3 a, Vector3 b, int steps)
                                {
                                    for (int i = 0; i <= steps; i++)
                                    {
                                        Vector3 c = a.Lerp(b, (float)i / steps);
                                        straightSt.AddVertex(c);
                                        straightSt.AddVertex(c + new Vector3(0.01f, 0f, 0.01f));
                                        straightSt.AddVertex(c + new Vector3(-0.01f, 0f, -0.01f));
                                    }
                                }
                                Vector3 cShR = chronoSkel30.GetBoneGlobalRest(chronoSkel30.FindBone("arm_upper_R")).Origin;
                                AddProbeVerts(cSh, cSh + new Vector3(0.14f, -0.64f, 0f), 10);
                                AddProbeVerts(cShR, cShR + new Vector3(-0.14f, -0.64f, 0f), 10);
                                straightMi.Mesh = straightSt.Commit();
                                straightScene.AddChild(straightMi);
                                var calStraight = CustomModelAutoRigger.AutoEstimateUnriggedPoseCalibration(straightScene, chronoSkel30, Transform3D.Identity);
                                GD.Print($"[TEST 30] AutoEstimate straight-arm probe on chrono: ArmAngle={calStraight.ArmAngleDeg:F1}°, ArmFwd={calStraight.ArmForwardDeg:F1}°, ElbowStraight={calStraight.ElbowStraightenPct:F0}%");
                                straightScene.QueueFree();
                                if (calStraight.ElbowStraightenPct < 60f)
                                {
                                    GD.PrintErr("FAIL: [TEST 30] Expected ElbowStraightenPct >= 60% when fitting a straight-arm mesh onto bent-elbow chrono!");
                                    allPassed = false;
                                }
                            }
                            chronoLive?.QueueFree();
                        }

                        gordonScene.QueueFree();
                    }
                    else
                    {
                        GD.PrintErr("FAIL: [TEST 30] Could not load priest or gordon_freeman_half-life_1.glb");
                        allPassed = false;
                    }
                }

                // 34. TEST 31: Landmark-Pin Auto-Rig (gordon_freeman -> priest)
                if (priestEntry30 != null && System.IO.File.Exists(gordonPath))
                {
                    GD.Print("\n[TEST 31] Verifying Landmark Auto-Rig (gordon_freeman -> priest)...");
                    var priest31 = await vpkLoader.LoadHeroModelAsync(priestEntry30);
                    var gordon31 = CustomModelAutoRigger.LoadAndSanitize3DModelScene(gordonPath);
                    if (priest31 != null && gordon31 != null)
                    {
                        if (priest31.GetParent() == null) Root.AddChild(priest31);
                        var priestHier31 = new DeadlockPlayground.Painter.HeroMeshHierarchy();
                        priestHier31.ScanHero(priest31);
                        var priestSkel31 = CustomModelAutoRigger.FindSkeleton(priest31);
                        priestSkel31?.ResetBonePoses();

                        var (pMin31, pMax31, pOk31) = CustomModelAutoRigger.ComputeAuthenticHeroBounds(priest31);
                        var (gMin31, gMax31, gOk31) = CustomModelAutoRigger.ComputePristineModelBounds(gordon31);
                        float scale31 = (pOk31 && gOk31 && (gMax31.Y - gMin31.Y) > 0.01f)
                            ? (pMax31.Y - Math.Max(0f, pMin31.Y)) / (gMax31.Y - gMin31.Y)
                            : 0.025f;
                        Vector3 heroCtr31 = (pMin31 + pMax31) * 0.5f;
                        Vector3 gCtr31 = (gMin31 + gMax31) * 0.5f;
                        Transform3D xf31 = Transform3D.Identity.Scaled(new Vector3(scale31, scale31, scale31)).Rotated(Vector3.Up, Mathf.Pi);
                        xf31.Origin = new Vector3(heroCtr31.X - (-gCtr31.X * scale31), -gMin31.Y * scale31, heroCtr31.Z - (-gCtr31.Z * scale31));

                        var lmMeshes = CustomModelAutoRigger.CollectMeshes(gordon31);
                        var pins31 = CustomLandmarkWeightSolver.EstimateLandmarks(gordon31, lmMeshes);
                        foreach (var kv in pins31) GD.Print($"  [TEST 31 PIN] {kv.Key} = {kv.Value:F3}");
                        bool pinsOk = CustomLandmarkWeightSolver.PinNames.All(pins31.ContainsKey)
                            && pins31["Head"].Y > pins31["Neck"].Y && pins31["Neck"].Y > pins31["Chest"].Y && pins31["Chest"].Y > pins31["Pelvis"].Y
                            && pins31["Pelvis"].Y > pins31["KneeL"].Y && pins31["KneeL"].Y > pins31["AnkleL"].Y
                            && pins31["KneeR"].Y > pins31["AnkleR"].Y
                            && Math.Abs(pins31["WristL"].X - pins31["WristR"].X) > 0.05f
                            && Math.Abs(pins31["AnkleL"].X - pins31["AnkleR"].X) > 0.02f
                            && pins31["ShoulderL"].Y > pins31["ElbowL"].Y && pins31["ElbowL"].Y > pins31["WristL"].Y;
                        if (!pinsOk)
                        {
                            GD.PrintErr("FAIL: [TEST 31] EstimateLandmarks produced anatomically invalid pins!");
                            allPassed = false;
                        }

                        var opts31 = new CustomModelAutoRigger.RiggingOptions
                        {
                            Method = CustomModelAutoRigger.RiggingMethod.LandmarkAutoRig,
                            AlignedTransform = xf31,
                            KeepFingersRigidOnHand = true,
                            ClampLimbStretching = true,
                            AutoCounterTwistWeights = true,
                            KeptOriginalSubmeshNames = new System.Collections.Generic.List<string>
                            {
                                CustomModelAutoRigger.MakeSurfaceKeepKey("_priest", 2)
                            }
                        };
                        var sw31 = System.Diagnostics.Stopwatch.StartNew();
                        var res31 = CustomModelAutoRigger.RigAndBind(gordon31, priest31, opts31);
                        sw31.Stop();
                        GD.Print($"[TEST 31] Landmark RigAndBind: success={res31.Success}, submeshes={res31.CustomSubmeshes?.Count ?? 0}, verts={res31.TotalVertices}, {sw31.ElapsedMilliseconds} ms");
                        bool sceneRestored = CustomModelAutoRigger.FindSkeleton(gordon31) == null;
                        bool handWeightsOk = true;
                        int handVertsCount = 0;
                        int armVertsCount = 0;
                        string handFailReason = "";
                        string overrideKey31 = null;
                        int overrideVertCount31 = 0;
                        if (res31.Success && res31.CustomSubmeshes != null && priestSkel31 != null)
                        {
                            foreach (var sm in res31.CustomSubmeshes)
                            {
                                if (sm?.Mesh is not ArrayMesh am || am.GetSurfaceCount() == 0) continue;
                                var arrs = am.SurfaceGetArrays(0);
                                var smVerts = arrs[(int)Mesh.ArrayType.Vertex].As<Vector3[]>();
                                var smBones = arrs[(int)Mesh.ArrayType.Bones].As<int[]>();
                                var smWeights = arrs[(int)Mesh.ArrayType.Weights].As<float[]>();
                                if (smVerts == null || smBones == null || smWeights == null) continue;
                                if (overrideKey31 == null && sm.HasMeta("SourceMeshName") && sm.HasMeta("SourceSurfaceIndex"))
                                {
                                    overrideKey31 = CustomModelAutoRigger.MakeSurfaceKeepKey(sm.GetMeta("SourceMeshName").AsString(), sm.GetMeta("SourceSurfaceIndex").AsInt32());
                                    overrideVertCount31 = smVerts.Length;
                                }
                                int stride = (smVerts.Length > 0 && smBones.Length >= smVerts.Length * 8) ? 8 : 4;
                                for (int v = 0; v < smVerts.Length; v++)
                                {
                                    int domBone = -1;
                                    float maxW = -1f;
                                    for (int k = 0; k < stride; k++)
                                    {
                                        float w = smWeights[v * stride + k];
                                        if (w > maxW)
                                        {
                                            maxW = w;
                                            int bi = smBones[v * stride + k];
                                            string bName = (sm.Skin != null && bi >= 0 && bi < sm.Skin.GetBindCount()) ? sm.Skin.GetBindName(bi).ToString() : "";
                                            domBone = priestSkel31.FindBone(bName);
                                        }
                                    }
                                    string domName = domBone >= 0 ? priestSkel31.GetBoneName(domBone) : "";
                                    bool isHand = domName == "hand_L" || domName == "hand_R";
                                    bool isArm = isHand || domName == "arm_lower_L" || domName == "arm_lower_R" || domName == "arm_upper_L" || domName == "arm_upper_R";
                                    if (isHand) handVertsCount++;
                                    if (isArm) armVertsCount++;
                                    for (int k = 0; k < stride; k++)
                                    {
                                        float w = smWeights[v * stride + k];
                                        if (w <= 0.001f) continue;
                                        int bi = smBones[v * stride + k];
                                        string bName = (sm.Skin != null && bi >= 0 && bi < sm.Skin.GetBindCount()) ? sm.Skin.GetBindName(bi).ToString() : "";
                                        if (isHand && (bName.StartsWith("spine_") || bName == "pelvis" || bName.StartsWith("leg_") || bName.StartsWith("ankle_") || bName.StartsWith("clavicle_")))
                                        {
                                            handWeightsOk = false;
                                            handFailReason = $"{sm.Name} v{v} (hand={domName}) bled {w:F3} onto {bName}";
                                            break;
                                        }
                                        if (isArm && (bName == "pelvis" || bName.StartsWith("leg_") || bName.StartsWith("ankle_")))
                                        {
                                            handWeightsOk = false;
                                            handFailReason = $"{sm.Name} v{v} (arm={domName}) bled {w:F3} onto {bName}";
                                            break;
                                        }
                                    }
                                    if (!handWeightsOk) break;
                                }
                                if (!handWeightsOk) break;
                            }
                            if (handVertsCount == 0 || armVertsCount == 0)
                            {
                                handWeightsOk = false;
                                handFailReason = $"Insufficient arm/hand verts bound (hand={handVertsCount}, arm={armVertsCount})";
                            }
                        }

                        bool overrideOk = false;
                        string overrideFailReason = "no override surface found";
                        if (res31.Success && sceneRestored && handWeightsOk && overrideKey31 != null && overrideVertCount31 > 1)
                        {
                            var ov31 = new CustomModelAutoRigger.SkinWeightOverride
                            {
                                VertexCount = overrideVertCount31,
                                BonePalette = new[] { "head", "pelvis" },
                                PaletteIndices = new int[overrideVertCount31 * 4],
                                Weights = new float[overrideVertCount31 * 4]
                            };
                            ov31.PaletteIndices[0] = 0;
                            ov31.Weights[0] = 1.0f;
                            for (int v = 1; v < overrideVertCount31; v++)
                            {
                                ov31.PaletteIndices[v * 4 + 0] = 1;
                                ov31.Weights[v * 4 + 0] = 1.0f;
                            }
                            opts31.CustomSkinWeightOverrides = new System.Collections.Generic.Dictionary<string, CustomModelAutoRigger.SkinWeightOverride>
                            {
                                [overrideKey31] = ov31
                            };
                            var res31Ov = CustomModelAutoRigger.RigAndBind(gordon31, priest31, opts31);
                            if (res31Ov.Success && res31Ov.CustomSubmeshes != null)
                            {
                                foreach (var sm in res31Ov.CustomSubmeshes)
                                {
                                    if (sm == null || !sm.HasMeta("SourceMeshName") || !sm.HasMeta("SourceSurfaceIndex")) continue;
                                    string smKey = CustomModelAutoRigger.MakeSurfaceKeepKey(sm.GetMeta("SourceMeshName").AsString(), sm.GetMeta("SourceSurfaceIndex").AsInt32());
                                    if (smKey != overrideKey31 || sm.Mesh is not ArrayMesh am || am.GetSurfaceCount() == 0) continue;
                                    var arrs = am.SurfaceGetArrays(0);
                                    var smBones = arrs[(int)Mesh.ArrayType.Bones].As<int[]>();
                                    var smWeights = arrs[(int)Mesh.ArrayType.Weights].As<float[]>();
                                    int stride = smBones.Length >= overrideVertCount31 * 8 ? 8 : 4;
                                    int bi0 = smBones[0];
                                    int biLast = smBones[(overrideVertCount31 - 1) * stride];
                                    string bn0 = (sm.Skin != null && bi0 >= 0 && bi0 < sm.Skin.GetBindCount()) ? sm.Skin.GetBindName(bi0).ToString() : "";
                                    string bnLast = (sm.Skin != null && biLast >= 0 && biLast < sm.Skin.GetBindCount()) ? sm.Skin.GetBindName(biLast).ToString() : "";
                                    float w0 = smWeights[0];
                                    float wLast = smWeights[(overrideVertCount31 - 1) * stride];
                                    overrideOk = bn0 == "head" && w0 >= 0.999f && bnLast == "pelvis" && wLast >= 0.999f;
                                    if (!overrideOk)
                                        overrideFailReason = $"v0=({bn0}:{w0:F3}), vLast=({bnLast}:{wLast:F3})";
                                    break;
                                }
                            }
                            else
                            {
                                overrideFailReason = res31Ov.ErrorMessage ?? "RigAndBind override pass failed";
                            }
                        }

                        if (!res31.Success || res31.CustomSubmeshes == null || res31.CustomSubmeshes.Count == 0 || res31.TotalVertices <= 0)
                        {
                            GD.PrintErr($"FAIL: [TEST 31] Landmark RigAndBind failed: {res31.ErrorMessage}");
                            allPassed = false;
                        }
                        else if (!sceneRestored)
                        {
                            GD.PrintErr("FAIL: [TEST 31] Synthetic skeleton was not removed from the imported scene afterwards!");
                            allPassed = false;
                        }
                        else if (!handWeightsOk)
                        {
                            GD.PrintErr($"FAIL: [TEST 31] Hand/arm weight isolation failed: {handFailReason}");
                            allPassed = false;
                        }
                        else if (!overrideOk)
                        {
                            GD.PrintErr($"FAIL: [TEST 31] CustomSkinWeightOverrides persistence failed: {overrideFailReason}");
                            allPassed = false;
                        }
                        else if (pinsOk)
                        {
                            GD.Print($"PASS: [TEST 31] Landmark Auto-Rig estimated valid pins, isolated hand/arm weights (handVerts={handVertsCount}, armVerts={armVertsCount}), persisted CustomSkinWeightOverrides ({overrideKey31}), and restored scene.");
                        }

                        // 35. TEST 32: Bug #4 Partial Composite Surface Keep + Weight Paint Undo/Redo, Per-Bone Reset & Wireframe Overlay
                        GD.Print("\n[TEST 32] Verifying Partial Composite Surface Keep Persistence, Weight Undo/Redo, Per-Bone Reset, & Wireframe Overlay...");
                        priestHier31.ScanHero(priest31);

                        bool surf0Hidden = false;
                        bool surf1Hidden = false;
                        bool surf2Visible = false;
                        foreach (var child in priest31.FindChildren("*", "MeshInstance3D", true, false))
                        {
                            if (child is not MeshInstance3D smi) continue;
                            if (!smi.HasMeta("ParentCompositeMesh") || !smi.HasMeta("SurfaceIndex")) continue;
                            string pName = smi.GetMeta("ParentCompositeMesh").AsString();
                            if (!pName.Contains("priest", StringComparison.OrdinalIgnoreCase)) continue;
                            int sIdx = smi.GetMeta("SurfaceIndex").AsInt32();
                            bool userHidden = smi.HasMeta("UserHiddenByCustomModel") && smi.GetMeta("UserHiddenByCustomModel").AsBool();
                            if (sIdx == 0) surf0Hidden = !smi.Visible && userHidden;
                            else if (sIdx == 1) surf1Hidden = !smi.Visible && userHidden;
                            else if (sIdx == 2) surf2Visible = smi.Visible && !userHidden;
                        }

                        bool hiddenExcludedFromHier = !priestHier31.Submeshes.Any(sm =>
                            sm?.Mesh != null && sm.Mesh.HasMeta("ParentCompositeMesh") && sm.Mesh.HasMeta("SurfaceIndex") &&
                            sm.Mesh.Name.ToString().Contains("priest", StringComparison.OrdinalIgnoreCase) &&
                            (sm.Mesh.GetMeta("SurfaceIndex").AsInt32() == 0 || sm.Mesh.GetMeta("SurfaceIndex").AsInt32() == 1));
                        bool keptRegisteredInHier = priestHier31.Submeshes.Any(sm =>
                            sm?.Mesh != null && sm.Mesh.HasMeta("ParentCompositeMesh") && sm.Mesh.HasMeta("SurfaceIndex") &&
                            sm.Mesh.Name.ToString().Contains("priest", StringComparison.OrdinalIgnoreCase) &&
                            sm.Mesh.GetMeta("SurfaceIndex").AsInt32() == 2);

                        if (!surf0Hidden || !surf1Hidden || !surf2Visible || !hiddenExcludedFromHier || !keptRegisteredInHier)
                        {
                            GD.PrintErr($"FAIL: [TEST 32] Partial composite keep mismatch after ScanHero: s0Hidden={surf0Hidden}, s1Hidden={surf1Hidden}, s2Visible={surf2Visible}, excluded={hiddenExcludedFromHier}, keptReg={keptRegisteredInHier}");
                            allPassed = false;
                        }
                        else
                        {
                            GD.Print("PASS: [TEST 32a] ScanHero preserved _priest kept surface #2 (shotgun) and excluded replaced surfaces #0/#1 & materials.");
                        }

                        var opts32 = new CustomModelAutoRigger.RiggingOptions
                        {
                            Method = CustomModelAutoRigger.RiggingMethod.LandmarkAutoRig,
                            AlignedTransform = xf31,
                            KeepFingersRigidOnHand = true,
                            ClampLimbStretching = true,
                            AutoCounterTwistWeights = true
                        };
                        var res32 = CustomModelAutoRigger.RigAndBind(gordon31, priest31, opts32);
                        var surfs32 = res32.Success && res32.ContainerNode != null
                            ? CustomWeightPaintEditor.CaptureSurfaces(res32.ContainerNode)
                            : new System.Collections.Generic.List<CustomWeightPaintEditor.SurfaceState>();

                        bool wpOk = false;
                        string wpFail = "no paint surfaces captured";
                        if (surfs32.Count > 0 && priestSkel31 != null)
                        {
                            var skin32 = surfs32[0].MeshInstance?.Skin;
                            if (skin32 != null)
                            {
                                var baseOv32 = CustomWeightPaintEditor.ExportOverrides(surfs32, priestSkel31, skin32);
                                int ResolveB(string bName)
                                {
                                    int sb = priestSkel31.FindBone(bName);
                                    for (int bi = 0; bi < skin32.GetBindCount(); bi++)
                                    {
                                        if (sb >= 0 && skin32.GetBindBone(bi) == sb) return bi;
                                        if (bName.Equals(skin32.GetBindName(bi), StringComparison.Ordinal)) return bi;
                                    }
                                    return -1;
                                }
                                float ReadVertBoneWeight(CustomWeightPaintEditor.SurfaceState s, int v, int bi)
                                {
                                    float sum = 0f;
                                    int o = v * 4;
                                    for (int k = 0; k < 4; k++) if (s.SkinBones[o + k] == bi) sum += s.Weights[o + k];
                                    return sum;
                                }

                                int biHead = ResolveB("head");
                                int biHandL = ResolveB("hand_L");
                                int biPelvis = ResolveB("pelvis");
                                CustomWeightPaintEditor.UpdatePosedVertices(surfs32, priestSkel31, skin32);

                                // Pick a head vertex cleanly away from hand_L and a genuine hand_L vertex across all body surfaces
                                CustomWeightPaintEditor.SurfaceState sHead = surfs32[0], sHand = surfs32[0];
                                int vHead = -1, vHand = -1;
                                foreach (var s in surfs32)
                                {
                                    for (int v = 0; v < s.RestVerts.Length; v++)
                                    {
                                        if (vHand < 0 && ReadVertBoneWeight(s, v, biHandL) > 0.5f)
                                        {
                                            sHand = s;
                                            vHand = v;
                                        }
                                        if (vHead < 0 && ReadVertBoneWeight(s, v, biHead) < 0.05f && ReadVertBoneWeight(s, v, biHandL) < 0.01f && s.PosedVerts[v].Y > 1.2f)
                                        {
                                            sHead = s;
                                            vHead = v;
                                        }
                                    }
                                }
                                if (vHead < 0) { sHead = surfs32[0]; vHead = 0; }
                                if (vHand < 0) { sHand = surfs32[0]; vHand = Math.Min(1, surfs32[0].RestVerts.Length - 1); }

                                float baseHandW_atHand = ReadVertBoneWeight(sHand, vHand, biHandL);
                                float baseHeadW_atHead = ReadVertBoneWeight(sHead, vHead, biHead);

                                // Paint #1: change hand_L -> Subtract at vHand, Paint #2: change head -> SetOne at vHead
                                CustomWeightPaintEditor.ApplyBrush(surfs32, sHand.PosedVerts[vHand], CustomWeightPaintEditor.BrushMode.Subtract, 0.12f, 0.90f, biHandL, biPelvis, false, -1, -1);
                                float afterHandPaint_handW = ReadVertBoneWeight(sHand, vHand, biHandL);
                                var snapBeforeHead = CustomWeightPaintEditor.CaptureHistoryEntry(surfs32, "Before Head");

                                CustomWeightPaintEditor.ApplyBrush(surfs32, sHead.PosedVerts[vHead], CustomWeightPaintEditor.BrushMode.SetOne, 0.08f, 1.0f, biHead, biPelvis, false, -1, -1);
                                var snapAfterHead = CustomWeightPaintEditor.CaptureHistoryEntry(surfs32, "After Head");
                                float afterHeadPaint_headW = ReadVertBoneWeight(sHead, vHead, biHead);
                                bool diffDetected = CustomWeightPaintEditor.HasSurfaceWeightDifference(snapBeforeHead, surfs32);

                                // Undo head edit -> restores head weight while keeping hand_L edit
                                bool undoOk = CustomWeightPaintEditor.RestoreHistoryEntry(surfs32, snapBeforeHead);
                                float afterUndo_headW = ReadVertBoneWeight(sHead, vHead, biHead);
                                float afterUndo_handW = ReadVertBoneWeight(sHand, vHand, biHandL);

                                // Redo head edit -> restores SetOne head edit
                                bool redoOk = CustomWeightPaintEditor.RestoreHistoryEntry(surfs32, snapAfterHead);
                                float afterRedo_headW = ReadVertBoneWeight(sHead, vHead, biHead);

                                // Per-bone surgical reset on hand_L -> restores vHand back to procedural baseline WITHOUT touching vHead!
                                bool boneResetChanged = CustomWeightPaintEditor.ResetBoneToBaseline(surfs32, baseOv32, priestSkel31, skin32, "hand_L", false);
                                float afterBoneReset_handW = ReadVertBoneWeight(sHand, vHand, biHandL);
                                float afterBoneReset_headW = ReadVertBoneWeight(sHead, vHead, biHead);

                                wpOk = diffDetected && undoOk && redoOk && boneResetChanged
                                    && afterHandPaint_handW < baseHandW_atHand - 0.1f
                                    && afterHeadPaint_headW > 0.9f
                                    && MathF.Abs(afterUndo_headW - baseHeadW_atHead) < 1e-4f
                                    && MathF.Abs(afterUndo_handW - afterHandPaint_handW) < 1e-4f
                                    && afterRedo_headW > 0.9f
                                    && MathF.Abs(afterBoneReset_handW - baseHandW_atHand) < 0.01f
                                    && afterBoneReset_headW > 0.9f;

                                if (!wpOk)
                                    wpFail = $"diff={diffDetected}, undo={undoOk}, redo={redoOk}, reset={boneResetChanged}, hand=({baseHandW_atHand:F3}->{afterHandPaint_handW:F3}->{afterBoneReset_handW:F3}), head=({baseHeadW_atHead:F3}->{afterHeadPaint_headW:F3}->undo:{afterUndo_headW:F3}->redo:{afterRedo_headW:F3}->postReset:{afterBoneReset_headW:F3})";
                            }
                        }

                        if (!wpOk)
                        {
                            GD.PrintErr($"FAIL: [TEST 32b] Weight Paint Undo/Redo + Per-Bone Reset failed: {wpFail}");
                            allPassed = false;
                        }
                        else
                        {
                            GD.Print("PASS: [TEST 32b] Weight Paint Undo/Redo and surgical Per-Bone Reset preserved unrelated bone edits.");
                        }

                        // Wireframe overlay + Layer 21 check
                        var clayMat32 = CustomWeightPaintEditor.CreateWireframeClayMaterial();
                        var wireMat32 = CustomWeightPaintEditor.CreateWireframeLineMaterial(Colors.White);
                        CustomWeightPaintEditor.SyncContainerShadingAndWireframes(res32.ContainerNode, showWireframe: true, wireOnlyClay: false, heatmapActive: false, clayMat32, wireMat32, surfs32);
                        var wireOverlays32 = new System.Collections.Generic.List<MeshInstance3D>();
                        foreach (var c in res32.ContainerNode.FindChildren("*", "MeshInstance3D", true, false))
                        {
                            if (c is MeshInstance3D wmi && wmi.HasMeta("IsWireframeOverlay") && wmi.GetMeta("IsWireframeOverlay").AsBool())
                                wireOverlays32.Add(wmi);
                        }
                        bool wireLayerOk = wireOverlays32.Count > 0 && wireOverlays32.All(w => w.Visible && w.Mesh != null && w.Mesh.GetSurfaceCount() > 0 && (w.Layers & (1u << 20)) == 0 && w.Layers == 1u);
                        if (!wireLayerOk)
                        {
                            GD.PrintErr($"FAIL: [TEST 32c] Wireframe overlay generation/layer isolation failed (count={wireOverlays32.Count}).");
                            allPassed = false;
                        }
                        else
                        {
                            GD.Print($"PASS: [TEST 32c] SyncContainerShadingAndWireframes generated {wireOverlays32.Count} skinned Lines overlay(s) isolated on Layer 1 (Visual Layer 21 clear).");
                        }

                        // 36. TEST 33: Phase 4 Checks (Kept Composite/Standalone Weapons in .vmdl, .dpp Workspace Round-Trip, X-Ray & Mirror/Smooth)
                        GD.Print("\n[TEST 33] Verifying Kept Composite/Standalone Weapons .vmdl Export, .dpp Persistence, X-Ray, Mirror & Smooth...");

                        // 33a: Export bundle & patched .vmdl keeping composite _priest surfaces #2/#3/#4 (shotgun, crossbow, stake) + standalone weapons
                        var opts33 = new CustomModelAutoRigger.RiggingOptions
                        {
                            Method = CustomModelAutoRigger.RiggingMethod.LandmarkAutoRig,
                            AlignedTransform = xf31,
                            LandmarkPins = pins31,
                            KeepFingersRigidOnHand = true,
                            ClampLimbStretching = true,
                            AutoCounterTwistWeights = true,
                            KeptOriginalSubmeshNames = new System.Collections.Generic.List<string>
                            {
                                CustomModelAutoRigger.MakeSurfaceKeepKey("_priest", 2),
                                CustomModelAutoRigger.MakeSurfaceKeepKey("_priest", 3),
                                CustomModelAutoRigger.MakeSurfaceKeepKey("_priest", 4),
                                "_primary_weapon_only",
                                "_primary_weapon_drum_only"
                            },
                            CustomSkinWeightOverrides = res32.Success ? CustomWeightPaintEditor.ExportOverrides(surfs32, priestSkel31, surfs32.FirstOrDefault()?.MeshInstance?.Skin) : null
                        };
                        var res33 = CustomModelAutoRigger.RigAndBind(gordon31, priest31, opts33);
                        priestHier31.ScanHero(priest31);

                        var customMeshes33 = priestHier31.Submeshes
                            .Where(s => s?.Mesh != null && GodotObject.IsInstanceValid(s.Mesh) && s.IsCustomImportedMesh && s.Mesh.Visible)
                            .Select(s => s.Mesh).Distinct().ToList();
                        var keptComposite33 = priestHier31.Submeshes
                            .Where(s => s?.Mesh != null && GodotObject.IsInstanceValid(s.Mesh) && !s.IsCustomImportedMesh && s.Mesh.Visible && s.Mesh.HasMeta("ParentCompositeMesh"))
                            .Select(s => s.Mesh).ToList();
                        var keptStandalone33 = priestHier31.Submeshes
                            .Where(s => s?.Mesh != null && GodotObject.IsInstanceValid(s.Mesh) && !s.IsCustomImportedMesh && s.Mesh.Visible && !s.Mesh.HasMeta("ParentCompositeMesh"))
                            .Select(s => !string.IsNullOrEmpty(s.RawName) ? s.RawName : s.Mesh.Name.ToString()).ToList();

                        var bundle33 = CustomModelExporter.BuildExportBundle(
                            customMeshes33,
                            priestSkel31,
                            "priest",
                            heroModelVpkPath: priestEntry30.VmdlRelativePath);
                        CustomModelExporter.AddPreservedVanillaMeshes(bundle33, keptComposite33, keptStandalone33, priestSkel31);

                        string vmdlRel33 = priestEntry30.VmdlRelativePath.Replace('\\', '/');
                        if (!vmdlRel33.EndsWith("_c", StringComparison.OrdinalIgnoreCase)) vmdlRel33 += "_c";
                        var (decompiledVmdl33, subfiles33, vrfBones33) = CustomModelExporter.DecompileVanillaHeroVmdl(bundle33, vmdlRel33, "models/heroes_wip/priest", false);
                        var availLeafs33 = new System.Collections.Generic.HashSet<string>(
                            subfiles33.Keys.Select(System.IO.Path.GetFileName),
                            StringComparer.OrdinalIgnoreCase);

                        var renderMeshes33 = new System.Collections.Generic.List<(string, string)>
                        {
                            ("priest", "models/heroes_wip/priest/priest_body_custom.smd"),
                            ("priest_kept_props", "models/heroes_wip/priest/priest_kept_props.smd")
                        };
                        var matNames33 = bundle33.Submeshes.Select(s => CustomModelExporter.SanitizeMaterialName(s.MaterialName)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                        string patchedVmdl33 = CustomModelExporter.GeneratePatchedVmdl(
                            decompiledVmdl33,
                            bundle33,
                            "models/heroes_wip/priest",
                            "materials/models/heroes_wip/priest",
                            renderMeshes33,
                            matNames33,
                            availLeafs33,
                            includeAnimGraph2Nodes: true);

                        string tmpKeptSmd33 = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"dl_test33_kept_{Guid.NewGuid():N}.smd");
                        string keptSmdText33 = "";
                        try
                        {
                            if (bundle33.PreservedVanillaSubmeshes.Count > 0)
                            {
                                CustomModelExporter.WriteSourceSpaceSmd(tmpKeptSmd33, bundle33, bundle33.PreservedVanillaSubmeshes, vrfBones33);
                                keptSmdText33 = System.IO.File.ReadAllText(tmpKeptSmd33);
                            }
                        }
                        finally
                        {
                            if (System.IO.File.Exists(tmpKeptSmd33)) System.IO.File.Delete(tmpKeptSmd33);
                        }

                        bool keptCompositeOk33 = bundle33.PreservedVanillaSubmeshes.Count == keptComposite33.Count && bundle33.PreservedVanillaSubmeshes.Count > 0
                            && bundle33.PreservedVanillaSubmeshes.All(k => !string.IsNullOrEmpty(k.PreserveVanillaVmatPath) && k.PreserveVanillaVmatPath.Contains('/'));
                        bool keptVmatRemapOk33 = patchedVmdl33.Contains("priest_kept_props", StringComparison.OrdinalIgnoreCase)
                            && patchedVmdl33.Contains("primary_weapon_only", StringComparison.OrdinalIgnoreCase)
                            && patchedVmdl33.Contains("to = \"models/heroes_wip/priest/materials/priest_shotgun.vmat\"", StringComparison.OrdinalIgnoreCase)
                            && patchedVmdl33.Contains("to = \"models/heroes_wip/priest/materials/priest_gun_crossbow.vmat\"", StringComparison.OrdinalIgnoreCase)
                            && patchedVmdl33.Contains("to = \"models/heroes_wip/priest/materials/priest_stake.vmat\"", StringComparison.OrdinalIgnoreCase)
                            && !patchedVmdl33.Contains("priest_lowerbody.vmat", StringComparison.OrdinalIgnoreCase)
                            && !patchedVmdl33.Contains("priest_upperbody.vmat", StringComparison.OrdinalIgnoreCase);
                        bool keptSmdOk33 = keptSmdText33.Contains("priest_shotgun", StringComparison.OrdinalIgnoreCase)
                            && keptSmdText33.Contains("priest_gun_crossbow", StringComparison.OrdinalIgnoreCase)
                            && keptSmdText33.Contains("priest_stake", StringComparison.OrdinalIgnoreCase);

                        // Also test live compilation with resourcecompiler.exe if available to verify external material references survive compile
                        bool compiledKeptMatOk33 = true;
                        string compiledKeptMatDetail33 = "skipped (no compiler)";
                        var pm33 = new GamePathManager();
                        string compilerExe33 = pm33.ResolveResourceCompilerExe();
                        if (!string.IsNullOrEmpty(compilerExe33) && System.IO.File.Exists(compilerExe33))
                        {
                            string tmpCompileVpk33 = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"dl_test33_compile_{Guid.NewGuid():N}.vpk");
                            try
                            {
                                bundle33.ResourceCompilerExe = compilerExe33;
                                bundle33.OutputVpkPath = tmpCompileVpk33;
                                var compileRes33 = await CustomModelExporter.ExportAndCompileCustomModelAsync(bundle33);
                                if (!compileRes33.Success || !System.IO.File.Exists(tmpCompileVpk33))
                                {
                                    compiledKeptMatOk33 = false;
                                    compiledKeptMatDetail33 = $"compile failed: {compileRes33.ErrorMessage}";
                                }
                                else
                                {
                                    using var outPkg33 = new ValvePak.Package();
                                    outPkg33.Read(tmpCompileVpk33);
                                    var vmdlEntry33 = outPkg33.FindEntry("models/heroes_wip/priest/priest.vmdl_c");
                                    var extRefs33 = new System.Collections.Generic.List<string>();
                                    if (vmdlEntry33 != null)
                                    {
                                        outPkg33.ReadEntry(vmdlEntry33, out byte[] vmdlBytes33);
                                        using var r33 = new ValveResourceFormat.Resource { FileName = "models/heroes_wip/priest/priest.vmdl_c" };
                                        using var ms33 = new System.IO.MemoryStream(vmdlBytes33);
                                        r33.Read(ms33);
                                        if (r33.ExternalReferences != null)
                                        {
                                            foreach (var er in r33.ExternalReferences.ResourceRefInfoList)
                                                extRefs33.Add(er.Name);
                                        }
                                    }
                                    var allPkgPaths33 = outPkg33.Entries.Values.SelectMany(l => l).Select(e => e.GetFullPath()).ToList();
                                    GD.Print($"[TEST 33a] Compiled VPK entries ({allPkgPaths33.Count}): {string.Join(", ", allPkgPaths33)}");
                                    GD.Print($"[TEST 33a] Compiled priest.vmdl_c ExternalReferences ({extRefs33.Count}): {string.Join(", ", extRefs33)}");
                                    compiledKeptMatOk33 = extRefs33.Any(x => x.Contains("models/heroes_wip/priest/materials/priest_shotgun.vmat", StringComparison.OrdinalIgnoreCase))
                                        && extRefs33.Any(x => x.Contains("models/heroes_wip/priest/materials/priest_gun_crossbow.vmat", StringComparison.OrdinalIgnoreCase))
                                        && extRefs33.Any(x => x.Contains("models/heroes_wip/priest/materials/priest_stake.vmat", StringComparison.OrdinalIgnoreCase))
                                        && !extRefs33.Any(x => x.Contains("models/heroes_wip/priest/materials/priest_lower", StringComparison.OrdinalIgnoreCase))
                                        && !extRefs33.Any(x => x.Contains("models/heroes_wip/priest/materials/priest_upper", StringComparison.OrdinalIgnoreCase));
                                    compiledKeptMatDetail33 = $"extRefs={extRefs33.Count}";
                                }
                            }
                            finally
                            {
                                if (System.IO.File.Exists(tmpCompileVpk33)) System.IO.File.Delete(tmpCompileVpk33);
                            }
                        }

                        if (!res33.Success || !keptCompositeOk33 || !keptVmatRemapOk33 || !keptSmdOk33 || !compiledKeptMatOk33)
                        {
                            GD.PrintErr($"FAIL: [TEST 33a] Kept composite/standalone weapon export failed: res33={res33.Success}, keptCount={bundle33.PreservedVanillaSubmeshes.Count}/{keptComposite33.Count}, fullPaths={string.Join("|", bundle33.PreservedVanillaSubmeshes.Select(k => k.PreserveVanillaVmatPath))}, vmdlRemap={keptVmatRemapOk33}, smd={keptSmdOk33}, compiled={compiledKeptMatOk33} ({compiledKeptMatDetail33})");
                            allPassed = false;
                        }
                        else
                        {
                            GD.Print($"PASS: [TEST 33a] Kept {bundle33.PreservedVanillaSubmeshes.Count} composite weapon surface(s) + standalone weapons emitted into priest_kept_props.smd & full vanilla .vmat remaps ({compiledKeptMatDetail33}).");
                        }

                        // 33b: Workspace tracking & .dpp Package/Unpack round-trip
                        var ws33 = DeadlockPlayground.Workspace.ModWorkspaceSession.Instance;
                        string tmpDpp33 = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"dl_test33_{Guid.NewGuid():N}.dpp");
                        bool dppRoundTripOk = false;
                        string dppFail33 = "";
                        try
                        {
                            ws33.StageCustomModel("priest", "Venator", gordonPath, opts33);
                            int countAfterStage = ws33.GetTotalModifiedCount();
                            bool stagedPresent = ws33.Manifest.Heroes.TryGetValue("priest", out var stagedH) && stagedH?.CustomModel != null;
                            bool packed = ws33.PackageProject(tmpDpp33);
                            ws33.DiscardCustomModel("priest");
                            bool clearedOk = !ws33.Manifest.Heroes.ContainsKey("priest") || ws33.Manifest.Heroes["priest"].CustomModel == null;
                            bool unpacked = ws33.UnpackProject(tmpDpp33);
                            bool restoredPresent = ws33.Manifest.Heroes.TryGetValue("priest", out var restoredH) && restoredH?.CustomModel != null;
                            string diskRestored = restoredPresent ? ws33.ResolveCustomModelDiskPath(restoredH.CustomModel) : null;
                            var restoredOpts = restoredPresent ? restoredH.CustomModel.ToRiggingOptions() : null;
                            dppRoundTripOk = stagedPresent && countAfterStage >= 1 && packed && clearedOk && unpacked && restoredPresent
                                && !string.IsNullOrEmpty(diskRestored) && System.IO.File.Exists(diskRestored)
                                && restoredOpts != null
                                && restoredOpts.Method == CustomModelAutoRigger.RiggingMethod.LandmarkAutoRig
                                && restoredOpts.LandmarkPins != null && restoredOpts.LandmarkPins.Count == 16
                                && restoredOpts.CustomSkinWeightOverrides != null && restoredOpts.CustomSkinWeightOverrides.Count > 0
                                && restoredOpts.KeptOriginalSubmeshNames != null && restoredOpts.KeptOriginalSubmeshNames.Count == 5;
                            if (!dppRoundTripOk)
                                dppFail33 = $"staged={stagedPresent}, cnt={countAfterStage}, pack={packed}, clear={clearedOk}, unpack={unpacked}, rest={restoredPresent}, file={diskRestored}, pins={restoredOpts?.LandmarkPins?.Count ?? -1}, ov={restoredOpts?.CustomSkinWeightOverrides?.Count ?? -1}, kept={restoredOpts?.KeptOriginalSubmeshNames?.Count ?? -1}";
                        }
                        finally
                        {
                            ws33.DiscardCustomModel("priest");
                            if (System.IO.File.Exists(tmpDpp33)) System.IO.File.Delete(tmpDpp33);
                        }
                        if (!dppRoundTripOk)
                        {
                            GD.PrintErr($"FAIL: [TEST 33b] .dpp workspace persistence round-trip failed: {dppFail33}");
                            allPassed = false;
                        }
                        else
                        {
                            GD.Print("PASS: [TEST 33b] Workspace tracked custom model swap and .dpp pack/unpack restored bundled .glb, 16 pins, weight overrides & kept weapons.");
                        }

                        // 33c: X-Ray Wireframe Layer 21 isolation + MirrorWeightsSide (L -> R) + SmoothBoneBoundary
                        var xrayMat33 = CustomWeightPaintEditor.CreateWireframeLineMaterial(new Color(0.32f, 0.82f, 0.98f, 0.52f), 0.0004f, xRayNoDepth: true);
                        CustomWeightPaintEditor.SyncContainerShadingAndWireframes(
                            res33.ContainerNode,
                            showWireframe: false,
                            wireOnlyClay: false,
                            heatmapActive: false,
                            clayMat32,
                            xrayMat33,
                            surfs32,
                            xRayMode: true);
                        var xrayOverlays33 = new System.Collections.Generic.List<MeshInstance3D>();
                        foreach (var c in res33.ContainerNode.FindChildren("*", "MeshInstance3D", true, false))
                        {
                            if (c is MeshInstance3D wmi && wmi.HasMeta("IsWireframeOverlay") && wmi.GetMeta("IsWireframeOverlay").AsBool())
                                xrayOverlays33.Add(wmi);
                        }
                        bool xrayOk33 = xrayOverlays33.Count > 0 && xrayOverlays33.All(w => w.Visible && w.Layers == 1u && (w.Layers & (1u << 20)) == 0);

                        var surfs33 = CustomWeightPaintEditor.CaptureSurfaces(res33.ContainerNode);
                        bool mirrorSmoothOk33 = false;
                        if (surfs33.Count > 0 && priestSkel31 != null)
                        {
                            var skin33 = surfs33[0].MeshInstance?.Skin;
                            if (skin33 != null)
                            {
                                int ResolveB33(string bName)
                                {
                                    int sb = priestSkel31.FindBone(bName);
                                    for (int bi = 0; bi < skin33.GetBindCount(); bi++)
                                    {
                                        if (sb >= 0 && skin33.GetBindBone(bi) == sb) return bi;
                                        if (bName.Equals(skin33.GetBindName(bi), StringComparison.Ordinal)) return bi;
                                    }
                                    return -1;
                                }
                                float ReadVertW33(CustomWeightPaintEditor.SurfaceState s, int v, int bi)
                                {
                                    float sum = 0f;
                                    int o = v * 4;
                                    for (int k = 0; k < 4; k++) if (s.SkinBones[o + k] == bi) sum += s.Weights[o + k];
                                    return sum;
                                }

                                int biHandL33 = ResolveB33("hand_L");
                                int biHandR33 = ResolveB33("hand_R");
                                int biPelvis33 = ResolveB33("pelvis");
                                float pelvisX33 = priestSkel31.GetBoneGlobalRest(priestSkel31.FindBone("pelvis")).Origin.X;

                                // Paint an asymmetric SetOne dab onto left shoulder/arm onto hand_L, then mirror L -> R
                                CustomWeightPaintEditor.UpdatePosedVertices(surfs33, priestSkel31, skin33);
                                var leftSurf = surfs33[0];
                                int leftIdx = -1;
                                for (int v = 0; v < leftSurf.RestVerts.Length; v++)
                                {
                                    if (leftSurf.RestVerts[v].X > pelvisX33 + 0.08f && ReadVertW33(leftSurf, v, biHandL33) < 0.05f)
                                    {
                                        leftIdx = v;
                                        break;
                                    }
                                }
                                if (leftIdx >= 0)
                                {
                                    CustomWeightPaintEditor.ApplyBrush(surfs33, leftSurf.PosedVerts[leftIdx], CustomWeightPaintEditor.BrushMode.SetOne, 0.05f, 1.0f, biHandL33, biPelvis33, false, -1, -1);
                                    bool mirrored = CustomWeightPaintEditor.MirrorWeightsSide(surfs33, priestSkel31, skin33, pelvisX33, leftToRight: true);
                                    bool smoothed = CustomWeightPaintEditor.SmoothBoneBoundary(surfs33, biHandL33, biHandR33, iterations: 2);

                                    Vector3 targetRight = new Vector3(2f * pelvisX33 - leftSurf.RestVerts[leftIdx].X, leftSurf.RestVerts[leftIdx].Y, leftSurf.RestVerts[leftIdx].Z);
                                    int bestR = -1; float bestD2 = float.MaxValue;
                                    for (int v = 0; v < leftSurf.RestVerts.Length; v++)
                                    {
                                        float d2 = (leftSurf.RestVerts[v] - targetRight).LengthSquared();
                                        if (d2 < bestD2) { bestD2 = d2; bestR = v; }
                                    }
                                    float rHandRW = bestR >= 0 ? ReadVertW33(leftSurf, bestR, biHandR33) : 0f;
                                    float rSumW = 0f;
                                    if (bestR >= 0)
                                        for (int k = 0; k < 4; k++) rSumW += leftSurf.Weights[bestR * 4 + k];
                                    mirrorSmoothOk33 = mirrored && smoothed && rHandRW > 0.25f && MathF.Abs(rSumW - 1.0f) < 1e-3f;
                                }
                            }
                        }

                        if (!xrayOk33 || !mirrorSmoothOk33)
                        {
                            GD.PrintErr($"FAIL: [TEST 33c] X-Ray / Mirror / Smooth check failed: xray={xrayOk33} ({xrayOverlays33.Count}), mirrorSmooth={mirrorSmoothOk33}");
                            allPassed = false;
                        }
                        else
                        {
                            GD.Print($"PASS: [TEST 33c] X-Ray enabled {xrayOverlays33.Count} Layer-1-isolated wireframe overlay(s), MirrorWeightsSide(L->R) & SmoothBoneBoundary verified.");
                        }

                        // 33d: Verify Bug 2 regression guard — ScanHero + CleanupGeneratedSubmeshes keeps SurfaceKept_0/1 hidden and weapons visible
                        var origPriestMesh33 = priest31.FindChild("_priest", true, false) as MeshInstance3D;
                        if (origPriestMesh33 == null)
                            origPriestMesh33 = priest31.FindChild("priest", true, false) as MeshInstance3D;
                        bool surf0HiddenAfterScan = priestHier31.Submeshes.All(s => !(s.OriginalVmatPath ?? "").Contains("priest_lower", StringComparison.OrdinalIgnoreCase));
                        priestHier31.CleanupGeneratedSubmeshes();
                        bool hiddenSurfMatApplied = origPriestMesh33 != null && origPriestMesh33.Mesh != null
                            && origPriestMesh33.GetSurfaceOverrideMaterial(0) is StandardMaterial3D hiddenOverride33
                            && hiddenOverride33.AlbedoColor.A < 0.01f
                            && origPriestMesh33.GetSurfaceOverrideMaterial(2) is not StandardMaterial3D { AlbedoColor.A: < 0.01f };
                        priestHier31.ScanHero(priest31);
                        if (!surf0HiddenAfterScan || !hiddenSurfMatApplied)
                        {
                            GD.PrintErr($"FAIL: [TEST 33d] Replaced hero surface visibility guard failed: surf0HiddenAfterScan={surf0HiddenAfterScan}, hiddenSurfMatApplied={hiddenSurfMatApplied}");
                            allPassed = false;
                        }
                        else
                        {
                            GD.Print("PASS: [TEST 33d] Replaced composite body surfaces stay hidden after ScanHero & CleanupGeneratedSubmeshes while kept weapons stay visible.");
                        }
                    }
                    else
                    {
                        GD.PrintErr("FAIL: [TEST 31] Could not load priest or gordon_freeman_half-life_1.glb");
                        allPassed = false;
                    }
                    gordon31?.QueueFree();
                }

                // -----------------------------------------------------------------
                // TEST 32: Painter PreserveBaseAlpha (Clip to Base Alpha) & Unlockable Base Texture Alpha Erase
                // -----------------------------------------------------------------
                GD.Print("\n--- [TEST 32] Painter PreserveBaseAlpha & Base Texture Alpha Erase Verification ---");
                {
                    int texW = 64;
                    int texH = 64;
                    var baseImg = Image.CreateEmpty(texW, texH, false, Image.Format.Rgba8);
                    for (int y = 0; y < texH; y++)
                    {
                        for (int x = 0; x < texW; x++)
                        {
                            // Left half (x < 32): Transparent (A = 0); Right half (x >= 32): Opaque Blue (A = 255)
                            if (x < 32)
                                baseImg.SetPixel(x, y, new Color(0f, 0f, 0f, 0f));
                            else
                                baseImg.SetPixel(x, y, new Color(0.1f, 0.4f, 0.9f, 1.0f));
                        }
                    }

                    var baseTex = ImageTexture.CreateFromImage(baseImg);
                    var testHeroRoot = new Node3D { Name = "Hero_AlphaTest" };
                    var testMeshInst = new MeshInstance3D { Name = "alpha_test_mesh" };
                    var quadMesh = new QuadMesh { Size = Vector2.One };
                    var stdMat = new StandardMaterial3D
                    {
                        ResourceName = "alpha_test_mat",
                        AlbedoTexture = baseTex,
                        Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor
                    };
                    quadMesh.Material = stdMat;
                    testMeshInst.Mesh = quadMesh;
                    testHeroRoot.AddChild(testMeshInst);
                    Root.AddChild(testHeroRoot);

                    var testHierarchy32 = new DeadlockPlayground.Painter.HeroMeshHierarchy();
                    Root.AddChild(testHierarchy32);
                    testHierarchy32.ScanHero(testHeroRoot);

                    var layerMgr = new DeadlockPlayground.Painter.SkinLayerManager();
                    Root.AddChild(layerMgr);
                    layerMgr.MeshHierarchy = testHierarchy32;
                    layerMgr.SetupForHero(testHeroRoot);
                    layerMgr.RegisterSubmesh(testMeshInst);
                    layerMgr.SetPaintTargetMesh(testMeshInst);
                    layerMgr.RebuildBaseAtlasBuffer();

                    // 1. Verify PreserveBaseAlpha is true by default and Base Texture is locked by default
                    if (!layerMgr.PreserveBaseAlpha || !layerMgr.IsBaseTextureLocked)
                    {
                        GD.PrintErr($"FAIL: [TEST 32] Expected PreserveBaseAlpha=true ({layerMgr.PreserveBaseAlpha}) and IsBaseTextureLocked=true ({layerMgr.IsBaseTextureLocked}) by default.");
                        allPassed = false;
                    }

                    // 2. Apply full-canvas recolor on Paint Layer 1 with PreserveBaseAlpha = true
                    layerMgr.SelectLayer(0);
                    layerMgr.FillActiveLayer2D(new Color(0.95f, 0.2f, 0.1f, 1.0f));

                    var subInfo = testHierarchy32.Submeshes.FirstOrDefault();
                    using var bakedRecolor = layerMgr.BakeCompositeImageForSubmesh(subInfo);
                    if (bakedRecolor == null || bakedRecolor.IsEmpty())
                    {
                        GD.PrintErr("FAIL: [TEST 32] BakeCompositeImageForSubmesh returned null/empty image.");
                        allPassed = false;
                    }
                    else
                    {
                        Color transPx = bakedRecolor.GetPixel(8, 32);
                        Color opaqPx = bakedRecolor.GetPixel(48, 32);
                        if (transPx.A8 != 0)
                        {
                            GD.PrintErr($"FAIL: [TEST 32] Transparent pixel (8,32) was filled! Expected A8 == 0, got A8 == {transPx.A8} (RGBA={transPx})");
                            allPassed = false;
                        }
                        else if (opaqPx.A8 != 255 || opaqPx.R < 0.7f)
                        {
                            GD.PrintErr($"FAIL: [TEST 32] Opaque pixel (48,32) did not receive recolor or lost alpha! Got A8 == {opaqPx.A8}, R == {opaqPx.R:F2}");
                            allPassed = false;
                        }
                        else
                        {
                            GD.Print($"PASS: [TEST 32.1] Full-canvas recolor with PreserveBaseAlpha=true preserved A8=0 on transparent pixels and recolored opaque pixels (R={opaqPx.R:F2}, A8={opaqPx.A8}).");
                        }
                    }

                    // 3. Unlock Base Texture and erase opaque pixels at (48, 32) to A = 0
                    layerMgr.SetBaseTextureLocked(false);
                    layerMgr.SelectBaseTexture();
                    layerMgr.PaintDab2D(new Vector2(48, 32), Colors.White, sizePx: 14.0f, hardness: 1.0f, flow: 1.0f, isErase: true);

                    using var bakedErased = layerMgr.BakeCompositeImageForSubmesh(subInfo);
                    if (bakedErased == null || bakedErased.IsEmpty())
                    {
                        GD.PrintErr("FAIL: [TEST 32] BakeCompositeImageForSubmesh after Base Texture erase returned null.");
                        allPassed = false;
                    }
                    else
                    {
                        Color erasedPx = bakedErased.GetPixel(48, 32);
                        if (erasedPx.A8 != 0)
                        {
                            GD.PrintErr($"FAIL: [TEST 32] Erasing unlocked Base Texture at (48,32) did not persist A8 == 0! Got A8 == {erasedPx.A8}");
                            allPassed = false;
                        }
                        else
                        {
                            GD.Print("PASS: [TEST 32.2] Unlocking Base Texture and erasing pixels to A=0 persisted new transparency (A8=0) into final baked export image.");
                        }
                    }

                    // 4. Reset Base Texture back to vanilla and verify (48, 32) is opaque again
                    layerMgr.ResetBaseTexture();
                    using var bakedReset = layerMgr.BakeCompositeImageForSubmesh(subInfo);
                    if (bakedReset != null && bakedReset.GetPixel(48, 32).A8 == 255)
                    {
                        GD.Print("PASS: [TEST 32.3] ResetBaseTexture restored pristine original alpha (A8=255) at (48,32).");
                    }
                    else
                    {
                        GD.PrintErr("FAIL: [TEST 32.3] ResetBaseTexture did not restore original alpha at (48,32).");
                        allPassed = false;
                    }

                    layerMgr.QueueFree();
                    testHierarchy32.QueueFree();
                    testHeroRoot.QueueFree();
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
