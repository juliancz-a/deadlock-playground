using Godot;
using System;
using System.Text.RegularExpressions;

public partial class ChangelogModalUI : PanelContainer
{
    [Signal] public delegate void DialogClosedEventHandler();

    public const string CurrentVersion = "v1.0.1";
    public const string ConfigPath = "user://settings.cfg";
    public const string ConfigSection = "News";
    public const string ConfigKey = "LastReadVersion";

    public const string InitialChangelogMarkdown = @"### Version 1.0.1 — New Heroes

#### New Content:
* Added support for 6 new update heroes from `models/heroes_wip/`:
  - Violet
  - Baba
  - Deadman Danny
  - Nurse Harrow
  - Ratking
  - Solomon
* Highlighted the new WIP roster in the Hero Selection menu with dedicated badges.";

    private Button _btnClose;
    private Button _btnBottomClose;
    private RichTextLabel _richTextLabel;
    private Label _titleLabel;

    public override void _Ready()
    {
        _titleLabel = GetNodeOrNull<Label>("MarginContainer/VBoxContainer/HeaderBar/Title")
                   ?? FindChild("Title", true, false) as Label;
        _btnClose = GetNodeOrNull<Button>("MarginContainer/VBoxContainer/HeaderBar/BtnClose")
                 ?? FindChild("BtnClose", true, false) as Button;
        _btnBottomClose = GetNodeOrNull<Button>("MarginContainer/VBoxContainer/BottomBar/BtnBottomClose")
                       ?? FindChild("BtnBottomClose", true, false) as Button;
        _richTextLabel = GetNodeOrNull<RichTextLabel>("MarginContainer/VBoxContainer/ContentCard/Margin/ScrollContainer/RichTextLabel")
                      ?? FindChild("RichTextLabel", true, false) as RichTextLabel;

        if (_btnClose != null) _btnClose.Pressed += Close;
        if (_btnBottomClose != null) _btnBottomClose.Pressed += Close;

        if (_richTextLabel != null)
        {
            _richTextLabel.BbcodeEnabled = true;
            _richTextLabel.MetaClicked += OnMetaClicked;
            _richTextLabel.MetaHoverStarted += OnMetaHoverStarted;
            _richTextLabel.MetaHoverEnded += OnMetaHoverEnded;
            SetChangelogContent(InitialChangelogMarkdown);
        }

        MouseFilter = MouseFilterEnum.Stop;
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
        MarkVersionAsRead(CurrentVersion);
        StudioUIManager.Instance?.UpdateNewsBadgeState();
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
