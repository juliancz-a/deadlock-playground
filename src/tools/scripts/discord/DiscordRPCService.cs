using System;
using System.IO;
using System.IO.Pipes;
using System.Threading.Tasks;
using Godot;
using Godot.Collections;

namespace DeadlockPlayground.Core;

public partial class DiscordRPCService : Node
{
    public static DiscordRPCService Instance { get; private set; }

    // Replace with your Discord Application ID from the Developer Portal
    [Export] public string ApplicationId { get; set; } = "1556778276834254949";

    /// <summary>Frames-worth of delay before the first Discord probe, so the window is fully up first.</summary>
    private const double StartupDelaySeconds = 2.0;
    private const double RetryDelaySeconds = 30.0;
    private const int PipeProbeTimeoutMs = 250;
    private const int MaxPipeIndex = 9;

    private Node _presenceNode;
    private long _startTimestamp;
    private bool _exiting;

    // Presence requested before Discord was reachable; replayed once the GDScript client is created.
    private (string details, string state, string heroKey, string largeImageKey, string largeImageText)? _pendingActivity;

    public override void _EnterTree()
    {
        Instance = this;
    }

    public override void _Ready()
    {
        StartupDiagnosticTracker.Mark("Discord RPC: scheduled (non-blocking background probe)");
        _ = ProbeAndInitializeAsync();
    }

    /// <summary>
    /// The GDScript client opens the Windows named pipe synchronously on the main thread, which can block
    /// forever if the pipe exists but nobody services it (stuck/elevated Discord, bridges such as arRPC...).
    /// We therefore probe the pipe on a worker thread with a hard timeout and only create the client once
    /// Discord answered. If it never does, the app simply runs without Rich Presence.
    /// </summary>
    private async Task ProbeAndInitializeAsync()
    {
        try
        {
            await ToSignal(GetTree().CreateTimer(StartupDelaySeconds), SceneTreeTimer.SignalName.Timeout);

            while (!_exiting && _presenceNode == null)
            {
                bool reachable = await Task.Run(ProbeDiscordPipe).ConfigureAwait(true);
                if (_exiting) return;

                if (reachable)
                {
                    InitializeDiscord();
                    return;
                }

                StartupDiagnosticTracker.Log("Discord RPC: Discord not reachable, will retry later");
                await ToSignal(GetTree().CreateTimer(RetryDelaySeconds), SceneTreeTimer.SignalName.Timeout);
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[DiscordRPC] Background probe failed: {ex.Message}");
            StartupDiagnosticTracker.Log($"Discord RPC: probe failed: {ex.Message}");
        }
    }

    /// <summary>Runs on a thread-pool thread. Never touches Godot APIs.</summary>
    private static bool ProbeDiscordPipe()
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                // Unix sockets are bridged by the GDScript client through netcat; nothing to pre-validate.
                return true;
            }

            string[] existing;
            try
            {
                existing = Directory.GetFiles(@"\\.\pipe\", "discord-ipc-*");
            }
            catch
            {
                // Pipe enumeration unavailable; fall through to direct probing.
                existing = null;
            }

            if (existing != null && existing.Length == 0)
            {
                return false;
            }

            for (int i = 0; i <= MaxPipeIndex; i++)
            {
                try
                {
                    using var client = new NamedPipeClientStream(".", $"discord-ipc-{i}", PipeDirection.InOut, PipeOptions.None);
                    client.Connect(PipeProbeTimeoutMs);
                    return true;
                }
                catch
                {
                    // Not present / busy / timed out: try the next index.
                }
            }
        }
        catch
        {
            // Swallow everything: this must never take the app down.
        }

        return false;
    }

    private void InitializeDiscord()
    {
        try
        {
            StartupDiagnosticTracker.Mark("Discord RPC: initializing client");

            // Path to the pure GDScript plugin file inside the addons directory
            var presenceScript = GD.Load<GDScript>("res://addons/discord_rich_presence/discord_rich_presence.gd");
            if (presenceScript == null)
            {
                GD.PrintErr("[DiscordRPC] Failed to load 'discord_rich_presence.gd'. Verify the path inside addons.");
                return;
            }

            _presenceNode = (Node)presenceScript.New();
            _presenceNode.Name = "DiscordRichPresence";

            _presenceNode.Set("app_id", ApplicationId);

            AddChild(_presenceNode);

            _startTimestamp = (long)Time.GetUnixTimeFromSystem();

            // Replay whatever was requested while Discord was unreachable, otherwise use the default presence.
            if (_pendingActivity.HasValue)
            {
                var p = _pendingActivity.Value;
                _pendingActivity = null;
                UpdateActivity(p.details, p.state, p.heroKey, p.largeImageKey, p.largeImageText);
            }
            else
            {
                UpdateActivity(
                    details: "Experimenting with Deadlock Heroes",
                    state: "In the Playground"
                );
            }

            GD.Print("[DiscordRPC] Discord RPC service successfully initialized.");
            StartupDiagnosticTracker.Mark("Discord RPC: client initialized");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[DiscordRPC] Initialization error: {ex.Message}");
            StartupDiagnosticTracker.Log($"Discord RPC: initialization error: {ex.Message}");
        }
    }

    /// <summary>
    /// Updates Discord presence activity by passing a Dictionary conforming to Discord RPC schema.
    /// </summary>
    public void UpdateActivity(
        string details, 
        string state, 
        string heroKey = null, 
        string largeImageKey = "logo_main", 
        string largeImageText = "Deadlock Playground")
    {
        if (_presenceNode == null || !IsInstanceValid(_presenceNode))
        {
            // Client not created yet (deferred startup / Discord closed): remember the latest request.
            _pendingActivity = (details, state, heroKey, largeImageKey, largeImageText);
            return;
        }

        try
        {
            var activity = new Dictionary();
            activity["details"] = details;
            activity["state"] = state;

            // Session timestamps
            var timestamps = new Dictionary();
            timestamps["start"] = _startTimestamp;
            activity["timestamps"] = timestamps;

            // Rich presence art assets
            var assets = new Dictionary();
            if (!string.IsNullOrEmpty(largeImageKey))
            {
                assets["large_image"] = largeImageKey;
                assets["large_image_text"] = largeImageText;
            }

            if (!string.IsNullOrEmpty(heroKey))
            {
                assets["small_image"] = heroKey.ToLowerInvariant();
                assets["small_image_text"] = $"Hero: {heroKey}";
            }

            activity["assets"] = assets;

            // Invoke the GDScript set_activity() method
            _presenceNode.Call("set_activity", activity);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[DiscordRPC] Failed to dispatch presence update to Discord: {ex.Message}");
        }
    }

    /// <summary>
    /// Clears the active Discord presence upon shutdown or scene exit.
    /// </summary>
    public void ClearActivity()
    {
        _pendingActivity = null;
        if (_presenceNode == null || !IsInstanceValid(_presenceNode))
        {
            return;
        }

        try
        {
            _presenceNode.Call("clear_activity");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[DiscordRPC] Failed to clear activity: {ex.Message}");
        }
    }

    public override void _ExitTree()
    {
        _exiting = true;
        ClearActivity();
        if (Instance == this)
        {
            Instance = null;
        }
    }
}