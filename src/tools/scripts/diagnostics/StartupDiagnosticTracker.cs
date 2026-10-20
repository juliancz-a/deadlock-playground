using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Godot;

namespace DeadlockPlayground.Core;

/// <summary>
/// Lightweight, thread-safe boot checkpoint logger. Every line is flushed to disk immediately
/// (AutoFlush + WriteThrough) so that even a hard driver hang or a Task Manager kill preserves the
/// exact last stage reached. Also runs a background watchdog during the first seconds of launch that
/// logs <c>[FREEZE DETECTED]</c> when the main thread stops sending heartbeats.
///
/// Log location: <c>&lt;folder of the exe&gt;/logs/startup_yyyyMMdd_HHmmss.log</c>, falling back to
/// <c>user://logs</c> when that folder is not writable (or when running from the Godot editor).
/// </summary>
public static class StartupDiagnosticTracker
{
    private const double WatchWindowSeconds = 30.0;
    private const double StallThresholdSeconds = 5.0;
    private const int MaxLogFiles = 10;

    // Modules injected by GPU/overlay hooks that are known to interfere with swapchains.
    private static readonly string[] OverlayModuleHints =
    {
        "rtss", "nvspcap", "nvoglv", "nvwgf2", "gameoverlayrenderer", "discordhook", "overwolf",
        "msiafterburner", "graphics-hook", "obs-", "reshade", "dxgi.dll", "d3d12", "vulkan-1",
        "atiadlxx", "amdxc", "nvapi", "nvngx", "geforce", "medal", "streamlabs", "action_", "fraps"
    };

    private static readonly object Gate = new();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static StreamWriter _writer;
    private static bool _writerAttempted;
    private static bool _began;
    private static string _logPath;
    private static volatile string _lastStage = "<none>";
    private static long _lastBeatTimestamp = Stopwatch.GetTimestamp();
    private static volatile bool _watchdogStop;
    private static Thread _watchdog;
    private static DateTime _processStart = DateTime.Now;

    public static string LogFilePath => _logPath;
    public static string LastStage => _lastStage;
    public static bool IsLoggingEnabled
    {
        get => UserSettings.EnableDiagnosticLogs;
        set => UserSettings.EnableDiagnosticLogs = value;
    }

    static StartupDiagnosticTracker()
    {
        UserSettings.EnableDiagnosticLogsChanged += OnLoggingToggled;
    }

    private static void OnLoggingToggled(bool enabled)
    {
        lock (Gate)
        {
            if (!enabled)
            {
                CloseWriter();
            }
            else
            {
                _writerAttempted = false;
                EnsureWriter();
                Log("Diagnostic logging enabled by user");
            }
        }
    }

    private static void CloseWriter()
    {
        if (_writer != null)
        {
            try
            {
                _writer.WriteLine(Format($"[T{System.Environment.CurrentManagedThreadId}] Diagnostic logging disabled by user"));
                _writer.Flush();
                _writer.Dispose();
            }
            catch { /* ignore */ }
            _writer = null;
            _writerAttempted = false;
            _logPath = null;
        }
    }

    /// <summary>Records a startup milestone (also becomes the "current stage" reported by the watchdog).</summary>
    public static void Mark(string stage)
    {
        _lastStage = stage;
        WriteLine(Format($"[T{System.Environment.CurrentManagedThreadId}] {stage}"));
    }

    /// <summary>Writes an informational line without changing the current stage.</summary>
    public static void Log(string message)
    {
        WriteLine(Format($"[T{System.Environment.CurrentManagedThreadId}] {message}"));
    }

    /// <summary>Called by the main thread every frame (see StartupDiagnosticsNode).</summary>
    public static void Heartbeat()
    {
        Interlocked.Exchange(ref _lastBeatTimestamp, Stopwatch.GetTimestamp());
    }

    /// <summary>Logs system info, hooks global exception handlers and starts the freeze watchdog. Idempotent.</summary>
    public static void Begin()
    {
        lock (Gate)
        {
            if (_began) return;
            _began = true;
        }

        try { _processStart = Process.GetCurrentProcess().StartTime; } catch { /* fall back to first-touch time */ }

        Heartbeat();
        Mark("Diagnostics started");
        LogSystemInfo();
        HookGlobalExceptionHandlers();
        StartWatchdog();
    }

    public static void Shutdown()
    {
        Mark("Shutting down");
        _watchdogStop = true;
        lock (Gate)
        {
            if (_writer != null)
            {
                try
                {
                    _writer.Flush();
                    _writer.Dispose();
                }
                catch { /* ignore */ }
                _writer = null;
            }
        }
    }

    // ------------------------------------------------------------------ writer

    private static string Format(string text)
    {
        double sinceProcess = (DateTime.Now - _processStart).TotalSeconds;
        return $"[{DateTime.Now:HH:mm:ss.fff}] [+{sinceProcess,7:F2}s] {text}";
    }

    private static void WriteLine(string line)
    {
        if (!UserSettings.EnableDiagnosticLogs) return;

        lock (Gate)
        {
            EnsureWriter();
            try { _writer?.WriteLine(line); } catch { /* never let logging crash the app */ }
        }
    }

    private static void EnsureWriter()
    {
        if (!UserSettings.EnableDiagnosticLogs) return;
        if (_writerAttempted) return;
        _writerAttempted = true;

        foreach (string dir in CandidateDirectories())
        {
            try
            {
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, $"startup_{DateTime.Now:yyyyMMdd_HHmmss}.log");
                var fs = new FileStream(path, FileMode.Create, System.IO.FileAccess.Write, FileShare.ReadWrite, 4096, FileOptions.WriteThrough);
                _writer = new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = true };
                _logPath = path;
                PruneOldLogs(dir);
                _writer.WriteLine($"# Deadlock Playground startup log - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                _writer.WriteLine($"# File: {path}");
                return;
            }
            catch
            {
                _writer = null;
            }
        }
    }

    private static IEnumerable<string> CandidateDirectories()
    {
        string exeDir = null;
        try
        {
            if (!OS.HasFeature("editor"))
            {
                exeDir = OS.GetExecutablePath().GetBaseDir();
            }
        }
        catch { /* ignore */ }

        if (!string.IsNullOrEmpty(exeDir))
        {
            yield return Path.Combine(exeDir, "logs");
        }

        string userDir = null;
        try { userDir = ProjectSettings.GlobalizePath("user://logs"); } catch { /* ignore */ }
        if (!string.IsNullOrEmpty(userDir))
        {
            yield return userDir;
        }
    }

    private static void PruneOldLogs(string dir)
    {
        try
        {
            var old = Directory.GetFiles(dir, "startup_*.log")
                .OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal)
                .Skip(MaxLogFiles);
            foreach (string f in old)
            {
                try { File.Delete(f); } catch { /* ignore */ }
            }
        }
        catch { /* ignore */ }
    }

    // ------------------------------------------------------------------ system info

    private static void Info(string label, Func<object> getter)
    {
        string value;
        try { value = Convert.ToString(getter()) ?? "<null>"; }
        catch (Exception ex) { value = $"<error: {ex.GetType().Name}: {ex.Message}>"; }
        Log($"  {label}: {value}");
    }

    private static void LogSystemInfo()
    {
        Log("=== System ===");
        Info("App version", () => ProjectSettings.GetSetting("application/config/version"));
        Info("Godot", () => Engine.GetVersionInfo()["string"]);
        Info(".NET runtime", () => RuntimeInformation.FrameworkDescription);
        Info("OS", () => $"{OS.GetName()} {OS.GetVersion()} ({RuntimeInformation.OSDescription})");
        Info("Process arch", () => RuntimeInformation.ProcessArchitecture);
        Info("CPU", () => $"{OS.GetProcessorName()} ({OS.GetProcessorCount()} logical cores)");
        Info("RAM (MB)", () =>
        {
            var mem = OS.GetMemoryInfo();
            long physical = mem.ContainsKey("physical") ? mem["physical"].AsInt64() : -1;
            long available = mem.ContainsKey("available") ? mem["available"].AsInt64() : -1;
            return $"physical={(physical > 0 ? physical / (1024 * 1024) : -1)}, available={(available > 0 ? available / (1024 * 1024) : -1)}";
        });
        Info("Elevated (admin)", IsElevated);

        Log("=== Graphics ===");
        Info("GPU adapter", () => RenderingServer.GetVideoAdapterName());
        Info("GPU vendor", () => RenderingServer.GetVideoAdapterVendor());
        Info("GPU type", () => RenderingServer.GetVideoAdapterType());
        Info("GPU API version", () => RenderingServer.GetVideoAdapterApiVersion());
        Info("GPU driver info", () => string.Join(" | ", OS.GetVideoAdapterDriverInfo()));
        Info("Active rendering driver", () => RenderingServer.GetCurrentRenderingDriverName());
        Info("Active rendering method", () => RenderingServer.GetCurrentRenderingMethod());
        Info("Configured driver.windows", () => ProjectSettings.GetSetting("rendering/rendering_device/driver.windows"));
        Info("Display server", () => DisplayServer.GetName());
        Info("Screens", () =>
        {
            int count = DisplayServer.GetScreenCount();
            var sb = new StringBuilder();
            for (int i = 0; i < count; i++)
            {
                sb.Append($"[{i}: {DisplayServer.ScreenGetSize(i)} @ {DisplayServer.ScreenGetRefreshRate(i):F0}Hz] ");
            }
            return sb.ToString().Trim();
        });

        Log("=== Launch ===");
        Info("Cmdline args", () => string.Join(" ", OS.GetCmdlineArgs()));
        Info("Cmdline user args", () => string.Join(" ", OS.GetCmdlineUserArgs()));
        Info("Executable", () => OS.GetExecutablePath());
        Info("Working dir", () => Directory.GetCurrentDirectory());
        Info("Exe drive", () =>
        {
            var drive = new DriveInfo(Path.GetPathRoot(OS.GetExecutablePath()) ?? "C:\\");
            return $"{drive.Name} type={drive.DriveType} format={drive.DriveFormat} free={drive.AvailableFreeSpace / (1024L * 1024 * 1024)}GB";
        });
        Info("user:// dir", () => ProjectSettings.GlobalizePath("user://"));
        Info("Log file", () => _logPath ?? "<none>");

        // Process.Modules can be slow; enumerate off the main thread only if logging is enabled.
        if (UserSettings.EnableDiagnosticLogs)
        {
            ThreadPool.QueueUserWorkItem(_ => LogOverlayModules("at launch"));
        }
    }

    private static object IsElevated()
    {
        if (!OperatingSystem.IsWindows()) return "n/a";
#pragma warning disable CA1416
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(identity)
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
#pragma warning restore CA1416
    }

    /// <summary>Logs native modules injected into the process by overlays / capture software / GPU drivers.</summary>
    private static void LogOverlayModules(string when)
    {
        try
        {
            var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ProcessModule module in Process.GetCurrentProcess().Modules)
            {
                string name = module.ModuleName ?? string.Empty;
                string lower = name.ToLowerInvariant();
                if (OverlayModuleHints.Any(h => lower.Contains(h)))
                {
                    names.Add(name);
                }
            }
            Log($"Overlay/driver modules loaded {when}: {(names.Count == 0 ? "<none>" : string.Join(", ", names))}");
        }
        catch (Exception ex)
        {
            Log($"Overlay module scan failed {when}: {ex.Message}");
        }
    }

    private static void HookGlobalExceptionHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log($"[UNHANDLED EXCEPTION] terminating={e.IsTerminating} {e.ExceptionObject}");
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
            Log($"[UNOBSERVED TASK EXCEPTION] {e.Exception?.GetBaseException()}");
    }

    // ------------------------------------------------------------------ watchdog

    private static void StartWatchdog()
    {
        _watchdog = new Thread(WatchdogLoop)
        {
            IsBackground = true,
            Name = "StartupWatchdog",
            Priority = ThreadPriority.BelowNormal
        };
        _watchdog.Start();
    }

    private static double SecondsSince(long timestamp)
    {
        return (Stopwatch.GetTimestamp() - timestamp) / (double)Stopwatch.Frequency;
    }

    private static void WatchdogLoop()
    {
        long start = Stopwatch.GetTimestamp();
        bool stalled = false;
        double lastReported = 0;

        try
        {
            while (!_watchdogStop)
            {
                Thread.Sleep(250);

                double sinceBeat = SecondsSince(Interlocked.Read(ref _lastBeatTimestamp));

                if (!stalled && sinceBeat > StallThresholdSeconds)
                {
                    stalled = true;
                    lastReported = sinceBeat;
                    Log($"[FREEZE DETECTED] Main thread stalled during stage: {_lastStage} (no heartbeat for {sinceBeat:F1}s)");
                }
                else if (stalled && sinceBeat < 1.0)
                {
                    stalled = false;
                    Log($"[RECOVERED] Main thread resumed after a stall. Last stage: {_lastStage}");
                }
                else if (stalled && sinceBeat - lastReported >= 10.0)
                {
                    lastReported = sinceBeat;
                    Log($"[STILL FROZEN] {sinceBeat:F0}s without heartbeat. Stage: {_lastStage}");
                }

                // Keep watching past the window only while a stall is in progress.
                if (!stalled && SecondsSince(start) > WatchWindowSeconds)
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Log($"Watchdog crashed: {ex.Message}");
            return;
        }

        Log($"Startup watchdog window closed (last stage: {_lastStage})");
        LogOverlayModules($"after {WatchWindowSeconds:F0}s");
    }
}
