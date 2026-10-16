using Godot;
using System;

namespace DeadlockPlayground.Particles;

public enum PlaybackMode
{
    Animation,
    Particle
}

/// <summary>
/// Bridges PlayerTabUI timeline controls (scrub slider, play/pause, stop, speed)
/// to deterministic particle simulation in ParticleStage3D.
/// </summary>
public class ParticlePlaybackBridge
{
    private readonly ParticleStage3D _particleStage;
    private PlaybackMode _currentMode = PlaybackMode.Animation;

    public PlaybackMode CurrentMode => _currentMode;
    public bool IsParticleMode => _currentMode == PlaybackMode.Particle;

    public ParticlePlaybackBridge(ParticleStage3D particleStage)
    {
        _particleStage = particleStage;
        if (_particleStage != null)
        {
            _particleStage.ParticleLoaded += OnParticleLoaded;
            _particleStage.ParticleUnloaded += OnParticleUnloaded;
        }
    }

    private void OnParticleLoaded(string vpcfPath, string variantName, string abilityName)
    {
        _currentMode = PlaybackMode.Particle;
    }

    private void OnParticleUnloaded()
    {
        _currentMode = PlaybackMode.Animation;
    }

    public void OnPlayPauseToggled(bool isPlaying)
    {
        if (!IsParticleMode || _particleStage == null) return;

        if (isPlaying)
        {
            _particleStage.Play();
        }
        else
        {
            _particleStage.Pause();
        }
    }

    public void OnStopPressed()
    {
        if (!IsParticleMode || _particleStage == null) return;
        _particleStage.Stop();
    }

    public void OnTimelineSliderValueChanged(double value)
    {
        if (!IsParticleMode || _particleStage == null) return;
        _particleStage.Seek((float)value);
    }

    public void OnSpeedSelected(float speedMultiplier)
    {
        if (!IsParticleMode || _particleStage == null) return;
        _particleStage.SetSpeed(speedMultiplier);
    }

    public void OnLoopToggled(bool isLooping)
    {
        if (!IsParticleMode || _particleStage == null) return;
        _particleStage.SetLooping(isLooping);
    }
}
