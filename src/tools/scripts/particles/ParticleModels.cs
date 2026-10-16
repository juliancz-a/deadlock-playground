using System;
using System.Collections.Generic;

namespace DeadlockPlayground.Particles.Models;

/// <summary>
/// Represents a single particle asset entry associated with an ability variant, modifier, or weapon hook.
/// </summary>
public record ParticleEntry(
    string VariantName,
    string VpcfPath
);

/// <summary>
/// Represents a collection of particles belonging to an ability or weapon set.
/// </summary>
public record AbilityParticleGroup(
    string AbilityKey,
    string LocalizationToken,
    IReadOnlyList<ParticleEntry> Particles
);

/// <summary>
/// Root index mapping hero keys ("Dynamo", "Lash", "Infernus", etc.) to their abilities and particles.
/// </summary>
public record HeroParticleIndex(
    IReadOnlyDictionary<string, IReadOnlyList<AbilityParticleGroup>> Heroes
);
