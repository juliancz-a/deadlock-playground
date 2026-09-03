using Godot;
using System;

public partial class IntroScript : Control
{
	private AnimationPlayer _animationPlayer;
	public override void _Ready()
	{
		_animationPlayer = GetNode<AnimationPlayer>("AnimationPlayer");
		
		_animationPlayer.AnimationFinished += OnAnimationFinished;
	}

	public void OnAnimationFinished(StringName animName)
	{
		if (animName == "intro")
		{
			GetTree().ChangeSceneToFile("res://main.tscn");
		}
	}
}
