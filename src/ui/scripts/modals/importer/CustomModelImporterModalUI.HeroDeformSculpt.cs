using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using Gizmo3DPlugin;
using DeadlockPlayground.Importer;
using DeadlockPlayground.Animation;
using DeadlockPlayground.Tools;
using DeadlockPlayground.Theming;

namespace DeadlockPlayground.UI
{
    public partial class CustomModelImporterModalUI
    {
        #region Interactive 3D Hero Deform Sculpt Brush

        private sealed class HeroDeformSurfaceCache
        {
            public MeshInstance3D MeshInstance;
            public int SurfaceIndex;
            public string DeformKey;
            public Mesh CachedMeshRef;
            public Transform3D MiToHero;
            public Transform3D HeroToWorld;
            public Vector3[] HeroVerts;
            public Vector3[] WorldVerts;
            public Vector3[] HeroNorms;
            public int[] Indices;
            public int[][] Neighbors;
        }

        private static Dictionary<string, Dictionary<int, Vector3>> CloneDeformSculpts(
            Dictionary<string, Dictionary<int, Vector3>> src)
        {
            var copy = new Dictionary<string, Dictionary<int, Vector3>>(StringComparer.OrdinalIgnoreCase);
            if (src == null) return copy;
            foreach (var (sk, vMap) in src)
            {
                if (!string.IsNullOrEmpty(sk) && vMap != null && vMap.Count > 0)
                {
                    copy[sk] = new Dictionary<int, Vector3>(vMap);
                }
            }
            return copy;
        }

        private void UpdateDeformBrushStatusLabel()
        {
            int vertCount = 0;
            foreach (var vMap in _deformSculptDeltas.Values) vertCount += vMap.Count;
            if (_lblDeformBrushStatus != null)
            {
                _lblDeformBrushStatus.Text = vertCount > 0
                    ? $"Sculpted {vertCount} hero vertex offset(s) across {_deformSculptDeltas.Count} surface(s)."
                    : "No sculpted vertices yet.";
            }
            if (_btnDeformBrushUndo != null) _btnDeformBrushUndo.Disabled = _deformUndoStack.Count == 0;
            if (_btnDeformBrushClear != null) _btnDeformBrushClear.Disabled = vertCount == 0;
        }

        private void OnDeformBrushToggled(bool enabled)
        {
            if (enabled)
            {
                if (IsMeshCutActive())
                {
                    ExitMeshCutMode();
                }
                if (_btnWeightPaintMode != null && _btnWeightPaintMode.ButtonPressed)
                {
                    _btnWeightPaintMode.ButtonPressed = false;
                }
                if (_chkDeformEnabled != null && !_chkDeformEnabled.ButtonPressed)
                {
                    _chkDeformEnabled.SetPressedNoSignal(true);
                    if (_boxDeformControls != null) _boxDeformControls.Visible = true;
                }
                if (_pinsDirtyNeedsRigRebuild)
                {
                    ExecuteLiveRigUpdate();
                }
                else
                {
                    ApplyLandmarkPreviewState();
                }
                RebuildHeroDeformSurfaceCache();
                SetBottomStatus("Deform Sculpt Brush active: drag directly on hero mesh (Shift+Drag pans, [ / ] resizes, Ctrl+Z undoes).", StatusTone.Info);
            }
            else
            {
                _isDeformBrushing = false;
                _deformFlattenPlaneLocked = false;
                _hasLastDeformBrushDab = false;
                _pendingDeformUndoSnapshot = null;
                UpdateBrushRingVisual(Vector3.Zero, Vector3.Up, 0.05f, false);
            }
        }

        private void OnDeformBrushUndoPressed()
        {
            if (_deformUndoStack.Count == 0) return;
            var prev = _deformUndoStack[^1];
            _deformUndoStack.RemoveAt(_deformUndoStack.Count - 1);
            _deformSculptDeltas.Clear();
            foreach (var (sk, vMap) in prev)
            {
                _deformSculptDeltas[sk] = new Dictionary<int, Vector3>(vMap);
            }
            UpdateDeformBrushStatusLabel();
            ApplyPreviewHeroDeformation();
            SetBottomStatus("Undid last hero deform brush stroke.", StatusTone.Info);
        }

        private void OnDeformBrushClearPressed()
        {
            if (_deformSculptDeltas.Count == 0) return;
            _deformUndoStack.Add(CloneDeformSculpts(_deformSculptDeltas));
            if (_deformUndoStack.Count > 25) _deformUndoStack.RemoveAt(0);
            _deformSculptDeltas.Clear();
            UpdateDeformBrushStatusLabel();
            ApplyPreviewHeroDeformation();
            SetBottomStatus("Cleared sculpted hero vertex offsets (Undo available).", StatusTone.Info);
        }

        private void RebuildHeroDeformSurfaceCache()
        {
            _deformSurfaceCaches.Clear();
            if (_previewHeroClone == null || !GodotObject.IsInstanceValid(_previewHeroClone)) return;

            Transform3D heroToWorld = _previewHeroClone.GlobalTransform;
            Transform3D heroInvWorld = heroToWorld.AffineInverse();

            var keptNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var chk in _heroKeepCheckboxes)
            {
                if (GodotObject.IsInstanceValid(chk) && chk.ButtonPressed && chk.HasMeta("SubmeshName"))
                    keptNames.Add(chk.GetMeta("SubmeshName").AsString());
            }

            var meshSet = new List<MeshInstance3D>();
            if (_previewGhostMeshes.Count > 0)
            {
                foreach (var gm in _previewGhostMeshes)
                    if (gm != null && GodotObject.IsInstanceValid(gm)) meshSet.Add(gm);
            }
            else
            {
                foreach (var cm in CustomModelAutoRigger.CollectMeshes(_previewHeroClone))
                    if (cm != null && GodotObject.IsInstanceValid(cm)) meshSet.Add(cm);
            }

            foreach (var mi in meshSet)
            {
                if (mi.Mesh == null || !mi.Visible) continue;
                int surfCount = mi.Mesh.GetSurfaceCount();
                Transform3D miToWorld = mi.GlobalTransform;
                Transform3D miToHero = heroInvWorld * miToWorld;

                for (int s = 0; s < surfCount; s++)
                {
                    if (keptNames.Count > 0 && !CustomModelAutoRigger.IsOriginalSurfaceKept(keptNames, mi.Name.ToString(), s))
                        continue;

                    var arrays = mi.Mesh.SurfaceGetArrays(s);
                    if (arrays == null || arrays.Count <= (int)Mesh.ArrayType.Vertex) continue;
                    var verts = CustomModelAutoRigger.GetVector3Array(arrays[(int)Mesh.ArrayType.Vertex]);
                    if (verts == null || verts.Length == 0) continue;

                    var norms = arrays.Count > (int)Mesh.ArrayType.Normal
                        ? CustomModelAutoRigger.GetVector3Array(arrays[(int)Mesh.ArrayType.Normal])
                        : null;
                    var indices = arrays.Count > (int)Mesh.ArrayType.Index
                        ? CustomModelAutoRigger.GetInt32Array(arrays[(int)Mesh.ArrayType.Index])
                        : null;
                    if (indices == null || indices.Length == 0)
                    {
                        indices = new int[verts.Length - verts.Length % 3];
                        for (int i = 0; i < indices.Length; i++) indices[i] = i;
                    }

                    var neighborSets = new HashSet<int>[verts.Length];
                    for (int i = 0; i < verts.Length; i++) neighborSets[i] = new HashSet<int>();
                    for (int t = 0; t + 2 < indices.Length; t += 3)
                    {
                        int a = indices[t], b = indices[t + 1], c = indices[t + 2];
                        if ((uint)a >= (uint)verts.Length || (uint)b >= (uint)verts.Length || (uint)c >= (uint)verts.Length) continue;
                        neighborSets[a].Add(b); neighborSets[a].Add(c);
                        neighborSets[b].Add(a); neighborSets[b].Add(c);
                        neighborSets[c].Add(a); neighborSets[c].Add(b);
                    }
                    var neighbors = new int[verts.Length][];
                    for (int i = 0; i < verts.Length; i++)
                    {
                        neighbors[i] = neighborSets[i].Count > 0 ? neighborSets[i].ToArray() : Array.Empty<int>();
                    }

                    var cache = new HeroDeformSurfaceCache
                    {
                        MeshInstance = mi,
                        SurfaceIndex = s,
                        DeformKey = CustomModelAutoRigger.MakeOriginalSurfaceDeformKey(mi, s),
                        CachedMeshRef = mi.Mesh,
                        MiToHero = miToHero,
                        HeroToWorld = heroToWorld,
                        HeroVerts = new Vector3[verts.Length],
                        WorldVerts = new Vector3[verts.Length],
                        HeroNorms = new Vector3[verts.Length],
                        Indices = indices,
                        Neighbors = neighbors
                    };
                    SyncSingleDeformSurfaceCache(cache, arrays);
                    _deformSurfaceCaches.Add(cache);
                }
            }
        }

        private void RefreshHeroDeformSurfaceCacheVertices()
        {
            if (_previewHeroClone == null || !GodotObject.IsInstanceValid(_previewHeroClone)) return;
            Transform3D heroToWorld = _previewHeroClone.GlobalTransform;
            Transform3D heroInvWorld = heroToWorld.AffineInverse();

            for (int i = 0; i < _deformSurfaceCaches.Count; i++)
            {
                var c = _deformSurfaceCaches[i];
                if (c.MeshInstance == null || !GodotObject.IsInstanceValid(c.MeshInstance) || c.MeshInstance.Mesh == null) continue;
                if (c.SurfaceIndex >= c.MeshInstance.Mesh.GetSurfaceCount()) continue;
                if (ReferenceEquals(c.CachedMeshRef, c.MeshInstance.Mesh) && c.HeroToWorld == heroToWorld) continue;

                c.HeroToWorld = heroToWorld;
                c.MiToHero = heroInvWorld * c.MeshInstance.GlobalTransform;
                c.CachedMeshRef = c.MeshInstance.Mesh;
                var arrays = c.MeshInstance.Mesh.SurfaceGetArrays(c.SurfaceIndex);
                SyncSingleDeformSurfaceCache(c, arrays);
            }
        }

        private static void SyncSingleDeformSurfaceCache(HeroDeformSurfaceCache cache, Godot.Collections.Array arrays)
        {
            if (arrays == null || arrays.Count <= (int)Mesh.ArrayType.Vertex) return;
            var verts = CustomModelAutoRigger.GetVector3Array(arrays[(int)Mesh.ArrayType.Vertex]);
            if (verts == null || verts.Length != cache.HeroVerts.Length) return;
            var norms = arrays.Count > (int)Mesh.ArrayType.Normal
                ? CustomModelAutoRigger.GetVector3Array(arrays[(int)Mesh.ArrayType.Normal])
                : null;
            Basis heroBasis = cache.MiToHero.Basis;
            for (int v = 0; v < verts.Length; v++)
            {
                Vector3 vh = cache.MiToHero * verts[v];
                cache.HeroVerts[v] = vh;
                cache.WorldVerts[v] = cache.HeroToWorld * vh;
                Vector3 nh = (norms != null && v < norms.Length) ? (heroBasis * norms[v]) : Vector3.Up;
                cache.HeroNorms[v] = nh.LengthSquared() > 1e-8f ? nh.Normalized() : Vector3.Up;
            }
        }

        private bool RaycastHeroDeformSurfaces(Vector3 rayOrigin, Vector3 rayDir, out HeroDeformSurfaceCache hitSurface, out Vector3 hitPointWorld, out Vector3 hitNormalWorld)
        {
            hitSurface = null;
            hitPointWorld = Vector3.Zero;
            hitNormalWorld = Vector3.Up;
            float bestT = float.MaxValue;

            for (int si = 0; si < _deformSurfaceCaches.Count; si++)
            {
                var c = _deformSurfaceCaches[si];
                if (c.MeshInstance == null || !GodotObject.IsInstanceValid(c.MeshInstance) || !c.MeshInstance.Visible) continue;
                var verts = c.WorldVerts;
                var idx = c.Indices;
                for (int t = 0; t + 2 < idx.Length; t += 3)
                {
                    int i0 = idx[t], i1 = idx[t + 1], i2 = idx[t + 2];
                    if ((uint)i0 >= (uint)verts.Length || (uint)i1 >= (uint)verts.Length || (uint)i2 >= (uint)verts.Length) continue;
                    Vector3 a = verts[i0], b = verts[i1], cPt = verts[i2];
                    if (CustomWeightPaintEditor.RayTriangle(rayOrigin, rayDir, a, b, cPt, out float hitT) && hitT < bestT)
                    {
                        bestT = hitT;
                        hitSurface = c;
                        hitPointWorld = rayOrigin + rayDir * hitT;
                        Vector3 n = (b - a).Cross(cPt - a);
                        hitNormalWorld = n.LengthSquared() > 1e-8f ? n.Normalized() : Vector3.Up;
                    }
                }
            }
            return hitSurface != null;
        }

        private bool HandleHeroDeformBrushInput(InputEvent @event)
        {
            if (!IsDeformBrushActive() || _previewCamera == null || _previewHeroClone == null || !GodotObject.IsInstanceValid(_previewHeroClone))
                return false;

            if (_deformSurfaceCaches.Count == 0)
            {
                RebuildHeroDeformSurfaceCache();
                if (_deformSurfaceCaches.Count == 0) return false;
            }

            if (@event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
            {
                if (mb.Pressed && !mb.ShiftPressed)
                {
                    RefreshHeroDeformSurfaceCacheVertices();
                    Vector3 orig = _previewCamera.ProjectRayOrigin(mb.Position);
                    Vector3 dir = _previewCamera.ProjectRayNormal(mb.Position);
                    if (RaycastHeroDeformSurfaces(orig, dir, out var hitSurf, out var hitPt, out var hitNorm))
                    {
                        _pendingDeformUndoSnapshot = CloneDeformSculpts(_deformSculptDeltas);
                        _deformBrushDirty = false;
                        _isDeformBrushing = true;
                        _hasLastDeformBrushDab = true;
                        _lastDeformBrushDabWorld = hitPt;
                        Transform3D heroInv = _previewHeroClone.GlobalTransform.AffineInverse();
                        _deformFlattenPlaneOriginHero = heroInv * hitPt;
                        Vector3 hnHero = heroInv.Basis * hitNorm;
                        _deformFlattenPlaneNormalHero = hnHero.LengthSquared() > 1e-8f ? hnHero.Normalized() : Vector3.Up;
                        _deformFlattenPlaneLocked = true;
                        _isOrbiting = false;
                        _isPanning = false;
                        _deformBrushDirty |= ApplyDeformBrushDab(hitPt, hitNorm);
                        GetViewport().SetInputAsHandled();
                        return true;
                    }
                }
                else if (!mb.Pressed && _isDeformBrushing)
                {
                    _isDeformBrushing = false;
                    _deformFlattenPlaneLocked = false;
                    _hasLastDeformBrushDab = false;
                    if (_deformBrushDirty && _pendingDeformUndoSnapshot != null)
                    {
                        _deformUndoStack.Add(_pendingDeformUndoSnapshot);
                        if (_deformUndoStack.Count > 25) _deformUndoStack.RemoveAt(0);
                    }
                    _pendingDeformUndoSnapshot = null;
                    _deformBrushDirty = false;
                    UpdateDeformBrushStatusLabel();
                    GetViewport().SetInputAsHandled();
                    return true;
                }
            }
            else if (@event is InputEventMouseMotion mm && !_isOrbiting && !_isPanning)
            {
                RefreshHeroDeformSurfaceCacheVertices();
                Vector3 orig = _previewCamera.ProjectRayOrigin(mm.Position);
                Vector3 dir = _previewCamera.ProjectRayNormal(mm.Position);
                if (RaycastHeroDeformSurfaces(orig, dir, out _, out var hitPt, out var hitNorm))
                {
                    float r = (float)(_sliderDeformBrushRadius?.Value ?? 0.08);
                    UpdateBrushRingVisual(hitPt, hitNorm, r, true);
                    if (_isDeformBrushing)
                    {
                        float stepDist = MathF.Max(0.004f, r * 0.14f);
                        if (!_hasLastDeformBrushDab || hitPt.DistanceSquaredTo(_lastDeformBrushDabWorld) >= stepDist * stepDist)
                        {
                            _lastDeformBrushDabWorld = hitPt;
                            _hasLastDeformBrushDab = true;
                            _deformBrushDirty |= ApplyDeformBrushDab(hitPt, hitNorm);
                        }
                        GetViewport().SetInputAsHandled();
                        return true;
                    }
                }
                else
                {
                    UpdateBrushRingVisual(Vector3.Zero, Vector3.Up, 0.05f, false);
                }
            }
            return false;
        }

        private bool ApplyDeformBrushDab(Vector3 hitWorld, Vector3 hitNormalWorld)
        {
            if (_previewHeroClone == null || !GodotObject.IsInstanceValid(_previewHeroClone)) return false;

            float radius = (float)(_sliderDeformBrushRadius?.Value ?? 0.08);
            float strength = (float)(_sliderDeformBrushStrength?.Value ?? 0.45);
            bool mirror = _chkDeformBrushMirror?.ButtonPressed ?? true;
            var mode = _optDeformBrushMode != null && _optDeformBrushMode.Selected >= 0
                ? (CustomModelAutoRigger.HeroSculptBrushMode)_optDeformBrushMode.GetItemId(_optDeformBrushMode.Selected)
                : CustomModelAutoRigger.HeroSculptBrushMode.PushIn;

            Transform3D heroInv = _previewHeroClone.GlobalTransform.AffineInverse();
            Vector3 hitHero = heroInv * hitWorld;
            Vector3 hitNormHero = (heroInv.Basis * hitNormalWorld);
            hitNormHero = hitNormHero.LengthSquared() > 1e-8f ? hitNormHero.Normalized() : Vector3.Up;

            Vector3 mirHitHero = new Vector3(-hitHero.X, hitHero.Y, hitHero.Z);
            Vector3 mirHitNormHero = new Vector3(-hitNormHero.X, hitNormHero.Y, hitNormHero.Z);

            float rSq = radius * radius;
            bool anyChanged = false;

            for (int si = 0; si < _deformSurfaceCaches.Count; si++)
            {
                var c = _deformSurfaceCaches[si];
                if (c.MeshInstance == null || !GodotObject.IsInstanceValid(c.MeshInstance) || !c.MeshInstance.Visible) continue;
                if (string.IsNullOrEmpty(c.DeformKey)) continue;

                if (!_deformSculptDeltas.TryGetValue(c.DeformKey, out var vertMap))
                {
                    vertMap = null;
                }

                Dictionary<int, Vector3> smoothSnapshot = (mode == CustomModelAutoRigger.HeroSculptBrushMode.Smooth && vertMap != null)
                    ? new Dictionary<int, Vector3>(vertMap)
                    : null;

                var hVerts = c.HeroVerts;
                var hNorms = c.HeroNorms;
                bool surfChanged = false;

                for (int v = 0; v < hVerts.Length; v++)
                {
                    Vector3 vp = hVerts[v];
                    float dSqPrimary = vp.DistanceSquaredTo(hitHero);
                    float dSqMirror = mirror ? vp.DistanceSquaredTo(mirHitHero) : float.MaxValue;
                    bool useMirror = dSqMirror < dSqPrimary;
                    float bestDSq = useMirror ? dSqMirror : dSqPrimary;
                    if (bestDSq >= rSq) continue;

                    float t = 1f - MathF.Sqrt(bestDSq) / radius;
                    float falloff = t * t * (3f - 2f * t);
                    if (falloff <= 0.0001f) continue;

                    Vector3 refNorm = useMirror ? mirHitNormHero : hitNormHero;
                    Vector3 curDelta = (vertMap != null && vertMap.TryGetValue(v, out var ex)) ? ex : Vector3.Zero;
                    Vector3 nextDelta = curDelta;

                    switch (mode)
                    {
                        case CustomModelAutoRigger.HeroSculptBrushMode.PushIn:
                        {
                            Vector3 dirIn = (-refNorm * 0.65f - hNorms[v] * 0.35f);
                            dirIn = dirIn.LengthSquared() > 1e-8f ? dirIn.Normalized() : -refNorm;
                            nextDelta = curDelta + dirIn * (strength * 0.016f * falloff);
                            break;
                        }
                        case CustomModelAutoRigger.HeroSculptBrushMode.PullOut:
                        {
                            Vector3 dirOut = (refNorm * 0.65f + hNorms[v] * 0.35f);
                            dirOut = dirOut.LengthSquared() > 1e-8f ? dirOut.Normalized() : refNorm;
                            nextDelta = curDelta + dirOut * (strength * 0.016f * falloff);
                            break;
                        }
                        case CustomModelAutoRigger.HeroSculptBrushMode.Flatten:
                        {
                            Vector3 pOrig = _deformFlattenPlaneLocked
                                ? (useMirror ? new Vector3(-_deformFlattenPlaneOriginHero.X, _deformFlattenPlaneOriginHero.Y, _deformFlattenPlaneOriginHero.Z) : _deformFlattenPlaneOriginHero)
                                : (useMirror ? mirHitHero : hitHero);
                            Vector3 pNorm = _deformFlattenPlaneLocked
                                ? (useMirror ? new Vector3(-_deformFlattenPlaneNormalHero.X, _deformFlattenPlaneNormalHero.Y, _deformFlattenPlaneNormalHero.Z) : _deformFlattenPlaneNormalHero)
                                : refNorm;
                            float signedDist = (vp - pOrig).Dot(pNorm);
                            Vector3 targetPos = vp - pNorm * signedDist;
                            nextDelta = curDelta + (targetPos - vp) * (strength * 0.42f * falloff);
                            break;
                        }
                        case CustomModelAutoRigger.HeroSculptBrushMode.Smooth:
                        {
                            int[] nbs = c.Neighbors[v];
                            if (nbs == null || nbs.Length == 0 || smoothSnapshot == null) continue;
                            Vector3 avg = Vector3.Zero;
                            for (int ni = 0; ni < nbs.Length; ni++)
                            {
                                if (smoothSnapshot.TryGetValue(nbs[ni], out var nd)) avg += nd;
                            }
                            avg /= nbs.Length;
                            nextDelta = curDelta.Lerp(avg, Math.Clamp(strength * 0.55f * falloff, 0f, 1f));
                            break;
                        }
                        case CustomModelAutoRigger.HeroSculptBrushMode.Erase:
                        {
                            if (curDelta.LengthSquared() < 1e-10f) continue;
                            nextDelta = curDelta * Math.Clamp(1f - strength * 0.55f * falloff, 0f, 1f);
                            break;
                        }
                    }

                    // Keep sculpt delta within sensible bounds (max 0.35m from rest) so vertices never shoot away
                    if (nextDelta.LengthSquared() > 0.35f * 0.35f)
                    {
                        nextDelta = nextDelta.Normalized() * 0.35f;
                    }

                    if (nextDelta.LengthSquared() < 1e-9f)
                    {
                        if (vertMap != null && vertMap.Remove(v))
                        {
                            surfChanged = true;
                        }
                    }
                    else
                    {
                        if (vertMap == null)
                        {
                            vertMap = new Dictionary<int, Vector3>();
                            _deformSculptDeltas[c.DeformKey] = vertMap;
                        }
                        vertMap[v] = nextDelta;
                        surfChanged = true;
                    }
                }

                if (vertMap != null && vertMap.Count == 0)
                {
                    _deformSculptDeltas.Remove(c.DeformKey);
                }

                if (surfChanged) anyChanged = true;
            }

            if (anyChanged)
            {
                ApplyPreviewHeroDeformation();
                UpdateDeformBrushStatusLabel();
            }
            return anyChanged;
        }

        #endregion
    }
}
