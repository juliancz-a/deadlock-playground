using Godot;

namespace DeadlockPlayground.Core;

/// <summary>
/// Autoload registered first in project.godot. Starts <see cref="StartupDiagnosticTracker"/> before any other
/// autoload / scene code and emits a heartbeat every frame so the watchdog can detect a stalled main thread.
/// </summary>
public partial class StartupDiagnosticsNode : Node
{
    private int _frames;

    public override void _EnterTree()
    {
        ProcessMode = ProcessModeEnum.Always;
        StartupDiagnosticTracker.Begin();
        StartupDiagnosticTracker.Mark("Autoload StartupDiagnostics entered tree");
    }

    public override void _Ready()
    {
        StartupDiagnosticTracker.Mark("Autoload StartupDiagnostics ready");
    }

    public override void _Process(double delta)
    {
        StartupDiagnosticTracker.Heartbeat();
        _frames++;
        if (_frames == 1) StartupDiagnosticTracker.Mark("First frame processed");
        else if (_frames == 10) StartupDiagnosticTracker.Mark("10 frames processed (main loop alive)");
        else if (_frames == 120) StartupDiagnosticTracker.Mark("120 frames processed (startup completed)");
    }

    public override void _Notification(int what)
    {
        if (what == NotificationWMCloseRequest)
        {
            StartupDiagnosticTracker.Mark("Window close requested");
        }
    }

    public override void _ExitTree()
    {
        StartupDiagnosticTracker.Shutdown();
    }
}
