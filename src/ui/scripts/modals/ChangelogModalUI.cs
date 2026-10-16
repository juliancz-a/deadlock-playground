#nullable enable
using Godot;
using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DeadlockPlayground.Tools;

public partial class ChangelogModalUI : PanelContainer
{
    [Signal] public delegate void DialogClosedEventHandler();

    public const string CurrentVersion = "v1.0.1";
    public const string ConfigPath = "user://settings.cfg";
    public const string ConfigSection = "News";
    public const string ConfigKey = "LastReadVersion";
    public const string DefaultWebsiteUrl = "https://deadlock-playground.top/";
    public const string DiscordUsername = "juliancz_";

    public const string InitialChangelogMarkdown = @"### Version 1.0.1 — New Heroes & Topbar Menu

#### New Content:
* Added support for 6 new update heroes from `models/heroes_wip/`:
  - Violet
  - Baba
  - Deadman Danny
  - Nurse Harrow
  - Ratking
  - Solomon
* Highlighted the new WIP roster in the Hero Selection menu with dedicated badges.
* Added update topbar menu and changelog viewing modal.";

    private Button? _btnClose;
    private Button? _btnBottomClose;
    private Button? _btnRefresh;
    private Button? _btnWebsite;
    private Button? _btnDiscord;
    private RichTextLabel? _richTextLabel;
    private Label? _titleLabel;
    private Label? _statusBadge;

    private string _activeVersion = CurrentVersion;
    private string _websiteUrl = DefaultWebsiteUrl;
    private string _discordUsername = DiscordUsername;
    private bool _isLoading = false;

    public override void _Ready()
    {
        _titleLabel = GetNodeOrNull<Label>("MarginContainer/VBoxContainer/HeaderBar/Title")
                   ?? FindChild("Title", true, false) as Label;
        _statusBadge = GetNodeOrNull<Label>("MarginContainer/VBoxContainer/HeaderBar/StatusBadge")
                    ?? FindChild("StatusBadge", true, false) as Label;
        _btnRefresh = GetNodeOrNull<Button>("MarginContainer/VBoxContainer/HeaderBar/BtnRefresh")
                   ?? FindChild("BtnRefresh", true, false) as Button;
        _btnClose = GetNodeOrNull<Button>("MarginContainer/VBoxContainer/HeaderBar/BtnClose")
                 ?? FindChild("BtnClose", true, false) as Button;
        _btnBottomClose = GetNodeOrNull<Button>("MarginContainer/VBoxContainer/BottomBar/BtnBottomClose")
                       ?? FindChild("BtnBottomClose", true, false) as Button;
        _btnWebsite = GetNodeOrNull<Button>("MarginContainer/VBoxContainer/BottomBar/BtnWebsite")
                   ?? FindChild("BtnWebsite", true, false) as Button;
        _btnDiscord = GetNodeOrNull<Button>("MarginContainer/VBoxContainer/BottomBar/BtnDiscord")
                   ?? FindChild("BtnDiscord", true, false) as Button;
        _richTextLabel = GetNodeOrNull<RichTextLabel>("MarginContainer/VBoxContainer/ContentCard/Margin/ScrollContainer/RichTextLabel")
                      ?? FindChild("RichTextLabel", true, false) as RichTextLabel;

        if (_btnClose != null) _btnClose.Pressed += Close;
        if (_btnBottomClose != null) _btnBottomClose.Pressed += Close;
        if (_btnRefresh != null) _btnRefresh.Pressed += () => _ = RefreshFeedAsync(true);
        if (_btnWebsite != null) _btnWebsite.Pressed += OnWebsiteClicked;
        if (_btnDiscord != null)
        {
            _btnDiscord.Pressed += OnDiscordClicked;
            _btnDiscord.Text = $"Discord: {_discordUsername}";
            _btnDiscord.TooltipText = $"Click to copy Discord username: {_discordUsername}";
        }

        if (_richTextLabel != null)
        {
            _richTextLabel.BbcodeEnabled = true;
            _richTextLabel.MetaClicked += OnMetaClicked;
            _richTextLabel.MetaHoverStarted += OnMetaHoverStarted;
            _richTextLabel.MetaHoverEnded += OnMetaHoverEnded;
            SetChangelogContent(InitialChangelogMarkdown);
        }

        MouseFilter = MouseFilterEnum.Stop;

        // Kick off initial background feed fetch
        _ = RefreshFeedAsync(false);
    }

    public async Task RefreshFeedAsync(bool forceRefresh)
    {
        if (_isLoading) return;
        _isLoading = true;

        if (_statusBadge != null)
        {
            _statusBadge.Text = "Checking...";
            _statusBadge.AddThemeColorOverride("font_color", new Color(0.95f, 0.75f, 0.2f, 1f));
        }

        try
        {
            var payload = await ChangelogFeedService.GetChangelogAsync(forceRefresh).ConfigureAwait(true);
            if (payload != null)
            {
                if (!string.IsNullOrEmpty(payload.Markdown))
                {
                    SetChangelogContent(payload.Markdown);
                }

                if (!string.IsNullOrEmpty(payload.Title) && _titleLabel != null)
                {
                    _titleLabel.Text = payload.Title;
                }

                if (!string.IsNullOrEmpty(payload.LatestVersion))
                {
                    _activeVersion = payload.LatestVersion.StartsWith("v", StringComparison.OrdinalIgnoreCase)
                        ? payload.LatestVersion
                        : $"v{payload.LatestVersion}";
                }

                if (payload.Links.TryGetValue("website", out var webUrl) && !string.IsNullOrEmpty(webUrl))
                {
                    _websiteUrl = webUrl;
                }
                if (payload.Links.TryGetValue("discord", out var discUser) && !string.IsNullOrEmpty(discUser))
                {
                    _discordUsername = discUser.Replace("https://discord.gg/", "").Trim();
                    if (_btnDiscord != null)
                    {
                        _btnDiscord.Text = $"Discord: {_discordUsername}";
                        _btnDiscord.TooltipText = $"Click to copy Discord username: {_discordUsername}";
                    }
                }

                if (_statusBadge != null)
                {
                    if (payload.IsFromRemote)
                    {
                        _statusBadge.Text = $"{_activeVersion} • Online";
                        _statusBadge.AddThemeColorOverride("font_color", new Color(0.45f, 0.75f, 0.55f, 1f));
                    }
                    else if (payload.IsFromCache)
                    {
                        _statusBadge.Text = $"{_activeVersion} • Cached";
                        _statusBadge.AddThemeColorOverride("font_color", new Color(0.7f, 0.7f, 0.7f, 0.8f));
                    }
                    else
                    {
                        _statusBadge.Text = $"{_activeVersion} • Offline";
                        _statusBadge.AddThemeColorOverride("font_color", new Color(0.6f, 0.6f, 0.6f, 0.7f));
                    }
                }

                // If modal is visible, mark version read immediately
                if (Visible)
                {
                    MarkVersionAsRead(_activeVersion);
                }

                StudioUIManager.Instance?.UpdateNewsBadgeState();
            }
        }
        catch (Exception ex)
        {
            GD.Print($"[ChangelogModalUI] Error refreshing feed: {ex.Message}");
            if (_statusBadge != null)
            {
                _statusBadge.Text = $"{_activeVersion} • Offline";
                _statusBadge.AddThemeColorOverride("font_color", new Color(0.6f, 0.6f, 0.6f, 0.7f));
            }
        }
        finally
        {
            _isLoading = false;
        }
    }

    public void SetChangelogContent(string markdown)
    {
        if (_richTextLabel != null)
        {
            _richTextLabel.Text = ConvertMarkdownToBbcode(markdown);
        }
    }

    public static string ConvertMarkdownToBbcode(string markdown)
    {
        if (string.IsNullOrEmpty(markdown)) return "";
        string bb = markdown;

        // Headers
        bb = Regex.Replace(bb, @"^###\s+(.+)$", "[font_size=18][b][color=#F59E0B]$1[/color][/b][/font_size]\n", RegexOptions.Multiline);
        bb = Regex.Replace(bb, @"^####\s+(.+)$", "[font_size=15][b][color=#E0E7FF]$1[/color][/b][/font_size]", RegexOptions.Multiline);

        // Bold & Italic
        bb = Regex.Replace(bb, @"\*\*(.+?)\*\*", "[b]$1[/b]");
        bb = Regex.Replace(bb, @"\*(.+?)\*", "[i]$1[/i]");

        // Inline Code
        bb = Regex.Replace(bb, @"`(.+?)`", "[color=#A5B4FC][code]$1[/code][/color]");

        // Bullet points
        bb = Regex.Replace(bb, @"^(\s*)\*\s+", "$1• ", RegexOptions.Multiline);
        bb = Regex.Replace(bb, @"^(\s*)-\s+", "$1  - ", RegexOptions.Multiline);

        // Links [text](url)
        bb = Regex.Replace(bb, @"\[(.*?)\]\((.*?)\)", "[url=$2][color=#60A5FA]$1[/color][/url]");

        return bb;
    }

    private void OnWebsiteClicked()
    {
        OS.ShellOpen(_websiteUrl);
    }

    private void OnDiscordClicked()
    {
        DisplayServer.ClipboardSet(_discordUsername);
        if (_btnDiscord != null)
        {
            _btnDiscord.Text = "✓ Copied!";
            GetTree().CreateTimer(2.0).Timeout += () =>
            {
                if (GodotObject.IsInstanceValid(_btnDiscord))
                    _btnDiscord.Text = $"💬 Discord: {_discordUsername}";
            };
        }
    }

    private void OnMetaClicked(Variant meta)
    {
        if (meta.VariantType == Variant.Type.String)
        {
            OS.ShellOpen(meta.AsString());
        }
    }

    private void OnMetaHoverStarted(Variant meta)
    {
        Input.SetDefaultCursorShape(Input.CursorShape.PointingHand);
    }

    private void OnMetaHoverEnded(Variant meta)
    {
        Input.SetDefaultCursorShape(Input.CursorShape.Arrow);
    }

    public void Open()
    {
        Visible = true;
        MoveToFront();
        MarkVersionAsRead(_activeVersion);
        StudioUIManager.Instance?.UpdateNewsBadgeState();
        _ = RefreshFeedAsync(false);
    }

    public void Close()
    {
        Visible = false;
        EmitSignal(SignalName.DialogClosed);
    }

    public static bool HasUnreadChangelog(string targetVersion = CurrentVersion)
    {
        var config = new ConfigFile();
        if (config.Load(ConfigPath) == Error.Ok)
        {
            string lastRead = (string)config.GetValue(ConfigSection, ConfigKey, "");
            return !targetVersion.Equals(lastRead, StringComparison.OrdinalIgnoreCase);
        }
        return true;
    }

    public static void MarkVersionAsRead(string targetVersion = CurrentVersion)
    {
        var config = new ConfigFile();
        config.Load(ConfigPath);
        config.SetValue(ConfigSection, ConfigKey, targetVersion);
        config.Save(ConfigPath);
    }
}
