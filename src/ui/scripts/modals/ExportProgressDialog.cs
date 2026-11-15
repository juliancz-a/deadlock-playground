using Godot;
using System;
using System.IO;
using System.Threading;
using DeadlockPlayground.Painter;
using DeadlockPlayground.Theming;

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
    private ColorRect _backdrop;
    private PanelContainer _dialogPanel;
    private string _stepStatusKind = "info";

    public override void _Ready()
    {
        FindNodes();
        ConnectEvents();
        ThemeManager.ThemeChanged += OnThemeChanged;
        ApplyThemeStyling();
    }

    public override void _ExitTree()
    {
        ThemeManager.ThemeChanged -= OnThemeChanged;
    }

    private void OnThemeChanged(ThemeProfile _)
    {
        if (GodotObject.IsInstanceValid(this))
        {
            ApplyThemeStyling();
        }
    }

    private void ApplyThemeStyling()
    {
        if (_backdrop != null && GodotObject.IsInstanceValid(_backdrop))
        {
            _backdrop.Color = ThemeManager.GetModalBackdropColor();
        }

        if (_dialogPanel != null && GodotObject.IsInstanceValid(_dialogPanel))
        {
            var modalTheme = ThemeManager.Instance?.GetModalTheme();
            if (modalTheme != null)
            {
                _dialogPanel.Theme = modalTheme;
            }
            _dialogPanel.ThemeTypeVariation = "ModalPanel";
        }

        if (_logConsole != null && GodotObject.IsInstanceValid(_logConsole))
        {
            var profile = ThemeManager.CurrentProfile;
            var consoleBg = ThemeManager.GetColor(nameof(ThemePaletteData.BackgroundTertiary));
            var border = ThemeManager.GetColor(nameof(ThemePaletteData.BorderColor));
            int radius = profile?.Style?.CornerRadius ?? 6;
            int bw = Math.Max(1, profile?.Style?.BorderWidth ?? 1);
            var consoleBox = new StyleBoxFlat
            {
                BgColor = consoleBg,
                BorderColor = new Color(border.R, border.G, border.B, 0.7f),
                BorderWidthLeft = bw,
                BorderWidthTop = bw,
                BorderWidthRight = bw,
                BorderWidthBottom = bw,
                CornerRadiusTopLeft = radius,
                CornerRadiusTopRight = radius,
                CornerRadiusBottomRight = radius,
                CornerRadiusBottomLeft = radius,
                ContentMarginLeft = 10,
                ContentMarginTop = 8,
                ContentMarginRight = 10,
                ContentMarginBottom = 8
            };
            _logConsole.AddThemeStyleboxOverride("normal", consoleBox);
            _logConsole.AddThemeColorOverride("default_color", ThemeManager.GetColor(nameof(ThemePaletteData.TextPrimary)));
        }

        ApplyStepStatusColor();
    }

    private void ApplyStepStatusColor()
    {
        if (_lblStepStatus == null || !GodotObject.IsInstanceValid(_lblStepStatus)) return;
        _lblStepStatus.Modulate = Colors.White;
        (string variation, string token) = _stepStatusKind switch
        {
            "success" => ("BadgeLabelSuccess", nameof(ThemePaletteData.SemanticSuccess)),
            "danger" => ("BadgeLabelDanger", nameof(ThemePaletteData.SemanticDanger)),
            _ => ("BadgeLabelInfo", nameof(ThemePaletteData.AccentSecondary))
        };
        _lblStepStatus.ThemeTypeVariation = variation;
        _lblStepStatus.AddThemeColorOverride("font_color", ThemeManager.GetColor(token));
    }

    private void FindNodes()
    {
        _backdrop ??= GetNodeOrNull<ColorRect>("Backdrop");
        _dialogPanel ??= GetNodeOrNull<PanelContainer>("DialogPanel");
        _lblHeader ??= GetNodeOrNull<Label>("DialogPanel/Margin/VBox/Title");
        _lblStepStatus ??= GetNodeOrNull<Label>("DialogPanel/Margin/VBox/LblStepStatus");
        _progressBar ??= GetNodeOrNull<ProgressBar>("DialogPanel/Margin/VBox/ProgressBar");
        _logConsole ??= GetNodeOrNull<RichTextLabel>("DialogPanel/Margin/VBox/LogConsole");
        _btnCancel ??= GetNodeOrNull<Button>("DialogPanel/Margin/VBox/HBoxButtons/BtnCancel");
        _btnOpenFolder ??= GetNodeOrNull<Button>("DialogPanel/Margin/VBox/HBoxButtons/BtnOpenFolder");
        _btnCopyLog ??= GetNodeOrNull<Button>("DialogPanel/Margin/VBox/HBoxButtons/BtnCopyLog");

        if (_lblStepStatus != null)
        {
            _lblStepStatus.ClipText = true;
            _lblStepStatus.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        }

        if (_logConsole != null)
        {
            _logConsole.SelectionEnabled = true;
            _logConsole.ContextMenuEnabled = true;
        }
    }

    public override void _Input(InputEvent @event)
    {
        if (!Visible || !_isCompleted) return;
        if (@event is InputEventKey key && key.Pressed && !key.Echo && key.Keycode == Key.Escape)
        {
            OnCancelOrClosePressed();
            GetViewport()?.SetInputAsHandled();
        }
    }

    private static string FormatSingleLineStatus(string raw, int maxLen = 85)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        string clean = raw
            .Replace("[INFO]", "")
            .Replace("[SUCCESS]", "")
            .Replace("[WARNING]", "")
            .Replace("[ERROR]", "")
            .Trim();

        int newlineIdx = clean.IndexOfAny(new[] { '\r', '\n' });
        if (newlineIdx >= 0)
        {
            clean = clean.Substring(0, newlineIdx).Trim();
        }

        if (clean.Length > maxLen)
        {
            clean = clean.Substring(0, maxLen - 3) + "...";
        }
        return clean;
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
        _stepStatusKind = "info";
        if (_lblStepStatus != null)
        {
            _lblStepStatus.Text = "Initializing export pipeline...";
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

        ApplyThemeStyling();
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
                    _lblStepStatus.Text = FormatSingleLineStatus(report.Message, 85);
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
                _stepStatusKind = "success";
                if (_lblStepStatus != null)
                {
                    _lblStepStatus.Text = "Export successfully completed!";
                }
                ApplyStepStatusColor();
            }
            else
            {
                string fullErr = result?.ErrorMessage ?? "Unknown error";
                if (_logConsole != null)
                {
                    string existingLog = _logConsole.GetParsedText() ?? "";
                    if (!existingLog.Contains(fullErr, StringComparison.Ordinal))
                    {
                        AppendLog($"[ERROR] {fullErr}", ExportLogLevel.Error);
                    }
                }

                _stepStatusKind = "danger";
                if (_lblStepStatus != null)
                {
                    string singleLineErr = FormatSingleLineStatus(fullErr, 75);
                    _lblStepStatus.Text = $"Export failed: {singleLineErr}";
                }
                ApplyStepStatusColor();
            }
        }).CallDeferred();
    }

    private void AppendLog(string message, ExportLogLevel level)
    {
        if (_logConsole == null) return;

        bool isLight = ThemeManager.IsLightTheme();
        string infoHex = isLight ? "#0f5298" : "#63b3ed";
        string successHex = isLight ? "#157347" : "#68d391";
        string warnHex = isLight ? "#9a5b00" : "#ecc94b";
        string errHex = isLight ? "#b91c1c" : "#fc8181";
        string mutedHex = isLight ? "#374151" : "#a0aec0";

        string colorTag = level switch
        {
            ExportLogLevel.Success => $"[color={successHex}]",
            ExportLogLevel.Warning => $"[color={warnHex}]",
            ExportLogLevel.Error => $"[color={errHex}]",
            _ => $"[color={mutedHex}]"
        };

        // Format [TAG] with distinct contrast-aware color
        string formatted = message;
        if (message.StartsWith("[INFO]"))
        {
            formatted = $"[color={infoHex}][b][INFO][/b][/color] " + message.Substring(6).TrimStart();
        }
        else if (message.StartsWith("[SUCCESS]"))
        {
            formatted = $"[color={successHex}][b][SUCCESS][/b][/color] " + message.Substring(9).TrimStart();
        }
        else if (message.StartsWith("[WARNING]"))
        {
            formatted = $"[color={warnHex}][b][WARNING][/b][/color] " + message.Substring(9).TrimStart();
        }
        else if (message.StartsWith("[ERROR]"))
        {
            formatted = $"[color={errHex}][b][ERROR][/b][/color] " + message.Substring(7).TrimStart();
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

