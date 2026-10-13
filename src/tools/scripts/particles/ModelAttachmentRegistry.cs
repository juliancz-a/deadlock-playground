using System;
using System.Collections.Generic;
using Godot;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.ResourceTypes.ModelData.Attachments;

namespace DeadlockPlayground.Particles;

/// <summary>
/// Authoritative metadata for a Source 2 model attachment socket.
/// </summary>
public class ModelAttachmentInfo
{
    public string Name { get; set; } = string.Empty;
    public string BoneName { get; set; } = string.Empty;
    public Vector3 LocalOffset { get; set; } = Vector3.Zero;
    public Quaternion LocalRotation { get; set; } = Quaternion.Identity;
}

/// <summary>
/// Central registry for extracting, caching, and querying model attachment sockets
/// directly from Source 2 .vmdl_c definitions with zero hardcoding or guessing.
/// </summary>
public static class ModelAttachmentRegistry
{
    private const float InchesToMeters = 0.0254f;
    private static readonly Dictionary<int, Dictionary<string, ModelAttachmentInfo>> _heroAttachmentCache = new();

    /// <summary>
    /// Registers all attachment sockets from a compiled VRF Model onto the target Godot hero node.
    /// </summary>
    public static void RegisterAttachments(Node3D heroNode, Model vrfModel)
    {
        if (heroNode == null || vrfModel == null || vrfModel.Attachments == null) return;

        var registry = new Dictionary<string, ModelAttachmentInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var kv in vrfModel.Attachments)
        {
            string attName = kv.Key;
            var att = kv.Value;
            if (att == null || att.Length == 0) continue;

            var primaryInfluence = att[0];
            string boneName = primaryInfluence.Name;

            // Source 2 uses Z-up in inches; Godot uses Y-up in meters.
            // Vector3 conversion: (X inches -> X meters, Z inches -> Y meters, -Y inches -> Z meters)
            var srcOffset = primaryInfluence.Offset;
            var godotOffset = new Vector3(
                srcOffset.X * InchesToMeters,
                srcOffset.Z * InchesToMeters,
                -srcOffset.Y * InchesToMeters
            );

            // Convert Source 2 rotation quaternion to Godot bone orientation
            var srcRot = primaryInfluence.Rotation;
            var godotRot = new Quaternion(srcRot.X, srcRot.Z, -srcRot.Y, srcRot.W).Normalized();

            registry[attName] = new ModelAttachmentInfo
            {
                Name = attName,
                BoneName = boneName,
                LocalOffset = godotOffset,
                LocalRotation = godotRot
            };
        }

        int heroId = heroNode.GetInstanceId().GetHashCode();
        _heroAttachmentCache[heroId] = registry;

        GD.Print($"[ModelAttachmentRegistry] Registered {registry.Count} authoritative attachment sockets for '{heroNode.Name}'.");
    }

    /// <summary>
    /// Attempts to retrieve an attachment socket by name for a specific hero node.
    /// </summary>
    public static bool TryGetAttachment(Node3D heroNode, string attachmentName, out ModelAttachmentInfo info)
    {
        info = null;
        if (heroNode == null || string.IsNullOrWhiteSpace(attachmentName)) return false;

        int heroId = heroNode.GetInstanceId().GetHashCode();
        if (_heroAttachmentCache.TryGetValue(heroId, out var registry))
        {
            return registry.TryGetValue(attachmentName, out info);
        }

        return false;
    }

    /// <summary>
    /// Cleans up cached attachment records when a hero is unloaded.
    /// </summary>
    public static void ClearHeroAttachments(Node3D heroNode)
    {
        if (heroNode == null) return;
        int heroId = heroNode.GetInstanceId().GetHashCode();
        _heroAttachmentCache.Remove(heroId);
    }
}
