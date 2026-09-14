using Godot;
using System;
using System.Collections.Generic;

namespace DeadlockPlayground.Painter
{
    public class SubmeshNodeInfo
    {
        public MeshInstance3D Mesh { get; set; }
        public string RawName { get; set; }
        public string DisplayName { get; set; }
        public int SurfaceIndex { get; set; } = 0;
        public bool IsAccessory { get; set; }
        public bool IsSoloed { get; set; }
        public MeshRaycaster Raycaster { get; set; }
    }

    public partial class HeroMeshHierarchy : Node
    {
        [Signal] public delegate void HierarchyChangedEventHandler();
        [Signal] public delegate void TargetMeshChangedEventHandler(MeshInstance3D mesh, int surfaceIndex);

        private readonly List<SubmeshNodeInfo> _submeshes = new();
        public IReadOnlyList<SubmeshNodeInfo> Submeshes => _submeshes;

        private SubmeshNodeInfo _activeTarget;
        public SubmeshNodeInfo ActiveTarget => _activeTarget;

        private bool _isAnySoloed = false;
        private readonly Dictionary<MeshInstance3D, bool> _preSoloVisibility = new();

        // Track dynamically created submesh nodes and hidden multi-surface nodes for clean teardown
        private readonly List<MeshInstance3D> _generatedSubmeshNodes = new();
        private readonly List<MeshInstance3D> _hiddenOriginalNodes = new();

        public void ScanHero(Node3D heroNode)
        {
            CleanupGeneratedSubmeshes();

            _submeshes.Clear();
            _preSoloVisibility.Clear();
            _isAnySoloed = false;
            _activeTarget = null;

            if (heroNode == null)
            {
                NotifyHierarchyChanged();
                return;
            }

            // 1. Gather all candidate MeshInstance3D nodes first so modifying the tree doesn't affect recursion
            var candidateMeshes = new List<MeshInstance3D>();
            CollectCandidateMeshesRecursive(heroNode, candidateMeshes);

            // 2. Process each mesh: register candidate meshes directly preserving original skinning and materials
            foreach (var mi in candidateMeshes)
            {
                string rawName = mi.Name.ToString();
                int surfaceCount = mi.Mesh?.GetSurfaceCount() ?? 0;
                if (surfaceCount == 0) continue;

                bool isAcc = IsAccessoryKeyword(rawName);

                if (surfaceCount == 1)
                {
                    _submeshes.Add(new SubmeshNodeInfo
                    {
                        Mesh = mi,
                        RawName = rawName,
                        DisplayName = CleanName(rawName),
                        SurfaceIndex = 0,
                        IsAccessory = isAcc,
                        IsSoloed = false
                    });
                }
                else
                {
                    // Hide original composite multi-surface mesh while painter operates on separated surface meshes
                    mi.Visible = false;
                    mi.Layers &= ~(uint)(1 << 20);
                    _hiddenOriginalNodes.Add(mi);

                    for (int s = 0; s < surfaceCount; s++)
                    {
                        string surfaceRawName = (s == 0) ? rawName : $"{rawName}[{s}]";
                        string clean = CleanName(rawName);

                        string matName = ResolveSurfaceMaterialName(mi, s);
                        string display;
                        if (!string.IsNullOrEmpty(matName))
                        {
                            string cleanMat = CleanName(matName);
                            if (string.Equals(clean, cleanMat, StringComparison.OrdinalIgnoreCase))
                            {
                                display = (s == 0) ? clean : $"{clean} [{s}]";
                            }
                            else
                            {
                                display = $"{clean} ({cleanMat})";
                            }
                        }
                        else
                        {
                            display = (s == 0) ? clean : $"{clean} [{s}]";
                        }

                        // Extract surface geometry into an isolated MeshInstance3D sharing skin & skeleton
                        var newMesh = new ArrayMesh();
                        var arrays = mi.Mesh.SurfaceGetArrays(s);
                        newMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
                        var mat = mi.GetSurfaceOverrideMaterial(s) ?? mi.Mesh.SurfaceGetMaterial(s);
                        if (mat != null)
                        {
                            newMesh.SurfaceSetMaterial(0, mat);
                        }
                        newMesh.LightmapSizeHint = mi.Mesh.LightmapSizeHint != Vector2I.Zero ? mi.Mesh.LightmapSizeHint : new Vector2I(256, 256);

                        var newMi = new MeshInstance3D
                        {
                            Name = $"{mi.Name}_surf_{s}_{display.Replace(" ", "_").Replace("(", "").Replace(")", "")}",
                            Mesh = newMesh,
                            Skin = mi.Skin,
                            Skeleton = mi.Skeleton,
                            Transform = mi.Transform,
                            Layers = mi.Layers,
                            Visible = true
                        };

                        mi.GetParent()?.AddChild(newMi);
                        _generatedSubmeshNodes.Add(newMi);

                        _submeshes.Add(new SubmeshNodeInfo
                        {
                            Mesh = newMi,
                            RawName = surfaceRawName,
                            DisplayName = display,
                            SurfaceIndex = 0,
                            IsAccessory = isAcc,
                            IsSoloed = false
                        });
                    }
                }
            }

            // 3. Preprocess meshes: Ensure non-zero lightmap hints for GPU Texture Painter atlas packing
            EnsureLightmapUv2Coordinates();

            // 4. Select body or first submesh as default target
            if (_submeshes.Count > 0)
            {
                var bodySubmesh = _submeshes.Find(s => s.RawName.ToLowerInvariant().Contains("body")) ?? _submeshes[0];
                SelectTarget(bodySubmesh, bodySubmesh.SurfaceIndex);
            }

            NotifyHierarchyChanged();
        }

        private void EnsureLightmapUv2Coordinates()
        {
            foreach (var submesh in _submeshes)
            {
                var mi = submesh.Mesh;
                if (mi == null || mi.Mesh == null) continue;

                // Ensure non-zero lightmap size hint so OverlayAtlasManager packs the mesh into the atlas
                if (mi.Mesh.LightmapSizeHint == Vector2I.Zero)
                {
                    mi.Mesh.LightmapSizeHint = new Vector2I(256, 256);
                }
            }
        }

        private static void CollectCandidateMeshesRecursive(Node node, List<MeshInstance3D> results)
        {
            if (node is MeshInstance3D mi && mi.Mesh != null)
            {
                string lowerName = mi.Name.ToString().ToLowerInvariant();
                if (!lowerName.Contains("marker") && !lowerName.Contains("gizmo") && !lowerName.Contains("preview"))
                {
                    results.Add(mi);
                }
            }

            foreach (Node child in node.GetChildren())
            {
                CollectCandidateMeshesRecursive(child, results);
            }
        }

        private static bool IsAccessoryKeyword(string name)
        {
            string lower = name.ToLowerInvariant();
            return lower.Contains("acc") ||
                   lower.Contains("prop") ||
                   lower.Contains("weapon") ||
                   lower.Contains("hat") ||
                   lower.Contains("gear") ||
                   lower.Contains("glass") ||
                   lower.Contains("mask") ||
                   lower.Contains("cape") ||
                   lower.Contains("coat") ||
                   lower.Contains("card") ||
                   lower.Contains("bag") ||
                   lower.Contains("strap") ||
                   lower.Contains("holster") ||
                   lower.Contains("boot");
        }

        private static string CleanName(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "Mesh Part";

            string leaf = raw.Trim();
            int lastSlash = Math.Max(leaf.LastIndexOf('/'), leaf.LastIndexOf('\\'));
            if (lastSlash >= 0 && lastSlash < leaf.Length - 1)
            {
                leaf = leaf.Substring(lastSlash + 1);
            }

            int dotIndex = leaf.LastIndexOf('.');
            if (dotIndex > 0)
            {
                leaf = leaf.Substring(0, dotIndex);
            }

            string[] prefixes = new[] { "model_", "mesh_", "hero_", "mat_" };
            bool stripped;
            do
            {
                stripped = false;
                foreach (var p in prefixes)
                {
                    if (leaf.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                    {
                        leaf = leaf.Substring(p.Length);
                        stripped = true;
                    }
                }
            } while (stripped && leaf.Length > 0);

            string clean = leaf.TrimStart('.', '_', '-').Trim();
            if (string.IsNullOrEmpty(clean)) return "Mesh Part";

            string formatted = clean.Replace('_', ' ').Replace('-', ' ').Trim();
            if (string.IsNullOrEmpty(formatted)) return "Mesh Part";

            return char.ToUpperInvariant(formatted[0]) + formatted.Substring(1);
        }

        private static string ResolveSurfaceMaterialName(MeshInstance3D mi, int s)
        {
            if (mi == null) return null;
            var mat = mi.GetSurfaceOverrideMaterial(s) ?? mi.Mesh?.SurfaceGetMaterial(s);
            if (mat == null) return null;

            string name = mat.ResourceName;
            if (string.IsNullOrEmpty(name))
            {
                name = mat.ResourcePath;
            }

            if (!string.IsNullOrEmpty(name))
            {
                int lastSlash = Math.Max(name.LastIndexOf('/'), name.LastIndexOf('\\'));
                if (lastSlash >= 0 && lastSlash < name.Length - 1)
                {
                    name = name.Substring(lastSlash + 1);
                }
                int dot = name.IndexOf('.');
                if (dot > 0)
                {
                    name = name.Substring(0, dot);
                }
                return name;
            }
            return null;
        }

        public void SelectTarget(SubmeshNodeInfo info, int surfaceIndex = -1)
        {
            if (info == null || info.Mesh == null) return;
            _activeTarget = info;
            int targetSurface = (surfaceIndex >= 0) ? surfaceIndex : info.SurfaceIndex;
            NotifyTargetMeshChanged(info.Mesh, targetSurface);
        }

        public void SetMeshVisibility(SubmeshNodeInfo info, bool visible)
        {
            if (info == null || info.Mesh == null || !GodotObject.IsInstanceValid(info.Mesh)) return;
            info.Mesh.Visible = visible;
            NotifyHierarchyChanged();
        }

        public void ToggleSolo(SubmeshNodeInfo info)
        {
            if (info == null || info.Mesh == null || !GodotObject.IsInstanceValid(info.Mesh)) return;

            if (info.IsSoloed)
            {
                // Unsolo: Restore previous visibility states
                foreach (var s in _submeshes)
                {
                    s.IsSoloed = false;
                    if (s.Mesh != null && GodotObject.IsInstanceValid(s.Mesh))
                    {
                        if (_preSoloVisibility.TryGetValue(s.Mesh, out bool prevVis))
                        {
                            s.Mesh.Visible = prevVis;
                        }
                    }
                }
                _isAnySoloed = false;
                _preSoloVisibility.Clear();
            }
            else
            {
                // Solo: Save current states if not already soloing
                if (!_isAnySoloed)
                {
                    _preSoloVisibility.Clear();
                    foreach (var s in _submeshes)
                    {
                        if (s.Mesh != null && GodotObject.IsInstanceValid(s.Mesh))
                        {
                            _preSoloVisibility[s.Mesh] = s.Mesh.Visible;
                        }
                    }
                }

                foreach (var s in _submeshes)
                {
                    s.IsSoloed = (s == info);
                    if (s.Mesh != null && GodotObject.IsInstanceValid(s.Mesh))
                    {
                        s.Mesh.Visible = (s == info);
                    }
                }
                _isAnySoloed = true;
            }

            NotifyHierarchyChanged();
        }

        public void ShowAll()
        {
            _isAnySoloed = false;
            _preSoloVisibility.Clear();

            foreach (var s in _submeshes)
            {
                s.IsSoloed = false;
                if (s.Mesh != null && GodotObject.IsInstanceValid(s.Mesh))
                {
                    s.Mesh.Visible = true;
                }
            }
            NotifyHierarchyChanged();
        }

        public void HideAccessories()
        {
            _isAnySoloed = false;
            _preSoloVisibility.Clear();

            foreach (var s in _submeshes)
            {
                s.IsSoloed = false;
                if (s.Mesh != null && GodotObject.IsInstanceValid(s.Mesh))
                {
                    s.Mesh.Visible = !s.IsAccessory;
                }
            }
            NotifyHierarchyChanged();
        }

        public void CleanupGeneratedSubmeshes()
        {
            foreach (var node in _generatedSubmeshNodes)
            {
                if (node != null && GodotObject.IsInstanceValid(node))
                {
                    node.QueueFree();
                }
            }
            _generatedSubmeshNodes.Clear();

            foreach (var orig in _hiddenOriginalNodes)
            {
                if (orig != null && GodotObject.IsInstanceValid(orig))
                {
                    orig.Visible = true;
                }
            }
            _hiddenOriginalNodes.Clear();
        }

        public override void _ExitTree()
        {
            CleanupGeneratedSubmeshes();
            base._ExitTree();
        }

        private void NotifyHierarchyChanged()
        {
            EmitSignal(SignalName.HierarchyChanged);
        }

        private void NotifyTargetMeshChanged(MeshInstance3D mesh, int surfaceIndex)
        {
            EmitSignal(SignalName.TargetMeshChanged, mesh, surfaceIndex);
        }
    }
}
