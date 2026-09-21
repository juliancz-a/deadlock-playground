using Godot;
using System;

public partial class AboutDialog : PanelContainer
{
	[Export] private RichTextLabel _AboutText;
	
	public override void _Ready()
	{
		_AboutText.MetaClicked += OnMetaClicked;
		_AboutText.MetaHoverStarted += OnMetaHoverStarted;
		_AboutText.MetaHoverEnded += OnMetaHoverEnded;
	}

	private void OnMetaClicked(Godot.Variant meta)
	{
		if (meta.VariantType == Variant.Type.String)
		{
			OS.ShellOpen(meta.AsString());
		}
		
	}

	private void OnMetaHoverStarted(Godot.Variant meta)
	{
		Input.SetDefaultCursorShape(Input.CursorShape.PointingHand);
	}

	private void OnMetaHoverEnded(Godot.Variant meta)
	{
		Input.SetDefaultCursorShape(Input.CursorShape.Arrow);
	}
}
