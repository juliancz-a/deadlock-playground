using Godot;
using System;

public partial class FeedbackDialog : PanelContainer
{
    [Signal] public delegate void DialogClosedEventHandler();

    private Button _btnClose;
    private Button _btnBottomClose;
    private Button _btnGithub;
    private Button _btnDiscord;
    private Button _btnCopyDiscord;
    private Label _lblDiscordCopied;

    private const string GithubUrl = "https://github.com/juliancz-a/deadlock-playground";
    private const string DiscordUsername = "juliancz_";

    public override void _Ready()
    {
        _btnClose = GetNodeOrNull<Button>("MarginContainer/VBoxContainer/HeaderBar/BtnClose");
        _btnBottomClose = GetNodeOrNull<Button>("MarginContainer/VBoxContainer/BottomBar/BtnBottomClose");
        _btnGithub = GetNodeOrNull<Button>("MarginContainer/VBoxContainer/ContentBox/GithubCard/HBox/BtnGithub");
        _btnDiscord = GetNodeOrNull<Button>("MarginContainer/VBoxContainer/ContentBox/DiscordCard/HBox/BtnDiscord");
        _btnCopyDiscord = GetNodeOrNull<Button>("MarginContainer/VBoxContainer/ContentBox/DiscordCard/HBox/BtnCopyDiscord");
        _lblDiscordCopied = GetNodeOrNull<Label>("MarginContainer/VBoxContainer/ContentBox/DiscordCard/HBox/VBox/LblDiscordCopied");

        if (_btnClose != null) _btnClose.Pressed += Close;
        if (_btnBottomClose != null) _btnBottomClose.Pressed += Close;

        if (_btnGithub != null)
        {
            _btnGithub.Pressed += () => OS.ShellOpen(GithubUrl);
        }

        if (_btnDiscord != null)
        {
            _btnDiscord.Pressed += OnCopyDiscordPressed;
        }

        if (_btnCopyDiscord != null)
        {
            _btnCopyDiscord.Pressed += OnCopyDiscordPressed;
        }
    }

    private void OnCopyDiscordPressed()
    {
        DisplayServer.ClipboardSet(DiscordUsername);
        if (_lblDiscordCopied != null)
        {
            _lblDiscordCopied.Visible = true;
            _lblDiscordCopied.Text = "Username copied to clipboard!";
            
            var timer = GetTree()?.CreateTimer(2.5f);
            if (timer != null)
            {
                timer.Timeout += () =>
                {
                    if (GodotObject.IsInstanceValid(_lblDiscordCopied))
                    {
                        _lblDiscordCopied.Visible = false;
                    }
                };
            }
        }
    }

    public void Open()
    {
        Visible = true;
        MoveToFront();
        if (_lblDiscordCopied != null)
        {
            _lblDiscordCopied.Visible = false;
        }
    }

    public void Close()
    {
        Visible = false;
        EmitSignal(SignalName.DialogClosed);
    }
}
