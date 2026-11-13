using System;
using System.IO;
using System.Linq;
using System.Numerics;
using Godot;
using SteamDatabase.ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.IO;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.ResourceTypes.ModelAnimation;
using ValveResourceFormat.ResourceTypes.ModelAnimation2;
using DeadlockPlayground.Animation;
using DeadlockPlayground.Catalog;
using DeadlockPlayground.Tools;

namespace DeadlockPlayground.Tests
{
    public partial class ShivAnimRoundTripTest : SceneTree
    {
        public override async void _Initialize()
        {
            GD.Print("=================================================");
            GD.Print("   SHIV RELOAD_IDLE ROUND-TRIP & REPAIR TEST     ");
            GD.Print("=================================================");

            try
            {
                string vpkPath = DeadlockPlayground.Particles.HeroParticleService.ResolveVpkPath();
                if (string.IsNullOrEmpty(vpkPath) || !File.Exists(vpkPath))
                {
                    GD.PrintErr("[Test] Deadlock pak01_dir.vpk not found!");
                    Quit(1);
                    return;
                }

                var vpkLoader = new VpkLoaderTest { VpkPath = vpkPath };
                Root.AddChild(vpkLoader);

                var shivEntry = DeadlockHeroCatalog.GetByCodename("shiv");
                if (shivEntry == null)
                {
                    GD.PrintErr("[Test] Shiv entry not found in catalog!");
                    Quit(1);
                    return;
                }

                Node3D heroNode = await vpkLoader.LoadHeroModelAsync(shivEntry);
                if (heroNode == null)
                {
                    GD.PrintErr("[Test] Failed to load Shiv!");
                    Quit(1);
                    return;
                }

                var skeleton = heroNode.FindChildren("*", "Skeleton3D", true, false).OfType<Skeleton3D>().FirstOrDefault();
                var animPlayer = heroNode.FindChildren("*", "AnimationPlayer", true, false).OfType<AnimationPlayer>().FirstOrDefault();
                if (skeleton == null || animPlayer == null)
                {
                    GD.PrintErr("[Test] Skeleton3D or AnimationPlayer not found!");
                    Quit(1);
                    return;
                }

                var reloadIdle = vpkLoader.CurrentHeroAnimations.FirstOrDefault(a =>
                    a.CleanName.Equals("reload_idle", StringComparison.OrdinalIgnoreCase));
                if (reloadIdle == null)
                {
                    GD.PrintErr("[Test] reload_idle not found in CurrentHeroAnimations!");
                    Quit(1);
                    return;
                }

                using var vanPkg = new Package();
                vanPkg.Read(vpkPath);
                var fileLoader = new GameFileLoader(vanPkg, "models/heroes_staging/shiv");
                var nmSkel = Skeleton.FromSkeletonResource(fileLoader, "models/heroes_staging/shiv/shiv.vnmskel");

                var shivMdlEntry = vanPkg.FindEntry("models/heroes_staging/shiv/shiv.vmdl_c");
                vanPkg.ReadEntry(shivMdlEntry, out byte[] shivMdlBytes);
                using var shivMdlRes = new ValveResourceFormat.Resource { FileName = "models/heroes_staging/shiv/shiv.vmdl_c" };
                shivMdlRes.Read(new MemoryStream(shivMdlBytes));
                var vrfModel = (Model)shivMdlRes.DataBlock;

                var vanClipEntry = vanPkg.FindEntry("models/heroes_staging/shiv/clips/reload_idle.vnmclip_c");
                vanPkg.ReadEntry(vanClipEntry, out byte[] vanClipBytes);

                // --- TEST 1: Vanilla reload_idle Round-Trip ---
                GD.Print("\n[TEST 1] Decoding vanilla reload_idle -> Source2ClipPatcher...");
                var decodedVan = DeadlockAnimLoader.DecodeAnimationToGodot(
                    reloadIdle, skeleton, animPlayer, vrfModel, fileLoader, null, "reload_idle_rt", inPlace: false);

                byte[] rtBytes = Source2ClipPatcher.CreateOrPatchClip(
                    vanClipBytes, decodedVan, skeleton, "reload_idle", reloadIdle.Fps,
                    "models/heroes_staging/shiv/clips/reload_idle.vnmclip_c", "shiv", fileLoader);

                if (rtBytes == null)
                {
                    GD.PrintErr("[TEST 1 FAIL] Source2ClipPatcher returned null!");
                    Quit(1);
                    return;
                }

                VerifyClipFrame0("TEST 1 (Vanilla Round-Trip)", rtBytes, vanClipBytes, nmSkel, vrfModel, out bool test1Ok);
                if (!test1Ok)
                {
                    Quit(1);
                    return;
                }

                // --- TEST 2: Repair pak43_dir.vpk -> pak43_fixed_dir.vpk ---
                string oldModPath = Path.GetFullPath(Path.Combine(ProjectSettings.GlobalizePath("res://.."), "scratch_out", "shiv_anim_test", "pak43_dir.vpk"));
                string fixedModPath = Path.GetFullPath(Path.Combine(ProjectSettings.GlobalizePath("res://.."), "scratch_out", "shiv_anim_test", "pak43_fixed_dir.vpk"));

                if (File.Exists(oldModPath))
                {
                    GD.Print($"\n[TEST 2] Repairing '{oldModPath}' -> '{fixedModPath}'...");
                    using var oldPkg = new Package();
                    oldPkg.Read(oldModPath);
                    var oldClipEntry = oldPkg.FindEntry("models/heroes_staging/shiv/clips/reload_idle.vnmclip_c");
                    oldPkg.ReadEntry(oldClipEntry, out byte[] oldClipBytes);

                    using var oldRes = new ValveResourceFormat.Resource { FileName = "models/heroes_staging/shiv/clips/reload_idle.vnmclip_c" };
                    oldRes.Read(new MemoryStream(oldClipBytes));
                    var oldAnim = new ClipAnimation((AnimationClip)oldRes.DataBlock);

                    // Decode via DeadlockAnimLoader using a temporary AnimSequenceInfo pointing at the old clip
                    var modInfo = new DeadlockAnimLoader.AnimSequenceInfo
                    {
                        RawName = reloadIdle.RawName,
                        CleanName = reloadIdle.CleanName,
                        DisplayName = reloadIdle.DisplayName,
                        Category = reloadIdle.Category,
                        Duration = reloadIdle.Duration,
                        Fps = oldAnim.Fps > 0 ? oldAnim.Fps : 30f,
                        FrameCount = oldAnim.FrameCount,
                        IsLooping = reloadIdle.IsLooping,
                        IsAdditive = oldAnim.IsAdditive,
                        VpkClipPath = "models/heroes_staging/shiv/clips/reload_idle.vnmclip_c",
                        VrfAnimation = oldAnim
                    };

                    var decodedMod = DeadlockAnimLoader.DecodeAnimationToGodot(
                        modInfo, skeleton, animPlayer, vrfModel, fileLoader, null, "reload_idle_mod", inPlace: false);

                    // Undo the +90 deg Z rotation that SkeletonRetargeter inherited from pak43_dir.vpk's broken root_motion
                    var undo90Z = new System.Numerics.Quaternion(0f, 0f, -0.70710677f, 0.70710677f);
                    var srcToGltf = new System.Numerics.Quaternion(-0.5f, -0.5f, -0.5f, 0.5f);
                    var gltfToSrc = new System.Numerics.Quaternion(0.5f, 0.5f, 0.5f, 0.5f);

                    for (int t = decodedMod.GetTrackCount() - 1; t >= 0; t--)
                    {
                        string path = decodedMod.TrackGetPath(t).ToString();
                        string bone = path.Contains(':') ? path.Split(':').Last() : "";

                        // Remove IK helper tracks so ApplyAutomatedIkConstraints recomputes them cleanly in Source 2 space
                        if (bone.Equals("weaponHand_L", StringComparison.OrdinalIgnoreCase) ||
                            bone.Equals("weaponHand_R", StringComparison.OrdinalIgnoreCase) ||
                            bone.Equals("arm_L_IKTARGET", StringComparison.OrdinalIgnoreCase) ||
                            bone.Equals("arm_R_IKTARGET", StringComparison.OrdinalIgnoreCase) ||
                            bone.Equals("leg_L_IKTARGET", StringComparison.OrdinalIgnoreCase) ||
                            bone.Equals("leg_R_IKTARGET", StringComparison.OrdinalIgnoreCase))
                        {
                            decodedMod.RemoveTrack(t);
                            continue;
                        }

                        if (bone.Equals("root_motion", StringComparison.OrdinalIgnoreCase))
                        {
                            if (decodedMod.TrackGetType(t) == Godot.Animation.TrackType.Rotation3D)
                            {
                                for (int k = 0; k < decodedMod.TrackGetKeyCount(t); k++)
                                {
                                    decodedMod.TrackSetKeyValue(t, k, new Godot.Quaternion(srcToGltf.X, srcToGltf.Y, srcToGltf.Z, srcToGltf.W));
                                }
                            }
                            else if (decodedMod.TrackGetType(t) == Godot.Animation.TrackType.Position3D)
                            {
                                for (int k = 0; k < decodedMod.TrackGetKeyCount(t); k++)
                                {
                                    decodedMod.TrackSetKeyValue(t, k, Godot.Vector3.Zero);
                                }
                            }
                        }
                        else if (bone.Equals("pelvis", StringComparison.OrdinalIgnoreCase))
                        {
                            if (decodedMod.TrackGetType(t) == Godot.Animation.TrackType.Position3D)
                            {
                                for (int k = 0; k < decodedMod.TrackGetKeyCount(t); k++)
                                {
                                    var gp = decodedMod.TrackGetKeyValue(t, k).AsVector3();
                                    var sp = System.Numerics.Vector3.Transform(new System.Numerics.Vector3(gp.X, gp.Y, gp.Z), gltfToSrc);
                                    sp = System.Numerics.Vector3.Transform(sp, undo90Z);
                                    var fp = System.Numerics.Vector3.Transform(sp, srcToGltf);
                                    decodedMod.TrackSetKeyValue(t, k, new Godot.Vector3(fp.X, fp.Y, fp.Z));
                                }
                            }
                            else if (decodedMod.TrackGetType(t) == Godot.Animation.TrackType.Rotation3D)
                            {
                                for (int k = 0; k < decodedMod.TrackGetKeyCount(t); k++)
                                {
                                    var gr = decodedMod.TrackGetKeyValue(t, k).AsQuaternion();
                                    var sr = System.Numerics.Quaternion.Normalize(gltfToSrc * new System.Numerics.Quaternion(gr.X, gr.Y, gr.Z, gr.W));
                                    sr = System.Numerics.Quaternion.Normalize(undo90Z * sr);
                                    var fr = System.Numerics.Quaternion.Normalize(srcToGltf * sr);
                                    decodedMod.TrackSetKeyValue(t, k, new Godot.Quaternion(fr.X, fr.Y, fr.Z, fr.W));
                                }
                            }
                        }
                    }

                    byte[] fixedClipBytes = Source2ClipPatcher.CreateOrPatchClip(
                        vanClipBytes, decodedMod, skeleton, "reload_idle", modInfo.Fps,
                        "models/heroes_staging/shiv/clips/reload_idle.vnmclip_c", "shiv", fileLoader);

                    VerifyClipFrame0("TEST 2 (Repaired pak43_fixed_dir.vpk)", fixedClipBytes, vanClipBytes, nmSkel, vrfModel, out bool test2Ok);
                    if (!test2Ok)
                    {
                        Quit(1);
                        return;
                    }

                    using var outPkg = new Package();
                    outPkg.AddFile("models/heroes_staging/shiv/clips/reload_idle.vnmclip_c", fixedClipBytes);
                    outPkg.Write(fixedModPath);
                    GD.Print($"[TEST 2 PASS] Wrote repaired VPK to '{fixedModPath}' ({fixedClipBytes.Length} bytes).");
                }
                else
                {
                    GD.PrintErr($"[TEST 2] '{oldModPath}' not found, skipping repair.");
                }

                GD.Print("\nALL SHIV ANIMATION VERIFICATION TESTS PASSED!");
                Quit(0);
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[Test Crash]: {ex.Message}\n{ex.StackTrace}");
                Quit(1);
            }
        }

        private static void VerifyClipFrame0(string label, byte[] testBytes, byte[] vanBytes, Skeleton nmSkel, Model vrfModel, out bool ok)
        {
            ok = true;
            using var testRes = new ValveResourceFormat.Resource { FileName = "reload_idle.vnmclip_c" };
            testRes.Read(new MemoryStream(testBytes));
            using var vanRes = new ValveResourceFormat.Resource { FileName = "reload_idle.vnmclip_c" };
            vanRes.Read(new MemoryStream(vanBytes));

            var testClip = (AnimationClip)testRes.DataBlock;
            var testAnim = new ClipAnimation(testClip);
            var vanAnim = new ClipAnimation((AnimationClip)vanRes.DataBlock);

            var trackSettings = testClip.Data.Root["m_trackCompressionSettings"] as ValveKeyValue.KVObject;
            int prevOff = -1;
            bool hasTrackReadOffsets = trackSettings != null;
            if (trackSettings != null)
            {
                for (int i = 0; i < testClip.TrackCompressionSettings.Length; i++)
                {
                    var offNode = trackSettings[i]?["m_nTrackReadOffset"];
                    if (offNode == null) { hasTrackReadOffsets = false; break; }
                    int off = (int)offNode;
                    if (off < prevOff) { hasTrackReadOffsets = false; break; }
                    prevOff = off;
                }
            }
            GD.Print($"[{label}] m_nTrackReadOffset valid={hasTrackReadOffsets}, maxOffset={prevOff}");

            var tf = new Frame(nmSkel, vrfModel.FlexControllers); tf.Clear(nmSkel); tf.FrameIndex = 0; testAnim.DecodeFrame(tf);
            var vf = new Frame(nmSkel, vrfModel.FlexControllers); vf.Clear(nmSkel); vf.FrameIndex = 0; vanAnim.DecodeFrame(vf);

            int rmIdx = Array.FindIndex(nmSkel.Bones, b => b.Name == "root_motion");
            int pelIdx = Array.FindIndex(nmSkel.Bones, b => b.Name == "pelvis");

            var rmRot = System.Numerics.Quaternion.Normalize(tf.Bones[rmIdx].Angle);
            float rmDot = MathF.Abs(System.Numerics.Quaternion.Dot(rmRot, System.Numerics.Quaternion.Identity));

            var pelPos = tf.Bones[pelIdx].Position;
            var vanPelPos = vf.Bones[pelIdx].Position;
            float pelPosErr = System.Numerics.Vector3.Distance(pelPos, vanPelPos);

            var pelRot = System.Numerics.Quaternion.Normalize(tf.Bones[pelIdx].Angle);
            var vanPelRot = System.Numerics.Quaternion.Normalize(vf.Bones[pelIdx].Angle);
            float pelRotDot = MathF.Abs(System.Numerics.Quaternion.Dot(pelRot, vanPelRot));

            GD.Print($"[{label}] root_motion rot={rmRot} (dot={rmDot:F5})");
            GD.Print($"[{label}] pelvis pos={pelPos} vs van={vanPelPos} (err={pelPosErr:F4} in)");
            GD.Print($"[{label}] pelvis rot={pelRot} vs van={vanPelRot} (dot={pelRotDot:F5})");

            if (!hasTrackReadOffsets || rmDot < 0.999f || pelPosErr > 0.05f || pelRotDot < 0.999f)
            {
                GD.PrintErr($"[{label} FAIL] Assertion failed!");
                ok = false;
            }
            else
            {
                GD.Print($"[{label} PASS]");
            }
        }
    }
}
