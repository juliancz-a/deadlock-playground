using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Godot;

namespace DeadlockPlayground.Importer
{
    /// <summary>
    /// Pre-flight memory check and low-RAM guard for the model importer. Looks at *free* physical RAM
    /// and page-file commit headroom at open time (other apps such as Blender may be using most of it)
    /// and decides whether to continue normally, offer Light Mode, or refuse safely instead of crashing.
    /// </summary>
    public static class ImporterMemoryGuard
    {
        public enum Verdict { Ok, LowMemory, Critical }

        public readonly struct Report
        {
            public Report(Verdict verdict, long freeBytes, long totalBytes, long commitFreeBytes, long estimateBytes, string message)
            {
                Verdict = verdict;
                FreeBytes = freeBytes;
                TotalBytes = totalBytes;
                CommitFreeBytes = commitFreeBytes;
                EstimateBytes = estimateBytes;
                Message = message;
            }
            public Verdict Verdict { get; }
            public long FreeBytes { get; }
            public long TotalBytes { get; }
            public long CommitFreeBytes { get; }
            public long EstimateBytes { get; }
            public string Message { get; }
        }

        /// <summary>Set when the user/guard enables Light Mode (skip ghost, lower res, manual rig update).</summary>
        public static bool LightMode { get; set; }

        /// <summary>When true, skips the Critical RAM confirmation prompt for the remainder of the app session.</summary>
        public static bool SuppressPromptForSession { get; set; }

        // Tunables (calibrated to warn BEFORE Windows commit/native Godot OOM aborts the process)
        private const long BaseOverheadBytes = 500L * 1024 * 1024;       // preview viewport, GPU staging, hero clone
        private const long SafetyMarginBytes = 640L * 1024 * 1024;       // leave ~640 MB safety buffer
        private const long CriticalMinFreeBytes = 1024L * 1024 * 1024;   // below ~1.0 GB free physical or commit -> Critical warning
        private const long LightModeFreeBytes = 1792L * 1024 * 1024;     // below ~1.75 GB free -> non-blocking Light Mode hint
        private const double FileExpansionFactor = 8.0;                  // decoded geometry + uncompressed RGBA textures + native staging vs. file size

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private class MemoryStatusEx
        {
            public uint dwLength = (uint)Marshal.SizeOf(typeof(MemoryStatusEx));
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx lpBuffer);

        public static bool TryQueryMemory(out long freeBytes, out long totalBytes)
        {
            return TryQueryMemory(out freeBytes, out totalBytes, out _);
        }

        public static bool TryQueryMemory(out long freeBytes, out long totalBytes, out long availCommitBytes)
        {
            freeBytes = 0;
            totalBytes = 0;
            availCommitBytes = 0;
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    var s = new MemoryStatusEx();
                    if (GlobalMemoryStatusEx(s))
                    {
                        freeBytes = (long)s.ullAvailPhys;
                        totalBytes = (long)s.ullTotalPhys;
                        availCommitBytes = (long)s.ullAvailPageFile;
                        return true;
                    }
                }
                var info = GC.GetGCMemoryInfo();
                totalBytes = info.TotalAvailableMemoryBytes;
                freeBytes = Math.Max(0, totalBytes - GC.GetTotalMemory(false));
                availCommitBytes = freeBytes;
                return totalBytes > 0;
            }
            catch (Exception ex)
            {
                GD.PushWarning($"[ImporterMemoryGuard] Memory query failed: {ex.Message}");
                return false;
            }
        }

        public static long EstimateHeroGeometryBytes(Node3D heroNode)
        {
            if (heroNode == null || !GodotObject.IsInstanceValid(heroNode)) return 0;
            long bytes = 0;
            try
            {
                foreach (var child in heroNode.FindChildren("*", "MeshInstance3D", true, false))
                {
                    if (child is not MeshInstance3D mi || mi.Mesh == null) continue;
                    int sc = mi.Mesh.GetSurfaceCount();
                    for (int s = 0; s < sc; s++)
                    {
                        long len = mi.Mesh is ArrayMesh am
                            ? am.SurfaceGetArrayLen(s)
                            : (mi.Mesh.SurfaceGetArrays(s)?[(int)Mesh.ArrayType.Vertex].AsVector3Array()?.Length ?? 0);
                        bytes += len * 64; // vertex, normal, tangent, uv, bones, weights
                    }
                }
            }
            catch { /* best effort */ }
            return bytes;
        }

        public static long EstimateBytes(string filePath, Node3D heroNode = null)
        {
            long fileSize = 0;
            try { if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath)) fileSize = new FileInfo(filePath).Length; }
            catch { /* ignore */ }
            long heroBytes = EstimateHeroGeometryBytes(heroNode);
            return BaseOverheadBytes + heroBytes * 2 + (long)(fileSize * FileExpansionFactor);
        }

        public static long EstimateBatchBytes(IEnumerable<string> filePaths, Node3D heroNode = null)
        {
            long totalFileSize = 0;
            if (filePaths != null)
            {
                foreach (var fp in filePaths)
                {
                    try
                    {
                        if (!string.IsNullOrEmpty(fp) && File.Exists(fp))
                            totalFileSize += new FileInfo(fp).Length;
                    }
                    catch { /* ignore */ }
                }
            }
            long heroBytes = EstimateHeroGeometryBytes(heroNode);
            return BaseOverheadBytes + heroBytes * 2 + (long)(totalFileSize * FileExpansionFactor);
        }

        public static Report Evaluate(string filePath, Node3D heroNode = null)
        {
            return EvaluateBatch(string.IsNullOrEmpty(filePath) ? Array.Empty<string>() : new[] { filePath }, heroNode);
        }

        public static Report EvaluateBatch(IEnumerable<string> filePaths, Node3D heroNode = null)
        {
            long estimate = EstimateBatchBytes(filePaths, heroNode);
            if (!TryQueryMemory(out long free, out long total, out long commitFree))
            {
                LightMode = false;
                return new Report(Verdict.Ok, 0, 0, 0, estimate, "Memory status unavailable; continuing.");
            }

            // On Windows, native C++ / D3D12 allocations fail immediately when EITHER physical RAM
            // OR page-file commit headroom (ullAvailPageFile) is exhausted. Always bound by the minimum!
            long effectiveHeadroom = commitFree > 0 ? Math.Min(free, commitFree) : free;
            string freeGb = $"{free / 1073741824.0:F1} GB";
            string commitGb = $"{commitFree / 1073741824.0:F1} GB";
            string needGb = $"{estimate / 1073741824.0:F1} GB";

            if (effectiveHeadroom < CriticalMinFreeBytes || estimate + SafetyMarginBytes > effectiveHeadroom)
            {
                string detail = commitFree > 0 && commitFree < free
                    ? $"Available Windows virtual memory / commit headroom is critically low ({commitGb} commit free, {freeGb} physical free, ~{needGb} estimated)."
                    : $"Free physical RAM is critically low ({freeGb} free, {commitGb} commit free, ~{needGb} estimated).";
                return new Report(Verdict.Critical, free, total, commitFree, estimate,
                    $"{detail}\n" +
                    "Heavy background apps (e.g. Blender, browsers, Deadlock) may cause an immediate out-of-memory crash.\n\n" +
                    "Close other apps or use Light Mode (disables hero ghost & realtime re-rig) to save RAM.");
            }

            if (effectiveHeadroom < LightModeFreeBytes)
            {
                return new Report(Verdict.LowMemory, free, total, commitFree, estimate,
                    $"Low RAM ({freeGb} free, {commitGb} commit free) — Light Mode is available in the footer if performance is tight.");
            }

            LightMode = false;
            return new Report(Verdict.Ok, free, total, commitFree, estimate, "");
        }

        public static string FormatFooterMemoryText()
        {
            TryQueryMemory(out long free, out _);
            long managed = GC.GetTotalMemory(false);
            ulong engine = OS.GetStaticMemoryUsage();
            double usedGb = (managed + (long)engine) / 1073741824.0;
            double freeGb = free / 1073741824.0;
            string lm = LightMode ? " [Light]" : "";
            return $"Memory: {usedGb:F2} GB / {freeGb:F1} GB free{lm}";
        }

        public static void Log(string tag)
        {
            TryQueryMemory(out long free, out long total, out long commitFree);
            long managed = GC.GetTotalMemory(false);
            ulong engine = OS.GetStaticMemoryUsage();
            GD.Print($"[ImporterMemory] {tag}: free {free / 1048576} MB / total {total / 1048576} MB, " +
                     $"commitAvail {commitFree / 1048576} MB, managed {managed / 1048576} MB, engine {engine / 1048576} MB, lightMode={LightMode}");
        }

        /// <summary>
        /// Clones a hero/preview hierarchy duplicating only nodes and Skeleton3D bones while SHARING
        /// read-only Mesh, Skin, and Material resources so memory does not balloon on Duplicate().
        /// </summary>
        public static Node3D CloneHierarchySharingResources(Node3D source)
        {
            if (source == null || !GodotObject.IsInstanceValid(source)) return null;
            var rootClone = DuplicateNodeShallow(source) as Node3D;
            if (rootClone == null) return null;
            CopyChildrenSharingResources(source, rootClone);
            return rootClone;
        }

        private static Node DuplicateNodeShallow(Node src)
        {
            if (src is Skeleton3D srcSkel)
            {
                var skel = new Skeleton3D { Name = srcSkel.Name, Transform = srcSkel.Transform, Visible = srcSkel.Visible };
                int count = srcSkel.GetBoneCount();
                for (int b = 0; b < count; b++)
                {
                    skel.AddBone(srcSkel.GetBoneName(b));
                }
                for (int b = 0; b < count; b++)
                {
                    int p = srcSkel.GetBoneParent(b);
                    if (p >= 0 && p < count) skel.SetBoneParent(b, p);
                    skel.SetBoneRest(b, srcSkel.GetBoneRest(b));
                    skel.SetBonePosePosition(b, srcSkel.GetBonePosePosition(b));
                    skel.SetBonePoseRotation(b, srcSkel.GetBonePoseRotation(b));
                    skel.SetBonePoseScale(b, srcSkel.GetBonePoseScale(b));
                    skel.SetBoneEnabled(b, srcSkel.IsBoneEnabled(b));
                }
                CopyMetadata(src, skel);
                return skel;
            }

            if (src is MeshInstance3D srcMi)
            {
                var mi = new MeshInstance3D
                {
                    Name = srcMi.Name,
                    Transform = srcMi.Transform,
                    Visible = srcMi.Visible,
                    Mesh = srcMi.Mesh,       // shared read-only!
                    // Always duplicate Skin (tiny ~15 KB resource) so preview rig updates/bone removals
                    // can NEVER mutate the live hero's Skin bind indices or inverse bind poses on Cancel!
                    Skin = srcMi.Skin != null ? (Skin)srcMi.Skin.Duplicate() : null,
                    Skeleton = srcMi.Skeleton,
                    Layers = srcMi.Layers,
                    MaterialOverride = srcMi.MaterialOverride
                };
                if (srcMi.Mesh != null)
                {
                    int sc = srcMi.Mesh.GetSurfaceCount();
                    for (int s = 0; s < sc; s++)
                    {
                        var ov = srcMi.GetSurfaceOverrideMaterial(s);
                        if (ov != null) mi.SetSurfaceOverrideMaterial(s, ov);
                    }
                }
                CopyMetadata(src, mi);
                return mi;
            }

            if (src is AnimationPlayer srcAp)
            {
                var ap = new AnimationPlayer
                {
                    Name = srcAp.Name,
                    RootNode = srcAp.RootNode,
                    CallbackModeProcess = srcAp.CallbackModeProcess,
                    SpeedScale = srcAp.SpeedScale
                };
                // Give the cloned preview hero an isolated empty AnimationLibrary so it never shares
                // or mutates cached animations on the live stage hero, and always re-decodes clean
                // tracks bound specifically to _previewHeroSkeleton.
                ap.AddAnimationLibrary("", new AnimationLibrary());
                CopyMetadata(src, ap);
                return ap;
            }

            if (src is BoneAttachment3D srcBa)
            {
                var ba = new BoneAttachment3D
                {
                    Name = srcBa.Name,
                    Transform = srcBa.Transform,
                    Visible = srcBa.Visible,
                    BoneName = srcBa.BoneName,
                    BoneIdx = srcBa.BoneIdx,
                    OverridePose = srcBa.OverridePose,
                    UseExternalSkeleton = srcBa.UseExternalSkeleton,
                    ExternalSkeleton = srcBa.ExternalSkeleton
                };
                CopyMetadata(src, ba);
                return ba;
            }

            if (src is Node3D srcN3)
            {
                var n3 = new Node3D { Name = srcN3.Name, Transform = srcN3.Transform, Visible = srcN3.Visible };
                CopyMetadata(src, n3);
                return n3;
            }

            var node = new Node { Name = src.Name };
            CopyMetadata(src, node);
            return node;
        }

        private static void CopyMetadata(Node src, Node dst)
        {
            foreach (var meta in src.GetMetaList())
            {
                Variant v = src.GetMeta(meta);
                // Deep-duplicate Godot Dictionary/Array metadata so preview edits never mutate live hero metadata!
                if (v.VariantType == Variant.Type.Dictionary)
                    v = v.AsGodotDictionary().Duplicate(true);
                else if (v.VariantType == Variant.Type.Array)
                    v = v.AsGodotArray().Duplicate(true);
                dst.SetMeta(meta, v);
            }
        }

        private static void CopyChildrenSharingResources(Node src, Node dst)
        {
            foreach (var child in src.GetChildren())
            {
                string cName = child.Name.ToString();
                string cType = child.GetType().Name;
                string lower = cName.ToLowerInvariant();

                // Skip runtime gizmos, bone pickers, and generated split submeshes right away
                if (cType.Contains("SkeletonGizmo") || cType.Contains("BoneLayer") || cType.Contains("CharacterIK") ||
                    cName.StartsWith("@") || (child is BoneAttachment3D && cName.StartsWith("Pick_", StringComparison.Ordinal)) ||
                    lower.Contains("gizmo") || lower.Contains("bonepicker") ||
                    lower.Contains("pickerhandle") || lower.Contains("pickerarea") || lower.Contains("pickershape") ||
                    lower.Contains("ikhandle") || lower.Contains("bonehandle") || lower.Contains("resizehandle") ||
                    lower.Contains("tipvisual") || lower.Contains("polevisual"))
                {
                    continue;
                }
                if (child is MeshInstance3D mi && (mi.HasMeta("ParentCompositeMesh") || cName.Contains("_surf_") || mi.HasMeta("IsCustomImportedMesh")))
                {
                    continue;
                }

                var childClone = DuplicateNodeShallow(child);
                dst.AddChild(childClone);
                CopyChildrenSharingResources(child, childClone);
            }
        }

        /// <summary>
        /// Loads a model scene with a memory pre-check and OutOfMemory protection.
        /// Returns null (and fills <paramref name="userMessage"/>) when the import cannot proceed.
        /// </summary>
        public static Node3D LoadSceneGuarded(string filePath, out string userMessage, out Verdict verdict, Node3D heroNode = null, bool forceAllowCriticalInLightMode = false)
        {
            userMessage = null;
            Log("before load");
            var report = Evaluate(filePath, heroNode);
            verdict = report.Verdict;
            GD.Print($"[ImporterMemory] verdict={report.Verdict}, estimate={report.EstimateBytes / 1048576} MB");

            if (report.Verdict == Verdict.Critical && !forceAllowCriticalInLightMode)
            {
                userMessage = report.Message;
                return null;
            }
            if (report.Verdict == Verdict.LowMemory || (report.Verdict == Verdict.Critical && forceAllowCriticalInLightMode))
            {
                LightMode = true;
                userMessage = report.Message;
            }

            try
            {
                var scene = CustomModelAutoRigger.LoadAndSanitize3DModelScene(filePath);
                Log("after load");
                return scene;
            }
            catch (OutOfMemoryException)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                verdict = Verdict.Critical;
                userMessage = "Ran out of memory while loading the model. Close other memory-heavy apps (e.g. Blender) and try again.";
                return null;
            }
        }

        /// <summary>
        /// Asynchronous model parsing wrapper that reports stage progress and handles OutOfMemoryException cleanly.
        /// Note: Godot scene instantiation runs on the caller thread after file pre-read / buffer preprocessing.
        /// </summary>
        public static async Task<(Node3D Scene, string UserMessage, Verdict Verdict)> LoadSceneGuardedAsync(
            string filePath,
            Node3D heroNode,
            Action<float, string> onProgress = null,
            bool allowCriticalInLightMode = false,
            CancellationToken ct = default,
            bool forceFullQuality = false)
        {
            onProgress?.Invoke(0.10f, "Checking free system memory...");
            Log("before async load");
            var report = Evaluate(filePath, heroNode);
            if (report.Verdict == Verdict.Critical && !allowCriticalInLightMode && !forceFullQuality)
            {
                return (null, report.Message, report.Verdict);
            }

            string msg = report.Verdict != Verdict.Ok ? report.Message : null;
            if (forceFullQuality)
                LightMode = false;
            else if (report.Verdict == Verdict.Critical && allowCriticalInLightMode)
                LightMode = true;

            onProgress?.Invoke(0.30f, $"Reading '{Path.GetFileName(filePath)}'...");
            try
            {
                // Pre-warm OS file cache on worker thread so UI stays responsive during disk read
                await Task.Run(() =>
                {
                    ct.ThrowIfCancellationRequested();
                    if (File.Exists(filePath))
                    {
                        using var fs = new FileStream(filePath, FileMode.Open, System.IO.FileAccess.Read, FileShare.ReadWrite);
                        byte[] buf = new byte[Math.Min(fs.Length, 4 * 1024 * 1024)];
                        _ = fs.Read(buf, 0, buf.Length);
                    }
                }, ct);

                ct.ThrowIfCancellationRequested();
                onProgress?.Invoke(0.65f, "Parsing 3D scene & materials...");
                var scene = CustomModelAutoRigger.LoadAndSanitize3DModelScene(filePath);
                onProgress?.Invoke(1.0f, "Ready.");
                Log("after async load");
                return (scene, msg, report.Verdict);
            }
            catch (OutOfMemoryException)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                return (null, "Ran out of memory while loading the model. Close other memory-heavy apps (e.g. Blender) and try again.", Verdict.Critical);
            }
            catch (OperationCanceledException)
            {
                return (null, "Model load cancelled.", Verdict.Ok);
            }
        }
    }
}
