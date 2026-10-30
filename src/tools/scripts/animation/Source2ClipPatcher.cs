using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using SteamDatabase.ValvePak;
using ValveKeyValue;
using ValveResourceFormat;
using ValveResourceFormat.IO;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.ResourceTypes.ModelAnimation2;
using ValveResourceFormat.Serialization.KeyValues;

namespace DeadlockPlayground.Animation
{
    /// <summary>
    /// Binary encoder and in-place patcher for Source 2 AnimGraph 2 animation clips (.vnmclip_c).
    /// Serializes modified Godot skeletal animation tracks directly into native Source 2 format
    /// for packaging into playable Deadlock .vpk mods.
    /// </summary>
    public static class Source2ClipPatcher
    {
        // Source 2 (Z-up, inches) <-> Godot / glTF (Y-up, meters)
        private const float InchesToMeters = 0.0254f;
        private const float MetersToInches = 1.0f / InchesToMeters;
        private static readonly System.Numerics.Quaternion GltfToSourceRotation = new(0.5f, 0.5f, 0.5f, 0.5f);

        private static readonly float ValueRangeMin = -1.0f / MathF.Sqrt(2.0f);
        private static readonly float ValueRangeMax = 1.0f / MathF.Sqrt(2.0f);
        private static readonly float ValueRangeLength = ValueRangeMax - ValueRangeMin;

        public class PatchOptions
        {
            public float Fps { get; set; } = 30f;
            public bool IsLooping { get; set; } = true;
            public bool IsAdditive { get; set; } = false;
        }

        public static byte[] CreateOrPatchClip(
            byte[] existingResourceBytes,
            Godot.Animation godotAnim,
            Godot.Skeleton3D skeleton,
            string animName,
            float fps = 30f,
            string targetVpkPath = null,
            string heroId = null,
            IFileLoader fileLoader = null)
        {
            if (godotAnim == null || skeleton == null) return null;

            var options = new PatchOptions
            {
                Fps = fps > 0 ? fps : 30f,
                IsLooping = godotAnim.LoopMode != Godot.Animation.LoopModeEnum.None,
                IsAdditive = godotAnim.HasMeta("is_additive") && godotAnim.GetMeta("is_additive").AsBool()
            };

            Package openedPkg = null;
            try
            {
                if (fileLoader == null || existingResourceBytes == null || existingResourceBytes.Length == 0)
                {
                    try
                    {
                        var pm = new GamePathManager();
                        pm.LoadConfig();
                        string deadlockPath = pm.CurrentGamePath;
                        if (!string.IsNullOrEmpty(deadlockPath))
                        {
                            string vpkPath = Path.Combine(deadlockPath, "game", "citadel", "pak01_dir.vpk");
                            if (File.Exists(vpkPath))
                            {
                                openedPkg = new Package();
                                openedPkg.Read(vpkPath);
                                fileLoader ??= new GameFileLoader(openedPkg, "");

                                if (existingResourceBytes == null || existingResourceBytes.Length == 0)
                                {
                                    // 1. Try direct targetVpkPath if specified
                                    if (!string.IsNullOrEmpty(targetVpkPath))
                                    {
                                        string cleanPath = targetVpkPath.Replace('\\', '/').TrimStart('/');
                                        var entry = openedPkg.FindEntry(cleanPath);
                                        if (entry != null)
                                        {
                                            openedPkg.ReadEntry(entry, out byte[] data);
                                            existingResourceBytes = data;
                                        }
                                    }

                                    // 2. Query pkg.Entries["vnmclip_c"] for matching clip
                                    if (existingResourceBytes == null && openedPkg.Entries.TryGetValue("vnmclip_c", out var clips))
                                    {
                                        string cleanSearch = DeadlockPlayground.Tools.DeadlockAnimLoader.SanitizePoseName(animName).ToLowerInvariant();
                                        string heroSearch = !string.IsNullOrEmpty(heroId) ? heroId.ToLowerInvariant() : "";

                                        // A. Exact file name match in hero folder
                                        var heroClips = !string.IsNullOrEmpty(heroSearch)
                                            ? clips.Where(c => c.DirectoryName.ToLowerInvariant().Contains(heroSearch)).ToList()
                                            : clips;

                                        var exact = heroClips.FirstOrDefault(c => string.Equals(c.FileName, cleanSearch, StringComparison.OrdinalIgnoreCase))
                                                 ?? clips.FirstOrDefault(c => string.Equals(c.FileName, cleanSearch, StringComparison.OrdinalIgnoreCase));

                                        if (exact != null)
                                        {
                                            openedPkg.ReadEntry(exact, out byte[] data);
                                            existingResourceBytes = data;
                                        }
                                        else
                                        {
                                            // B. Fuzzy prefix/suffix match (e.g. weapon_{animName}, out_of_combat_{animName})
                                            var fuzzy = heroClips.FirstOrDefault(c =>
                                                c.FileName.EndsWith($"_{cleanSearch}", StringComparison.OrdinalIgnoreCase) ||
                                                c.FileName.StartsWith($"{cleanSearch}_", StringComparison.OrdinalIgnoreCase) ||
                                                c.FileName.Contains(cleanSearch, StringComparison.OrdinalIgnoreCase));

                                            if (fuzzy != null)
                                            {
                                                openedPkg.ReadEntry(fuzzy, out byte[] data);
                                                existingResourceBytes = data;
                                            }
                                        }

                                        // C. Fallback: If still not found (e.g. sequence was embedded in .vmdl_c),
                                        // use any existing hero clip as a valid Source 2 AnimGraph 2 binary template!
                                        if (existingResourceBytes == null && heroClips.Count > 0)
                                        {
                                            var templateClip = heroClips.FirstOrDefault(c => !c.FileName.Contains("lean") && !c.FileName.Contains("aim"))
                                                            ?? heroClips.First();
                                            openedPkg.ReadEntry(templateClip, out byte[] data);
                                            existingResourceBytes = data;
                                            Godot.GD.Print($"[Source2ClipPatcher] Used '{templateClip.FileName}' as structural template for '{animName}'.");
                                        }
                                        else if (existingResourceBytes == null && clips.Count > 0)
                                        {
                                            var fallback = clips.First();
                                            openedPkg.ReadEntry(fallback, out byte[] data);
                                            existingResourceBytes = data;
                                            Godot.GD.Print($"[Source2ClipPatcher] Used generic clip '{fallback.FileName}' as structural template for '{animName}'.");
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Godot.GD.Print($"[Source2ClipPatcher] Notice: Base VPK lookup for '{animName}': {ex.Message}");
                    }
                }

                if (existingResourceBytes == null || existingResourceBytes.Length == 0)
                {
                    Godot.GD.PrintErr($"[Source2ClipPatcher] Failed to resolve base .vnmclip_c template for '{animName}'.");
                    return null;
                }

                return PatchClipResource(existingResourceBytes, godotAnim, skeleton, options, animName, fileLoader);
            }
            finally
            {
                openedPkg?.Dispose();
            }
        }

        /// <summary>
        /// Encodes a modified Godot Animation into a native Source 2 compiled .vnmclip_c resource byte array.
        /// If a base .vnmclip_c is provided (e.g. cloned from pak01_dir.vpk), metadata, events, float curves,
        /// and model sampling chains are preserved while the pose track data is replaced.
        /// </summary>
        public static byte[] PatchClipResource(
            byte[] originalVnmclipBytes,
            Godot.Animation godotAnim,
            Godot.Skeleton3D skeleton,
            PatchOptions options = null,
            string animName = null,
            IFileLoader fileLoader = null)
        {
            if (godotAnim == null || skeleton == null) return null;
            options ??= new PatchOptions();

            // 1. Read existing resource to preserve structure and blocks
            using var inMs = new MemoryStream(originalVnmclipBytes);
            using var resource = new Resource();
            resource.Read(inMs);

            var dataBlock = resource.DataBlock as BinaryKV3;
            if (dataBlock == null)
            {
                throw new InvalidDataException("Provided resource DATA block is not BinaryKV3.");
            }

            var rootObj = dataBlock.Data.Root;
            float fps = options.Fps > 0 ? options.Fps : 30f;
            int numFrames = Math.Max(1, (int)Math.Round(godotAnim.Length * fps) + 1);
            float duration = (float)godotAnim.Length;

            if (!string.IsNullOrEmpty(animName))
            {
                SetOrReplace(rootObj, "m_clipName", animName);
            }

            // 2. Resolve target skeleton (from m_skeleton if specified in .vnmclip_c)
            ValveResourceFormat.ResourceTypes.ModelAnimation.Skeleton targetSkel = null;
            if (fileLoader != null && rootObj.ContainsKey("m_skeleton"))
            {
                string secSkelPath = rootObj["m_skeleton"].ToString();
                try
                {
                    targetSkel = ValveResourceFormat.ResourceTypes.ModelAnimation.Skeleton.FromSkeletonResource(fileLoader, secSkelPath);
                    if (targetSkel != null)
                    {
                        Godot.GD.Print($"[Source2ClipPatcher] Resolved target skeleton '{secSkelPath}' with {targetSkel.Bones.Length} bones.");
                    }
                }
                catch (Exception ex)
                {
                    Godot.GD.PrintErr($"[Source2ClipPatcher] Failed to load target skeleton '{secSkelPath}': {ex.Message}");
                }
            }

            // Sample skeleton bones across all frames matching target skeleton ordering if available
            int boneCount = targetSkel != null && targetSkel.Bones.Length > 0
                ? targetSkel.Bones.Length
                : skeleton.GetBoneCount();
            var targetBones = targetSkel != null && targetSkel.Bones.Length > 0 ? targetSkel.Bones : null;
            var boneTracks = new (int PosTrack, int RotTrack)[boneCount];

            for (int b = 0; b < boneCount; b++)
            {
                string bName = targetBones != null ? targetBones[b].Name : skeleton.GetBoneName(b);
                int pTrack = -1;
                int rTrack = -1;

                for (int t = 0; t < godotAnim.GetTrackCount(); t++)
                {
                    string path = godotAnim.TrackGetPath(t).ToString();
                    if (!path.EndsWith($":{bName}", StringComparison.OrdinalIgnoreCase)) continue;

                    var type = godotAnim.TrackGetType(t);
                    if (type == Godot.Animation.TrackType.Position3D) pTrack = t;
                    else if (type == Godot.Animation.TrackType.Rotation3D) rTrack = t;
                }
                boneTracks[b] = (pTrack, rTrack);
            }

            // Pose samples in Source 2 coordinate space [frame, bone]
            var samplePos = new System.Numerics.Vector3[numFrames, boneCount];
            var sampleRot = new System.Numerics.Quaternion[numFrames, boneCount];

            for (int f = 0; f < numFrames; f++)
            {
                double time = numFrames > 1 ? (f / (double)fps) : 0.0;
                if (time > godotAnim.Length) time = godotAnim.Length;

                for (int b = 0; b < boneCount; b++)
                {
                    string bName = targetBones != null ? targetBones[b].Name : skeleton.GetBoneName(b);
                    int gBoneIdx = skeleton.FindBone(bName);
                    var (pTrack, rTrack) = boneTracks[b];

                    bool isPelvis = bName.Equals("pelvis", StringComparison.OrdinalIgnoreCase);
                    bool isRootMotion = bName.Equals("root_motion", StringComparison.OrdinalIgnoreCase) ||
                                        bName.Equals("root", StringComparison.OrdinalIgnoreCase);

                    System.Numerics.Vector3 sPos;
                    System.Numerics.Quaternion sRot;

                    if (gBoneIdx != -1)
                    {
                        Godot.Vector3 gPos = skeleton.GetBoneRest(gBoneIdx).Origin;
                        Godot.Quaternion gRot = skeleton.GetBoneRest(gBoneIdx).Basis.GetRotationQuaternion();

                        if (pTrack != -1 && godotAnim.TrackGetKeyCount(pTrack) > 0)
                        {
                            gPos = godotAnim.PositionTrackInterpolate(pTrack, time);
                        }
                        if (rTrack != -1 && godotAnim.TrackGetKeyCount(rTrack) > 0)
                        {
                            gRot = godotAnim.RotationTrackInterpolate(rTrack, time).Normalized();
                        }

                        if (isPelvis)
                        {
                            // Pelvis in Godot (Y-up, meters) -> Source 2 (Z-up, inches).
                            // In Godot, pelvis has SourceToGltfRotation applied during import to stand upright.
                            // During export, GltfToSourceRotation converts Y-up waist height and orientation back to Source 2 Z-up!
                            sPos = new System.Numerics.Vector3(gPos.X, gPos.Y, gPos.Z) * MetersToInches;
                            sPos = System.Numerics.Vector3.Transform(sPos, GltfToSourceRotation);
                            sRot = new System.Numerics.Quaternion(gRot.X, gRot.Y, gRot.Z, gRot.W);
                            sRot = System.Numerics.Quaternion.Normalize(GltfToSourceRotation * sRot);
                        }
                        else if (isRootMotion)
                        {
                            // In Source 2 AnimGraph 2 (.vnmskel), root_motion is the top-level ground reference frame (Z-up, X-forward).
                            // Its native rest orientation in Source 2 is Identity (0, 0, 0, 1).
                            // All character bones (including pelvis and IK targets) are parented to root_motion.
                            // Applying GltfToSourceRotation to root_motion would pitch the ENTIRE secondary skeleton 90 degrees horizontal!
                            if (pTrack == -1 || (MathF.Abs(gPos.X) < 0.001f && MathF.Abs(gPos.Z) < 0.001f))
                            {
                                sPos = System.Numerics.Vector3.Zero;
                            }
                            else
                            {
                                // Godot (-Z forward, +X right, +Y up) -> Source 2 (+X forward, -Y right, +Z up)
                                sPos = new System.Numerics.Vector3(-gPos.Z, -gPos.X, gPos.Y) * MetersToInches;
                            }

                            if (rTrack == -1 || (MathF.Abs(gRot.X) < 0.001f && MathF.Abs(gRot.Z) < 0.001f && MathF.Abs(gRot.Y) < 0.001f))
                            {
                                sRot = System.Numerics.Quaternion.Identity;
                            }
                            else
                            {
                                // Godot yaw around Y to Source 2 yaw around Z
                                sRot = new System.Numerics.Quaternion(0f, 0f, -gRot.Y, gRot.W);
                            }
                        }
                        else
                        {
                            // Standard child bone: local rotation & position relative to parent
                            sPos = new System.Numerics.Vector3(gPos.X, gPos.Y, gPos.Z) * MetersToInches;
                            sRot = new System.Numerics.Quaternion(gRot.X, gRot.Y, gRot.Z, gRot.W);
                        }
                    }
                    else if (targetBones != null)
                    {
                        // Bone exists in target skeleton (e.g. vnmskel) but is not keyed in Godot model
                        sPos = targetBones[b].Position;
                        sRot = targetBones[b].Angle;
                    }
                    else
                    {
                        sPos = System.Numerics.Vector3.Zero;
                        sRot = System.Numerics.Quaternion.Identity;
                    }

                    samplePos[f, b] = sPos;
                    sampleRot[f, b] = sRot;
                }
            }

            // 2b. Automated IK Target Constraint Solver
            // Source 2 AnimGraph 2 uses Two-Bone IK nodes for feet and hands driven by IK target bones
            // (e.g. leg_L_IKTARGET -> ankle_L, arm_L_IKTARGET -> hand_L).
            // When mod animations only author FK body bones, unkeyed IK targets remain frozen at rest floor height,
            // causing in-game IK to drag and twist feet/hands during jumps and air animations.
            // We solve and bake world-space tracking constraints into any unkeyed IK targets.
            ApplyAutomatedIkConstraints(samplePos, sampleRot, numFrames, boneCount, targetBones, skeleton, boneTracks);

            // 3. Compute compression settings per bone
            var trackCompressionSettings = new KVObject[boneCount];
            var isRotStatic = new bool[boneCount];
            var isTransStatic = new bool[boneCount];
            var transStart = new System.Numerics.Vector3[boneCount];
            var transLength = new System.Numerics.Vector3[boneCount];
            var constRot = new System.Numerics.Quaternion[boneCount];

            for (int b = 0; b < boneCount; b++)
            {
                var minP = samplePos[0, b];
                var maxP = samplePos[0, b];
                bool rotChanged = false;
                var firstRot = sampleRot[0, b];

                for (int f = 1; f < numFrames; f++)
                {
                    minP = System.Numerics.Vector3.Min(minP, samplePos[f, b]);
                    maxP = System.Numerics.Vector3.Max(maxP, samplePos[f, b]);

                    float dot = Math.Abs(System.Numerics.Quaternion.Dot(firstRot, sampleRot[f, b]));
                    if (dot < 0.99999f) rotChanged = true;
                }

                var lenP = maxP - minP;
                bool transChanged = lenP.Length() > 0.001f;

                isTransStatic[b] = !transChanged;
                isRotStatic[b] = !rotChanged;
                constRot[b] = firstRot;

                transStart[b] = minP;
                transLength[b] = new System.Numerics.Vector3(
                    lenP.X > 0.0001f ? lenP.X : 1.0f,
                    lenP.Y > 0.0001f ? lenP.Y : 1.0f,
                    lenP.Z > 0.0001f ? lenP.Z : 1.0f
                );

                var setting = KVObject.Collection();
                setting.Add("m_bIsRotationStatic", isRotStatic[b]);
                setting.Add("m_bIsTranslationStatic", isTransStatic[b]);
                setting.Add("m_bIsScaleStatic", true);

                var rX = KVObject.Collection();
                rX.Add("m_flRangeStart", transStart[b].X);
                rX.Add("m_flRangeLength", transChanged ? transLength[b].X : 0f);
                setting.Add("m_translationRangeX", rX);

                var rY = KVObject.Collection();
                rY.Add("m_flRangeStart", transStart[b].Y);
                rY.Add("m_flRangeLength", transChanged ? transLength[b].Y : 0f);
                setting.Add("m_translationRangeY", rY);

                var rZ = KVObject.Collection();
                rZ.Add("m_flRangeStart", transStart[b].Z);
                rZ.Add("m_flRangeLength", transChanged ? transLength[b].Z : 0f);
                setting.Add("m_translationRangeZ", rZ);

                var sR = KVObject.Collection();
                sR.Add("m_flRangeStart", 1.0f);
                sR.Add("m_flRangeLength", 0f);
                setting.Add("m_scaleRange", sR);

                var cRot = KVObject.Array();
                cRot.Add(constRot[b].X);
                cRot.Add(constRot[b].Y);
                cRot.Add(constRot[b].Z);
                cRot.Add(constRot[b].W);
                setting.Add("m_constantRotation", cRot);

                trackCompressionSettings[b] = setting;
            }

            // 4. Encode frames into compressed ushort buffer
            var compressedPoseDataUshorts = new List<ushort>();
            var compressedPoseOffsets = new long[numFrames];

            for (int f = 0; f < numFrames; f++)
            {
                compressedPoseOffsets[f] = compressedPoseDataUshorts.Count;

                for (int b = 0; b < boneCount; b++)
                {
                    // Rotation
                    if (!isRotStatic[b])
                    {
                        var (u0, u1, u2) = EncodeQuaternion(sampleRot[f, b]);
                        compressedPoseDataUshorts.Add(u0);
                        compressedPoseDataUshorts.Add(u1);
                        compressedPoseDataUshorts.Add(u2);
                    }

                    // Translation
                    if (!isTransStatic[b])
                    {
                        var (tx, ty, tz) = EncodeTranslation(samplePos[f, b], transStart[b], transLength[b]);
                        compressedPoseDataUshorts.Add(tx);
                        compressedPoseDataUshorts.Add(ty);
                        compressedPoseDataUshorts.Add(tz);
                    }
                }
            }

            byte[] poseBytes = new byte[compressedPoseDataUshorts.Count * sizeof(ushort)];
            Buffer.BlockCopy(compressedPoseDataUshorts.ToArray(), 0, poseBytes, 0, poseBytes.Length);

            // 5. Update KVObject properties
            SetOrReplace(rootObj, "m_nNumFrames", numFrames);
            SetOrReplace(rootObj, "m_flDuration", duration);
            SetOrReplace(rootObj, "m_bIsAdditive", options.IsAdditive);
            SetOrReplace(rootObj, "m_trackCompressionSettings", KVObject.Array(trackCompressionSettings));

            var offsetsArr = KVObject.Array(compressedPoseOffsets.Length);
            foreach (var off in compressedPoseOffsets)
            {
                offsetsArr.Add((long)off);
            }
            SetOrReplace(rootObj, "m_compressedPoseOffsets", offsetsArr);
            SetOrReplace(rootObj, "m_compressedPoseData", KVObject.Blob(poseBytes));

            // 6. Serialize BinaryKV3 DATA block
            using var dataMs = new MemoryStream();
            dataBlock.Serialize(dataMs);
            byte[] serializedDataBytes = dataMs.ToArray();

            // 7. Re-assemble final compiled Resource bytes
            using var outMs = new MemoryStream();
            using var writer = new BinaryWriter(outMs);

            // Re-read header info from original bytes
            using var origReader = new BinaryReader(new MemoryStream(originalVnmclipBytes));
            uint origFileSize = origReader.ReadUInt32();
            ushort headerVersion = origReader.ReadUInt16();
            ushort version = origReader.ReadUInt16();
            uint blockOffset = origReader.ReadUInt32();
            uint blockCount = origReader.ReadUInt32();

            // Calculate new file size and offsets
            // Header (16 bytes) + BlockTable (1 * 12 bytes = 12 bytes) + DATA block
            uint headerSize = 16u;
            uint blockTableSize = 12u; // 1 block entry: DATA
            uint dataStartOffset = headerSize + blockTableSize;

            uint totalFileSize = dataStartOffset + (uint)serializedDataBytes.Length;

            // Write Resource Header
            writer.Write(totalFileSize);
            writer.Write(headerVersion);
            writer.Write(version);
            writer.Write(8u); // blockOffset to table (8 bytes after this field)
            writer.Write(1u); // We emit 1 block (DATA)

            // Block Table Entry for DATA
            writer.Write((uint)BlockType.DATA);
            writer.Write(8u); // Offset relative to the start of this field to the data (after 4-byte Size field)
            writer.Write((uint)serializedDataBytes.Length);

            // Write serialized DATA block
            writer.Write(serializedDataBytes);

            byte[] result = outMs.ToArray();

            // Self-verify that VRF can parse the generated Resource cleanly
            try
            {
                using var verifyMs = new MemoryStream(result);
                using var verifyRes = new Resource();
                verifyRes.Read(verifyMs);
                if (verifyRes.DataBlock is not BinaryKV3)
                {
                    Godot.GD.PrintErr("[Source2ClipPatcher] Warning: Synthesized resource DataBlock is not BinaryKV3.");
                }
            }
            catch (Exception ex)
            {
                Godot.GD.PrintErr($"[Source2ClipPatcher] Verification failed for patched clip: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// Encodes a normalized quaternion using Source 2's Smallest-Three 48-bit packing.
        /// </summary>
        public static (ushort u0, ushort u1, ushort u2) EncodeQuaternion(System.Numerics.Quaternion q)
        {
            q = System.Numerics.Quaternion.Normalize(q);

            float[] c = [q.X, q.Y, q.Z, q.W];
            int largestIdx = 0;
            float maxAbs = Math.Abs(c[0]);

            for (int i = 1; i < 4; i++)
            {
                float a = Math.Abs(c[i]);
                if (a > maxAbs)
                {
                    maxAbs = a;
                    largestIdx = i;
                }
            }

            // Force largest component positive (Q and -Q represent identical rotations)
            if (c[largestIdx] < 0f)
            {
                c[0] = -c[0];
                c[1] = -c[1];
                c[2] = -c[2];
                c[3] = -c[3];
            }

            // Extract the other three components in declaration order
            float a0, a1, a2;
            switch (largestIdx)
            {
                case 0: a0 = c[1]; a1 = c[2]; a2 = c[3]; break;
                case 1: a0 = c[0]; a1 = c[2]; a2 = c[3]; break;
                case 2: a0 = c[0]; a1 = c[1]; a2 = c[3]; break;
                default: a0 = c[0]; a1 = c[1]; a2 = c[2]; break;
            }

            // Quantize to 15-bit UNORM over [-1/sqrt(2), 1/sqrt(2)]
            ushort u0 = Quantize15(a0);
            ushort u1 = Quantize15(a1);
            ushort u2 = Quantize15(a2);

            // Pack largest component index into bit 15 of u0 and u1
            // Decoding expects: largestValueIndex = (data[0] >> 14 & 2) | (data[1] >> 15)
            if ((largestIdx & 2) != 0) u0 |= 0x8000;
            if ((largestIdx & 1) != 0) u1 |= 0x8000;

            return (u0, u1, u2);
        }

        private static ushort Quantize15(float val)
        {
            float norm = (val - ValueRangeMin) / ValueRangeLength;
            return (ushort)Math.Clamp((int)MathF.Round(norm * 0x7FFF), 0, 0x7FFF);
        }

        public static (ushort tx, ushort ty, ushort tz) EncodeTranslation(
            System.Numerics.Vector3 pos,
            System.Numerics.Vector3 rangeStart,
            System.Numerics.Vector3 rangeLength)
        {
            ushort tx = (ushort)Math.Clamp((int)MathF.Round(((pos.X - rangeStart.X) / rangeLength.X) * 0xFFFF), 0, 0xFFFF);
            ushort ty = (ushort)Math.Clamp((int)MathF.Round(((pos.Y - rangeStart.Y) / rangeLength.Y) * 0xFFFF), 0, 0xFFFF);
            ushort tz = (ushort)Math.Clamp((int)MathF.Round(((pos.Z - rangeStart.Z) / rangeLength.Z) * 0xFFFF), 0, 0xFFFF);
            return (tx, ty, tz);
        }

        private static void SetOrReplace(KVObject obj, string key, object val)
        {
            if (obj.ContainsKey(key)) obj.Remove(key);
            if (val is KVObject kv) obj.Add(key, kv);
            else if (val is int i) obj.Add(key, i);
            else if (val is long l) obj.Add(key, l);
            else if (val is float f) obj.Add(key, f);
            else if (val is double d) obj.Add(key, d);
            else if (val is bool b) obj.Add(key, b);
            else if (val is string s) obj.Add(key, s);
        }

        private static readonly (string IkBone, string TargetBone)[] IkConstraintPairs = new[]
        {
            ("leg_L_IKTARGET", "ankle_L"),
            ("leg_R_IKTARGET", "ankle_R"),
            ("arm_L_IKTARGET", "hand_L"),
            ("arm_R_IKTARGET", "hand_R"),
            ("weaponHand_L", "hand_L"),
            ("weaponHand_R", "hand_R"),
        };

        /// <summary>
        /// Solves and bakes world-space forward kinematics from FK limb bones into corresponding IK target bones
        /// (e.g. leg_L_IKTARGET -> ankle_L, arm_L_IKTARGET -> hand_L, weaponHand_L -> hand_L).
        /// This ensures Deadlock's in-game Two-Bone Foot and Hand IK solvers follow the animated limbs cleanly
        /// without snapping, twisting, or dragging feet to the floor when airborne.
        /// </summary>
        public static void ApplyAutomatedIkConstraints(
            System.Numerics.Vector3[,] samplePos,
            System.Numerics.Quaternion[,] sampleRot,
            int numFrames,
            int boneCount,
            ValveResourceFormat.ResourceTypes.ModelAnimation.Bone[] targetBones,
            Godot.Skeleton3D skeleton,
            (int PosTrack, int RotTrack)[] boneTracks)
        {
            // Resolve bone indices for constraint pairs
            var activePairs = new List<(int IkIdx, int TargetIdx)>();
            foreach (var (ikName, targetName) in IkConstraintPairs)
            {
                int ikIdx = FindBoneIndex(ikName, targetBones, skeleton, boneCount);
                int targetIdx = FindBoneIndex(targetName, targetBones, skeleton, boneCount);

                if (ikIdx < 0 || targetIdx < 0) continue;

                // If user authored explicit animation tracks on this IK bone in Godot, respect their authored keys
                if (boneTracks != null && ikIdx < boneTracks.Length)
                {
                    var (pTrack, rTrack) = boneTracks[ikIdx];
                    if (pTrack != -1 || rTrack != -1) continue;
                }

                activePairs.Add((ikIdx, targetIdx));
            }

            if (activePairs.Count == 0) return;

            // Pre-resolve parent indices for all bones
            var parentIndices = new int[boneCount];
            for (int b = 0; b < boneCount; b++)
            {
                if (targetBones != null)
                {
                    var p = targetBones[b].Parent;
                    parentIndices[b] = p != null ? Array.IndexOf(targetBones, p) : -1;
                }
                else
                {
                    parentIndices[b] = skeleton != null ? skeleton.GetBoneParent(b) : -1;
                }
            }

            var worldMats = new System.Numerics.Matrix4x4[boneCount];
            var computed = new bool[boneCount];

            System.Numerics.Matrix4x4 GetWorldMat(int idx, int f)
            {
                if (idx < 0 || idx >= boneCount) return System.Numerics.Matrix4x4.Identity;
                if (computed[idx]) return worldMats[idx];

                int p = parentIndices[idx];
                var localMat = System.Numerics.Matrix4x4.CreateFromQuaternion(sampleRot[f, idx]) *
                               System.Numerics.Matrix4x4.CreateTranslation(samplePos[f, idx]);

                worldMats[idx] = (p >= 0 && p < boneCount && p != idx)
                    ? localMat * GetWorldMat(p, f)
                    : localMat;

                computed[idx] = true;
                return worldMats[idx];
            }

            for (int f = 0; f < numFrames; f++)
            {
                Array.Clear(computed, 0, boneCount);

                foreach (var (ikIdx, targetIdx) in activePairs)
                {
                    var targetWorld = GetWorldMat(targetIdx, f);
                    int pIdx = parentIndices[ikIdx];

                    if (pIdx >= 0 && pIdx < boneCount)
                    {
                        var parentWorld = GetWorldMat(pIdx, f);
                        if (System.Numerics.Matrix4x4.Invert(parentWorld, out var invParent))
                        {
                            var mLocal = targetWorld * invParent;
                            samplePos[f, ikIdx] = mLocal.Translation;
                            sampleRot[f, ikIdx] = System.Numerics.Quaternion.Normalize(
                                System.Numerics.Quaternion.CreateFromRotationMatrix(mLocal)
                            );
                        }
                    }
                    else
                    {
                        samplePos[f, ikIdx] = targetWorld.Translation;
                        sampleRot[f, ikIdx] = System.Numerics.Quaternion.Normalize(
                            System.Numerics.Quaternion.CreateFromRotationMatrix(targetWorld)
                        );
                    }

                    // Invalidate computed cache for this IK bone so downstream bones (e.g. weaponHand parented to arm_IK) evaluate updated world transform
                    computed[ikIdx] = false;
                }
            }

            try
            {
                Godot.GD.Print($"[Source2ClipPatcher] Applied automated IK target constraints to {activePairs.Count} bones across {numFrames} frames.");
            }
            catch
            {
                Console.WriteLine($"[Source2ClipPatcher] Applied automated IK target constraints to {activePairs.Count} bones across {numFrames} frames.");
            }
        }

        private static int FindBoneIndex(
            string name,
            ValveResourceFormat.ResourceTypes.ModelAnimation.Bone[] targetBones,
            Godot.Skeleton3D skeleton,
            int boneCount)
        {
            if (targetBones != null)
            {
                for (int i = 0; i < targetBones.Length; i++)
                {
                    if (targetBones[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                        return i;
                }
            }
            if (skeleton != null)
            {
                for (int i = 0; i < skeleton.GetBoneCount(); i++)
                {
                    if (skeleton.GetBoneName(i).Equals(name, StringComparison.OrdinalIgnoreCase))
                        return i;
                }
            }
            return -1;
        }
    }
}
