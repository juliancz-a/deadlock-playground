using System;
using System.Collections.Generic;
using Godot;

namespace DeadlockPlayground.Importer
{
    /// <summary>
    /// Interactive 3D Fitting Room overlay for Deadlock Playground.
    /// Displays the currently loaded Deadlock hero as a semi-transparent ghost reference mannequin (cyan/gold X-Ray silhouette),
    /// provides interactive alignment controls (scale, position offset, rotation), and enables auto-fitting to hero height.
    /// </summary>
    public partial class CustomModelFitter3D : Node3D
    {
        public Node3D HeroNode { get; private set; }
        public Skeleton3D HeroSkeleton { get; private set; }
        public Node3D ImportedScene { get; private set; }
        public Node3D FitterRoot { get; private set; }
        public bool IsFittingActive { get; private set; } = false;

        public float UniformScale { get; set; } = 1.0f;
        public Vector3 PositionOffset { get; set; } = Vector3.Zero;
        public new Vector3 RotationDegrees { get; set; } = Vector3.Zero;

        private readonly Dictionary<MeshInstance3D, Material[]> _originalMaterials = new();
        private StandardMaterial3D _ghostMaterial;

        public override void _Ready()
        {
            _ghostMaterial = new StandardMaterial3D
            {
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                AlbedoColor = new Color(0.18f, 0.75f, 0.95f, 0.32f), // Cyan/gold ghost silhouette
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Always
            };

            FitterRoot = new Node3D { Name = "FitterRoot" };
            AddChild(FitterRoot);
        }

        /// <summary>
        /// Starts the 3D Fitting Room session, turning the hero into a ghost mannequin and attaching the imported scene.
        /// </summary>
        public void StartFitting(Node3D heroNode, Node3D importedScene)
        {
            HeroNode = heroNode;
            ImportedScene = importedScene;
            HeroSkeleton = CustomModelAutoRigger.FindSkeleton(heroNode);

            if (ImportedScene != null)
            {
                if (ImportedScene.GetParent() != null)
                {
                    ImportedScene.GetParent().RemoveChild(ImportedScene);
                }
                FitterRoot.AddChild(ImportedScene);

                // Clear Layer 21 from imported preview meshes
                foreach (var mi in CustomModelAutoRigger.CollectMeshes(ImportedScene))
                {
                    mi.Layers &= ~(uint)(1 << 20);
                }
            }

            ApplyGhostMannequinToHero();
            IsFittingActive = true;

            // Automatically run initial auto-fit
            AutoFitToHero();
        }

        /// <summary>
        /// Computes bounding boxes of the hero and imported model, scales to match hero height,
        /// and centers X/Z and floor Y=0.
        /// </summary>
        public void AutoFitToHero()
        {
            if (HeroNode == null || ImportedScene == null) return;

            var (heroMin, heroMax, heroOk) = CustomModelAutoRigger.ComputeWorldVertexBounds(HeroNode);
            var (modelMin, modelMax, modelOk) = CustomModelAutoRigger.ComputeWorldVertexBounds(ImportedScene);

            float heroHeight = heroOk ? (heroMax.Y - heroMin.Y) : 1.8f;
            float modelHeight = modelOk ? (modelMax.Y - modelMin.Y) : 1.0f;

            if (modelHeight < 0.0001f)
            {
                UniformScale = 1.0f;
                PositionOffset = Vector3.Zero;
                RotationDegrees = Vector3.Zero;
                ApplyTransformToFitter();
                return;
            }

            UniformScale = heroHeight / modelHeight;

            Vector3 heroCenter = (heroMin + heroMax) * 0.5f;
            Vector3 modelCenter = (modelMin + modelMax) * 0.5f;

            float posX = heroCenter.X - (modelCenter.X * UniformScale);
            float posZ = heroCenter.Z - (modelCenter.Z * UniformScale);
            float posY = heroMin.Y - (modelMin.Y * UniformScale);

            PositionOffset = new Vector3(posX, posY, posZ);
            RotationDegrees = Vector3.Zero;

            ApplyTransformToFitter();
            GD.Print($"[CustomModelFitter3D] Auto-fit applied: Scale={UniformScale:F3}, Pos={PositionOffset}");
        }

        public void UpdateTransform(float scale, Vector3 pos, Vector3 rotDeg)
        {
            UniformScale = Mathf.Max(scale, 0.001f);
            PositionOffset = pos;
            RotationDegrees = rotDeg;
            ApplyTransformToFitter();
        }

        private void ApplyTransformToFitter()
        {
            if (FitterRoot == null) return;

            Transform3D t = Transform3D.Identity;
            t = t.Scaled(new Vector3(UniformScale, UniformScale, UniformScale));
            t = t.Rotated(Vector3.Up, Mathf.DegToRad(RotationDegrees.Y));
            t = t.Rotated(Vector3.Right, Mathf.DegToRad(RotationDegrees.X));
            t = t.Rotated(Vector3.Forward, Mathf.DegToRad(RotationDegrees.Z));
            t.Origin = PositionOffset;

            FitterRoot.Transform = t;
        }

        public Transform3D GetAlignedWorldTransform()
        {
            return FitterRoot?.GlobalTransform ?? Transform3D.Identity;
        }

        public void ApplyGhostMannequinToHero()
        {
            if (HeroNode == null) return;
            _originalMaterials.Clear();

            var meshes = CustomModelAutoRigger.CollectMeshes(HeroNode);
            foreach (var mi in meshes)
            {
                if (mi.Mesh == null || mi.HasMeta("IsCustomImportedMesh")) continue;

                int sCount = mi.Mesh.GetSurfaceCount();
                Material[] origMats = new Material[sCount];

                for (int s = 0; s < sCount; s++)
                {
                    origMats[s] = mi.GetSurfaceOverrideMaterial(s) ?? mi.Mesh.SurfaceGetMaterial(s);
                    mi.SetSurfaceOverrideMaterial(s, _ghostMaterial);
                }

                _originalMaterials[mi] = origMats;
                mi.Layers &= ~(uint)(1 << 20); // Keep Layer 21 cleared
            }
        }

        public void RestoreHeroMaterials()
        {
            foreach (var (mi, mats) in _originalMaterials)
            {
                if (mi == null || !GodotObject.IsInstanceValid(mi) || mi.Mesh == null) continue;

                for (int s = 0; s < mats.Length && s < mi.Mesh.GetSurfaceCount(); s++)
                {
                    mi.SetSurfaceOverrideMaterial(s, mats[s]);
                }
            }
            _originalMaterials.Clear();
        }

        public void EndFitting(bool confirmed)
        {
            RestoreHeroMaterials();
            IsFittingActive = false;

            if (ImportedScene != null && GodotObject.IsInstanceValid(ImportedScene))
            {
                if (!confirmed)
                {
                    ImportedScene.QueueFree();
                }
                else
                {
                    if (ImportedScene.GetParent() == FitterRoot)
                    {
                        FitterRoot.RemoveChild(ImportedScene);
                    }
                }
            }

            QueueFree();
        }

        private static Aabb ComputeNodeAabb(Node3D root)
        {
            Aabb totalAabb = new Aabb(Vector3.Zero, Vector3.Zero);
            bool first = true;

            var meshes = CustomModelAutoRigger.CollectMeshes(root);
            foreach (var mi in meshes)
            {
                if (mi.Mesh == null) continue;
                Aabb localAabb = mi.Mesh.GetAabb();
                Aabb transformedAabb = mi.Transform * localAabb;

                if (first)
                {
                    totalAabb = transformedAabb;
                    first = false;
                }
                else
                {
                    totalAabb = totalAabb.Merge(transformedAabb);
                }
            }

            return totalAabb;
        }
    }
}
