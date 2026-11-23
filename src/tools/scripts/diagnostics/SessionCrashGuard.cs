using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using DeadlockPlayground.Importer;
using DeadlockPlayground.Workspace;

namespace DeadlockPlayground.Core;

/// <summary>
/// Zero-freeze Crash Guard &amp; Session Recovery service.
/// Uses a tiny dirty-exit sentinel (<c>user://workspace_session/session.lock</c>),
/// an in-memory breadcrumb ring buffer (zero disk I/O on hot paths), and debounced
/// background-thread atomic JSON micro-checkpoints (including an optional importer draft)
/// so crashes—including native GPU/driver hangs—can recover previous work without periodic auto-save freezes.
/// </summary>
public static class SessionCrashGuard
{
    private const int MaxBreadcrumbs = 60;
    private const int MaxCrashLogs = 12;

    public sealed class SessionSentinelData
    {
        public int Pid { get; set; }
        public string StartedUtc { get; set; } = "";
        public string LastUpdatedUtc { get; set; } = "";
        public string ActiveHeroId { get; set; } = "";
        public string ActiveHeroDisplayName { get; set; } = "";
        public string ActiveTab { get; set; } = "";
        public string LastBreadcrumb { get; set; } = "";
        public string LastCrashLogPath { get; set; } = "";
    }

    public sealed class ImporterDraftData
    {
        public string HeroId { get; set; } = "";
        public string ModelFilePath { get; set; } = "";
        public string SavedUtc { get; set; } = "";
        public int WorkflowIntent { get; set; }
        public string AccessorySlotId { get; set; } = "";
        public WorkspaceCustomModelEntry ModelConfig { get; set; }
    }

    private static readonly object Gate = new();
    private static readonly Queue<string> Breadcrumbs = new();
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private static bool _initialized;
    private static bool _cleanExitMarked;
    private static string _sessionDir = "";
    private static string _sentinelPath = "";
    private static string _importerDraftPath = "";
    private static string _crashLogsDir = "";

    private static SessionSentinelData _current = new();
    private static int _sentinelFlushScheduled;
    private static int _draftWriteVersion;

    public static SessionSentinelData PreviousUncleanSession { get; private set; }
    public static ImporterDraftData PendingImporterDraft { get; private set; }
    public static bool WasPreviousExitUnclean => PreviousUncleanSession != null;

    /// <summary>
    /// Initializes the crash guard on app startup, checks for a dirty-exit sentinel from a previous crash,
    /// writes a fresh sentinel for this PID, and hooks global unhandled exception loggers.
    /// </summary>
    public static void BeginSession()
    {
        lock (Gate)
        {
            if (_initialized) return;
            _initialized = true;
            _cleanExitMarked = false;

            try
            {
                _sessionDir = ProjectSettings.GlobalizePath("user://workspace_session");
                Directory.CreateDirectory(_sessionDir);
                _sentinelPath = Path.Combine(_sessionDir, "session.lock");
                _importerDraftPath = Path.Combine(_sessionDir, "importer_draft.json");
                _crashLogsDir = ResolveCrashLogsDir();

                // 1. Detect unclean previous exit
                if (File.Exists(_sentinelPath))
                {
                    try
                    {
                        string prevJson = File.ReadAllText(_sentinelPath);
                        PreviousUncleanSession = JsonSerializer.Deserialize<SessionSentinelData>(prevJson, JsonOpts)
                                                 ?? new SessionSentinelData { LastBreadcrumb = "(unreadable sentinel)" };
                        GD.Print($"[SessionCrashGuard] Detected unclean previous exit (PID={PreviousUncleanSession.Pid}, Hero='{PreviousUncleanSession.ActiveHeroId}', Last='{PreviousUncleanSession.LastBreadcrumb}').");
                    }
                    catch (Exception ex)
                    {
                        PreviousUncleanSession = new SessionSentinelData { LastBreadcrumb = $"Sentinel read error: {ex.Message}" };
                    }
                }

                // 2. Load any orphaned importer draft if present
                if (File.Exists(_importerDraftPath))
                {
                    try
                    {
                        string draftJson = File.ReadAllText(_importerDraftPath);
                        var draft = JsonSerializer.Deserialize<ImporterDraftData>(draftJson, JsonOpts);
                        if (draft != null && !string.IsNullOrEmpty(draft.ModelFilePath) && File.Exists(draft.ModelFilePath))
                        {
                            PendingImporterDraft = draft;
                        }
                        else
                        {
                            TryDeleteFile(_importerDraftPath);
                        }
                    }
                    catch
                    {
                        TryDeleteFile(_importerDraftPath);
                    }
                }

                // 3. Write fresh sentinel for current process
                string nowIso = DateTime.UtcNow.ToString("O");
                _current = new SessionSentinelData
                {
                    Pid = System.Environment.ProcessId,
                    StartedUtc = nowIso,
                    LastUpdatedUtc = nowIso,
                    LastBreadcrumb = "Session started"
                };
                WriteJsonAtomic(_sentinelPath, _current);
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[SessionCrashGuard] Init warning: {ex.Message}");
            }
        }

        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    /// <summary>
    /// Records a lightweight action breadcrumb in memory (0 ms main-thread cost)
    /// and schedules a debounced background atomic flush of <c>session.lock</c>.
    /// </summary>
    public static void RecordBreadcrumb(string action, string activeHeroId = null, string activeHeroDisplayName = null, string activeTab = null)
    {
        if (string.IsNullOrWhiteSpace(action)) return;

        lock (Gate)
        {
            string stamped = $"[{DateTime.Now:HH:mm:ss.fff}] {action}";
            Breadcrumbs.Enqueue(stamped);
            while (Breadcrumbs.Count > MaxBreadcrumbs) Breadcrumbs.Dequeue();

            _current.LastBreadcrumb = action;
            _current.LastUpdatedUtc = DateTime.UtcNow.ToString("O");
            if (!string.IsNullOrWhiteSpace(activeHeroId)) _current.ActiveHeroId = activeHeroId;
            if (!string.IsNullOrWhiteSpace(activeHeroDisplayName)) _current.ActiveHeroDisplayName = activeHeroDisplayName;
            if (!string.IsNullOrWhiteSpace(activeTab)) _current.ActiveTab = activeTab;
        }

        ScheduleSentinelFlush();
    }

    /// <summary>
    /// Schedules a zero-freeze background write of the active Model Importer draft (~20 KB JSON).
    /// Coalesced so rapid slider/gizmo changes only write once after ~1.5s idle.
    /// </summary>
    public static void ScheduleImporterDraftSave(
        string heroId,
        string modelFilePath,
        int workflowIntent,
        string accessorySlotId,
        CustomModelAutoRigger.RiggingOptions options)
    {
        if (string.IsNullOrWhiteSpace(modelFilePath) || options == null || string.IsNullOrEmpty(_importerDraftPath))
            return;

        int ver = Interlocked.Increment(ref _draftWriteVersion);
        WorkspaceCustomModelEntry cfg;
        try
        {
            cfg = WorkspaceCustomModelEntry.FromRiggingOptions(modelFilePath, Path.GetFileName(modelFilePath), options);
        }
        catch
        {
            return;
        }

        var draft = new ImporterDraftData
        {
            HeroId = heroId ?? "",
            ModelFilePath = modelFilePath,
            SavedUtc = DateTime.UtcNow.ToString("O"),
            WorkflowIntent = workflowIntent,
            AccessorySlotId = accessorySlotId ?? "",
            ModelConfig = cfg
        };

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1500);
                if (Volatile.Read(ref _draftWriteVersion) != ver) return;
                WriteJsonAtomic(_importerDraftPath, draft);
            }
            catch { /* best effort */ }
        });
    }

    public static void ClearImporterDraft()
    {
        Interlocked.Increment(ref _draftWriteVersion);
        PendingImporterDraft = null;
        TryDeleteFile(_importerDraftPath);
    }

    public static void DismissRecoveryNotice()
    {
        PreviousUncleanSession = null;
    }

    /// <summary>
    /// Removes <c>session.lock</c> on a clean quit so next launch knows no crash occurred.
    /// </summary>
    public static void MarkCleanExit()
    {
        lock (Gate)
        {
            if (_cleanExitMarked) return;
            _cleanExitMarked = true;
            TryDeleteFile(_sentinelPath);
        }
    }

    /// <summary>
    /// Safely runs an async UI workflow so unhandled exceptions are logged to a crash report
    /// and surfaced via <paramref name="onError"/> instead of terminating the process.
    /// </summary>
    public static async Task SafeRunAsync(Func<Task> action, string context, Action<string> onError = null)
    {
        if (action == null) return;
        RecordBreadcrumb($"Start: {context}");
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation
        }
        catch (Exception ex)
        {
            string logPath = ReportCaughtException(context, ex);
            onError?.Invoke($"{context} failed: {ex.Message}" + (string.IsNullOrEmpty(logPath) ? "" : $" (see {Path.GetFileName(logPath)})"));
        }
    }

    /// <summary>
    /// Flushes the breadcrumb ring buffer, memory snapshot, and exception stack trace to a crash log file.
    /// </summary>
    public static string ReportCaughtException(string context, Exception ex, bool isTerminating = false)
    {
        GD.PrintErr($"[SessionCrashGuard] Exception in '{context}': {ex}");
        return WriteCrashReport(context, ex?.ToString() ?? "<null>", isTerminating);
    }

    public static string GetCrashLogsDirectory() => _crashLogsDir;

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        string detail = e.ExceptionObject?.ToString() ?? "<unknown unhandled exception>";
        string path = WriteCrashReport("AppDomain.UnhandledException", detail, e.IsTerminating);
        if (!string.IsNullOrEmpty(path))
        {
            lock (Gate)
            {
                _current.LastCrashLogPath = path;
                _current.LastUpdatedUtc = DateTime.UtcNow.ToString("O");
                WriteJsonAtomic(_sentinelPath, _current);
            }
        }
    }

    private static void OnUnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e)
    {
        string detail = e.Exception?.GetBaseException()?.ToString() ?? "<unobserved task exception>";
        WriteCrashReport("TaskScheduler.UnobservedTaskException", detail, isTerminating: false);
        e.SetObserved();
    }

    private static void ScheduleSentinelFlush()
    {
        if (string.IsNullOrEmpty(_sentinelPath)) return;
        if (Interlocked.Exchange(ref _sentinelFlushScheduled, 1) != 0) return;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1000);
                SessionSentinelData snapshot;
                lock (Gate)
                {
                    if (_cleanExitMarked) return;
                    snapshot = new SessionSentinelData
                    {
                        Pid = _current.Pid,
                        StartedUtc = _current.StartedUtc,
                        LastUpdatedUtc = _current.LastUpdatedUtc,
                        ActiveHeroId = _current.ActiveHeroId,
                        ActiveHeroDisplayName = _current.ActiveHeroDisplayName,
                        ActiveTab = _current.ActiveTab,
                        LastBreadcrumb = _current.LastBreadcrumb,
                        LastCrashLogPath = _current.LastCrashLogPath
                    };
                }
                WriteJsonAtomic(_sentinelPath, snapshot);
            }
            catch { /* best effort */ }
            finally
            {
                Interlocked.Exchange(ref _sentinelFlushScheduled, 0);
            }
        });
    }

    private static string WriteCrashReport(string context, string exceptionText, bool isTerminating)
    {
        try
        {
            string dir = string.IsNullOrEmpty(_crashLogsDir) ? ResolveCrashLogsDir() : _crashLogsDir;
            Directory.CreateDirectory(dir);
            PruneOldCrashLogs(dir);

            string fileName = $"crash_{DateTime.Now:yyyyMMdd_HHmmss}.log";
            string fullPath = Path.Combine(dir, fileName);

            string[] crumbs;
            SessionSentinelData snap;
            lock (Gate)
            {
                crumbs = Breadcrumbs.ToArray();
                snap = _current;
            }

            ImporterMemoryGuard.TryQueryMemory(out long freeBytes, out long totalBytes, out long commitFree);
            long managedMb = GC.GetTotalMemory(false) / (1024 * 1024);

            var sb = new StringBuilder(2048);
            sb.AppendLine("=== Deadlock Playground Crash Report ===");
            sb.AppendLine($"Time (UTC)  : {DateTime.UtcNow:O}");
            sb.AppendLine($"Context     : {context} (terminating={isTerminating})");
            sb.AppendLine($"OS / PID    : {System.Environment.OSVersion} | PID {System.Environment.ProcessId}");
            sb.AppendLine($"Active Hero : {snap?.ActiveHeroDisplayName} ({snap?.ActiveHeroId})");
            sb.AppendLine($"Active Tab  : {snap?.ActiveTab}");
            sb.AppendLine($"Memory      : Free {freeBytes / 1048576} MB / Total {totalBytes / 1048576} MB | CommitAvail {commitFree / 1048576} MB | Managed {managedMb} MB");
            sb.AppendLine();
            sb.AppendLine("--- Exception ---");
            sb.AppendLine(exceptionText);
            sb.AppendLine();
            sb.AppendLine($"--- Recent Breadcrumbs ({crumbs.Length}) ---");
            foreach (string b in crumbs) sb.AppendLine("  " + b);

            using var fs = new FileStream(fullPath, FileMode.Create, System.IO.FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
            using var sw = new StreamWriter(fs, Encoding.UTF8);
            sw.Write(sb.ToString());
            sw.Flush();

            lock (Gate)
            {
                _current.LastCrashLogPath = fullPath;
            }
            return fullPath;
        }
        catch
        {
            return "";
        }
    }

    private static string ResolveCrashLogsDir()
    {
        try
        {
            string exeDir = Path.GetDirectoryName(OS.GetExecutablePath());
            if (!string.IsNullOrEmpty(exeDir) && !OS.HasFeature("editor"))
            {
                string candidate = Path.Combine(exeDir, "logs");
                Directory.CreateDirectory(candidate);
                return candidate;
            }
        }
        catch { /* fallback */ }

        string userLogs = ProjectSettings.GlobalizePath("user://logs");
        try { Directory.CreateDirectory(userLogs); } catch { }
        return userLogs;
    }

    private static void PruneOldCrashLogs(string dir)
    {
        try
        {
            var files = Directory.GetFiles(dir, "crash_*.log")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .Skip(MaxCrashLogs - 1)
                .ToList();
            foreach (var f in files) TryDeleteFile(f);
        }
        catch { /* ignore */ }
    }

    private static void WriteJsonAtomic<T>(string path, T data)
    {
        if (string.IsNullOrEmpty(path)) return;
        string dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        string tmp = path + ".tmp";
        string json = JsonSerializer.Serialize(data, JsonOpts);
        File.WriteAllText(tmp, json, Encoding.UTF8);
        File.Move(tmp, path, overwrite: true);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path);
        }
        catch { /* ignore */ }
    }
}
