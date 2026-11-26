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
    public const string LastSeenAppVersionKey = "LastSeenAppVersion";
    public const string DefaultWebsiteUrl = "https://deadlock-playground.top/";
    public const string DefaultDiscordUrl = "https://discord.gg/tWHuT6ywV";

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

    private string _activeVersion = GetNormalizedLocalVersion();
    private string _websiteUrl = DefaultWebsiteUrl;
    private string _discordUrl = DefaultDiscordUrl;
    private bool _isLoading = false;

    public string ActiveVersion => _activeVersion;

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
            _btnDiscord.Text = "Discord";
            _btnDiscord.TooltipText = $"Join the official Deadlock Playground Discord ({_discordUrl})";
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
            _statusBadge.ThemeTypeVariation = "BadgeLabelWarning";
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
                if (payload.Links.TryGetValue("discord", out var discUrl) && !string.IsNullOrEmpty(discUrl))
                {
                    _discordUrl = discUrl.Trim();
                    if (_btnDiscord != null)
                    {
                        _btnDiscord.Text = "Discord";
                        _btnDiscord.TooltipText = $"Join the official Deadlock Playground Discord ({_discordUrl})";
                    }
                }

                if (_statusBadge != null)
                {
                    if (payload.IsFromRemote)
                    {
                        _statusBadge.Text = $"{_activeVersion} • Online";
                        _statusBadge.ThemeTypeVariation = "BadgeLabelSuccess";
                    }
                    else if (payload.IsFromCache)
                    {
                        _statusBadge.Text = $"{_activeVersion} • Cached";
                        _statusBadge.ThemeTypeVariation = "MutedLabel";
                    }
                    else
                    {
                        _statusBadge.Text = $"{_activeVersion} • Offline";
                        _statusBadge.ThemeTypeVariation = "MutedLabel";
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
                _statusBadge.ThemeTypeVariation = "MutedLabel";
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

        string normalized = markdown.Replace("\r\n", "\n").Replace("\r", "\n");
        string[] lines = normalized.Split('\n');
        var sb = new System.Text.StringBuilder();
        bool inCodeBlock = false;

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            string trimmed = line.Trim();

            // Fenced code blocks (```)
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                if (!inCodeBlock)
                {
                    inCodeBlock = true;
                    sb.Append("[color=#A5B4FC][code]");
                }
                else
                {
                    inCodeBlock = false;
                    sb.AppendLine("[/code][/color]");
                }
                continue;
            }

            if (inCodeBlock)
            {
                sb.AppendLine(line);
                continue;
            }

            // Blank line
            if (trimmed.Length == 0)
            {
                sb.AppendLine();
                continue;
            }

            // Gracefully unwrap accidental "* ## Header" or "- ---"
            string unwrapped = Regex.Replace(trimmed, @"^[-*•]\s+(?=(?:#{1,6}\s+|(?:---+|===+|\*\*\*+|___+)$))", "");

            // Horizontal rule / separator (---, ***, ___, ===)
            if (Regex.IsMatch(unwrapped, @"^(?:-{3,}|={3,}|\*{3,}|_{3,})$"))
            {
                sb.AppendLine("[color=#3D524A]──────────────────────────────────────────────────[/color]");
                continue;
            }

            // Headers (# to ######)
            var headerMatch = Regex.Match(unwrapped, @"^(#{1,6})\s+(.+)$");
            if (headerMatch.Success)
            {
                int level = headerMatch.Groups[1].Value.Length;
                string title = FormatInlineMarkdownToBbcode(headerMatch.Groups[2].Value.Trim());
                string formattedHeader = level switch
                {
                    1 => $"[font_size=21][b][color=#EFDEBF]{title}[/color][/b][/font_size]",
                    2 => $"[font_size=18][b][color=#F59E0B]{title}[/color][/b][/font_size]",
                    3 => $"[font_size=16][b][color=#F59E0B]{title}[/color][/b][/font_size]",
                    4 => $"[font_size=14][b][color=#E0E7FF]{title}[/color][/b][/font_size]",
                    _ => $"[font_size=13][b][color=#94A3B8]{title}[/color][/b][/font_size]"
                };
                sb.AppendLine(formattedHeader);
                continue;
            }

            // Blockquote (> ...)
            var quoteMatch = Regex.Match(trimmed, @"^>\s?(.*)$");
            if (quoteMatch.Success)
            {
                string quoteText = FormatInlineMarkdownToBbcode(quoteMatch.Groups[1].Value);
                sb.AppendLine($"[color=#94A3B8][i]│ {quoteText}[/i][/color]");
                continue;
            }

            // Ordered list (1. item)
            var orderedMatch = Regex.Match(line, @"^(\s*)(\d+)\.\s+(.+)$");
            if (orderedMatch.Success)
            {
                int indentSpaces = orderedMatch.Groups[1].Value.Replace("\t", "  ").Length;
                string indent = indentSpaces >= 2 ? new string(' ', Math.Min(indentSpaces, 8)) : "";
                string num = orderedMatch.Groups[2].Value;
                string itemText = FormatInlineMarkdownToBbcode(orderedMatch.Groups[3].Value);
                sb.AppendLine($"{indent}[color=#F59E0B]{num}.[/color] {itemText}");
                continue;
            }

            // Unordered list (*, -, +, •)
            var bulletMatch = Regex.Match(line, @"^(\s*)[-*+•]\s+(.+)$");
            if (bulletMatch.Success)
            {
                int indentSpaces = bulletMatch.Groups[1].Value.Replace("\t", "  ").Length;
                string itemText = FormatInlineMarkdownToBbcode(bulletMatch.Groups[2].Value);
                if (indentSpaces >= 2)
                {
                    string indent = new string(' ', Math.Min(indentSpaces + 2, 10));
                    sb.AppendLine($"{indent}[color=#94A3B8]◦[/color] {itemText}");
                }
                else
                {
                    sb.AppendLine($"[color=#72947F]•[/color] {itemText}");
                }
                continue;
            }

            // Regular paragraph line
            sb.AppendLine(FormatInlineMarkdownToBbcode(line));
        }

        if (inCodeBlock)
        {
            sb.Append("[/code][/color]");
        }

        return sb.ToString().TrimEnd();
    }

    private static string FormatInlineMarkdownToBbcode(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";

        // Protect inline code spans so *, _, [, ] inside `code` are not parsed as formatting
        var codeSpans = new System.Collections.Generic.List<string>();
        string result = Regex.Replace(text, @"`([^`]+?)`", m =>
        {
            int idx = codeSpans.Count;
            codeSpans.Add($"[color=#A5B4FC][code]{m.Groups[1].Value}[/code][/color]");
            return $"\u0000CODE{idx}\u0000";
        });

        // Links [text](url)
        result = Regex.Replace(result, @"\[([^\]]+?)\]\(([^)]+?)\)", "[url=$2][color=#60A5FA]$1[/color][/url]");

        // Bold + Italic (***text***)
        result = Regex.Replace(result, @"\*\*\*(.+?)\*\*\*", "[b][i]$1[/i][/b]");

        // Bold (**text** or __text__)
        result = Regex.Replace(result, @"\*\*(.+?)\*\*", "[b]$1[/b]");
        result = Regex.Replace(result, @"(?<!\w)__(.+?)__(?!\w)", "[b]$1[/b]");

        // Italic (*text* or _text_ bounded by non-word chars to preserve snake_case)
        result = Regex.Replace(result, @"\*(.+?)\*", "[i]$1[/i]");
        result = Regex.Replace(result, @"(?<!\w)_(.+?)_(?!\w)", "[i]$1[/i]");

        // Strikethrough (~~text~~)
        result = Regex.Replace(result, @"~~(.+?)~~", "[s]$1[/s]");

        // Restore inline code spans
        for (int i = 0; i < codeSpans.Count; i++)
        {
            result = result.Replace($"\u0000CODE{i}\u0000", codeSpans[i]);
        }

        return result;
    }

    private void OnWebsiteClicked()
    {
        OS.ShellOpen(_websiteUrl);
    }

    private void OnDiscordClicked()
    {
        OS.ShellOpen(_discordUrl);
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

    public void Open(bool forceRefresh = false)
    {
        Visible = true;
        MoveToFront();
        MarkVersionAsRead(_activeVersion);
        MarkAppVersionSeen();
        StudioUIManager.Instance?.UpdateNewsBadgeState();
        _ = RefreshFeedAsync(forceRefresh);
    }

    public void Close()
    {
        Visible = false;
        EmitSignal(SignalName.DialogClosed);
    }

    public static string GetNormalizedLocalVersion()
    {
        string local = UpdateChecker.GetLocalVersion();
        if (string.IsNullOrWhiteSpace(local))
        {
            return CurrentVersion;
        }
        local = local.Trim();
        return local.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? local : $"v{local}";
    }

    /// <summary>
    /// Evaluates at startup whether the application was updated since the last launch
    /// (via either the in-app updater or a manual update) and should automatically show the changelog once.
    /// Must be called before first-run settings are written so fresh installs are not treated as updates.
    /// </summary>
    public static bool EvaluatePendingUpdateAutoOpen()
    {
        string currentVersion = GetNormalizedLocalVersion();

        if (UpdateChecker.IsPostUpdateLaunch)
        {
            return true;
        }

        var config = new ConfigFile();
        bool configExists = config.Load(ConfigPath) == Error.Ok;
        if (!configExists)
        {
            // Fresh install: record current version so future updates trigger auto-open,
            // without popping up the changelog on first launch.
            MarkAppVersionSeen(currentVersion);
            return false;
        }

        string lastSeen = (string)config.GetValue(ConfigSection, LastSeenAppVersionKey, "");
        if (string.IsNullOrWhiteSpace(lastSeen))
        {
            // Existing user upgrading from a build prior to LastSeenAppVersion tracking.
            string lastRead = (string)config.GetValue(ConfigSection, ConfigKey, "");
            if (currentVersion.Equals(lastRead, StringComparison.OrdinalIgnoreCase))
            {
                MarkAppVersionSeen(currentVersion);
                return false;
            }
            return true;
        }

        if (SemanticVersion.TryParse(currentVersion, out var currentSemVer) &&
            SemanticVersion.TryParse(lastSeen, out var lastSeenSemVer))
        {
            if (currentSemVer > lastSeenSemVer)
            {
                return true;
            }
            if (currentSemVer < lastSeenSemVer)
            {
                // Keep stored version aligned if user intentionally switched builds
                MarkAppVersionSeen(currentVersion);
            }
            return false;
        }

        return !currentVersion.Equals(lastSeen, StringComparison.OrdinalIgnoreCase);
    }

    public static void MarkAppVersionSeen(string? targetVersion = null)
    {
        string effectiveVersion = string.IsNullOrWhiteSpace(targetVersion)
            ? GetNormalizedLocalVersion()
            : targetVersion.Trim();
        var config = new ConfigFile();
        config.Load(ConfigPath);
        config.SetValue(ConfigSection, LastSeenAppVersionKey, effectiveVersion);
        config.Save(ConfigPath);
    }

    public static bool HasUnreadChangelog(string? targetVersion = null)
    {
        string effectiveVersion = string.IsNullOrWhiteSpace(targetVersion)
            ? GetNormalizedLocalVersion()
            : targetVersion;
        var config = new ConfigFile();
        if (config.Load(ConfigPath) == Error.Ok)
        {
            string lastRead = (string)config.GetValue(ConfigSection, ConfigKey, "");
            return !effectiveVersion.Equals(lastRead, StringComparison.OrdinalIgnoreCase);
        }
        return true;
    }

    public static void MarkVersionAsRead(string? targetVersion = null)
    {
        string effectiveVersion = string.IsNullOrWhiteSpace(targetVersion)
            ? GetNormalizedLocalVersion()
            : targetVersion;
        var config = new ConfigFile();
        config.Load(ConfigPath);
        config.SetValue(ConfigSection, ConfigKey, effectiveVersion);
        config.Save(ConfigPath);
    }
}
