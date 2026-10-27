using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Godot;
using SteamDatabase.ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.IO;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.ResourceTypes.ModelAnimation;
using DeadlockPlayground.Tools;

namespace DeadlockPlayground.Animation
{
    /// <summary>
    /// Headless harness that validates lazily decoded hero clips (DeadlockAnimLoader.DecodeAnimationToGodot)
    /// against (A) raw Source 2 forward kinematics and (B) the glTF-baked clips exported by VRF (oracle).
    /// Run:
    ///   Godot_..._console.exe --headless --path src --script res://tools/scripts/animation/AnimDecodeDiagnostics.cs -- --hero=haze
    /// Writes scratch/anim_diag/{hero}_report.md and exits with code 1 if any expected-upright clip fails.
    /// </summary>
    public partial class AnimDecodeDiagnostics : SceneTree
    {
        private readonly StringBuilder _report = new();
        private int _fails;
        private static readonly System.Numerics.Quaternion SourceToGltf = new(-0.5f, -0.5f, -0.5f, 0.5f);
        private string _hero = "haze";

        private void Log(string s) { GD.Print(s); _report.AppendLine(s); }

        public override void _Initialize()
        {
            foreach (var a in OS.GetCmdlineUserArgs())
                if (a.StartsWith("--hero=")) _hero = a.Substring(7);
            try { Run(); }
            catch (Exception ex) { Log($"ERROR: {ex.Message}\n{ex.StackTrace}"); _fails++; }
            try
            {
                string dir = Path.GetFullPath(Path.Combine(ProjectSettings.GlobalizePath("res://"), "..", "scratch", "anim_diag"));
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, $"{_hero}_report.md"), _report.ToString());
                GD.Print($"[AnimDiag] report written to {dir}");
            }
            catch (Exception ex) { GD.PrintErr(ex.Message); }
            Quit(_fails > 0 ? 1 : 0);
        }

        private void Run()
        {
            Log($"# Anim decode diagnostics - hero '{_hero}'\n");
            var pm = new GamePathManager();
            string gamePath = pm.AutoDetectDeadlockPath();
            string vpk = Path.Combine(gamePath, "game", "citadel", "pak01_dir.vpk");
            if (!File.Exists(vpk)) throw new Exception($"pak01_dir.vpk not found at {vpk}");
            var pkg = new Package();
            pkg.Read(vpk);

            var catEntry = DeadlockPlayground.Catalog.DeadlockHeroCatalog.ResolveHero(_hero)
                        ?? DeadlockPlayground.Catalog.DeadlockHeroCatalog.GetByCodename(_hero);
            PackageEntry entry = null;
            if (catEntry != null)
            {
                entry = pkg.FindEntry(catEntry.VmdlRelativePath);
            }
            if (entry == null)
            {
                foreach (var root in new[] { "heroes_staging", "heroes_wip", "heroes" })
                    entry ??= pkg.FindEntry($"models/{root}/{_hero}/{_hero}.vmdl_c");
            }
            if (entry == null && pkg.Entries.TryGetValue("vmdl_c", out var list))
                entry = list.FirstOrDefault(e => $"{e.DirectoryName}/{e.FileName}".EndsWith($"/{_hero}/{_hero}", StringComparison.OrdinalIgnoreCase) ||
                                                 $"{e.DirectoryName}/{e.FileName}".Contains($"/{_hero}", StringComparison.OrdinalIgnoreCase));
            if (entry == null) throw new Exception("hero vmdl not found");
            Log($"Model: {entry.DirectoryName}/{entry.FileName}.{entry.TypeName}");

            pkg.ReadEntry(entry, out byte[] data);
            using var resource = new ValveResourceFormat.Resource();
            resource.Read(new MemoryStream(data));
            var model = (Model)resource.DataBlock;
            var loader = new GameFileLoader(pkg, entry.DirectoryName);
            var vrfSkel = model.Skeleton;
            var infos = DeadlockAnimLoader.IndexAnimations(model, loader);
            Log($"Indexed {infos.Count} clips, VRF skeleton bones: {vrfSkel.Bones.Length}\n");

            // ---- clip selection
            var picks = new List<DeadlockAnimLoader.AnimSequenceInfo>();
            string[] pats = { "stand_idle", "run_n", "run_e", "run_w", "sprint_n", "crouch_run_n", "crouch_idle", "reload", "melee", "jump", "zipline", "taunt", "flinch", "slide_aim", "fire" };
            foreach (var p in pats)
                foreach (var i in infos.Where(i => i.CleanName.ToLowerInvariant().Contains(p) && !i.IsAdditive).Take(2))
                    if (!picks.Contains(i)) picks.Add(i);
            picks.AddRange(infos.Where(i => i.IsAdditive && !picks.Contains(i)).Take(3));
            var idleInfo = infos.FirstOrDefault(i => i.CleanName.ToLowerInvariant().Contains("stand_idle") && !i.IsAdditive);
            Log($"Selected {picks.Count} clips for the test.\n");

            // ---- export GLB with the picked clips baked (oracle)
            var exporter = new GltfModelExporter(loader)
            {
                ExportAnimations = true, ComposeAdditiveAnimations = true, AdaptTextures = false,
                ExportMaterials = false, ExportExtras = false, ProgressReporter = new Progress<string>()
            };
            foreach (var a in picks)
            {
                string raw = a.RawName; string clean = raw.Replace('\\', '/'); string noExt = Path.ChangeExtension(clean, null);
                foreach (var v in new[] { raw, clean, "@" + clean, noExt, Path.GetFileName(clean), Path.GetFileName(noExt) })
                    exporter.AnimationFilter.Add(v);
            }
            string tmp = Path.Combine(Path.GetTempPath(), $"diag_{_hero}_{Guid.NewGuid():N}.glb");
            exporter.Export(resource, tmp);
            byte[] glb = File.ReadAllBytes(tmp);
            File.Delete(tmp);

            var doc = new GltfDocument(); var state = new GltfState();
            if (doc.AppendFromBuffer(glb, "", state) != Error.Ok) throw new Exception("GLB parse failed");
            var scene = (Node3D)doc.GenerateScene(state);
            Root.AddChild(scene);
            var skel = FindNode<Skeleton3D>(scene) ?? throw new Exception("no Skeleton3D");
            var player = FindNode<AnimationPlayer>(scene);
            if (player == null) { player = new AnimationPlayer { Name = "AnimationPlayer" }; scene.AddChild(player); }
            Log($"Godot skeleton bones: {skel.GetBoneCount()}, baked clips in GLB: {player.GetAnimationList().Length}\n");

            ProbeRoot(skel, vrfSkel, scene);

            // ---- per clip
            Log("\n## Per-clip results\n");
            Log("| clip | cat | additive | trk/bones | A (src FK) | B rootΔ° / maxΔ° / bones>2° | B after parentless fix | C baked | C lazy raw | C lazy inplace | C lazy fixed | verdict |");
            Log("|---|---|---|---|---|---|---|---|---|---|---|---|");
            var rootDeltas = new List<double>(); var childDeltas = new List<double>();
            int failA = 0, failB = 0, failCraw = 0, failCip = 0, failCbaked = 0, failCfix = 0, failBfix = 0, compared = 0, tested = 0, outlierDumps = 0;
            var outlierLog = new List<string>();
            var restAabb = Aabb(skel, rest: true, null, 0);

            foreach (var info in picks)
            {
                tested++;
                int trk = (info.VrfAnimation is ClipAnimation ca) ? ca.Clip.TrackCompressionSettings.Length : -1;
                string a = info.IsAdditive ? "n/a(add)" : CheckA(model, info, loader);
                bool aOk = a.StartsWith("OK") || a.StartsWith("n/a");
                if (!aOk) failA++;

                var raw = DeadlockAnimLoader.DecodeAnimationToGodot(info, skel, player, model, loader, null, "diag_raw_" + info.CleanName, false);
                var ip = DeadlockAnimLoader.DecodeAnimationToGodot(info, skel, player, model, loader, null, "diag_ip_" + info.CleanName, true);
                var baked = FindBaked(player, info);

                string b = "no baked clip";
                string bFix = "-";
                Godot.Animation fixedAnim = raw != null ? SimulateParentlessFix(skel, raw) : null;
                if (baked != null && raw != null)
                {
                    compared++;
                    var (rootErr, maxErr, nBad, missing, perBone) = Compare(skel, raw, baked);
                    b = $"{rootErr:F1} / {maxErr:F1} / {nBad} (missing {missing})";
                    var (fRoot, fMax, fBad, _, fPer) = Compare(skel, fixedAnim, baked);
                    bFix = $"{fRoot:F1} / {fMax:F1} / {fBad}";
                    if (fBad > 3) failBfix++;
                    if (nBad > 1 && outlierDumps < 6 && (fBad > 0))
                    {
                        outlierDumps++;
                        outlierLog.Add(DumpOutliers(skel, vrfSkel, info, fPer));
                    }
                    if (!info.IsAdditive) { rootDeltas.Add(rootErr); childDeltas.Add(MedianOfNonRoot(perBone)); }
                    if (rootErr > 2 || nBad > 3) failB++;
                    if (info.CleanName.Contains("stand_idle") || info.CleanName.Contains("run_n"))
                        Log($"<!-- {info.CleanName} q(lazy^-1*baked) root euler° = {RootDeltaEuler(skel, raw, baked)} -->");
                }

                bool okB = true, okR = false, okI = false;
                string cb = baked != null ? CheckC(skel, baked, restAabb, out okB) : "-"; if (baked != null && !okB) failCbaked++;
                string cr = raw != null ? CheckC(skel, raw, restAabb, out okR) : "decode null"; if (raw == null || !okR) failCraw++;
                string ci = ip != null ? CheckC(skel, ip, restAabb, out okI) : "decode null"; if (ip == null || !okI) failCip++;

                bool okF = false;
                string cf = fixedAnim != null ? CheckC(skel, fixedAnim, restAabb, out okF) : "-"; if (fixedAnim == null || !okF) failCfix++;

                bool isDeltaClip = info.CleanName.Contains("aim") || info.CleanName.Contains("recoil") || info.CleanName.Contains("flinch") || info.CleanName.EndsWith("_l") || info.CleanName.EndsWith("_r");
                bool expectUpright = !isDeltaClip && (info.Category is "Idles & Stances" or "Locomotion" or "Combat & Attacks");
                bool okAll = isDeltaClip ? (b.StartsWith("0") || b.StartsWith("no")) : (aOk && (raw != null && okR) && (ip != null && okI));
                string verdict = okAll ? "PASS" : (expectUpright ? "FAIL" : "WARN");
                if (verdict == "FAIL") _fails++;
                Log($"| {info.CleanName} | {info.Category} | {info.IsAdditive} | {trk}/{vrfSkel.Bones.Length} | {a} | {b} | {bFix} | {cb} | {cr} | {ci} | {cf} | {verdict} |");
            }

            // ---- summary + auto conclusions
            Log("\n## Summary\n");
            Log($"- clips tested: {tested}, compared with baked: {compared}");
            Log($"- Check A (Source data upright) failures: {failA}");
            Log($"- Check B (lazy vs baked) failures: {failB}");
            Log($"- Check C failures -> baked(oracle): {failCbaked}, lazy raw: {failCraw}, lazy in-place: {failCip}, lazy with simulated parentless fix: {failCfix}");
            Log($"- Check B failures after simulated parentless-root fix (>3 bones off): {failBfix}");
            if (outlierLog.Count > 0) { Log("\n## Remaining outlier bones after the simulated fix (partial clips)\n"); foreach (var o in outlierLog) Log(o); }
            DumpApi(picks.FirstOrDefault(p => p.CleanName == "hideout_run_n")?.VrfAnimation, model);
            foreach (var pn in new[] { "hideout_run_n", "jump_ground" })
            {
                var pi = picks.FirstOrDefault(p => p.CleanName == pn);
                var pb = pi != null ? FindBaked(player, pi) : null;
                var pr = pi != null ? player.GetAnimationLibrary("").GetAnimation("diag_raw_" + pn) : null;
                if (pi != null && pb != null && pr != null) ProbeBones(skel, model, pi, pr, pb);
            }

            Log("\n## Collapsed Animations Investigation\n");
            string[] collapsedQueries = { "wall", "impact", "custom01", "flinch", "_aim", "zipline_swing", "lean", "fire_l", "fire_r" };
            var foundCollapsed = infos.Where(i => collapsedQueries.Any(q => i.CleanName.ToLowerInvariant().Contains(q))).ToList();
            Log($"Found {foundCollapsed.Count} matching clips for collapsed queries:");
            foreach (var fc in foundCollapsed)
            {
                var anim = fc.VrfAnimation;
                int trkCount = (anim is ClipAnimation ca) ? ca.Clip.TrackCompressionSettings.Length : -1;
                string skelName = (anim is ClipAnimation ca2) ? ca2.Clip.SkeletonName : anim.TargetSkeletonName;
                var frame = new Frame(model.Skeleton, model.FlexControllers);
                frame.Clear(model.Skeleton);
                frame.FrameIndex = 0;
                anim.DecodeFrame(frame);

                int pelvisIdx = Array.FindIndex(model.Skeleton.Bones, b => b.Name.Equals("pelvis", StringComparison.OrdinalIgnoreCase));
                var pPos = pelvisIdx >= 0 ? frame.Bones[pelvisIdx].Position : System.Numerics.Vector3.Zero;
                var pRot = pelvisIdx >= 0 ? frame.Bones[pelvisIdx].Angle : System.Numerics.Quaternion.Identity;

                float maxBoneDist = 0f;
                int nonZeroTracks = 0;
                for (int b = 0; b < frame.Bones.Length; b++)
                {
                    if (frame.Bones[b].Position.Length() > 0.05f) nonZeroTracks++;
                    if (frame.Bones[b].Position.Length() > maxBoneDist) maxBoneDist = frame.Bones[b].Position.Length();
                }

                int idRotBones = 0;
                int bindRotBones = 0;
                for (int b = 0; b < frame.Bones.Length; b++)
                {
                    var q = frame.Bones[b].Angle;
                    var bq = model.Skeleton.Bones[b].Angle;
                    float dotId = Math.Abs(q.W);
                    float dotBind = Math.Abs(System.Numerics.Quaternion.Dot(q, bq));
                    if (dotId > 0.999f) idRotBones++;
                    if (dotBind > 0.999f) bindRotBones++;
                }

                var deltaTest = anim.GetAdditiveDelta(pelvisIdx >= 0 ? pelvisIdx : 0, frame.Bones[pelvisIdx >= 0 ? pelvisIdx : 0]);
                Log($"- `{fc.CleanName}` | GetAdditiveDelta: P={deltaTest.Position}, Q={deltaTest.Angle}");
            }

            var liftInfo = infos.FirstOrDefault(i => i.CleanName == "lift");
            if (liftInfo != null)
            {
                var dRaw = DeadlockAnimLoader.DecodeAnimationToGodot(liftInfo, skel, player, model, loader, null, "test_raw_lift", false);
                var dIp = DeadlockAnimLoader.DecodeAnimationToGodot(liftInfo, skel, player, model, loader, null, "test_ip_lift", true);
                Log($"\n## Root-motion in-place locking validation (lift):");
                for (int t = 0; t < dRaw.GetTrackCount(); t++)
                {
                    if (dRaw.TrackGetType(t) != Godot.Animation.TrackType.Position3D) continue;
                    var path = dRaw.TrackGetPath(t).ToString();
                    if (path.Contains("root_motion"))
                    {
                        var p0 = dRaw.PositionTrackInterpolate(t, 0);
                        var pEnd = dRaw.PositionTrackInterpolate(t, dRaw.Length);
                        Log($"- Raw root_motion: travel = {(pEnd - p0).Length():F2}m (dx={(pEnd.X - p0.X):F2}, dy={(pEnd.Y - p0.Y):F2}, dz={(pEnd.Z - p0.Z):F2})");
                    }
                }
                for (int t = 0; t < dIp.GetTrackCount(); t++)
                {
                    if (dIp.TrackGetType(t) != Godot.Animation.TrackType.Position3D) continue;
                    var path = dIp.TrackGetPath(t).ToString();
                    if (path.Contains("root_motion"))
                    {
                        var p0 = dIp.PositionTrackInterpolate(t, 0);
                        var pEnd = dIp.PositionTrackInterpolate(t, dIp.Length);
                        Log($"- In-place root_motion: travel = {(pEnd - p0).Length():F2}m (dx={(pEnd.X - p0.X):F2}, dy={(pEnd.Y - p0.Y):F2}, dz={(pEnd.Z - p0.Z):F2})");
                    }
                }
            }
            Log("\n## Probing all 'item_*' animations:\n");
            var itemClips = infos.Where(i => i.CleanName.StartsWith("item_", StringComparison.OrdinalIgnoreCase)).ToList();
            Log($"Found {itemClips.Count} item clips.");
            int pelvisBoneIdx = Array.FindIndex(model.Skeleton.Bones, b => b.Name.Equals("pelvis", StringComparison.OrdinalIgnoreCase));
            foreach (var ic in itemClips)
            {
                var ca = ic.VrfAnimation as ClipAnimation;
                int trk = ca != null ? ca.Clip.TrackCompressionSettings.Length : -1;
                string skelName = ca != null ? ca.Clip.SkeletonName : ic.VrfAnimation?.TargetSkeletonName;
                
                var fr = new Frame(model.Skeleton, model.FlexControllers);
                fr.Clear(model.Skeleton);
                fr.FrameIndex = 0;
                ic.VrfAnimation.DecodeFrame(fr);
                float pLen = pelvisBoneIdx >= 0 ? fr.Bones[pelvisBoneIdx].Position.Length() : 0f;
                Log($"- `{ic.CleanName}` | Tracks: {trk}/{model.Skeleton.Bones.Length} | pelvisLen: {pLen:F1}\" | Skel: {skelName}");
            }

            try
            {
                var skelRes = loader.LoadFile("models/heroes_staging/haze/haze.vnmskel_c") ?? loader.LoadFileCompiled("models/heroes_staging/haze/haze.vnmskel");
                if (skelRes != null)
                {
                var secondarySkel = Skeleton.FromSkeletonResource(loader, "models/heroes_staging/haze/haze.vnmskel");
                if (secondarySkel != null)
                {
                    Log($"Skeleton.FromSkeletonResource SUCCESS! secondarySkel bones: {secondarySkel.Bones.Length}");
                    var retargeter = new SkeletonRetargeter(model.Skeleton, secondarySkel);
                    Log($"retargeter created: HasMappedBones={retargeter.HasMappedBones}");

                    var flinchAnim = infos.FirstOrDefault(p => p.CleanName == "flinch_back")?.VrfAnimation;
                    if (flinchAnim is ClipAnimation fca)
                    {
                        var flinchSecFrame = new Frame(secondarySkel, model.FlexControllers);
                        flinchSecFrame.Clear(secondarySkel);
                        flinchSecFrame.FrameIndex = 0;
                        flinchAnim.DecodeFrame(flinchSecFrame);
                        var fModelWorld = new System.Numerics.Matrix4x4[model.Skeleton.Bones.Length];
                        retargeter.Retarget(flinchSecFrame, fModelWorld);
                        int pIdx = Array.FindIndex(model.Skeleton.Bones, b => b.Name.Equals("pelvis", StringComparison.OrdinalIgnoreCase));
                        System.Numerics.Matrix4x4.Decompose(fModelWorld[pIdx], out _, out var fCompAngle, out var fCompPos);
                        Log($"flinch_back retargeted pelvis pos: {fCompPos} (len={fCompPos.Length():F1}\"), rot={fCompAngle}");
                    }

                    var itemStandAnim = infos.FirstOrDefault(p => p.CleanName == "item_stand_idle")?.VrfAnimation;
                    if (itemStandAnim != null)
                    {
                        var itemSecFrame = new Frame(secondarySkel, model.FlexControllers);
                        itemSecFrame.Clear(secondarySkel);
                        itemSecFrame.FrameIndex = 0;
                        itemStandAnim.DecodeFrame(itemSecFrame);
                        var itemModelWorld = new System.Numerics.Matrix4x4[model.Skeleton.Bones.Length];
                        retargeter.Retarget(itemSecFrame, itemModelWorld);
                        int pIdx = Array.FindIndex(model.Skeleton.Bones, b => b.Name.Equals("pelvis", StringComparison.OrdinalIgnoreCase));
                        System.Numerics.Matrix4x4.Decompose(itemModelWorld[pIdx], out _, out var fCompAngle, out var fCompPos);
                        Log($"item_stand_idle retargeted pelvis pos: {fCompPos} (len={fCompPos.Length():F1}\"), rot={fCompAngle}");
                    }

                    var hideoutAnim = infos.FirstOrDefault(p => p.CleanName == "hideout_run_n")?.VrfAnimation;
                    var modelWorld = new System.Numerics.Matrix4x4[model.Skeleton.Bones.Length];
                    if (hideoutAnim != null)
                    {
                        var hideoutSecFrame = new Frame(secondarySkel, model.FlexControllers);
                        hideoutSecFrame.Clear(secondarySkel);
                        hideoutSecFrame.FrameIndex = 0;
                        hideoutAnim.DecodeFrame(hideoutSecFrame);
                        retargeter.Retarget(hideoutSecFrame, modelWorld);
                    }

                    var invWorld = new System.Numerics.Matrix4x4[model.Skeleton.Bones.Length];
                    for (int i = 0; i < model.Skeleton.Bones.Length; i++)
                    {
                        System.Numerics.Matrix4x4.Invert(modelWorld[i], out invWorld[i]);
                    }

                    int bakedMatchCount = 0;
                    int bakedTotalChecked = 0;
                    double maxRetargetErr = 0;
                    var bakedHideout = FindBaked(player, picks.First(p => p.CleanName == "hideout_run_n"));
                    for (int b = 0; b < model.Skeleton.Bones.Length; b++)
                    {
                        var vrfBone = model.Skeleton.Bones[b];
                        System.Numerics.Matrix4x4 localMat;
                        if (vrfBone.Parent == null)
                        {
                            localMat = modelWorld[b];
                        }
                        else
                        {
                            int p = Array.IndexOf(model.Skeleton.Bones, vrfBone.Parent);
                            localMat = modelWorld[b] * invWorld[p];
                        }
                        System.Numerics.Matrix4x4.Decompose(localMat, out _, out var compAngle, out var compPos);
                        var (convPos, convRot) = DeadlockAnimLoader.BakeConversion(compPos, compAngle, vrfBone.Parent == null);
                        var gRot = new Quaternion(convRot.X, convRot.Y, convRot.Z, convRot.W).Normalized();
                        var (_, bRot) = Sample(bakedHideout, vrfBone.Name, 0);
                        if (bRot.HasValue)
                        {
                            bakedTotalChecked++;
                            double err = Ang(gRot, bRot.Value);
                            if (err > maxRetargetErr) maxRetargetErr = err;
                            if (err < 2.0) bakedMatchCount++;
                        }
                    }
                    Log($"Retargeted hideout_run_n vs baked: {bakedMatchCount}/{bakedTotalChecked} bones match within 2°! Max error: {maxRetargetErr:F1}°");
                }
                else
                {
                    Log("Skeleton.FromSkeletonResource returned null");
                }
                }
                else
                {
                    Log("secondary skeleton models/heroes_staging/haze/haze.vnmskel_c NOT FOUND by loader");
                }
            }
            catch (Exception ex)
            {
                Log($"Failed loading secondary skeleton: {ex.Message}");
            }

            if (rootDeltas.Count > 0)
            {
                Log($"- median root error vs baked: {Median(rootDeltas):F1}°, median non-root bone error: {Median(childDeltas):F1}°");
                if (Median(rootDeltas) > 20 && Median(childDeltas) < 5)
                    Log("- **S1 CONFIRMED**: root rotation differs from the baked oracle by a constant amount while child bones match -> root conversion in BakeConversion is wrong for this skeleton import.");
                else if (Median(rootDeltas) < 2 && Median(childDeltas) < 5)
                    Log("- S1 ruled out: root and children match the baked oracle.");
                else Log("- S1 inconclusive: both root and children differ; see per-bone dump.");
            }
            if (failCraw < failCip)
                Log("- Raw (in-place off) passes more often than in-place on -> **S2 CONFIRMED** (in-place lock corrupts the pose).");
            else if (failCip < failCraw)
                Log("- In-place passes more than raw (unexpected).");
            else
                Log("- In-place lock does not change pass/fail counts (S2 not the primary cause).");
            if (failCbaked > 0) Log("- WARNING: Check C fails on baked oracle clips; thresholds/metric need review.");
        }

        // ------------------------------------------------------------ S1 probe
        private void ProbeRoot(Skeleton3D skel, Skeleton vrf, Node scene)
        {
            Log("## Root / import-space probe\n");
            var rootBone = vrf.Bones.First(b => b.Parent == null);
            int gi = skel.FindBone(rootBone.Name);
            Log($"- VRF top root bone: '{rootBone.Name}' (godot idx {gi})");
            var parentless = vrf.Bones.Where(b => b.Parent == null).Select(b => b.Name).ToList();
            Log($"- VRF parentless bones: {string.Join(", ", parentless)}");
            Log("- Root-ish bones: " + string.Join(", ", vrf.Bones.Where(b => b.Name.ToLowerInvariant().Contains("root") || b.Name.ToLowerInvariant().Contains("world")).Select(b => $"{b.Name}(parent={(b.Parent?.Name ?? "none")})")));
            var bindQ = rootBone.Angle;
            var conv = System.Numerics.Quaternion.Normalize(SourceToGltf * bindQ);
            if (gi != -1)
            {
                var rest = skel.GetBoneRest(gi);
                var rq = rest.Basis.GetRotationQuaternion();
                var gConv = new Quaternion(conv.X, conv.Y, conv.Z, conv.W);
                var gRaw = new Quaternion(bindQ.X, bindQ.Y, bindQ.Z, bindQ.W);
                Log($"- Godot root REST rot (euler°): {ToDeg(rq.GetEuler())}, pos: {rest.Origin}");
                Log($"- VRF bind rot raw (euler°): {ToDeg(gRaw.GetEuler())}; S*bind: {ToDeg(gConv.GetEuler())}");
                Log($"- angle(godotRest, S*bind) = {Ang(rq, gConv):F1}°, angle(godotRest, rawBind) = {Ang(rq, gRaw):F1}°");
                Log(Ang(rq, gConv) < 2 ? "  -> root rest matches S*bind: BakeConversion root rule consistent with import."
                    : "  -> **root rest does NOT match S*bind**: hardcoded conversion disagrees with the imported skeleton (S1 evidence).");
            }
            var spine = skel.FindBone("pelvis");
            if (spine != -1)
            {
                var vb = vrf.Bones.First(b => b.Name == "pelvis");
                var gr = skel.GetBoneRest(spine);
                var q = new Quaternion(vb.Angle.X, vb.Angle.Y, vb.Angle.Z, vb.Angle.W);
                Log($"- pelvis: angle(godotRest, vrfBind)={Ang(gr.Basis.GetRotationQuaternion(), q):F1}°, pos godot={gr.Origin} vrf*0.0254={new Vector3(vb.Position.X, vb.Position.Y, vb.Position.Z) * 0.0254f}");
            }
            Node n = skel;
            Log("- Ancestor transforms (skeleton -> scene root):");
            while (n != null && n != Root)
            {
                if (n is Node3D n3) Log($"  - {n.Name}: euler° {ToDeg(n3.Transform.Basis.GetRotationQuaternion().GetEuler())}, scale {n3.Transform.Basis.Scale}");
                n = n.GetParent();
            }
        }

        // ------------------------------------------------------------ Check A
        private string CheckA(Model model, DeadlockAnimLoader.AnimSequenceInfo info, IFileLoader fileLoader)
        {
            var sk = model.Skeleton;
            var sb = new List<string>(); bool ok = true;

            Skeleton secondarySkel = null;
            SkeletonRetargeter retargeter = null;
            if (info.VrfAnimation is ClipAnimation ca && !string.IsNullOrEmpty(ca.Clip.SkeletonName) && fileLoader != null)
            {
                if (!ca.Clip.SkeletonName.Equals(sk.Name, StringComparison.OrdinalIgnoreCase) ||
                    ca.Clip.TrackCompressionSettings.Length != sk.Bones.Length)
                {
                    secondarySkel = DeadlockAnimLoader.GetOrLoadSecondarySkeleton(fileLoader, ca.Clip.SkeletonName);
                    if (secondarySkel != null)
                    {
                        retargeter = DeadlockAnimLoader.GetOrCreateRetargeter(sk, secondarySkel, ca.Clip.SkeletonName);
                    }
                }
            }

            foreach (int fi in new[] { 0, Math.Max(0, info.FrameCount / 2) })
            {
                (System.Numerics.Vector3 P, System.Numerics.Quaternion R)[] g;
                if (retargeter != null && secondarySkel != null)
                {
                    var secFrame = new Frame(secondarySkel, model.FlexControllers);
                    secFrame.Clear(secondarySkel); secFrame.FrameIndex = fi; info.VrfAnimation.DecodeFrame(secFrame);
                    var modelWorld = new System.Numerics.Matrix4x4[sk.Bones.Length];
                    retargeter.Retarget(secFrame, modelWorld);
                    g = new (System.Numerics.Vector3 P, System.Numerics.Quaternion R)[sk.Bones.Length];
                    for (int i = 0; i < sk.Bones.Length; i++)
                    {
                        System.Numerics.Matrix4x4.Decompose(modelWorld[i], out _, out var r, out var p);
                        g[i] = (p, r);
                    }
                }
                else
                {
                    var frame = new Frame(sk, model.FlexControllers);
                    frame.Clear(sk); frame.FrameIndex = fi; info.VrfAnimation.DecodeFrame(frame);
                    g = Fk(sk, frame);
                }

                int pelvis = IndexOf(sk, "pelvis"), head = IndexOf(sk, "head"), foot = IndexOf(sk, "ankle_L");
                if (pelvis < 0 || head < 0 || foot < 0) return "OK? (bones missing)";
                float pz = g[pelvis].P.Z, hz = g[head].P.Z, fz = g[foot].P.Z;
                bool up = hz > pz && pz > fz && pz > 15f; // pelvis should be well above floor (inches); ~25-47 in practice
                ok &= up;
                sb.Add($"f{fi}: z h/p/f={hz:F0}/{pz:F0}/{fz:F0}");
            }
            return (ok ? "OK " : "BAD ") + string.Join("; ", sb);
        }

        private static (System.Numerics.Vector3 P, System.Numerics.Quaternion R)[] Fk(Skeleton sk, Frame frame)
        {
            var g = new (System.Numerics.Vector3 P, System.Numerics.Quaternion R)[sk.Bones.Length];
            var done = new bool[sk.Bones.Length];
            void Solve(int i)
            {
                if (done[i]) return;
                var b = sk.Bones[i]; var local = frame.Bones[i];
                if (b.Parent == null) g[i] = (local.Position, local.Angle);
                else
                {
                    int p = Array.IndexOf(sk.Bones, b.Parent); Solve(p);
                    g[i] = (g[p].P + System.Numerics.Vector3.Transform(local.Position, g[p].R), System.Numerics.Quaternion.Normalize(g[p].R * local.Angle));
                }
                done[i] = true;
            }
            for (int i = 0; i < sk.Bones.Length; i++) Solve(i);
            return g;
        }

        private static int IndexOf(Skeleton sk, string name) => Array.FindIndex(sk.Bones, b => b.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        // ------------------------------------------------------------ Check B
        private static Godot.Animation FindBaked(AnimationPlayer player, DeadlockAnimLoader.AnimSequenceInfo info)
        {
            string want = info.CleanName.ToLowerInvariant();
            foreach (var n in player.GetAnimationList())
            {
                if (n.StartsWith("diag_")) continue;
                string c = DeadlockAnimLoader.SanitizePoseName(n).ToLowerInvariant();
                if (c == want || n.ToLowerInvariant().EndsWith(want)) return player.GetAnimation(n);
            }
            return null;
        }

        private static (Godot.Vector3? pos, Quaternion? rot) Sample(Godot.Animation a, string bone, double t)
        {
            Godot.Vector3? p = null; Quaternion? r = null;
            for (int i = 0; i < a.GetTrackCount(); i++)
            {
                string path = a.TrackGetPath(i).ToString();
                int c = path.LastIndexOf(':');
                if (c < 0 || !path.Substring(c + 1).Equals(bone, StringComparison.OrdinalIgnoreCase) || a.TrackGetKeyCount(i) == 0) continue;
                var type = a.TrackGetType(i);
                if (type == Godot.Animation.TrackType.Position3D) p = a.PositionTrackInterpolate(i, t);
                else if (type == Godot.Animation.TrackType.Rotation3D) r = a.RotationTrackInterpolate(i, t).Normalized();
            }
            return (p, r);
        }

        private (double rootErr, double maxErr, int nBad, int missing, Dictionary<string, double> perBone) Compare(Skeleton3D skel, Godot.Animation lazy, Godot.Animation baked)
        {
            double rootErr = 0, maxErr = 0; int nBad = 0, missing = 0; var per = new Dictionary<string, double>();
            for (int i = 0; i < skel.GetBoneCount(); i++)
            {
                string bn = skel.GetBoneName(i);
                var (_, lr) = Sample(lazy, bn, 0); var (_, br) = Sample(baked, bn, 0);
                if (lr == null || br == null) { if (br != null) missing++; continue; }
                double e = Ang(lr.Value, br.Value); per[bn] = e;
                if (e > maxErr) maxErr = e; if (e > 2) nBad++;
                if (skel.GetBoneParent(i) == -1) rootErr = Math.Max(rootErr, e);
            }
            return (rootErr, maxErr, nBad, missing, per);
        }

        private string RootDeltaEuler(Skeleton3D skel, Godot.Animation lazy, Godot.Animation baked)
        {
            for (int i = 0; i < skel.GetBoneCount(); i++)
                if (skel.GetBoneParent(i) == -1)
                {
                    var (_, lr) = Sample(lazy, skel.GetBoneName(i), 0); var (_, br) = Sample(baked, skel.GetBoneName(i), 0);
                    if (lr != null && br != null) return ToDeg((lr.Value.Inverse() * br.Value).GetEuler()) + " | baked*lazy^-1 = " + ToDeg((br.Value * lr.Value.Inverse()).GetEuler());
                }
            return "n/a";
        }

        private static double MedianOfNonRoot(Dictionary<string, double> d) => d.Count == 0 ? 0 : Median(d.Values.ToList());

        // Manual forward kinematics from local pose (Skeleton3D's own global pose cache is not reliable headless).
        private static Godot.Vector3[] FkWorld(Skeleton3D skel, bool rest)
        {
            int n = skel.GetBoneCount();
            var g = new Transform3D[n]; var done = new bool[n];
            void Solve(int i)
            {
                if (done[i]) return;
                var local = rest ? skel.GetBoneRest(i) : new Transform3D(new Basis(skel.GetBonePoseRotation(i)), skel.GetBonePosePosition(i));
                int p = skel.GetBoneParent(i);
                if (p < 0) g[i] = local; else { Solve(p); g[i] = g[p] * local; }
                done[i] = true;
            }
            for (int i = 0; i < n; i++) Solve(i);
            var w = new Godot.Vector3[n];
            for (int i = 0; i < n; i++) w[i] = g[i].Origin;
            return w;
        }

        private static (Godot.Vector3 size, Godot.Vector3 min) Aabb(Skeleton3D skel, bool rest, Godot.Animation anim, double t)
        {
            if (!rest) ApplyClip(skel, anim, t);
            var w = FkWorld(skel, rest);
            var mn = new Godot.Vector3(1e9f, 1e9f, 1e9f); var mx = -mn;
            for (int i = 0; i < w.Length; i++)
            {
                if (BoneLayerManager.IsProceduralClothBone(skel.GetBoneName(i))) continue;
                var p = w[i];
                mn = new Godot.Vector3(Mathf.Min(mn.X, p.X), Mathf.Min(mn.Y, p.Y), Mathf.Min(mn.Z, p.Z));
                mx = new Godot.Vector3(Mathf.Max(mx.X, p.X), Mathf.Max(mx.Y, p.Y), Mathf.Max(mx.Z, p.Z));
            }
            return (mx - mn, mn);
        }

        private static void ApplyClip(Skeleton3D skel, Godot.Animation a, double t)
        {
            for (int i = 0; i < skel.GetBoneCount(); i++) skel.ResetBonePose(i);
            for (int i = 0; i < a.GetTrackCount(); i++)
            {
                string path = a.TrackGetPath(i).ToString(); int c = path.LastIndexOf(':');
                if (c < 0 || a.TrackGetKeyCount(i) == 0) continue;
                int bi = skel.FindBone(path.Substring(c + 1)); if (bi < 0) continue;
                var type = a.TrackGetType(i);
                if (type == Godot.Animation.TrackType.Position3D) skel.SetBonePosePosition(bi, a.PositionTrackInterpolate(i, t));
                else if (type == Godot.Animation.TrackType.Rotation3D) skel.SetBonePoseRotation(bi, a.RotationTrackInterpolate(i, t).Normalized());
            }
        }

        private string CheckC(Skeleton3D skel, Godot.Animation anim, (Godot.Vector3 size, Godot.Vector3 min) rest, out bool ok)
        {
            ok = true; var parts = new List<string>();
            foreach (double t in new[] { 0.0, anim.Length * 0.5 })
            {
                var (size, _) = Aabb(skel, false, anim, t);
                var w = FkWorld(skel, false);
                int pelvis = skel.FindBone("pelvis"), head = skel.FindBone("head"), ankleL = skel.FindBone("ankle_L");
                float upDot = 0; bool ordered = true;
                if (pelvis >= 0 && head >= 0 && ankleL >= 0)
                {
                    upDot = (w[head] - w[pelvis]).Normalized().Y;
                    ordered = w[head].Y > w[pelvis].Y && w[pelvis].Y > w[ankleL].Y;
                }
                float hRatio = size.Y / Mathf.Max(rest.size.Y, 1e-4f);
                float maxRatio = Mathf.Max(size.X, Mathf.Max(size.Y, size.Z)) / Mathf.Max(Mathf.Max(rest.size.X, Mathf.Max(rest.size.Y, rest.size.Z)), 1e-4f);
                bool good = upDot > 0.5f && ordered && hRatio > 0.45f && maxRatio is > 0.45f and < 2.2f;
                ok &= good;
                parts.Add($"{(good ? "ok" : "BAD")} up={upDot:F2} h={hRatio:F2}");
            }
            return string.Join(" / ", parts);
        }

        // What-if: apply the Source->glTF conversion to EVERY parentless bone (pelvis is a 2nd root in Deadlock skeletons)
        private static Godot.Animation SimulateParentlessFix(Skeleton3D skel, Godot.Animation src)
        {
            var s = new Quaternion(SourceToGltf.X, SourceToGltf.Y, SourceToGltf.Z, SourceToGltf.W);
            var dup = (Godot.Animation)src.Duplicate(true);
            int firstRoot = -1;
            for (int i = 0; i < skel.GetBoneCount(); i++) if (skel.GetBoneParent(i) == -1) { firstRoot = i; break; }
            for (int t = 0; t < dup.GetTrackCount(); t++)
            {
                string path = dup.TrackGetPath(t).ToString(); int c = path.LastIndexOf(':'); if (c < 0) continue;
                int bi = skel.FindBone(path.Substring(c + 1));
                if (bi < 0 || skel.GetBoneParent(bi) != -1 || bi == firstRoot) continue;
                var type = dup.TrackGetType(t);
                for (int k = 0; k < dup.TrackGetKeyCount(t); k++)
                {
                    if (type == Godot.Animation.TrackType.Position3D) dup.TrackSetKeyValue(t, k, s * (Godot.Vector3)dup.TrackGetKeyValue(t, k));
                    else if (type == Godot.Animation.TrackType.Rotation3D) dup.TrackSetKeyValue(t, k, (s * (Quaternion)dup.TrackGetKeyValue(t, k)).Normalized());
                }
            }
            return dup;
        }

        // Prints the public API surface of the VRF animation/clip types so retargeting hooks can be located.
        private void DumpApi(object anim, Model model)
        {
            if (anim == null) return;
            Log("\n## VRF API surface (for retargeting)\n");
            void Dump(Type t)
            {
                var members = t.GetMembers(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly)
                    .Where(m => m.MemberType is System.Reflection.MemberTypes.Property or System.Reflection.MemberTypes.Method or System.Reflection.MemberTypes.Field)
                    .Where(m => !m.Name.StartsWith("get_") && !m.Name.StartsWith("set_") && m.Name != "GetType" && m.Name != "ToString" && m.Name != "Equals" && m.Name != "GetHashCode")
                    .Select(m => m.Name).Distinct();
                Log($"- `{t.FullName}`: {string.Join(", ", members)}");
            }
            for (var t = anim.GetType(); t != null && t != typeof(object); t = t.BaseType) Dump(t);
            if (anim is ClipAnimation ca)
            {
                Dump(ca.Clip.GetType());
                foreach (var p in ca.Clip.GetType().GetProperties())
                    if (p.PropertyType == typeof(string[]) || p.PropertyType == typeof(int[]) || p.PropertyType == typeof(string))
                    {
                        try { var v = p.GetValue(ca.Clip); Log($"  - Clip.{p.Name} = {(v is Array arr ? $"[{arr.Length}] " + string.Join(",", arr.Cast<object>().Take(12)) : v)}"); } catch { }
                    }
            }
            // Inspect how WriteAnimationGraphClips uses SkeletonRetargeter
            try
            {
                var writeMethod = typeof(GltfModelExporter).GetMethod("WriteAnimationGraphClips", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (writeMethod != null)
                {
                    Log($"WriteAnimationGraphClips MethodBody local vars: {writeMethod.GetMethodBody()?.LocalVariables.Count}");
                    foreach (var lv in writeMethod.GetMethodBody()?.LocalVariables ?? Enumerable.Empty<System.Reflection.LocalVariableInfo>())
                    {
                        Log($"   LocalVar: {lv.LocalType.FullName}");
                    }
                }

                var bakeMethod = typeof(GltfModelExporter).GetMethod("BakeConversion", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (bakeMethod != null)
                {
                    var exp = (GltfModelExporter)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(GltfModelExporter));
                    var resTrue = bakeMethod.Invoke(exp, new object[] { System.Numerics.Vector3.One, System.Numerics.Quaternion.Identity, true });
                    var resFalse = bakeMethod.Invoke(exp, new object[] { System.Numerics.Vector3.One, System.Numerics.Quaternion.Identity, false });
                    Log($"BakeConversion(true) = {resTrue}");
                    Log($"BakeConversion(false) = {resFalse}");
                }
            }
            catch (Exception ex) { Log($"Failed to inspect WriteAnimationGraphClips: {ex.Message}"); }

            Dump(typeof(Frame));
        }


        private void ProbeBones(Skeleton3D skel, Model model, DeadlockAnimLoader.AnimSequenceInfo info, Godot.Animation lazy, Godot.Animation baked)
        {
            Log($"\n## Per-bone probe: {info.CleanName}\n");
            Log("| bone | vrf frame0 pos(in) | lazy pos(m) | baked pos(m) | godot rest pos | lazy rot° | baked rot° | vrf raw rot° |");
            Log("|---|---|---|---|---|---|---|---|");
            var sk = model.Skeleton; var frame = new Frame(sk, model.FlexControllers);
            frame.Clear(sk); frame.FrameIndex = 0; info.VrfAnimation.DecodeFrame(frame);
            foreach (var bn in new[] { "root_motion", "pelvis", "spine_0", "arm_R_IKTARGET", "attachWorld", "ankle_R", "hand_R", "head" })
            {
                int vi = IndexOf(sk, bn); int gi = skel.FindBone(bn); if (vi < 0 || gi < 0) continue;
                var (lp, lr) = Sample(lazy, bn, 0); var (bp, br) = Sample(baked, bn, 0);
                var fb = frame.Bones[vi]; var q = new Quaternion(fb.Angle.X, fb.Angle.Y, fb.Angle.Z, fb.Angle.W);
                Log($"| {bn} | ({fb.Position.X:F1},{fb.Position.Y:F1},{fb.Position.Z:F1}) | {lp?.ToString("F2") ?? "-"} | {bp?.ToString("F2") ?? "-"} | {skel.GetBoneRest(gi).Origin.ToString("F2")} | {(lr.HasValue ? ToDeg(lr.Value.GetEuler()) : "-")} | {(br.HasValue ? ToDeg(br.Value.GetEuler()) : "-")} | {ToDeg(q.GetEuler())} |");
            }
            Log($"- tracks in baked clip: {baked.GetTrackCount()}, lazy clip: {lazy.GetTrackCount()}");
        }

        private static string DumpOutliers(Skeleton3D skel, Skeleton vrf, DeadlockAnimLoader.AnimSequenceInfo info, Dictionary<string, double> per)
        {
            var bad = per.Where(kv => kv.Value > 2).Select(kv => kv.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var origins = new List<string>();
            foreach (var n in bad)
            {
                int bi = skel.FindBone(n); int p = skel.GetBoneParent(bi);
                if (p < 0 || !bad.Contains(skel.GetBoneName(p))) origins.Add($"{n}({per[n]:F0}°)");
            }
            var tracked = per.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var unkeyedLazy = new List<string>();
            for (int i = 0; i < skel.GetBoneCount(); i++) if (!tracked.Contains(skel.GetBoneName(i)) && !BoneLayerManager.IsProceduralClothBone(skel.GetBoneName(i))) unkeyedLazy.Add(skel.GetBoneName(i));
            return $"- **{info.CleanName}** ({(info.VrfAnimation as ClipAnimation)?.Clip.TrackCompressionSettings.Length}/{vrf.Bones.Length} tracks, target skel `{info.VrfAnimation.TargetSkeletonName}`): {bad.Count} bad bones; origin bones (parent OK): {string.Join(", ", origins.Take(12))}; bones with no common key: {string.Join(", ", unkeyedLazy.Take(12))}";
        }

        // ------------------------------------------------------------ utils
        private static double Ang(Quaternion a, Quaternion b) => Mathf.RadToDeg(2.0 * Math.Acos(Math.Min(1.0, Math.Abs(a.Normalized().Dot(b.Normalized())))));
        private static string ToDeg(Godot.Vector3 e) => $"({Mathf.RadToDeg(e.X):F0}, {Mathf.RadToDeg(e.Y):F0}, {Mathf.RadToDeg(e.Z):F0})";
        private static double Median(List<double> v) { if (v.Count == 0) return 0; var s = v.OrderBy(x => x).ToList(); return s[s.Count / 2]; }
        private static T FindNode<T>(Node n) where T : Node
        {
            if (n is T t) return t;
            foreach (Node c in n.GetChildren()) { var r = FindNode<T>(c); if (r != null) return r; }
            return null;
        }
    }
}
