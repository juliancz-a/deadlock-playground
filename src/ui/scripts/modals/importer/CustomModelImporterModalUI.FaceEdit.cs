using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using DeadlockPlayground.Importer;

namespace DeadlockPlayground.UI
{
    public enum FaceEditSelectTool
    {
        Lasso = 0,
        Box = 1,
        Paint = 2,
        Island = 3
    }

    public enum FaceEditTransformMode
    {
        None = 0,
        Move = 1,
        Scale = 2,
        Inflate = 3
    }

    public partial class CustomModelImporterModalUI
    {
        // =========================================================================
        // Blender-Style Face Selection & Proportional Drag inside Floating Sculpt HUD
        // =========================================================================

        private bool _isFaceEditSubMode; // false = Brush Sculpt, true = Edit Faces
        private FaceEditSelectTool _faceEditSelectTool = FaceEditSelectTool.Lasso;
        private FaceEditTransformMode _faceEditTransformMode = FaceEditTransformMode.None;
        private int _faceEditAxisLock; // 0 = Free, 1 = X, 2 = Y, 3 = Z
        private bool _faceEditProportional = true;
        private float _faceEditPropRadius = 0.14f;
        private bool _faceEditSelectThrough = true;

        // Per-cache selected triangle indices
        private readonly Dictionary<ImpSculptSurfaceCache, HashSet<int>> _faceEditSelectedTris = new();
        private bool _faceEditDraggingLasso;
        private bool _faceEditDraggingBox;
        private bool _faceEditDraggingPaint;
        private MeshCutSelectOp _faceEditDragOp = MeshCutSelectOp.Replace;

        // Active modal transform state (Move / Scale / Inflate)
        private Vector2 _faceEditTransformStartMouse;
        private Vector3 _faceEditTransformPivotWorld;
        private Vector3 _faceEditTransformStartHitWorld;
        private Vector3 _faceEditTransformPlaneNormal;
        private Dictionary<string, Dictionary<int, Vector3>> _faceEditPreTransformDeltas;
        private readonly List<(ImpSculptSurfaceCache Cache, int VertIdx, float Weight, Vector3 InitialDelta, Vector3 StartWorldPos, Vector3 StartWorldNorm)> _faceEditAffectedVerts = new();

        private MeshInstance3D _faceEditOverlay3D;
        private MeshInstance3D _faceEditAxisLine3D;

        private bool IsFaceEditActive() => IsImportedSculptActive() && _isFaceEditSubMode;

        private void ClearFaceEditSelection(bool refreshVisual = true)
        {
            CancelFaceEditTransform(restore: false);
            _faceEditSelectedTris.Clear();
            if (refreshVisual)
            {
                RefreshFaceEditOverlay3D();
                SyncImportedSculptViewportHud();
            }
        }

        private int GetFaceEditSelectedTriCount()
        {
            int total = 0;
            foreach (var set in _faceEditSelectedTris.Values) total += set.Count;
            return total;
        }

        private void DestroyFaceEditOverlays3D()
        {
            if (_faceEditOverlay3D != null && GodotObject.IsInstanceValid(_faceEditOverlay3D))
            {
                _faceEditOverlay3D.GetParent()?.RemoveChild(_faceEditOverlay3D);
                _faceEditOverlay3D.QueueFree();
            }
            _faceEditOverlay3D = null;

            if (_faceEditAxisLine3D != null && GodotObject.IsInstanceValid(_faceEditAxisLine3D))
            {
                _faceEditAxisLine3D.GetParent()?.RemoveChild(_faceEditAxisLine3D);
                _faceEditAxisLine3D.QueueFree();
            }
            _faceEditAxisLine3D = null;
        }

        private void RefreshFaceEditOverlay3D()
        {
            if (!IsFaceEditActive() || _previewWorldRoot == null || !GodotObject.IsInstanceValid(_previewWorldRoot))
            {
                if (_faceEditOverlay3D != null && GodotObject.IsInstanceValid(_faceEditOverlay3D))
                    _faceEditOverlay3D.Visible = false;
                return;
            }

            if (_faceEditOverlay3D == null || !GodotObject.IsInstanceValid(_faceEditOverlay3D))
            {
                _faceEditOverlay3D = new MeshInstance3D
                {
                    Name = "FaceEditSelectionOverlay3D",
                    Layers = 1u, // Clear Visual Layer 21
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
                };
                _faceEditOverlay3D.SetMeta("IsMeshCutOverlay", true);
                _previewWorldRoot.AddChild(_faceEditOverlay3D);
            }

            var verts = new List<Vector3>();
            Transform3D worldInv = _previewWorldRoot.GlobalTransform.AffineInverse();
            foreach (var (c, triSet) in _faceEditSelectedTris)
            {
                if (triSet.Count == 0 || c.Indices == null) continue;
                foreach (int t in triSet)
                {
                    int o = t * 3;
                    if (o + 2 >= c.Indices.Length) continue;
                    int a = c.Indices[o], b = c.Indices[o + 1], d = c.Indices[o + 2];
                    if ((uint)a >= (uint)c.CurrentWorldVerts.Length ||
                        (uint)b >= (uint)c.CurrentWorldVerts.Length ||
                        (uint)d >= (uint)c.CurrentWorldVerts.Length) continue;

                    Vector3 va = worldInv * (c.CurrentWorldVerts[a] + c.CurrentWorldNorms[a] * 0.0012f);
                    Vector3 vb = worldInv * (c.CurrentWorldVerts[b] + c.CurrentWorldNorms[b] * 0.0012f);
                    Vector3 vd = worldInv * (c.CurrentWorldVerts[d] + c.CurrentWorldNorms[d] * 0.0012f);
                    verts.Add(va); verts.Add(vb); verts.Add(vd);
                }
            }

            if (verts.Count < 3)
            {
                _faceEditOverlay3D.Visible = false;
                _faceEditOverlay3D.Mesh = null;
                return;
            }

            var arr = new Godot.Collections.Array();
            arr.Resize((int)Mesh.ArrayType.Max);
            arr[(int)Mesh.ArrayType.Vertex] = verts.ToArray();
            var am = new ArrayMesh();
            am.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arr);
            var mat = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                AlbedoColor = _faceEditTransformMode != FaceEditTransformMode.None
                    ? new Color(0.28f, 0.92f, 1.0f, 0.45f)
                    : new Color(1.0f, 0.62f, 0.14f, 0.48f),
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                NoDepthTest = _faceEditSelectThrough,
                RenderPriority = 55
            };
            am.SurfaceSetMaterial(0, mat);
            _faceEditOverlay3D.Mesh = am;
            _faceEditOverlay3D.Visible = true;
        }

        private void UpdateFaceEditAxisGuideVisual()
        {
            if (_faceEditTransformMode == FaceEditTransformMode.None || _faceEditAxisLock == 0 ||
                _previewWorldRoot == null || !GodotObject.IsInstanceValid(_previewWorldRoot))
            {
                if (_faceEditAxisLine3D != null && GodotObject.IsInstanceValid(_faceEditAxisLine3D))
                    _faceEditAxisLine3D.Visible = false;
                return;
            }

            if (_faceEditAxisLine3D == null || !GodotObject.IsInstanceValid(_faceEditAxisLine3D))
            {
                _faceEditAxisLine3D = new MeshInstance3D
                {
                    Name = "FaceEditAxisGuide3D",
                    Mesh = new ImmediateMesh(),
                    Layers = 1u,
                    MaterialOverride = new StandardMaterial3D
                    {
                        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                        VertexColorUseAsAlbedo = true,
                        NoDepthTest = true,
                        RenderPriority = 95
                    }
                };
                _faceEditAxisLine3D.SetMeta("IsMeshCutOverlay", true);
                _previewWorldRoot.AddChild(_faceEditAxisLine3D);
            }

            Vector3 axis = _faceEditAxisLock switch
            {
                1 => Vector3.Right,
                2 => Vector3.Up,
                _ => Vector3.Back
            };
            Color col = _faceEditAxisLock switch
            {
                1 => new Color(1.0f, 0.25f, 0.25f),
                2 => new Color(0.30f, 0.95f, 0.35f),
                _ => new Color(0.30f, 0.62f, 1.0f)
            };
            Transform3D worldInv = _previewWorldRoot.GlobalTransform.AffineInverse();
            Vector3 p0 = worldInv * (_faceEditTransformPivotWorld - axis * 1.5f);
            Vector3 p1 = worldInv * (_faceEditTransformPivotWorld + axis * 1.5f);
            var im = (ImmediateMesh)_faceEditAxisLine3D.Mesh;
            im.ClearSurfaces();
            im.SurfaceBegin(Mesh.PrimitiveType.Lines);
            im.SurfaceSetColor(col); im.SurfaceAddVertex(p0);
            im.SurfaceSetColor(col); im.SurfaceAddVertex(p1);
            im.SurfaceEnd();
            _faceEditAxisLine3D.Visible = true;
        }

        private void BeginFaceEditTransform(FaceEditTransformMode mode, Vector2? startMouseOverride = null)
        {
            if (!IsFaceEditActive() || _previewCamera == null) return;
            if (GetFaceEditSelectedTriCount() == 0)
            {
                SetBottomStatus("Select one or more faces first (Lasso / Box / Paint / Island), then press G (Move), S (Scale), or Alt+S (Inflate).", StatusTone.Warning);
                return;
            }

            if (_faceEditTransformMode != FaceEditTransformMode.None)
                CancelFaceEditTransform(restore: true);

            _faceEditTransformMode = mode;
            _faceEditAxisLock = 0;
            _faceEditPreTransformDeltas = CloneImportedSculptDeltas(_importedSculptDeltas) ?? new();
            _faceEditAffectedVerts.Clear();

            bool mirrorX = _btnImpSculptMirror?.ButtonPressed ?? true;
            Transform3D heroGlobal = (_previewHeroClone != null && GodotObject.IsInstanceValid(_previewHeroClone))
                ? _previewHeroClone.GlobalTransform
                : Transform3D.Identity;
            Transform3D heroInv = heroGlobal.AffineInverse();

            // Gather selected vertices & compute centroid
            var selectedVertsByCache = new Dictionary<ImpSculptSurfaceCache, HashSet<int>>();
            var selWorldPts = new List<Vector3>();
            Vector3 pivotSum = Vector3.Zero;
            int pivotCnt = 0;

            foreach (var (c, triSet) in _faceEditSelectedTris)
            {
                if (triSet.Count == 0) continue;
                var vSet = new HashSet<int>();
                foreach (int t in triSet)
                {
                    int o = t * 3;
                    if (o + 2 >= c.Indices.Length) continue;
                    vSet.Add(c.Indices[o]);
                    vSet.Add(c.Indices[o + 1]);
                    vSet.Add(c.Indices[o + 2]);
                }
                selectedVertsByCache[c] = vSet;
                foreach (int v in vSet)
                {
                    if ((uint)v < (uint)c.CurrentWorldVerts.Length)
                    {
                        Vector3 wp = c.CurrentWorldVerts[v];
                        pivotSum += wp;
                        pivotCnt++;
                        selWorldPts.Add(wp);
                    }
                }
            }
            if (pivotCnt == 0)
            {
                _faceEditTransformMode = FaceEditTransformMode.None;
                return;
            }
            _faceEditTransformPivotWorld = pivotSum / pivotCnt;

            // Compute proportional falloff weights (1.0 on selected verts, smoothstep within _faceEditPropRadius if Proportional Editing is ON)
            float propR = MathF.Max(0.01f, _faceEditPropRadius);
            float propRSq = propR * propR;
            foreach (var c in _impSculptCaches)
            {
                selectedVertsByCache.TryGetValue(c, out var vSet);
                _importedSculptDeltas.TryGetValue(c.SurfaceKey, out var existingMap);

                for (int v = 0; v < c.CurrentWorldVerts.Length; v++)
                {
                    float w = 0f;
                    if (vSet != null && vSet.Contains(v))
                    {
                        w = 1f;
                    }
                    else if (_faceEditProportional && selWorldPts.Count > 0)
                    {
                        Vector3 wp = c.CurrentWorldVerts[v];
                        float minDSq = propRSq;
                        for (int si = 0; si < selWorldPts.Count; si++)
                        {
                            float dSq = wp.DistanceSquaredTo(selWorldPts[si]);
                            if (dSq < minDSq) minDSq = dSq;
                        }
                        if (minDSq < propRSq)
                        {
                            float t = 1f - MathF.Sqrt(minDSq) / propR;
                            w = t * t * (3f - 2f * t);
                        }
                    }

                    if (w > 1e-4f)
                    {
                        Vector3 initD = (existingMap != null && existingMap.TryGetValue(v, out var d)) ? d : Vector3.Zero;
                        _faceEditAffectedVerts.Add((c, v, w, initD, c.CurrentWorldVerts[v], c.CurrentWorldNorms[v]));
                    }
                }
            }

            Vector2 mousePos = startMouseOverride ?? _subViewportContainer?.GetLocalMousePosition() ?? Vector2.Zero;
            _faceEditTransformStartMouse = mousePos;
            _faceEditTransformPlaneNormal = (-_previewCamera.GlobalTransform.Basis.Z).Normalized();
            Vector3 rOrig = _previewCamera.ProjectRayOrigin(mousePos);
            Vector3 rDir = _previewCamera.ProjectRayNormal(mousePos);
            float denom = rDir.Dot(_faceEditTransformPlaneNormal);
            _faceEditTransformStartHitWorld = MathF.Abs(denom) > 1e-5f
                ? rOrig + rDir * ((_faceEditTransformPivotWorld - rOrig).Dot(_faceEditTransformPlaneNormal) / denom)
                : _faceEditTransformPivotWorld;

            RefreshFaceEditOverlay3D();
            UpdateFaceEditAxisGuideVisual();
            SyncImportedSculptViewportHud();
            SetBottomStatus($"{mode} Faces — Drag mouse, press X/Y/Z for axis lock, O for Proportional, Left-click to apply, Right-click/Esc to cancel.", StatusTone.Info);
        }

        private void UpdateFaceEditTransform(Vector2 mousePos)
        {
            if (_faceEditTransformMode == FaceEditTransformMode.None || _previewCamera == null || _faceEditAffectedVerts.Count == 0)
                return;

            Vector3 rOrig = _previewCamera.ProjectRayOrigin(mousePos);
            Vector3 rDir = _previewCamera.ProjectRayNormal(mousePos);
            float denom = rDir.Dot(_faceEditTransformPlaneNormal);
            Vector3 curHit = MathF.Abs(denom) > 1e-5f
                ? rOrig + rDir * ((_faceEditTransformPivotWorld - rOrig).Dot(_faceEditTransformPlaneNormal) / denom)
                : _faceEditTransformStartHitWorld;

            Vector3 deltaWorld = curHit - _faceEditTransformStartHitWorld;
            if (_faceEditAxisLock == 1) deltaWorld = new Vector3(deltaWorld.X, 0f, 0f);
            else if (_faceEditAxisLock == 2) deltaWorld = new Vector3(0f, deltaWorld.Y, 0f);
            else if (_faceEditAxisLock == 3) deltaWorld = new Vector3(0f, 0f, deltaWorld.Z);

            float mouseDx = (mousePos.X - _faceEditTransformStartMouse.X) * 0.0045f;
            float scaleFactor = Math.Clamp(1f + mouseDx, 0.15f, 3.0f);
            float inflateDist = mouseDx * 0.18f;

            var dirtyCaches = new HashSet<ImpSculptSurfaceCache>();
            foreach (var item in _faceEditAffectedVerts)
            {
                if (!_importedSculptDeltas.TryGetValue(item.Cache.SurfaceKey, out var map))
                {
                    map = new Dictionary<int, Vector3>();
                    _importedSculptDeltas[item.Cache.SurfaceKey] = map;
                }

                Vector3 shiftWorld = Vector3.Zero;
                if (_faceEditTransformMode == FaceEditTransformMode.Move)
                {
                    shiftWorld = deltaWorld * item.Weight;
                }
                else if (_faceEditTransformMode == FaceEditTransformMode.Scale)
                {
                    Vector3 rel = item.StartWorldPos - _faceEditTransformPivotWorld;
                    Vector3 scaledRel = rel;
                    if (_faceEditAxisLock == 0) scaledRel = rel * scaleFactor;
                    else if (_faceEditAxisLock == 1) scaledRel.X *= scaleFactor;
                    else if (_faceEditAxisLock == 2) scaledRel.Y *= scaleFactor;
                    else if (_faceEditAxisLock == 3) scaledRel.Z *= scaleFactor;
                    shiftWorld = (scaledRel - rel) * item.Weight;
                }
                else if (_faceEditTransformMode == FaceEditTransformMode.Inflate)
                {
                    shiftWorld = item.StartWorldNorm * (inflateDist * item.Weight);
                }

                Vector3 localShift = item.Cache.WorldToLocal.Basis * shiftWorld;
                map[item.VertIdx] = item.InitialDelta + localShift;
                dirtyCaches.Add(item.Cache);
            }

            if (dirtyCaches.Count > 0)
            {
                ApplyLiveImportedSculptPreview(dirtyCaches);
                RefreshFaceEditOverlay3D();
            }
        }

        private void CommitFaceEditTransform()
        {
            if (_faceEditTransformMode == FaceEditTransformMode.None) return;
            if (_faceEditPreTransformDeltas != null)
            {
                _importedSculptUndoStack.Add(_faceEditPreTransformDeltas);
                if (_importedSculptUndoStack.Count > 30) _importedSculptUndoStack.RemoveAt(0);
                _faceEditPreTransformDeltas = null;
            }
            _faceEditTransformMode = FaceEditTransformMode.None;
            _faceEditAxisLock = 0;
            _faceEditAffectedVerts.Clear();
            UpdateFaceEditAxisGuideVisual();
            RefreshFaceEditOverlay3D();
            UpdateImportedSculptStatusLabel();
            SyncImportedSculptViewportHud();
            SetBottomStatus("Applied face transform (Ctrl+Z to undo).", StatusTone.Success);
        }

        private void CancelFaceEditTransform(bool restore)
        {
            if (_faceEditTransformMode == FaceEditTransformMode.None) return;
            if (restore && _faceEditPreTransformDeltas != null)
            {
                _importedSculptDeltas.Clear();
                foreach (var (k, v) in _faceEditPreTransformDeltas)
                    _importedSculptDeltas[k] = new Dictionary<int, Vector3>(v);
                var all = new HashSet<ImpSculptSurfaceCache>(_impSculptCaches);
                ApplyLiveImportedSculptPreview(all);
            }
            _faceEditPreTransformDeltas = null;
            _faceEditTransformMode = FaceEditTransformMode.None;
            _faceEditAxisLock = 0;
            _faceEditAffectedVerts.Clear();
            UpdateFaceEditAxisGuideVisual();
            RefreshFaceEditOverlay3D();
            SyncImportedSculptViewportHud();
        }

        private void ApplyFaceEditSelectionOps(ImpSculptSurfaceCache cache, IEnumerable<int> tris, MeshCutSelectOp op)
        {
            if (op == MeshCutSelectOp.Replace)
                _faceEditSelectedTris.Clear();

            if (!_faceEditSelectedTris.TryGetValue(cache, out var set))
            {
                set = new HashSet<int>();
                _faceEditSelectedTris[cache] = set;
            }

            bool mirrorX = _btnImpSculptMirror?.ButtonPressed ?? true;
            var toApply = new HashSet<int>(tris);
            if (mirrorX && toApply.Count > 0)
            {
                Transform3D heroGlobal = (_previewHeroClone != null && GodotObject.IsInstanceValid(_previewHeroClone))
                    ? _previewHeroClone.GlobalTransform
                    : Transform3D.Identity;
                Transform3D heroInv = heroGlobal.AffineInverse();
                int triCnt = cache.Indices.Length / 3;
                var triCentersHero = new Vector3[triCnt];
                for (int t = 0; t < triCnt; t++)
                {
                    int o = t * 3;
                    Vector3 cW = (cache.CurrentWorldVerts[cache.Indices[o]] +
                                  cache.CurrentWorldVerts[cache.Indices[o + 1]] +
                                  cache.CurrentWorldVerts[cache.Indices[o + 2]]) / 3f;
                    triCentersHero[t] = heroInv * cW;
                }
                var mirrored = new List<int>();
                foreach (int t in toApply)
                {
                    if ((uint)t >= (uint)triCnt) continue;
                    Vector3 target = new Vector3(-triCentersHero[t].X, triCentersHero[t].Y, triCentersHero[t].Z);
                    int bestT = -1;
                    float bestDSq = 0.035f * 0.035f;
                    for (int cand = 0; cand < triCnt; cand++)
                    {
                        float dSq = triCentersHero[cand].DistanceSquaredTo(target);
                        if (dSq < bestDSq) { bestDSq = dSq; bestT = cand; }
                    }
                    if (bestT >= 0) mirrored.Add(bestT);
                }
                foreach (int m in mirrored) toApply.Add(m);
            }

            foreach (int t in toApply)
            {
                if (op == MeshCutSelectOp.Subtract) set.Remove(t);
                else set.Add(t);
            }
        }

        private void GrowOrShrinkFaceSelection(bool grow)
        {
            if (!IsFaceEditActive() || GetFaceEditSelectedTriCount() == 0) return;
            foreach (var (c, set) in _faceEditSelectedTris.ToList())
            {
                if (set.Count == 0) continue;
                int triCnt = c.Indices.Length / 3;
                var selVerts = new HashSet<int>();
                foreach (int t in set)
                {
                    int o = t * 3;
                    if (o + 2 >= c.Indices.Length) continue;
                    selVerts.Add(c.Indices[o]); selVerts.Add(c.Indices[o + 1]); selVerts.Add(c.Indices[o + 2]);
                }
                if (grow)
                {
                    for (int t = 0; t < triCnt; t++)
                    {
                        if (set.Contains(t)) continue;
                        int o = t * 3;
                        if (selVerts.Contains(c.Indices[o]) || selVerts.Contains(c.Indices[o + 1]) || selVerts.Contains(c.Indices[o + 2]))
                            set.Add(t);
                    }
                }
                else
                {
                    var borderVerts = new HashSet<int>();
                    for (int t = 0; t < triCnt; t++)
                    {
                        if (set.Contains(t)) continue;
                        int o = t * 3;
                        borderVerts.Add(c.Indices[o]); borderVerts.Add(c.Indices[o + 1]); borderVerts.Add(c.Indices[o + 2]);
                    }
                    set.RemoveWhere(t =>
                    {
                        int o = t * 3;
                        return borderVerts.Contains(c.Indices[o]) || borderVerts.Contains(c.Indices[o + 1]) || borderVerts.Contains(c.Indices[o + 2]);
                    });
                }
            }
            RefreshFaceEditOverlay3D();
            SyncImportedSculptViewportHud();
        }

        private bool HandleFaceEditInput(InputEvent @event)
        {
            if (!IsFaceEditActive() || _previewCamera == null) return false;

            // Active modal transform (Move / Scale / Inflate)
            if (_faceEditTransformMode != FaceEditTransformMode.None)
            {
                if (@event is InputEventMouseMotion mmTr)
                {
                    UpdateFaceEditTransform(mmTr.Position);
                    GetViewport().SetInputAsHandled();
                    return true;
                }
                if (@event is InputEventMouseButton mbTr && mbTr.Pressed)
                {
                    if (mbTr.ButtonIndex == MouseButton.Left)
                    {
                        CommitFaceEditTransform();
                        GetViewport().SetInputAsHandled();
                        return true;
                    }
                    if (mbTr.ButtonIndex == MouseButton.Right)
                    {
                        CancelFaceEditTransform(restore: true);
                        GetViewport().SetInputAsHandled();
                        return true;
                    }
                    if (_faceEditProportional && (mbTr.ButtonIndex == MouseButton.WheelUp || mbTr.ButtonIndex == MouseButton.WheelDown))
                    {
                        _faceEditPropRadius = Math.Clamp(_faceEditPropRadius + (mbTr.ButtonIndex == MouseButton.WheelUp ? 0.015f : -0.015f), 0.02f, 0.50f);
                        SyncImportedSculptViewportHud();
                        GetViewport().SetInputAsHandled();
                        return true;
                    }
                }
                return false;
            }

            if (@event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
            {
                if (mb.Pressed)
                {
                    if (_impSculptCaches.Count == 0) RebuildImportedSculptCaches();
                    _faceEditDragOp = ResolveSelectOpFromModifiers(mb, defaultAdd: _faceEditSelectTool == FaceEditSelectTool.Paint);

                    if (_faceEditSelectTool == FaceEditSelectTool.Lasso)
                    {
                        _isOrbiting = false; _isPanning = false;
                        _faceEditDraggingLasso = true;
                        _meshCutOverlay2D?.BeginLasso(mb.Position, _faceEditDragOp, restoreMode: false);
                        GetViewport().SetInputAsHandled();
                        return true;
                    }
                    if (_faceEditSelectTool == FaceEditSelectTool.Box)
                    {
                        _isOrbiting = false; _isPanning = false;
                        _faceEditDraggingBox = true;
                        _meshCutOverlay2D?.BeginBox(mb.Position, _faceEditDragOp, restoreMode: false);
                        GetViewport().SetInputAsHandled();
                        return true;
                    }
                    if (_faceEditSelectTool == FaceEditSelectTool.Island)
                    {
                        if (RaycastImportedSculptTriangle(mb.Position, out var hitCache, out int hitTri, out _, out _))
                        {
                            var island = CollectConnectedSculptTriangles(hitCache, hitTri);
                            ApplyFaceEditSelectionOps(hitCache, island, _faceEditDragOp);
                            RefreshFaceEditOverlay3D();
                            SyncImportedSculptViewportHud();
                            _isOrbiting = false; _isPanning = false;
                            GetViewport().SetInputAsHandled();
                            return true;
                        }
                    }
                    if (_faceEditSelectTool == FaceEditSelectTool.Paint)
                    {
                        if (RaycastImportedSculptTriangle(mb.Position, out var hitCache, out _, out var hitPt, out var hitNorm))
                        {
                            _isOrbiting = false; _isPanning = false;
                            _faceEditDraggingPaint = true;
                            SelectFacesInBrush(hitCache, hitPt, _faceEditDragOp);
                            UpdateBrushRingVisual(hitPt, hitNorm, (float)(_sliderImpSculptRadius?.Value ?? 0.10), true);
                            GetViewport().SetInputAsHandled();
                            return true;
                        }
                    }
                }
                else
                {
                    if (_faceEditDraggingLasso)
                    {
                        _faceEditDraggingLasso = false;
                        var poly = _meshCutOverlay2D?.EndLasso();
                        if (poly != null && poly.Length >= 3)
                            SelectFacesInLassoOrBox(poly, null, _faceEditDragOp);
                        GetViewport().SetInputAsHandled();
                        return true;
                    }
                    if (_faceEditDraggingBox)
                    {
                        _faceEditDraggingBox = false;
                        var rect = _meshCutOverlay2D?.EndBox() ?? new Rect2();
                        if (rect.Size.X >= 3f && rect.Size.Y >= 3f)
                            SelectFacesInLassoOrBox(null, rect, _faceEditDragOp);
                        GetViewport().SetInputAsHandled();
                        return true;
                    }
                    if (_faceEditDraggingPaint)
                    {
                        _faceEditDraggingPaint = false;
                        UpdateBrushRingVisual(Vector3.Zero, Vector3.Up, 0.05f, false);
                        GetViewport().SetInputAsHandled();
                        return true;
                    }
                }
            }
            else if (@event is InputEventMouseMotion mm)
            {
                if (_faceEditDraggingLasso)
                {
                    _meshCutOverlay2D?.AppendLassoPoint(mm.Position);
                    GetViewport().SetInputAsHandled();
                    return true;
                }
                if (_faceEditDraggingBox)
                {
                    _meshCutOverlay2D?.UpdateBox(mm.Position);
                    GetViewport().SetInputAsHandled();
                    return true;
                }
                if (_faceEditDraggingPaint && RaycastImportedSculptTriangle(mm.Position, out var hitCache, out _, out var hitPt, out var hitNorm))
                {
                    SelectFacesInBrush(hitCache, hitPt, _faceEditDragOp);
                    UpdateBrushRingVisual(hitPt, hitNorm, (float)(_sliderImpSculptRadius?.Value ?? 0.10), true);
                    GetViewport().SetInputAsHandled();
                    return true;
                }
            }

            return false;
        }

        private bool HandleFaceEditKeyShortcut(InputEventKey key)
        {
            if (!IsFaceEditActive()) return false;

            if (_faceEditTransformMode != FaceEditTransformMode.None)
            {
                if (key.Keycode == Key.Escape)
                {
                    CancelFaceEditTransform(restore: true);
                    return true;
                }
                if (key.Keycode == Key.Enter || key.Keycode == Key.KpEnter)
                {
                    CommitFaceEditTransform();
                    return true;
                }
                if (!key.CtrlPressed && !key.AltPressed)
                {
                    if (key.Keycode == Key.X) { _faceEditAxisLock = _faceEditAxisLock == 1 ? 0 : 1; UpdateFaceEditAxisGuideVisual(); SyncImportedSculptViewportHud(); return true; }
                    if (key.Keycode == Key.Y) { _faceEditAxisLock = _faceEditAxisLock == 2 ? 0 : 2; UpdateFaceEditAxisGuideVisual(); SyncImportedSculptViewportHud(); return true; }
                    if (key.Keycode == Key.Z) { _faceEditAxisLock = _faceEditAxisLock == 3 ? 0 : 3; UpdateFaceEditAxisGuideVisual(); SyncImportedSculptViewportHud(); return true; }
                }
                return false;
            }

            if (key.CtrlPressed && key.Keycode == Key.D) { ClearFaceEditSelection(); return true; }
            if (key.AltPressed && !key.CtrlPressed && key.Keycode == Key.S) { BeginFaceEditTransform(FaceEditTransformMode.Inflate); return true; }

            if (!key.CtrlPressed && !key.AltPressed)
            {
                switch (key.Keycode)
                {
                    case Key.G: BeginFaceEditTransform(FaceEditTransformMode.Move); return true;
                    case Key.S: BeginFaceEditTransform(FaceEditTransformMode.Scale); return true;
                    case Key.L: _faceEditSelectTool = FaceEditSelectTool.Lasso; SyncImportedSculptViewportHud(); return true;
                    case Key.B: _faceEditSelectTool = FaceEditSelectTool.Box; SyncImportedSculptViewportHud(); return true;
                    case Key.C: _faceEditSelectTool = FaceEditSelectTool.Paint; SyncImportedSculptViewportHud(); return true;
                    case Key.I: _faceEditSelectTool = FaceEditSelectTool.Island; SyncImportedSculptViewportHud(); return true;
                    case Key.O: _faceEditProportional = !_faceEditProportional; SyncImportedSculptViewportHud(); return true;
                    case Key.T: _faceEditSelectThrough = !_faceEditSelectThrough; RefreshFaceEditOverlay3D(); SyncImportedSculptViewportHud(); return true;
                    case Key.Equal:
                    case Key.KpAdd: GrowOrShrinkFaceSelection(true); return true;
                    case Key.Minus:
                    case Key.KpSubtract: GrowOrShrinkFaceSelection(false); return true;
                }
            }
            return false;
        }

        private bool RaycastImportedSculptTriangle(Vector2 mousePos, out ImpSculptSurfaceCache hitCache, out int hitTri, out Vector3 hitWorld, out Vector3 hitNorm)
        {
            hitCache = null; hitTri = -1; hitWorld = Vector3.Zero; hitNorm = Vector3.Up;
            if (_previewCamera == null || _impSculptCaches.Count == 0) return false;
            Vector3 rayOrig = _previewCamera.ProjectRayOrigin(mousePos);
            Vector3 rayDir = _previewCamera.ProjectRayNormal(mousePos);
            float bestT = float.MaxValue;
            foreach (var c in _impSculptCaches)
            {
                int triCnt = c.Indices.Length / 3;
                for (int t = 0; t < triCnt; t++)
                {
                    int a = c.Indices[t * 3], b = c.Indices[t * 3 + 1], d = c.Indices[t * 3 + 2];
                    if (CustomWeightPaintEditor.RayTriangle(rayOrig, rayDir, c.CurrentWorldVerts[a], c.CurrentWorldVerts[b], c.CurrentWorldVerts[d], out float hitT) && hitT < bestT)
                    {
                        bestT = hitT;
                        hitCache = c;
                        hitTri = t;
                        hitWorld = rayOrig + rayDir * hitT;
                        Vector3 n = c.CurrentWorldNorms[a] + c.CurrentWorldNorms[b] + c.CurrentWorldNorms[d];
                        hitNorm = n.LengthSquared() > 1e-8f ? n.Normalized() : Vector3.Up;
                    }
                }
            }
            return hitCache != null;
        }

        private static List<int> CollectConnectedSculptTriangles(ImpSculptSurfaceCache c, int seedTri)
        {
            int triCnt = c.Indices.Length / 3;
            var vertToTris = new Dictionary<int, List<int>>();
            for (int t = 0; t < triCnt; t++)
            {
                for (int k = 0; k < 3; k++)
                {
                    int v = c.Indices[t * 3 + k];
                    if (!vertToTris.TryGetValue(v, out var list)) { list = new List<int>(); vertToTris[v] = list; }
                    list.Add(t);
                }
            }
            var visited = new HashSet<int> { seedTri };
            var queue = new Queue<int>();
            queue.Enqueue(seedTri);
            while (queue.Count > 0)
            {
                int cur = queue.Dequeue();
                for (int k = 0; k < 3; k++)
                {
                    if (!vertToTris.TryGetValue(c.Indices[cur * 3 + k], out var nbs)) continue;
                    foreach (int nt in nbs)
                        if (visited.Add(nt)) queue.Enqueue(nt);
                }
            }
            return visited.ToList();
        }

        private void SelectFacesInBrush(ImpSculptSurfaceCache cache, Vector3 hitPt, MeshCutSelectOp op)
        {
            float r = (float)(_sliderImpSculptRadius?.Value ?? 0.10);
            float rSq = r * r;
            var tris = new List<int>();
            int triCnt = cache.Indices.Length / 3;
            Vector3 camPos = _previewCamera.GlobalPosition;
            for (int t = 0; t < triCnt; t++)
            {
                int a = cache.Indices[t * 3], b = cache.Indices[t * 3 + 1], d = cache.Indices[t * 3 + 2];
                Vector3 cW = (cache.CurrentWorldVerts[a] + cache.CurrentWorldVerts[b] + cache.CurrentWorldVerts[d]) / 3f;
                if (cW.DistanceSquaredTo(hitPt) > rSq) continue;
                if (!_faceEditSelectThrough)
                {
                    Vector3 n = (cache.CurrentWorldNorms[a] + cache.CurrentWorldNorms[b] + cache.CurrentWorldNorms[d]).Normalized();
                    if (n.Dot(camPos - cW) <= 0f) continue;
                }
                tris.Add(t);
            }
            if (tris.Count > 0)
            {
                ApplyFaceEditSelectionOps(cache, tris, op);
                RefreshFaceEditOverlay3D();
                SyncImportedSculptViewportHud();
            }
        }

        private void SelectFacesInLassoOrBox(IReadOnlyList<Vector2> poly, Rect2? box, MeshCutSelectOp op)
        {
            if (op == MeshCutSelectOp.Replace) _faceEditSelectedTris.Clear();
            Vector3 camPos = _previewCamera.GlobalPosition;
            foreach (var c in _impSculptCaches)
            {
                var tris = new List<int>();
                int triCnt = c.Indices.Length / 3;
                for (int t = 0; t < triCnt; t++)
                {
                    int a = c.Indices[t * 3], b = c.Indices[t * 3 + 1], d = c.Indices[t * 3 + 2];
                    Vector3 cW = (c.CurrentWorldVerts[a] + c.CurrentWorldVerts[b] + c.CurrentWorldVerts[d]) / 3f;
                    if (_previewCamera.IsPositionBehind(cW)) continue;
                    if (!_faceEditSelectThrough)
                    {
                        Vector3 n = (c.CurrentWorldNorms[a] + c.CurrentWorldNorms[b] + c.CurrentWorldNorms[d]).Normalized();
                        if (n.Dot(camPos - cW) <= 0f) continue;
                    }
                    Vector2 sp = _previewCamera.UnprojectPosition(cW);
                    bool inside = box.HasValue
                        ? box.Value.HasPoint(sp)
                        : PointInPolygon2D(sp, poly);
                    if (inside) tris.Add(t);
                }
                if (tris.Count > 0)
                    ApplyFaceEditSelectionOps(c, tris, op == MeshCutSelectOp.Replace ? MeshCutSelectOp.Add : op);
            }
            RefreshFaceEditOverlay3D();
            SyncImportedSculptViewportHud();
        }

        private static bool PointInPolygon2D(Vector2 p, IReadOnlyList<Vector2> poly)
        {
            bool inside = false;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            {
                Vector2 pi = poly[i], pj = poly[j];
                if (((pi.Y > p.Y) != (pj.Y > p.Y)) &&
                    (p.X < (pj.X - pi.X) * (p.Y - pi.Y) / (pj.Y - pi.Y + 1e-9f) + pi.X))
                    inside = !inside;
            }
            return inside;
        }
    }
}
