using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SteamDatabase.ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.IO;

namespace DeadlockPlayground.Animation
{
    public partial class RetargetDiagnostics : SceneTree
    {
        public override void _Initialize()
        {
            GD.Print("==================================================================");
            GD.Print("        DEADLOCK PLAYGROUND - RETARGET DIAGNOSTICS HARNESS       ");
            GD.Print("==================================================================");

            try
            {
                RunDiagnostics();
                GD.Print("\n[RetargetDiagnostics] Completed diagnostics successfully.");
                Quit(0);
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[RetargetDiagnostics] ERROR: {ex.Message}\n{ex.StackTrace}");
                Quit(1);
            }
        }

        public static void RunDiagnostics()
        {
            string baseDir = Directory.GetCurrentDirectory();
            string scratchDir = Path.Combine(baseDir, "scratch");
            if (Directory.Exists(Path.Combine(baseDir, "..", "scratch", "animations")))
            {
                scratchDir = Path.Combine(baseDir, "..", "scratch");
            }
            scratchDir = Path.GetFullPath(scratchDir);

            // 1. Target Hero Skeleton Inspection
            var (heroRoot, targetSkeleton) = BuildMockDeadlockHero();
            InspectTargetHero(targetSkeleton, "Mock_Deadlock_Hero");

            // Check if actual Deadlock VPK is present to also inspect real hero skeletons
            var pm = new GamePathManager();
            string gamePath = pm.AutoDetectDeadlockPath();
            GD.Print($"[Diagnostics] Deadlock path detected: '{gamePath}' (Valid: {pm.ValidateDeadlockPath(gamePath)})");

            Skeleton3D realTargetSkeleton = null;
            Node3D realHeroRoot = null;
            if (pm.ValidateDeadlockPath(gamePath))
            {
                try
                {
                    string vpkPath = Path.Combine(gamePath, "game", "citadel", "pak01_dir.vpk");
                    GD.Print($"[Diagnostics] vpkPath: '{vpkPath}', Exists: {File.Exists(vpkPath)}");
                    if (File.Exists(vpkPath))
                    {
                        var pkg = new Package();
                        pkg.Read(vpkPath);
                        var entry = pkg.FindEntry("models/heroes_staging/inferno_v4/inferno.vmdl_c")
                                 ?? pkg.FindEntry("models/heroes_staging/haze/haze.vmdl_c");
                        GD.Print($"[Diagnostics] entry found: {entry != null}");
                        if (entry != null)
                        {
                            pkg.ReadEntry(entry, out byte[] data);
                            using var res = new ValveResourceFormat.Resource();
                            using var ms = new MemoryStream(data);
                            res.Read(ms);
                            var loader = new GameFileLoader(pkg, entry.DirectoryName);
                            var exp = new GltfModelExporter(loader) { ExportAnimations = true, ExportMaterials = false, ProgressReporter = new Progress<string>() };
                            string tmp = Path.Combine(Path.GetTempPath(), "diag_hero.glb");
                            exp.Export(res, tmp);
                            byte[] glb = File.ReadAllBytes(tmp);
                            if (File.Exists(tmp)) File.Delete(tmp);
                            var doc = new GltfDocument();
                            var state = new GltfState();
                            doc.AppendFromBuffer(glb, "", state);
                            realHeroRoot = (Node3D)doc.GenerateScene(state);
                            realTargetSkeleton = FindSkeleton(realHeroRoot);
                            if (realTargetSkeleton != null)
                            {
                                InspectTargetHero(realTargetSkeleton, "Real_Deadlock_Hero_Inferno");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    GD.Print($"[Diagnostics] Real hero inspection: {ex.Message}");
                }
            }

            // 2. Baseline Test on Clips
            string[] testClips = new[]
            {
                Path.Combine(scratchDir, "animations", "mesh2motion", "dance-mesh2motion.glb"),
                Path.Combine(scratchDir, "animations", "mesh2motion", "exported-model.glb"),
                Path.Combine(scratchDir, "animations", "mixamo", "dance-mixamo.fbx"),
                Path.Combine(scratchDir, "animations", "mixamo", "Crying.fbx")
            };

            foreach (var clipPath in testClips)
            {
                if (!File.Exists(clipPath))
                {
                    GD.Print($"[Diagnostics] Clip file not found, skipping: '{clipPath}'");
                    continue;
                }

                RunClipDiagnostics(clipPath, realTargetSkeleton ?? targetSkeleton, scratchDir);
            }

            if (realHeroRoot != null) realHeroRoot.Free();
            heroRoot.Free();
        }

        private static void InspectTargetHero(Skeleton3D sk, string heroName)
        {
            GD.Print($"\n-------------------------------------------------------------");
            GD.Print($" Target Hero Skeleton: '{heroName}' ({sk.GetBoneCount()} bones)");
            GD.Print($"-------------------------------------------------------------");

            // Leg lengths and rest knee flexion
            int hipL = sk.FindBone("leg_upper_L");
            int kneeL = sk.FindBone("leg_lower_L");
            int ankleL = sk.FindBone("ankle_L");

            if (hipL != -1 && kneeL != -1 && ankleL != -1)
            {
                Vector3 pUpper = sk.GetBoneGlobalRest(hipL).Origin;
                Vector3 pLower = sk.GetBoneGlobalRest(kneeL).Origin;
                Vector3 pAnkle = sk.GetBoneGlobalRest(ankleL).Origin;

                float l1 = (pLower - pUpper).Length();
                float l2 = (pAnkle - pLower).Length();
                Vector3 u1 = (pLower - pUpper).Normalized();
                Vector3 u2 = (pAnkle - pLower).Normalized();
                float dot = Math.Clamp(u1.Dot(u2), -1f, 1f);
                float flexionDeg = Mathf.RadToDeg(Mathf.Acos(dot));

                GD.Print($"[Target Leg L] Thigh Length: {l1:F3}m, Calf Length: {l2:F3}m, Total: {(l1 + l2):F3}m");
                GD.Print($"[Target Leg L] Rest Knee Flexion: {flexionDeg:F2}° (u1={u1}, u2={u2})");

                int ballL = sk.FindBone("ball_L");
                if (ballL != -1)
                {
                    Vector3 pBall = sk.GetBoneGlobalRest(ballL).Origin;
                    Vector3 toe = pBall - pAnkle;
                    GD.Print($"[Target Foot L] Ankle to Ball (toeDir): {toe} (Len: {toe.Length():F3}m, Z-sign: {(toe.Z > 0 ? "+Z (Back/South)" : "-Z (Forward/North)")})");
                }

                int pelvis = sk.FindBone("pelvis");
                if (pelvis != -1)
                {
                    Quaternion pelvRot = sk.GetBoneGlobalRest(pelvis).Basis.GetRotationQuaternion();
                    Vector3 fwdZ = pelvRot * new Vector3(0, 0, 1);
                    Vector3 fwdNegZ = pelvRot * new Vector3(0, 0, -1);
                    GD.Print($"[Target Pelvis] pelvRot * +Z: {fwdZ}, pelvRot * -Z: {fwdNegZ}");
                }
            }

            // Finger hierarchy dump for hand_L
            int handL = sk.FindBone("hand_L");
            GD.Print($"\n[Target Hand L Hierarchy (hand_L={handL})]:");
            string[] fingers = new[] { "thumb", "index", "middle", "ring", "pinky" };
            foreach (var f in fingers)
            {
                GD.Print($"  Finger '{f}':");
                int meta = sk.FindBone($"finger_{f}_meta_L");
                if (meta != -1)
                {
                    int parent = sk.GetBoneParent(meta);
                    string parentName = parent != -1 ? sk.GetBoneName(parent) : "none";
                    Vector3 localPos = sk.GetBoneRest(meta).Origin;
                    GD.Print($"    meta: parent='{parentName}', localPos={localPos}");
                }
                for (int ph = 0; ph <= 2; ph++)
                {
                    int b = sk.FindBone($"finger_{f}_{ph}_L");
                    if (b != -1)
                    {
                        int parent = sk.GetBoneParent(b);
                        string parentName = parent != -1 ? sk.GetBoneName(parent) : "none";
                        Vector3 localPos = sk.GetBoneRest(b).Origin;
                        GD.Print($"    phalanx {ph}: parent='{parentName}', localPos={localPos}");
                    }
                }
                int end = sk.FindBone($"finger_{f}_end_L");
                if (end != -1)
                {
                    int parent = sk.GetBoneParent(end);
                    string parentName = parent != -1 ? sk.GetBoneName(parent) : "none";
                    Vector3 localPos = sk.GetBoneRest(end).Origin;
                    GD.Print($"    end: parent='{parentName}', localPos={localPos}");
                }
            }
        }

        public static void RunClipDiagnostics(string clipPath, Skeleton3D targetSkeleton, string scratchDir)
        {
            string fileName = Path.GetFileName(clipPath);
            string clipBaseName = Path.GetFileNameWithoutExtension(clipPath);
            string reportFile = Path.Combine(scratchDir, $"retarget_report_{clipBaseName}.txt");

            using var sw = new StreamWriter(reportFile, append: false);
            void Log(string msg)
            {
                GD.Print(msg);
                sw.WriteLine(msg);
            }

            Log($"\n=============================================================");
            Log($" RETARGET DIAGNOSTIC REPORT: {fileName}");
            Log($" Date: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            Log($"=============================================================");

            // Step A: Load file and inspect source skeleton
            using var loadResult = ExternalAnimationLoader.Load(clipPath);
            if (!loadResult.Success || loadResult.SourceSkeleton == null)
            {
                Log($"FAILED to load clip: {loadResult.ErrorMessage}");
                return;
            }

            var sk = loadResult.SourceSkeleton;
            Log($"Source Skeleton: '{sk.Name}' with {sk.GetBoneCount()} bones, {loadResult.Animations.Count} clip(s)");

            // Legs comparison at Node-Rest vs Skin-Bind
            int FindClean(string target)
            {
                for (int i = 0; i < sk.GetBoneCount(); i++)
                {
                    string c = HumanoidBoneMapper.CleanBoneName(sk.GetBoneName(i));
                    if (c.Equals(target, StringComparison.OrdinalIgnoreCase)) return i;
                }
                return -1;
            }

            int thighL = FindClean("leftupleg"); if (thighL == -1) thighL = FindClean("thigh_l");
            int calfL = FindClean("leftleg"); if (calfL == -1) calfL = FindClean("calf_l");
            int footL = FindClean("leftfoot"); if (footL == -1) footL = FindClean("foot_l");

            int thighR = FindClean("rightupleg"); if (thighR == -1) thighR = FindClean("thigh_r");
            int calfR = FindClean("rightleg"); if (calfR == -1) calfR = FindClean("calf_r");
            int footR = FindClean("rightfoot"); if (footR == -1) footR = FindClean("foot_r");

            if (thighL != -1 && calfL != -1 && footL != -1 && thighR != -1 && calfR != -1 && footR != -1)
            {
                Vector3 restThighL = sk.GetBoneGlobalRest(thighL).Origin;
                Vector3 restCalfL = sk.GetBoneGlobalRest(calfL).Origin;
                Vector3 restFootL = sk.GetBoneGlobalRest(footL).Origin;

                Vector3 restThighR = sk.GetBoneGlobalRest(thighR).Origin;
                Vector3 restCalfR = sk.GetBoneGlobalRest(calfR).Origin;
                Vector3 restFootR = sk.GetBoneGlobalRest(footR).Origin;

                Vector3 dirThighCalfL = (restCalfL - restThighL).Normalized();
                Vector3 dirCalfFootL = (restFootL - restCalfL).Normalized();
                Vector3 dirThighCalfR = (restCalfR - restThighR).Normalized();
                Vector3 dirCalfFootR = (restFootR - restCalfR).Normalized();

                float kneeFlexL = Mathf.RadToDeg(Mathf.Acos(Math.Clamp(dirThighCalfL.Dot(dirCalfFootL), -1f, 1f)));
                float kneeFlexR = Mathf.RadToDeg(Mathf.Acos(Math.Clamp(dirThighCalfR.Dot(dirCalfFootR), -1f, 1f)));

                // Symmetry check: compare Left vs Right (reflecting X)
                Vector3 reflectedThighR = new Vector3(-dirThighCalfR.X, dirThighCalfR.Y, dirThighCalfR.Z);
                float asymmetryThighDeg = Mathf.RadToDeg(Mathf.Acos(Math.Clamp(dirThighCalfL.Dot(reflectedThighR), -1f, 1f)));

                Log($"\n[Source Skeleton Node-Rest Pose Inspection]:");
                Log($"  Left Thigh->Calf dir:  {dirThighCalfL}");
                Log($"  Left Calf->Foot dir:   {dirCalfFootL}");
                Log($"  Left Knee Flexion:     {kneeFlexL:F2}°");
                Log($"  Right Thigh->Calf dir: {dirThighCalfR}");
                Log($"  Right Calf->Foot dir:  {dirCalfFootR}");
                Log($"  Right Knee Flexion:    {kneeFlexR:F2}°");
                Log($"  L/R Thigh Asymmetry:   {asymmetryThighDeg:F2}° {(asymmetryThighDeg > 5f ? "-> [DIRTY POSED ASYMMETRIC POSE DETECTED]" : "-> [NEUTRAL SYMMETRIC]")}");
            }

            // Step B: Retarget and measure per-frame metrics
            var autoMapping = HumanoidBoneMapper.AutoMap(sk, targetSkeleton);
            var (clipName, sourceClip) = loadResult.Animations.First();

            var options = new AnimationRetargeter.RetargetOptions
            {
                TargetFps = 30f,
                AutoGroundFeet = true,
                EnableFootIK = true,
                TargetAnimName = $"diag_{clipName}",
                SkeletonNodePath = "GeneralSkeleton"
            };

            var retargeted = AnimationRetargeter.Retarget(
                sk,
                sourceClip,
                targetSkeleton,
                autoMapping,
                options
            );

            if (retargeted == null)
            {
                Log($"FAILED: Retargeter returned null animation for {fileName}");
                return;
            }

            // Step C: Frame-by-frame analysis
            int tUpperL = targetSkeleton.FindBone("leg_upper_L");
            int tLowerL = targetSkeleton.FindBone("leg_lower_L");
            int tAnkleL = targetSkeleton.FindBone("ankle_L");

            int tUpperR = targetSkeleton.FindBone("leg_upper_R");
            int tLowerR = targetSkeleton.FindBone("leg_lower_R");
            int tAnkleR = targetSkeleton.FindBone("ankle_R");

            int frameCount = (int)(retargeted.Length * 30.0);
            Log($"\n[Frame-by-Frame Metrics ({frameCount} frames)]: ");

            float minKneeFlexL = 999f;
            float maxKneeFlexL = -999f;
            float minKneeFlexR = 999f;
            float maxKneeFlexR = -999f;
            float maxFrameJumpL = 0f;
            float maxFrameJumpR = 0f;
            int hyperextensionFramesL = 0;
            int hyperextensionFramesR = 0;

            float prevFlexL = 0f;
            float prevFlexR = 0f;

            int tBallL_diag = targetSkeleton.FindBone("ball_L");
            Vector3 restCharFwd_diag = new Vector3(0f, 0f, 1f);
            if (tBallL_diag != -1 && tAnkleL != -1)
            {
                Vector3 toe = targetSkeleton.GetBoneGlobalRest(tBallL_diag).Origin - targetSkeleton.GetBoneGlobalRest(tAnkleL).Origin;
                toe.Y = 0f;
                if (toe.LengthSquared() > 1e-4f) restCharFwd_diag = toe.Normalized();
            }
            int tPelvis_diag = targetSkeleton.FindBone("pelvis");
            Vector3 pelvLocalFwd_diag = tPelvis_diag != -1
                ? (targetSkeleton.GetBoneGlobalRest(tPelvis_diag).Basis.GetRotationQuaternion().Inverse() * restCharFwd_diag).Normalized()
                : new Vector3(0f, 0f, 1f);

            for (int f = 0; f < frameCount; f++)
            {
                double time = f / 30.0;
                int tPelvis = targetSkeleton.FindBone("pelvis");
                var (_, pelvRot) = EvaluateBoneModelTransform(targetSkeleton, retargeted, "GeneralSkeleton", tPelvis, time);
                Vector3 charFwd = pelvRot * pelvLocalFwd_diag;
                charFwd.Y = 0f;
                charFwd = charFwd.LengthSquared() > 1e-4f ? charFwd.Normalized() : restCharFwd_diag;

                Vector3 hipL = EvaluateBoneModelPos(targetSkeleton, retargeted, "GeneralSkeleton", tUpperL, time);
                Vector3 kneeL = EvaluateBoneModelPos(targetSkeleton, retargeted, "GeneralSkeleton", tLowerL, time);
                Vector3 ankleL = EvaluateBoneModelPos(targetSkeleton, retargeted, "GeneralSkeleton", tAnkleL, time);

                Vector3 hipR = EvaluateBoneModelPos(targetSkeleton, retargeted, "GeneralSkeleton", tUpperR, time);
                Vector3 kneeR = EvaluateBoneModelPos(targetSkeleton, retargeted, "GeneralSkeleton", tLowerR, time);
                Vector3 ankleR = EvaluateBoneModelPos(targetSkeleton, retargeted, "GeneralSkeleton", tAnkleR, time);

                // Signed Knee flexion:
                // Ankle - Hip line (d0). Knee should displace forward along character forward relative to d0.
                Vector3 d0L = ankleL - hipL;
                Vector3 u1L = (kneeL - hipL).Normalized();
                Vector3 u2L = (ankleL - kneeL).Normalized();
                float magFlexL = Mathf.RadToDeg(Mathf.Acos(Math.Clamp(u1L.Dot(u2L), -1f, 1f)));

                // Sign: does knee sit in front of the hip->ankle line relative to pelvis heading?
                Vector3 projKneeL = hipL + (d0L.Normalized()) * ((kneeL - hipL).Dot(d0L.Normalized()));
                Vector3 kneeOffsetL = kneeL - projKneeL;
                float signL = kneeOffsetL.Dot(charFwd) >= -0.01f ? 1f : -1f;
                float signedFlexL = magFlexL * signL;

                Vector3 d0R = ankleR - hipR;
                Vector3 u1R = (kneeR - hipR).Normalized();
                Vector3 u2R = (ankleR - kneeR).Normalized();
                float magFlexR = Mathf.RadToDeg(Mathf.Acos(Math.Clamp(u1R.Dot(u2R), -1f, 1f)));
                Vector3 projKneeR = hipR + (d0R.Normalized()) * ((kneeR - hipR).Dot(d0R.Normalized()));
                Vector3 kneeOffsetR = kneeR - projKneeR;
                float signR = kneeOffsetR.Dot(charFwd) >= -0.01f ? 1f : -1f;
                float signedFlexR = magFlexR * signR;

                if (signedFlexL < 0f) hyperextensionFramesL++;
                if (signedFlexR < 0f) hyperextensionFramesR++;

                if (signedFlexL < minKneeFlexL) minKneeFlexL = signedFlexL;
                if (signedFlexL > maxKneeFlexL) maxKneeFlexL = signedFlexL;
                if (signedFlexR < minKneeFlexR) minKneeFlexR = signedFlexR;
                if (signedFlexR > maxKneeFlexR) maxKneeFlexR = signedFlexR;

                if (f > 0)
                {
                    float jumpL = Math.Abs(signedFlexL - prevFlexL);
                    float jumpR = Math.Abs(signedFlexR - prevFlexR);
                    if (jumpL > maxFrameJumpL) maxFrameJumpL = jumpL;
                    if (jumpR > maxFrameJumpR) maxFrameJumpR = jumpR;
                }

                prevFlexL = signedFlexL;
                prevFlexR = signedFlexR;
            }

            Log($"  Left Knee Flexion Range:  [{minKneeFlexL:F2}°, {maxKneeFlexL:F2}°] (Hyperextensions: {hyperextensionFramesL}/{frameCount})");
            Log($"  Right Knee Flexion Range: [{minKneeFlexR:F2}°, {maxKneeFlexR:F2}°] (Hyperextensions: {hyperextensionFramesR}/{frameCount})");
            Log($"  Max Frame Jump (L):       {maxFrameJumpL:F2}°/frame {(maxFrameJumpL > 25f ? "-> [KNEE POP DETECTED]" : "-> [SMOOTH]")}");
            Log($"  Max Frame Jump (R):       {maxFrameJumpR:F2}°/frame {(maxFrameJumpR > 25f ? "-> [KNEE POP DETECTED]" : "-> [SMOOTH]")}");

            Log($"\nDiagnostic report written to: {reportFile}");
        }

        private static (Vector3 pos, Quaternion rot) EvaluateBoneModelTransform(
            Skeleton3D skeleton,
            Godot.Animation anim,
            string skeletonPrefix,
            int boneIdx,
            double time)
        {
            var chain = new List<int>();
            int curr = boneIdx;
            while (curr != -1)
            {
                chain.Add(curr);
                curr = skeleton.GetBoneParent(curr);
            }
            chain.Reverse();

            Quaternion gRot = Quaternion.Identity;
            Vector3 gPos = Vector3.Zero;

            foreach (int b in chain)
            {
                int p = skeleton.GetBoneParent(b);
                string bName = skeleton.GetBoneName(b);
                Transform3D rest = skeleton.GetBoneRest(b);

                int rotTrack = anim != null ? anim.FindTrack($"{skeletonPrefix}:{bName}", Godot.Animation.TrackType.Rotation3D) : -1;
                int posTrack = anim != null ? anim.FindTrack($"{skeletonPrefix}:{bName}", Godot.Animation.TrackType.Position3D) : -1;

                Quaternion lRot = rotTrack != -1
                    ? anim.RotationTrackInterpolate(rotTrack, time).Normalized()
                    : rest.Basis.GetRotationQuaternion().Normalized();

                Vector3 lPos = posTrack != -1
                    ? anim.PositionTrackInterpolate(posTrack, time)
                    : rest.Origin;

                if (p == -1)
                {
                    gRot = lRot;
                    gPos = lPos;
                }
                else
                {
                    gPos = gPos + gRot * lPos;
                    gRot = (gRot * lRot).Normalized();
                }
            }

            return (gPos, gRot);
        }

        private static Vector3 EvaluateBoneModelPos(
            Skeleton3D skeleton,
            Godot.Animation anim,
            string skeletonPrefix,
            int boneIdx,
            double time)
        {
            return EvaluateBoneModelTransform(skeleton, anim, skeletonPrefix, boneIdx, time).pos;
        }

        private static (Node3D root, Skeleton3D skeleton) BuildMockDeadlockHero()
        {
            var heroRoot = new Node3D { Name = "Hero_Holliday" };
            var skeleton = new Skeleton3D { Name = "GeneralSkeleton" };
            heroRoot.AddChild(skeleton);

            int AddBone(string name, int parent, Vector3 localPos, Quaternion localRot)
            {
                int idx = skeleton.GetBoneCount();
                skeleton.AddBone(name);
                if (parent != -1) skeleton.SetBoneParent(idx, parent);
                skeleton.SetBoneRest(idx, new Transform3D(new Basis(localRot), localPos));
                return idx;
            }

            // Spine chain (Deadlock: 4 spine bones in A-pose)
            int root = AddBone("root", -1, Vector3.Zero, Quaternion.Identity);
            int pelvis = AddBone("pelvis", root, new Vector3(0, 0.95f, 0), Quaternion.Identity);
            int spine0 = AddBone("spine_0", pelvis, new Vector3(0, 0.12f, 0), Quaternion.Identity);
            int spine1 = AddBone("spine_1", spine0, new Vector3(0, 0.12f, 0), Quaternion.Identity);
            int spine2 = AddBone("spine_2", spine1, new Vector3(0, 0.14f, 0), Quaternion.Identity);
            int spine3 = AddBone("spine_3", spine2, new Vector3(0, 0.14f, 0), Quaternion.Identity);
            int neck0 = AddBone("neck_0", spine3, new Vector3(0, 0.10f, 0), Quaternion.Identity);
            int head = AddBone("head", neck0, new Vector3(0, 0.12f, 0), Quaternion.Identity);

            // Left Arm (Source 2 Deadlock A-Pose: upper arm tilted ~35° down)
            Quaternion leftAPoseArmRot = new Quaternion(Vector3.Back, Mathf.DegToRad(35f));
            int clavL = AddBone("clavicle_L", spine3, new Vector3(0.08f, 0.05f, 0.02f), Quaternion.Identity);
            int armUpperL = AddBone("arm_upper_L", clavL, new Vector3(0.12f, 0f, 0f), leftAPoseArmRot);
            int armLowerL = AddBone("arm_lower_L", armUpperL, new Vector3(0.28f, 0f, 0f), Quaternion.Identity);
            int twistL0 = AddBone("arm_lower_L_TWIST", armLowerL, new Vector3(0.09f, 0f, 0f), Quaternion.Identity);
            int twistL1 = AddBone("arm_lower_L_TWIST1", armLowerL, new Vector3(0.18f, 0f, 0f), Quaternion.Identity);
            int handL = AddBone("hand_L", armLowerL, new Vector3(0.25f, 0f, 0f), Quaternion.Identity);

            // Left Hand Fingers with metacarpals and ends
            void AddFingerL(string prefix, Vector3 metaOffset, Vector3 f0Offset)
            {
                int meta = AddBone($"finger_{prefix}_meta_L", handL, metaOffset, Quaternion.Identity);
                int f0 = AddBone($"finger_{prefix}_0_L", meta, f0Offset, Quaternion.Identity);
                int f1 = AddBone($"finger_{prefix}_1_L", f0, new Vector3(0.035f, 0f, 0f), Quaternion.Identity);
                int f2 = AddBone($"finger_{prefix}_2_L", f1, new Vector3(0.025f, 0f, 0f), Quaternion.Identity);
                int end = AddBone($"finger_{prefix}_end_L", f2, new Vector3(0.020f, 0f, 0f), Quaternion.Identity);
            }
            AddFingerL("thumb", new Vector3(0.015f, -0.01f, 0.015f), new Vector3(0.025f, -0.01f, 0.015f));
            AddFingerL("index", new Vector3(0.035f, 0.005f, 0.01f), new Vector3(0.045f, 0.005f, 0.01f));
            AddFingerL("middle", new Vector3(0.035f, 0.005f, 0.0f), new Vector3(0.045f, 0.005f, 0.0f));
            AddFingerL("ring", new Vector3(0.030f, 0.005f, -0.01f), new Vector3(0.045f, 0.005f, -0.01f));
            AddFingerL("pinky", new Vector3(0.025f, 0.005f, -0.02f), new Vector3(0.045f, 0.005f, -0.02f));

            // Right Arm
            Quaternion rightAPoseArmRot = new Quaternion(Vector3.Back, Mathf.DegToRad(-35f));
            int clavR = AddBone("clavicle_R", spine3, new Vector3(-0.08f, 0.05f, 0.02f), Quaternion.Identity);
            int armUpperR = AddBone("arm_upper_R", clavR, new Vector3(-0.12f, 0f, 0f), rightAPoseArmRot);
            int armLowerR = AddBone("arm_lower_R", armUpperR, new Vector3(-0.28f, 0f, 0f), Quaternion.Identity);
            int twistR0 = AddBone("arm_lower_R_TWIST", armLowerR, new Vector3(-0.09f, 0f, 0f), Quaternion.Identity);
            int twistR1 = AddBone("arm_lower_R_TWIST1", armLowerR, new Vector3(-0.18f, 0f, 0f), Quaternion.Identity);
            int handR = AddBone("hand_R", armLowerR, new Vector3(-0.25f, 0f, 0f), Quaternion.Identity);

            // Right Hand Fingers with metacarpals and ends
            void AddFingerR(string prefix, Vector3 metaOffset, Vector3 f0Offset)
            {
                int meta = AddBone($"finger_{prefix}_meta_R", handR, metaOffset, Quaternion.Identity);
                int f0 = AddBone($"finger_{prefix}_0_R", meta, f0Offset, Quaternion.Identity);
                int f1 = AddBone($"finger_{prefix}_1_R", f0, new Vector3(-0.035f, 0f, 0f), Quaternion.Identity);
                int f2 = AddBone($"finger_{prefix}_2_R", f1, new Vector3(-0.025f, 0f, 0f), Quaternion.Identity);
                int end = AddBone($"finger_{prefix}_end_R", f2, new Vector3(-0.020f, 0f, 0f), Quaternion.Identity);
            }
            AddFingerR("thumb", new Vector3(-0.015f, -0.01f, 0.015f), new Vector3(-0.025f, -0.01f, 0.015f));
            AddFingerR("index", new Vector3(-0.035f, 0.005f, 0.01f), new Vector3(-0.045f, 0.005f, 0.01f));
            AddFingerR("middle", new Vector3(-0.035f, 0.005f, 0.0f), new Vector3(-0.045f, 0.005f, 0.0f));
            AddFingerR("ring", new Vector3(-0.030f, 0.005f, -0.01f), new Vector3(-0.045f, 0.005f, -0.01f));
            AddFingerR("pinky", new Vector3(-0.025f, 0.005f, -0.02f), new Vector3(-0.045f, 0.005f, -0.02f));

            // Left Leg
            int legUpperL = AddBone("leg_upper_L", pelvis, new Vector3(0.10f, -0.05f, 0f), Quaternion.Identity);
            int legLowerL = AddBone("leg_lower_L", legUpperL, new Vector3(0f, -0.42f, 0f), Quaternion.Identity);
            int ankleL = AddBone("ankle_L", legLowerL, new Vector3(0f, -0.40f, 0f), Quaternion.Identity);
            int ballL = AddBone("ball_L", ankleL, new Vector3(0f, -0.06f, 0.14f), Quaternion.Identity);
            int ballEndL = AddBone("ball_end_L", ballL, new Vector3(0f, 0f, 0.06f), Quaternion.Identity);

            // Right Leg
            int legUpperR = AddBone("leg_upper_R", pelvis, new Vector3(-0.10f, -0.05f, 0f), Quaternion.Identity);
            int legLowerR = AddBone("leg_lower_R", legUpperR, new Vector3(0f, -0.42f, 0f), Quaternion.Identity);
            int ankleR = AddBone("ankle_R", legLowerR, new Vector3(0f, -0.40f, 0f), Quaternion.Identity);
            int ballR = AddBone("ball_R", ankleR, new Vector3(0f, -0.06f, 0.14f), Quaternion.Identity);
            int ballEndR = AddBone("ball_end_R", ballR, new Vector3(0f, 0f, 0.06f), Quaternion.Identity);

            // Torso MeshInstance3D
            var meshInst = new MeshInstance3D { Name = "body" };
            var st = new SurfaceTool();
            st.Begin(Mesh.PrimitiveType.Triangles);
            for (int ySlice = 0; ySlice < 6; ySlice++)
            {
                float y = 0.95f + ySlice * 0.10f;
                float rx = 0.22f;
                float rz = 0.20f;
                for (int ang = 0; ang < 12; ang++)
                {
                    float theta = ang * (Mathf.Tau / 12f);
                    st.AddVertex(new Vector3(Mathf.Cos(theta) * rx, y, Mathf.Sin(theta) * rz));
                }
            }
            st.AddIndex(0); st.AddIndex(1); st.AddIndex(2);
            meshInst.Mesh = st.Commit();
            heroRoot.AddChild(meshInst);

            return (heroRoot, skeleton);
        }

        private static Skeleton3D FindSkeleton(Node node)
        {
            if (node == null) return null;
            if (node is Skeleton3D sk) return sk;
            foreach (Node child in node.GetChildren())
            {
                var s = FindSkeleton(child);
                if (s != null) return s;
            }
            return null;
        }
    }
}
