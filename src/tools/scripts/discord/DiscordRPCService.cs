using System;
using Godot;
using Godot.Collections;

namespace DeadlockPlayground.Core;

public partial class DiscordRPCService : Node
{
    public static DiscordRPCService Instance { get; private set; }

    // Replace with your Discord Application ID from the Developer Portal
    [Export] public string ApplicationId { get; set; } = "1556778276834254949";

    private Node _presenceNode;
    private long _startTimestamp;

    public override void _EnterTree()
    {
        Instance = this;
    }

    public override void _Ready()
    {
        InitializeDiscord();
    }

    private void InitializeDiscord()
    {
        try
        {
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

            // Set initial default presence state
            UpdateActivity(
                details: "Experimenting with Deadlock Heroes",
                state: "In the Playground"
            );

            GD.Print("[DiscordRPC] Discord RPC service successfully initialized.");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[DiscordRPC] Initialization error: {ex.Message}");
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
        ClearActivity();
        if (Instance == this)
        {
            Instance = null;
        }
    }
}