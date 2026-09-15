using Godot;
using System;
using System.IO;
using System.Threading;
using DeadlockPlayground.Painter;

public partial class ExportProgressDialog : CanvasLayer, IProgress<ExportProgressReport>
{
    [Signal] public delegate void ExportClosedEventHandler();

    [Export] private Label _lblHeader;
    [Export] private Label _lblStepStatus;
    [Export] private ProgressBar _progressBar;
    [Export] private RichTextLabel _logConsole;
    [Export] private Button _btnCancel;
    [Export] private Button _btnOpenFolder;
    [Export] private Button _btnCopyLog;

    private CancellationTokenSource _cts;
    private string _exportTargetFolder;
    private bool _isCompleted = false;

    public override void _Ready()
    {
        FindNodes();
        ConnectEvents();
    }

    private void FindNodes()
    {
        _lblHeader ??= GetNodeOrNull<Label>("DialogPanel/Margin/VBox/Title");
        _lblStepStatus ??= GetNodeOrNull<Label>("DialogPanel/Margin/VBox/LblStepStatus");
        _progressBar ??= GetNodeOrNull<ProgressBar>("DialogPanel/Margin/VBox/ProgressBar");
        _logConsole ??= GetNodeOrNull<RichTextLabel>("DialogPanel/Margin/VBox/LogConsole");
        _btnCancel ??= GetNodeOrNull<Button>("DialogPanel/Margin/VBox/HBoxButtons/BtnCancel");
        _btnOpenFolder ??= GetNodeOrNull<Button>("DialogPanel/Margin/VBox/HBoxButtons/BtnOpenFolder");
        _btnCopyLog ??= GetNodeOrNull<Button>("DialogPanel/Margin/VBox/HBoxButtons/BtnCopyLog");
    }

    private void ConnectEvents()
    {
        if (_btnCancel != null)
        {
            _btnCancel.Pressed += OnCancelOrClosePressed;
        }

        if (_btnOpenFolder != null)
        {
            _btnOpenFolder.Pressed += OnOpenFolderPressed;
        }

        if (_btnCopyLog != null)
        {
            _btnCopyLog.Pressed += OnCopyLogPressed;
        }
    }

    public void StartExport(CancellationTokenSource cts, string initialTargetFolder)
    {
        FindNodes();

        _cts = cts;
        _exportTargetFolder = initialTargetFolder;
        _isCompleted = false;

        if (_progressBar != null) _progressBar.Value = 0;
        if (_lblStepStatus != null)
        {
            _lblStepStatus.Text = "Initializing export pipeline...";
            _lblStepStatus.Modulate = new Color(0.75f, 0.82f, 0.95f);
        }
        if (_logConsole != null) _logConsole.Clear();
        if (_btnCancel != null)
        {
            _btnCancel.Text = "Cancel";
            _btnCancel.Disabled = false;
        }
        if (_btnOpenFolder != null)
        {
            _btnOpenFolder.Visible = false;
        }

        Visible = true;
    }

    public void Report(ExportProgressReport report)
    {
        if (report == null) return;

        // Ensure UI updates happen on Godot main thread
        Callable.From(() =>
        {
            if (_progressBar != null)
            {
                double target = Math.Clamp(report.Progress * 100.0, 0.0, 100.0);
                if (target >= _progressBar.Value)
                {
                    _progressBar.Value = target;
                }
            }

            if (!string.IsNullOrEmpty(report.Message))
            {
                AppendLog(report.Message, report.Level);

                if (_lblStepStatus != null)
                {
                    string clean = report.Message;
                    clean = clean.Replace("[INFO]", "").Replace("[SUCCESS]", "").Replace("[WARNING]", "").Replace("[ERROR]", "").Trim();
                    if (clean.Length > 80) clean = clean.Substring(0, 77) + "...";
                    _lblStepStatus.Text = clean;
                }
            }
        }).CallDeferred();
    }

    public void OnExportFinished(SkinExportResult result)
    {
        Callable.From(() =>
        {
            _isCompleted = true;
            if (_btnCancel != null)
            {
                _btnCancel.Text = "Done";
                _btnCancel.Disabled = false;
            }

            if (result != null && result.Success)
            {
                if (!string.IsNullOrEmpty(result.ModDirectory))
                {
                    _exportTargetFolder = result.ModDirectory;
                }
                if (_btnOpenFolder != null)
                {
                    _btnOpenFolder.Visible = true;
                    _btnOpenFolder.Disabled = false;
                }
                if (_progressBar != null) _progressBar.Value = 100;
                if (_lblStepStatus != null)
                {
                    _lblStepStatus.Text = "Export successfully completed!";
                    _lblStepStatus.Modulate = Colors.LightGreen;
                }
            }
            else
            {
                if (_lblStepStatus != null)
                {
                    _lblStepStatus.Text = $"Export failed: {result?.ErrorMessage ?? "Unknown error"}";
                    _lblStepStatus.Modulate = Colors.Salmon;
                }
            }
        }).CallDeferred();
    }

    private void AppendLog(string message, ExportLogLevel level)
    {
        if (_logConsole == null) return;

        string colorTag = level switch
        {
            ExportLogLevel.Success => "[color=#68d391]",
            ExportLogLevel.Warning => "[color=#ecc94b]",
            ExportLogLevel.Error => "[color=#fc8181]",
            _ => "[color=#a0aec0]"
        };

        // Format [TAG] with distinct color
        string formatted = message;
        if (message.StartsWith("[INFO]"))
        {
            formatted = "[color=#63b3ed][b][INFO][/b][/color] " + message.Substring(6).TrimStart();
        }
        else if (message.StartsWith("[SUCCESS]"))
        {
            formatted = "[color=#68d391][b][SUCCESS][/b][/color] " + message.Substring(9).TrimStart();
        }
        else if (message.StartsWith("[WARNING]"))
        {
            formatted = "[color=#ecc94b][b][WARNING][/b][/color] " + message.Substring(9).TrimStart();
        }
        else if (message.StartsWith("[ERROR]"))
        {
            formatted = "[color=#fc8181][b][ERROR][/b][/color] " + message.Substring(7).TrimStart();
        }
        else
        {
            formatted = $"{colorTag}{message}[/color]";
        }

        _logConsole.AppendText(formatted + "\n");
    }

    private void OnCancelOrClosePressed()
    {
        if (!_isCompleted)
        {
            // Cancel background compilation
            if (_cts != null && !_cts.IsCancellationRequested)
            {
                _cts.Cancel();
                if (_btnCancel != null)
                {
                    _btnCancel.Text = "Cancelling...";
                    _btnCancel.Disabled = true;
                }
                AppendLog("[WARNING] Cancellation requested. Stopping compiler process...", ExportLogLevel.Warning);
            }
        }
        else
        {
            Visible = false;
            EmitSignal(SignalName.ExportClosed);
        }
    }

    private void OnOpenFolderPressed()
    {
        string targetDir = _exportTargetFolder;
        if (string.IsNullOrEmpty(targetDir) || !Directory.Exists(targetDir))
        {
            targetDir = OS.GetSystemDir(OS.SystemDir.Desktop);
        }

        try
        {
            string globalPath = ProjectSettings.GlobalizePath(targetDir);
            OS.ShellOpen(globalPath);
        }
        catch (Exception ex)
        {
            AppendLog($"[ERROR] Failed to open folder in explorer: {ex.Message}", ExportLogLevel.Error);
        }
    }

    private void OnCopyLogPressed()
    {
        if (_logConsole == null) return;
        string text = _logConsole.GetParsedText();
        DisplayServer.ClipboardSet(text);
        if (_btnCopyLog != null)
        {
            string original = _btnCopyLog.Text;
            _btnCopyLog.Text = "Copied!";
            var timer = GetTree().CreateTimer(1.5f);
            timer.Timeout += () =>
            {
                if (GodotObject.IsInstanceValid(_btnCopyLog))
                    _btnCopyLog.Text = original;
            };
        }
    }
}

