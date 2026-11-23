using System.Collections.Generic;
using Godot;

namespace DeadlockPlayground.Importer
{
    /// <summary>
    /// Strategy interface for generating finger landmarks from hand geometry.
    /// Allows swapping between full pin-based placement (PinBasedFingersStrategy)
    /// and future tip-driven interpolation (e.g. TipDrivenFingers).
    /// </summary>
    public interface IFingerRigStrategy
    {
        string Id { get; }
        string DisplayName { get; }

        Dictionary<string, Vector3> DetectLandmarks(
            Node3D importedScene,
            IReadOnlyList<MeshInstance3D> meshes,
            Dictionary<string, Vector3> bodyPins);

        void MirrorLandmarks(
            Dictionary<string, Vector3> pins,
            bool leftToRight,
            Vector3 modelCenter);
    }

    /// <summary>
    /// Default pin-based finger landmark generator backed by <see cref="CustomFingerAutoRigger"/>.
    /// </summary>
    public sealed class PinBasedFingersStrategy : IFingerRigStrategy
    {
        public string Id => "pin_based";
        public string DisplayName => "Pin-Based (5x3 Phalanges)";

        public Dictionary<string, Vector3> DetectLandmarks(
            Node3D importedScene,
            IReadOnlyList<MeshInstance3D> meshes,
            Dictionary<string, Vector3> bodyPins)
        {
            return CustomFingerAutoRigger.AutoDetectFingerLandmarks(importedScene, meshes, bodyPins);
        }

        public void MirrorLandmarks(
            Dictionary<string, Vector3> pins,
            bool leftToRight,
            Vector3 modelCenter)
        {
            CustomFingerAutoRigger.MirrorFingerLandmarks(pins, leftToRight, modelCenter);
        }
    }
}

