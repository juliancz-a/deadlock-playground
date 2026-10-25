using System;
using Godot;

namespace DeadlockPlayground.UI;

/// <summary>
/// Modal dialog displaying essential Deadlock Source 2 console commands for testing custom maps in-game.
/// </summary>
public partial class MapConsoleCommandsDialog : Window
{
    [Export] private Button _btnClose;
    [Export] private Label _statusToastLabel;

    public override void _Ready()
    {
        _btnClose ??= FindChild("BtnClose", true, false) as Button;
        _statusToastLabel ??= FindChild("StatusToastLabel", true, false) as Label;

        if (_btnClose != null)
        {
            _btnClose.Pressed += () => Visible = false;
        }

        CloseRequested += () => Visible = false;

        WireCopyButton("Row1/BtnCopy1", "Row1/Cmd");
        WireCopyButton("Row2/BtnCopy2", "Row2/Cmd");
        WireCopyButton("Row3/BtnCopy3", "Row3/Cmd");
        WireCopyButton("Row4/BtnCopy4", "Row4/Cmd");
    }

    private void WireCopyButton(string btnPath, string cmdPath)
    {
        var btn = FindChild(System.IO.Path.GetFileName(btnPath), true, false) as Button;
        var cmd = FindChild(System.IO.Path.GetFileName(cmdPath), true, false) as LineEdit;
        if (btn != null && cmd != null)
        {
            btn.Pressed += () => CopyCommand(cmd.Text);
        }
    }

    public void CopyCommand(string command)
    {
        DisplayServer.ClipboardSet(command);
        if (_statusToastLabel != null)
        {
            _statusToastLabel.Text = $"✓ Copied: \"{command}\"";
            _statusToastLabel.Modulate = new Color(0.2f, 1.0f, 0.5f);
        }
    }
}
