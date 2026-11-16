using Godot;
using System;
using System.Collections.Generic;

namespace DeadlockPlayground.Painter
{
    public struct RaycastHitResult
    {
        public bool Hit;
        public Vector2 HitUV;
        public Vector3 WorldPosition;
        public Vector3 WorldNormal;
        public float Distance;
        public int TriangleIndex;
        public MeshInstance3D HitMesh;
        public int HitSurfaceIndex;
        public Vector2 UvAspectScale;
        public Vector3 WorldTangent;
        public Vector3 WorldBitangent;
        public float WorldUnitsPerU;
        public float WorldUnitsPerV;

        public Vector3 HitPositionWorld => WorldPosition;
        public Vector3 HitNormal => WorldNormal;
    }

    public class MeshRaycaster
    {
        public struct Triangle
        {
            public Vector3 V0, V1, V2;
            public Vector2 UV0, UV1, UV2;
            public Vector3 Normal;
            public int SurfaceIndex;
        }

        private Triangle[] _triangles = Array.Empty<Triangle>();
        private Vector3[] _restPositions = Array.Empty<Vector3>();
        private Vector3[] _restNormals = Array.Empty<Vector3>();
        private Vector3[] _posedPositions = Array.Empty<Vector3>();
        private Vector3[] _posedNormals = Array.Empty<Vector3>();
        private bool[] _vertHasNorm = Array.Empty<bool>();
        private int[] _triV0 = Array.Empty<int>();
        private int[] _triV1 = Array.Empty<int>();
        private int[] _triV2 = Array.Empty<int>();
        private bool[] _triHasAuthoredNorm = Array.Empty<bool>();

        private bool _hasSkinning = false;
        private int _skinStride = 4;
        private int[] _vertBones = Array.Empty<int>();
        private float[] _vertWeights = Array.Empty<float>();
        private int[] _bindToSkelBone = Array.Empty<int>();
        private int[] _uniqueUsedBinds = Array.Empty<int>();
        private Transform3D[] _bindMatrices = Array.Empty<Transform3D>();
        private ulong _lastPoseHash = 0;

        private Aabb _localBounds;
        private bool _isInitialized = false;

        public bool IsInitialized => _isInitialized;
        public int TriangleCount => _triangles.Length;
        public IReadOnlyList<Triangle> Triangles => _triangles;
        public Triangle GetTriangle(int index) => _triangles[index];

        /// <summary>
        /// Extracts triangle geometry and UV maps from the specified MeshInstance3D surface(s),
        /// plus optional 4/8-bone skinning weights so raycasts track posed skeletons accurately.
        /// If targetSurfaceIndex == -1, processes all surfaces of the mesh.
        /// </summary>
        public bool BuildFromMesh(MeshInstance3D meshInstance, int targetSurfaceIndex = -1)
        {
            _triangles = Array.Empty<Triangle>();
            _restPositions = Array.Empty<Vector3>();
            _restNormals = Array.Empty<Vector3>();
            _posedPositions = Array.Empty<Vector3>();
            _posedNormals = Array.Empty<Vector3>();
            _vertHasNorm = Array.Empty<bool>();
            _triV0 = Array.Empty<int>();
            _triV1 = Array.Empty<int>();
            _triV2 = Array.Empty<int>();
            _triHasAuthoredNorm = Array.Empty<bool>();
            _vertBones = Array.Empty<int>();
            _vertWeights = Array.Empty<float>();
            _bindToSkelBone = Array.Empty<int>();
            _uniqueUsedBinds = Array.Empty<int>();
            _bindMatrices = Array.Empty<Transform3D>();
            _hasSkinning = false;
            _skinStride = 4;
            _lastPoseHash = 0;
            _isInitialized = false;

            if (meshInstance == null || meshInstance.Mesh == null) return false;

            int surfaceCount = meshInstance.Mesh.GetSurfaceCount();
            if (surfaceCount == 0) return false;

            int startSurface = (targetSurfaceIndex >= 0) ? targetSurfaceIndex : 0;
            int endSurface = (targetSurfaceIndex >= 0) ? targetSurfaceIndex + 1 : surfaceCount;
            if (startSurface >= surfaceCount) return false;

            // Detect 4-bone vs 8-bone skinning across requested surfaces
            Skeleton3D skel = ResolveSkeletonForMesh(meshInstance);
            Skin skin = meshInstance.Skin;
            bool canSkin = skel != null && GodotObject.IsInstanceValid(skel) && skin != null && skin.GetBindCount() > 0;
            int detectedStride = 4;
            if (canSkin)
            {
                for (int s = startSurface; s < endSurface; s++)
                {
                    var arr = meshInstance.Mesh.SurfaceGetArrays(s);
                    if (arr == null || arr.Count <= (int)Mesh.ArrayType.Vertex) continue;
                    var verts = arr[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                    if (verts == null || verts.Length == 0) continue;
                    int[] b = arr.Count > (int)Mesh.ArrayType.Bones ? arr[(int)Mesh.ArrayType.Bones].AsInt32Array() : null;
                    float[] w = arr.Count > (int)Mesh.ArrayType.Weights ? arr[(int)Mesh.ArrayType.Weights].AsFloat32Array() : null;
                    if (b != null && w != null && b.Length >= verts.Length * 8 && w.Length >= verts.Length * 8)
                    {
                        detectedStride = 8;
                        break;
                    }
                }
            }
            _skinStride = detectedStride;

            var triList = new List<Triangle>();
            var restPosList = new List<Vector3>();
            var restNormList = new List<Vector3>();
            var vertHasNormList = new List<bool>();
            var triV0List = new List<int>();
            var triV1List = new List<int>();
            var triV2List = new List<int>();
            var triHasAuthoredNormList = new List<bool>();
            var vertBoneList = new List<int>();
            var vertWeightList = new List<float>();
            var usedBindsSet = new HashSet<int>();
            bool anySkinnedSurface = false;

            int bindCount = skin != null ? skin.GetBindCount() : 0;
            int skelBoneCount = skel != null ? skel.GetBoneCount() : 0;

            for (int s = startSurface; s < endSurface; s++)
            {
                var arrays = meshInstance.Mesh.SurfaceGetArrays(s);
                if (arrays == null || arrays.Count == 0) continue;

                var vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                var normals = arrays.Count > (int)Mesh.ArrayType.Normal ? arrays[(int)Mesh.ArrayType.Normal].AsVector3Array() : null;
                var uvs = arrays.Count > (int)Mesh.ArrayType.TexUV ? arrays[(int)Mesh.ArrayType.TexUV].AsVector2Array() : null;
                var indices = arrays.Count > (int)Mesh.ArrayType.Index ? arrays[(int)Mesh.ArrayType.Index].AsInt32Array() : null;
                var rawBones = canSkin && arrays.Count > (int)Mesh.ArrayType.Bones ? arrays[(int)Mesh.ArrayType.Bones].AsInt32Array() : null;
                var rawWeights = canSkin && arrays.Count > (int)Mesh.ArrayType.Weights ? arrays[(int)Mesh.ArrayType.Weights].AsFloat32Array() : null;

                if (vertices == null || vertices.Length == 0) continue;

                int baseVertIdx = restPosList.Count;
                bool hasNormals = normals != null && normals.Length == vertices.Length;
                bool hasUVs = uvs != null && uvs.Length == vertices.Length;
                int surfStride = (rawBones != null && rawWeights != null && rawBones.Length >= vertices.Length * 8 && rawWeights.Length >= vertices.Length * 8) ? 8 : 4;
                bool surfSkinned = canSkin && rawBones != null && rawWeights != null &&
                                   rawBones.Length >= vertices.Length * surfStride &&
                                   rawWeights.Length >= vertices.Length * surfStride;
                if (surfSkinned) anySkinnedSurface = true;

                for (int i = 0; i < vertices.Length; i++)
                {
                    restPosList.Add(vertices[i]);
                    restNormList.Add(hasNormals ? normals[i] : Vector3.Up);
                    vertHasNormList.Add(hasNormals);

                    for (int k = 0; k < _skinStride; k++)
                    {
                        if (surfSkinned && k < surfStride)
                        {
                            int bi = rawBones[i * surfStride + k];
                            float bw = rawWeights[i * surfStride + k];
                            if (bw > 1e-4f && bi >= 0 && bi < bindCount)
                            {
                                vertBoneList.Add(bi);
                                vertWeightList.Add(bw);
                                usedBindsSet.Add(bi);
                            }
                            else
                            {
                                vertBoneList.Add(0);
                                vertWeightList.Add(0f);
                            }
                        }
                        else
                        {
                            vertBoneList.Add(0);
                            vertWeightList.Add(0f);
                        }
                    }
                }

                void AddTriangle(int li0, int li1, int li2)
                {
                    Vector3 v0 = vertices[li0];
                    Vector3 v1 = vertices[li1];
                    Vector3 v2 = vertices[li2];

                    Vector2 uv0 = hasUVs ? uvs[li0] : Vector2.Zero;
                    Vector2 uv1 = hasUVs ? uvs[li1] : Vector2.Zero;
                    Vector2 uv2 = hasUVs ? uvs[li2] : Vector2.Zero;

                    Vector3 normal = (v1 - v0).Cross(v2 - v0).Normalized();
                    if (hasNormals)
                    {
                        Vector3 avgAuthored = (normals[li0] + normals[li1] + normals[li2]).Normalized();
                        if (avgAuthored.LengthSquared() > 0.001f && normal.Dot(avgAuthored) < 0f)
                        {
                            normal = -normal;
                        }
                    }

                    triList.Add(new Triangle
                    {
                        V0 = v0, V1 = v1, V2 = v2,
                        UV0 = uv0, UV1 = uv1, UV2 = uv2,
                        Normal = normal,
                        SurfaceIndex = s
                    });
                    triV0List.Add(baseVertIdx + li0);
                    triV1List.Add(baseVertIdx + li1);
                    triV2List.Add(baseVertIdx + li2);
                    triHasAuthoredNormList.Add(hasNormals);
                }

                if (indices != null && indices.Length >= 3)
                {
                    for (int i = 0; i < indices.Length - 2; i += 3)
                    {
                        int i0 = indices[i];
                        int i1 = indices[i + 1];
                        int i2 = indices[i + 2];
                        if (i0 < 0 || i1 < 0 || i2 < 0 || i0 >= vertices.Length || i1 >= vertices.Length || i2 >= vertices.Length) continue;
                        AddTriangle(i0, i1, i2);
                    }
                }
                else
                {
                    for (int i = 0; i < vertices.Length - 2; i += 3)
                    {
                        AddTriangle(i, i + 1, i + 2);
                    }
                }
            }

            _triangles = triList.ToArray();
            _restPositions = restPosList.ToArray();
            _restNormals = restNormList.ToArray();
            _posedPositions = (Vector3[])_restPositions.Clone();
            _posedNormals = (Vector3[])_restNormals.Clone();
            _vertHasNorm = vertHasNormList.ToArray();
            _triV0 = triV0List.ToArray();
            _triV1 = triV1List.ToArray();
            _triV2 = triV2List.ToArray();
            _triHasAuthoredNorm = triHasAuthoredNormList.ToArray();

            RecomputeBoundsFromPositions(_posedPositions);

            _hasSkinning = anySkinnedSurface && usedBindsSet.Count > 0 && canSkin;
            if (_hasSkinning)
            {
                _vertBones = vertBoneList.ToArray();
                _vertWeights = vertWeightList.ToArray();
                _bindToSkelBone = new int[bindCount];
                _bindMatrices = new Transform3D[bindCount];
                for (int bi = 0; bi < bindCount; bi++)
                {
                    int bIdx = skin.GetBindBone(bi);
                    if (bIdx < 0 || bIdx >= skelBoneCount)
                    {
                        string bName = skin.GetBindName(bi);
                        if (!string.IsNullOrEmpty(bName)) bIdx = skel.FindBone(bName);
                    }
                    _bindToSkelBone[bi] = (bIdx >= 0 && bIdx < skelBoneCount) ? bIdx : -1;
                    _bindMatrices[bi] = Transform3D.Identity;
                }
                _uniqueUsedBinds = new List<int>(usedBindsSet).ToArray();
                _lastPoseHash = 0;
                UpdateSkinningIfNeeded(meshInstance);
            }

            _isInitialized = _triangles.Length > 0;
            return _isInitialized;
        }

        private static Skeleton3D ResolveSkeletonForMesh(MeshInstance3D meshInstance)
        {
            if (meshInstance == null || !GodotObject.IsInstanceValid(meshInstance)) return null;
            Skeleton3D skel = null;
            if (!string.IsNullOrEmpty(meshInstance.Skeleton.ToString()))
            {
                skel = meshInstance.GetNodeOrNull<Skeleton3D>(meshInstance.Skeleton);
            }
            if (skel == null && meshInstance.GetParent() is Skeleton3D parentSkel)
            {
                skel = parentSkel;
            }
            return skel;
        }

        private void RecomputeBoundsFromPositions(Vector3[] positions)
        {
            if (positions == null || positions.Length == 0)
            {
                _localBounds = new Aabb();
                return;
            }
            Vector3 min = positions[0];
            Vector3 max = positions[0];
            for (int i = 1; i < positions.Length; i++)
            {
                Vector3 p = positions[i];
                if (p.X < min.X) min.X = p.X;
                if (p.Y < min.Y) min.Y = p.Y;
                if (p.Z < min.Z) min.Z = p.Z;
                if (p.X > max.X) max.X = p.X;
                if (p.Y > max.Y) max.Y = p.Y;
                if (p.Z > max.Z) max.Z = p.Z;
            }
            _localBounds = new Aabb(min, max - min);
        }

        private static ulong MixTransform(ulong h, in Transform3D xf)
        {
            static ulong F(ulong acc, float v)
            {
                int bits = BitConverter.SingleToInt32Bits(MathF.Abs(v) < 1e-6f ? 0f : v);
                unchecked
                {
                    acc ^= (uint)bits;
                    acc *= 1099511628211UL;
                }
                return acc;
            }
            h = F(h, xf.Basis.X.X); h = F(h, xf.Basis.X.Y); h = F(h, xf.Basis.X.Z);
            h = F(h, xf.Basis.Y.X); h = F(h, xf.Basis.Y.Y); h = F(h, xf.Basis.Y.Z);
            h = F(h, xf.Basis.Z.X); h = F(h, xf.Basis.Z.Y); h = F(h, xf.Basis.Z.Z);
            h = F(h, xf.Origin.X);  h = F(h, xf.Origin.Y);  h = F(h, xf.Origin.Z);
            return h;
        }

        /// <summary>
        /// Re-evaluates cached skinned triangle geometry whenever the attached Skeleton3D's pose
        /// changes, keeping raycasts and UV lookups synchronized with posed meshes.
        /// Returns immediately in O(uniqueUsedBones) when the pose has not changed.
        /// </summary>
        public void UpdateSkinningIfNeeded(MeshInstance3D meshInstance)
        {
            if (!_hasSkinning || meshInstance == null || !GodotObject.IsInstanceValid(meshInstance))
                return;

            Skeleton3D skel = ResolveSkeletonForMesh(meshInstance);
            Skin skin = meshInstance.Skin;
            if (skel == null || !GodotObject.IsInstanceValid(skel) || skin == null)
                return;

            Transform3D toMeshLocal = (meshInstance.IsInsideTree() && skel.IsInsideTree())
                ? meshInstance.GlobalTransform.AffineInverse() * skel.GlobalTransform
                : Transform3D.Identity;
            ulong poseHash = 14695981039346656037UL;
            poseHash = MixTransform(poseHash, toMeshLocal);
            for (int u = 0; u < _uniqueUsedBinds.Length; u++)
            {
                int bi = _uniqueUsedBinds[u];
                int boneIdx = bi < _bindToSkelBone.Length ? _bindToSkelBone[bi] : -1;
                if (boneIdx >= 0)
                {
                    poseHash = MixTransform(poseHash, skel.GetBoneGlobalPose(boneIdx));
                }
            }
            if (poseHash == 0) poseHash = 1;
            if (poseHash == _lastPoseHash) return;

            for (int u = 0; u < _uniqueUsedBinds.Length; u++)
            {
                int bi = _uniqueUsedBinds[u];
                int boneIdx = bi < _bindToSkelBone.Length ? _bindToSkelBone[bi] : -1;
                _bindMatrices[bi] = boneIdx >= 0
                    ? toMeshLocal * skel.GetBoneGlobalPose(boneIdx) * skin.GetBindPose(bi)
                    : toMeshLocal;
            }

            int vCount = _restPositions.Length;
            int stride = _skinStride;
            int bindLen = _bindMatrices.Length;

            Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 max = new Vector3(float.MinValue, float.MinValue, float.MinValue);

            for (int v = 0; v < vCount; v++)
            {
                Vector3 rp = _restPositions[v];
                Vector3 rn = _restNormals[v];
                bool hasN = _vertHasNorm[v];
                Vector3 accPos = Vector3.Zero;
                Vector3 accNorm = Vector3.Zero;
                float totalW = 0f;
                int baseOff = v * stride;

                for (int k = 0; k < stride; k++)
                {
                    float w = _vertWeights[baseOff + k];
                    if (w <= 1e-4f) continue;
                    int bi = _vertBones[baseOff + k];
                    if ((uint)bi >= (uint)bindLen) continue;
                    Transform3D m = _bindMatrices[bi];
                    accPos += (m * rp) * w;
                    if (hasN) accNorm += (m.Basis * rn) * w;
                    totalW += w;
                }

                Vector3 pp = totalW > 1e-4f ? accPos * (1f / totalW) : (toMeshLocal * rp);
                _posedPositions[v] = pp;
                if (hasN)
                {
                    _posedNormals[v] = totalW > 1e-4f && accNorm.LengthSquared() > 1e-6f
                        ? accNorm.Normalized()
                        : (toMeshLocal.Basis * rn).Normalized();
                }

                if (pp.X < min.X) min.X = pp.X;
                if (pp.Y < min.Y) min.Y = pp.Y;
                if (pp.Z < min.Z) min.Z = pp.Z;
                if (pp.X > max.X) max.X = pp.X;
                if (pp.Y > max.Y) max.Y = pp.Y;
                if (pp.Z > max.Z) max.Z = pp.Z;
            }

            if (vCount > 0)
            {
                _localBounds = new Aabb(min, max - min);
            }

            int tCount = _triangles.Length;
            for (int i = 0; i < tCount; i++)
            {
                int i0 = _triV0[i];
                int i1 = _triV1[i];
                int i2 = _triV2[i];
                Vector3 v0 = _posedPositions[i0];
                Vector3 v1 = _posedPositions[i1];
                Vector3 v2 = _posedPositions[i2];
                Vector3 n = (v1 - v0).Cross(v2 - v0).Normalized();
                if (_triHasAuthoredNorm[i])
                {
                    Vector3 avgAuth = (_posedNormals[i0] + _posedNormals[i1] + _posedNormals[i2]).Normalized();
                    if (avgAuth.LengthSquared() > 0.001f && n.Dot(avgAuth) < 0f)
                    {
                        n = -n;
                    }
                }
                _triangles[i].V0 = v0;
                _triangles[i].V1 = v1;
                _triangles[i].V2 = v2;
                _triangles[i].Normal = n;
            }

            _lastPoseHash = poseHash;
        }

        /// <summary>
        /// Wraps a UV coordinate outside [0, 1] into [0, 1) using Source 2 repeat tiling rules,
        /// preserving coordinates that already lie inside [0.0, 1.0].
        /// </summary>
        public static Vector2 WrapUv01(Vector2 uv)
        {
            float u = (uv.X < 0.0f || uv.X > 1.0f) ? Mathf.PosMod(uv.X, 1.0f) : uv.X;
            float v = (uv.Y < 0.0f || uv.Y > 1.0f) ? Mathf.PosMod(uv.Y, 1.0f) : uv.Y;
            return new Vector2(u, v);
        }

        /// <summary>
        /// Shifts a triangle's UV coordinates by an integer tile offset when its centroid lies outside [0, 1],
        /// mapping shifted UV tiles cleanly into [0, 1] without splitting edges or clamping islands.
        /// </summary>
        public static void WrapTriangleUvsToUnitSquare(ref Vector2 uv0, ref Vector2 uv1, ref Vector2 uv2)
        {
            float minX = MathF.Min(uv0.X, MathF.Min(uv1.X, uv2.X));
            float maxX = MathF.Max(uv0.X, MathF.Max(uv1.X, uv2.X));
            float minY = MathF.Min(uv0.Y, MathF.Min(uv1.Y, uv2.Y));
            float maxY = MathF.Max(uv0.Y, MathF.Max(uv1.Y, uv2.Y));

            Vector2 avg = (uv0 + uv1 + uv2) * (1.0f / 3.0f);
            float shiftX = (minX < -0.005f || maxX > 1.005f) ? MathF.Floor(avg.X) : 0.0f;
            float shiftY = (minY < -0.005f || maxY > 1.005f) ? MathF.Floor(avg.Y) : 0.0f;

            if (shiftX != 0.0f || shiftY != 0.0f)
            {
                Vector2 offset = new Vector2(shiftX, shiftY);
                uv0 -= offset;
                uv1 -= offset;
                uv2 -= offset;
            }
        }

        /// <summary>
        /// Casts a ray against the mesh geometry and returns the closest hit point, normal, surface index, and interpolated UV.
        /// </summary>
        public RaycastHitResult IntersectRay(MeshInstance3D meshInstance, Vector3 worldRayOrigin, Vector3 worldRayDir, bool cullBackfaces = false)
        {
            var result = new RaycastHitResult { Hit = false, Distance = float.MaxValue, HitSurfaceIndex = -1 };
            if (!_isInitialized || meshInstance == null) return result;
            UpdateSkinningIfNeeded(meshInstance);

            Transform3D invTransform = meshInstance.GlobalTransform.AffineInverse();
            Vector3 localOrigin = invTransform * worldRayOrigin;
            Vector3 localDir = invTransform.Basis * worldRayDir;
            float dirLen = localDir.Length();
            if (dirLen < 1e-6f) return result;
            localDir /= dirLen;

            // AABB quick test with generous margin
            if (!RayIntersectsAabb(localOrigin, localDir, _localBounds.Grow(0.15f)))
            {
                return result;
            }

            float closestDist = float.MaxValue;
            int bestTriIndex = -1;
            float bestU = 0f, bestV = 0f;

            for (int i = 0; i < _triangles.Length; i++)
            {
                var tri = _triangles[i];

                if (cullBackfaces)
                {
                    // For curved cloth folds or single-sided geometry with smoothed vertex normals,
                    // verify both authored normal and geometric face normal before culling,
                    // ensuring exterior folds are never falsely skipped in favor of inner layers.
                    Vector3 faceNormal = (tri.V1 - tri.V0).Cross(tri.V2 - tri.V0);
                    if (tri.Normal.Dot(localDir) > 0.08f && faceNormal.Dot(localDir) > 0.001f)
                    {
                        continue; // Strictly back-facing triangle
                    }
                }

                if (RayIntersectsTriangle(localOrigin, localDir, tri.V0, tri.V1, tri.V2, out float t, out float u, out float v))
                {
                    if (t > 1e-4f && t < closestDist)
                    {
                        closestDist = t;
                        bestTriIndex = i;
                        bestU = u;
                        bestV = v;
                    }
                }
            }

            if (bestTriIndex >= 0)
            {
                var hitTri = _triangles[bestTriIndex];
                float w = 1.0f - bestU - bestV;

                Vector2 interpolatedUV = w * hitTri.UV0 + bestU * hitTri.UV1 + bestV * hitTri.UV2;
                Vector3 localHitPos = localOrigin + localDir * closestDist;
                Vector3 worldHitPos = meshInstance.GlobalTransform * localHitPos;
                Vector3 worldNormal = (meshInstance.GlobalTransform.Basis * hitTri.Normal).Normalized();

                // Ensure surface normal faces the incoming ray on double-sided surfaces
                if (worldNormal.Dot(worldRayDir) > 0f)
                {
                    worldNormal = -worldNormal;
                }

                // Compute local UV aspect ratio compensation so circular brush stamps remain circular in 3D
                Vector3 e1 = hitTri.V1 - hitTri.V0;
                Vector3 e2 = hitTri.V2 - hitTri.V0;
                Vector2 duv1 = hitTri.UV1 - hitTri.UV0;
                Vector2 duv2 = hitTri.UV2 - hitTri.UV0;

                float det = duv1.X * duv2.Y - duv2.X * duv1.Y;
                Vector2 aspectScale = Vector2.One;
                if (MathF.Abs(det) > 1e-7f)
                {
                    Vector3 dPdu = (e1 * duv2.Y - e2 * duv1.Y) / det;
                    Vector3 dPdv = (e2 * duv1.X - e1 * duv2.X) / det;

                    float lenU = dPdu.Length();
                    float lenV = dPdv.Length();

                    if (lenU > 1e-4f && lenV > 1e-4f)
                    {
                        float ratio = lenV / lenU;
                        ratio = Mathf.Clamp(ratio, 0.25f, 4.0f);
                        if (ratio > 1.0f)
                        {
                            aspectScale = new Vector2(ratio, 1.0f);
                        }
                        else
                        {
                            aspectScale = new Vector2(1.0f, 1.0f / ratio);
                        }
                        aspectScale /= MathF.Sqrt(aspectScale.X * aspectScale.Y);
                    }
                }

                Vector3 worldTangent = Vector3.Zero;
                Vector3 worldBitangent = Vector3.Zero;
                float worldUnitsPerU = 1.0f;
                float worldUnitsPerV = 1.0f;

                if (MathF.Abs(det) > 1e-7f)
                {
                    Vector3 dPdu = (e1 * duv2.Y - e2 * duv1.Y) / det;
                    Vector3 dPdv = (e2 * duv1.X - e1 * duv2.X) / det;

                    Vector3 worldDPdu = meshInstance.GlobalTransform.Basis * dPdu;
                    Vector3 worldDPdv = meshInstance.GlobalTransform.Basis * dPdv;

                    worldUnitsPerU = worldDPdu.Length();
                    worldUnitsPerV = worldDPdv.Length();

                    worldTangent = worldDPdu.Normalized();
                    worldBitangent = worldDPdv.Normalized();

                    // Orthogonalize tangent against normal
                    worldTangent = (worldTangent - worldNormal * worldNormal.Dot(worldTangent)).Normalized();
                    worldBitangent = (worldBitangent - worldNormal * worldNormal.Dot(worldBitangent)).Normalized();
                }

                result.Hit = true;
                result.HitMesh = meshInstance;
                result.HitUV = WrapUv01(interpolatedUV);
                result.WorldPosition = worldHitPos;
                result.WorldNormal = worldNormal;
                result.WorldTangent = worldTangent;
                result.WorldBitangent = worldBitangent;
                result.WorldUnitsPerU = worldUnitsPerU;
                result.WorldUnitsPerV = worldUnitsPerV;
                result.Distance = closestDist;
                result.TriangleIndex = bestTriIndex;
                result.HitSurfaceIndex = hitTri.SurfaceIndex;
                result.UvAspectScale = aspectScale;
            }

            return result;
        }

        public RaycastHitResult RaycastMesh(Vector3 worldRayOrigin, Vector3 worldRayDir, MeshInstance3D meshInstance, bool cullBackfaces = false)
        {
            return IntersectRay(meshInstance, worldRayOrigin, worldRayDir, cullBackfaces);
        }

        private static bool RayIntersectsAabb(in Vector3 rayOrigin, in Vector3 rayDir, in Aabb aabb)
        {
            Vector3 min = aabb.Position;
            Vector3 max = aabb.Position + aabb.Size;

            float invDx = MathF.Abs(rayDir.X) > 1e-7f ? 1.0f / rayDir.X : (rayDir.X >= 0 ? 1e7f : -1e7f);
            float invDy = MathF.Abs(rayDir.Y) > 1e-7f ? 1.0f / rayDir.Y : (rayDir.Y >= 0 ? 1e7f : -1e7f);
            float invDz = MathF.Abs(rayDir.Z) > 1e-7f ? 1.0f / rayDir.Z : (rayDir.Z >= 0 ? 1e7f : -1e7f);

            float t1 = (min.X - rayOrigin.X) * invDx;
            float t2 = (max.X - rayOrigin.X) * invDx;
            float tmin = MathF.Min(t1, t2);
            float tmax = MathF.Max(t1, t2);

            float ty1 = (min.Y - rayOrigin.Y) * invDy;
            float ty2 = (max.Y - rayOrigin.Y) * invDy;
            tmin = MathF.Max(tmin, MathF.Min(ty1, ty2));
            tmax = MathF.Min(tmax, MathF.Max(ty1, ty2));

            float tz1 = (min.Z - rayOrigin.Z) * invDz;
            float tz2 = (max.Z - rayOrigin.Z) * invDz;
            tmin = MathF.Max(tmin, MathF.Min(tz1, tz2));
            tmax = MathF.Min(tmax, MathF.Max(tz1, tz2));

            return tmax >= MathF.Max(0f, tmin);
        }

        private static bool RayIntersectsTriangle(
            in Vector3 rayOrigin, in Vector3 rayDir,
            in Vector3 v0, in Vector3 v1, in Vector3 v2,
            out float t, out float u, out float v)
        {
            t = 0f; u = 0f; v = 0f;
            Vector3 edge1 = v1 - v0;
            Vector3 edge2 = v2 - v0;
            Vector3 pvec = rayDir.Cross(edge2);
            float det = edge1.Dot(pvec);

            if (MathF.Abs(det) < 1e-7f) return false;
            float invDet = 1.0f / det;

            Vector3 tvec = rayOrigin - v0;
            u = tvec.Dot(pvec) * invDet;
            if (u < 0.0f || u > 1.0f) return false;

            Vector3 qvec = tvec.Cross(edge1);
            v = rayDir.Dot(qvec) * invDet;
            if (v < 0.0f || u + v > 1.0f) return false;

            t = edge2.Dot(qvec) * invDet;
            return t > 1e-5f;
        }

        /// <summary>
        /// Evaluates a 2D UV coordinate against the mesh geometry and returns the corresponding
        /// 3D world position, normal, tangents, and units per UV.
        /// Enables seamless two-way positioning between 2D canvas and 3D viewport.
        /// </summary>
        public RaycastHitResult FindPointFromUV(MeshInstance3D meshInstance, Vector2 targetUV, Vector3? cameraPosition = null)
        {
            var result = new RaycastHitResult { Hit = false };
            if (!_isInitialized || meshInstance == null || _triangles.Length == 0) return result;
            UpdateSkinningIfNeeded(meshInstance);

            int closestTriIdx = -1;
            float closestDistSq = float.MaxValue;
            float closestU = 0f, closestV = 0f, closestW = 0f;

            RaycastHitResult bestCandidate = default;
            float bestCamDistSq = float.MaxValue;
            bool foundFacingCam = false;

            for (int i = 0; i < _triangles.Length; i++)
            {
                var tri = _triangles[i];
                Vector2 uv0 = tri.UV0;
                Vector2 uv1 = tri.UV1;
                Vector2 uv2 = tri.UV2;
                WrapTriangleUvsToUnitSquare(ref uv0, ref uv1, ref uv2);

                Vector2 v0 = uv1 - uv0;
                Vector2 v1 = uv2 - uv0;
                Vector2 v2 = targetUV - uv0;

                float d00 = v0.Dot(v0);
                float d01 = v0.Dot(v1);
                float d11 = v1.Dot(v1);
                float d20 = v2.Dot(v0);
                float d21 = v2.Dot(v1);

                float denom = d00 * d11 - d01 * d01;
                if (MathF.Abs(denom) < 1e-9f) continue;

                float invDenom = 1.0f / denom;
                float v = (d11 * d20 - d01 * d21) * invDenom;
                float w = (d00 * d21 - d01 * d20) * invDenom;
                float u = 1.0f - v - w;

                const float eps = -0.015f;
                if (u >= eps && v >= eps && w >= eps)
                {
                    u = Math.Clamp(u, 0.0f, 1.0f);
                    v = Math.Clamp(v, 0.0f, 1.0f);
                    w = Math.Clamp(w, 0.0f, 1.0f);
                    float sum = u + v + w;
                    if (sum > 0.0001f) { u /= sum; v /= sum; w /= sum; }

                    Vector3 localPos = u * tri.V0 + v * tri.V1 + w * tri.V2;
                    Vector3 localNormal = tri.Normal;
                    Vector3 worldPos = meshInstance.GlobalTransform * localPos;
                    Vector3 worldNormal = (meshInstance.GlobalTransform.Basis * localNormal).Normalized();

                    Vector3 e1 = tri.V1 - tri.V0;
                    Vector3 e2 = tri.V2 - tri.V0;
                    Vector2 duv1 = tri.UV1 - tri.UV0;
                    Vector2 duv2 = tri.UV2 - tri.UV0;
                    float det = duv1.X * duv2.Y - duv2.X * duv1.Y;
                    Vector3 worldTangent = Vector3.Zero;
                    Vector3 worldBitangent = Vector3.Zero;
                    float worldUnitsPerU = 1.0f;
                    float worldUnitsPerV = 1.0f;

                    if (MathF.Abs(det) > 1e-7f)
                    {
                        Vector3 dPdu = (e1 * duv2.Y - e2 * duv1.Y) / det;
                        Vector3 dPdv = (e2 * duv1.X - e1 * duv2.X) / det;
                        Vector3 worldDPdu = meshInstance.GlobalTransform.Basis * dPdu;
                        Vector3 worldDPdv = meshInstance.GlobalTransform.Basis * dPdv;
                        worldUnitsPerU = worldDPdu.Length();
                        worldUnitsPerV = worldDPdv.Length();
                        worldTangent = (worldDPdu.Normalized() - worldNormal * worldNormal.Dot(worldDPdu.Normalized())).Normalized();
                        worldBitangent = (worldDPdv.Normalized() - worldNormal * worldNormal.Dot(worldDPdv.Normalized())).Normalized();
                    }

                    var candidate = new RaycastHitResult
                    {
                        Hit = true,
                        HitUV = targetUV,
                        WorldPosition = worldPos,
                        WorldNormal = worldNormal,
                        WorldTangent = worldTangent,
                        WorldBitangent = worldBitangent,
                        WorldUnitsPerU = worldUnitsPerU,
                        WorldUnitsPerV = worldUnitsPerV,
                        HitMesh = meshInstance,
                        HitSurfaceIndex = tri.SurfaceIndex
                    };

                    if (!cameraPosition.HasValue)
                    {
                        return candidate;
                    }

                    Vector3 toCam = cameraPosition.Value - worldPos;
                    float distSq = toCam.LengthSquared();
                    bool facesCam = worldNormal.Dot(toCam) > 0.05f;

                    if (facesCam)
                    {
                        if (!foundFacingCam || distSq < bestCamDistSq)
                        {
                            foundFacingCam = true;
                            bestCamDistSq = distSq;
                            bestCandidate = candidate;
                        }
                    }
                    else if (!foundFacingCam && distSq < bestCamDistSq)
                    {
                        bestCamDistSq = distSq;
                        bestCandidate = candidate;
                    }
                    continue;
                }

                // Track closest point on UV boundary for edge tolerance
                float cu = Math.Clamp(u, 0.0f, 1.0f);
                float cv = Math.Clamp(v, 0.0f, 1.0f);
                float cw = Math.Clamp(w, 0.0f, 1.0f);
                float csum = cu + cv + cw;
                if (csum > 0.0001f) { cu /= csum; cv /= csum; cw /= csum; }
                Vector2 clampedUV = cu * uv0 + cv * uv1 + cw * uv2;
                float dSq = targetUV.DistanceSquaredTo(clampedUV);
                if (dSq < closestDistSq)
                {
                    closestDistSq = dSq;
                    closestTriIdx = i;
                    closestU = cu;
                    closestV = cv;
                    closestW = cw;
                }
            }

            if (bestCandidate.Hit)
            {
                return bestCandidate;
            }

            if (!result.Hit && closestTriIdx >= 0 && closestDistSq < 0.05f * 0.05f)
            {
                var tri = _triangles[closestTriIdx];
                float u = closestU;
                float v = closestV;
                float w = closestW;
                Vector3 localPos = u * tri.V0 + v * tri.V1 + w * tri.V2;
                Vector3 localNormal = tri.Normal;
                Vector3 worldPos = meshInstance.GlobalTransform * localPos;
                Vector3 worldNormal = (meshInstance.GlobalTransform.Basis * localNormal).Normalized();

                Vector3 e1 = tri.V1 - tri.V0;
                Vector3 e2 = tri.V2 - tri.V0;
                Vector2 duv1 = tri.UV1 - tri.UV0;
                Vector2 duv2 = tri.UV2 - tri.UV0;
                float det = duv1.X * duv2.Y - duv2.X * duv1.Y;
                Vector3 worldTangent = Vector3.Zero;
                Vector3 worldBitangent = Vector3.Zero;
                float worldUnitsPerU = 1.0f;
                float worldUnitsPerV = 1.0f;

                if (MathF.Abs(det) > 1e-7f)
                {
                    Vector3 dPdu = (e1 * duv2.Y - e2 * duv1.Y) / det;
                    Vector3 dPdv = (e2 * duv1.X - e1 * duv2.X) / det;
                    Vector3 worldDPdu = meshInstance.GlobalTransform.Basis * dPdu;
                    Vector3 worldDPdv = meshInstance.GlobalTransform.Basis * dPdv;
                    worldUnitsPerU = worldDPdu.Length();
                    worldUnitsPerV = worldDPdv.Length();
                    worldTangent = (worldDPdu.Normalized() - worldNormal * worldNormal.Dot(worldDPdu.Normalized())).Normalized();
                    worldBitangent = (worldDPdv.Normalized() - worldNormal * worldNormal.Dot(worldDPdv.Normalized())).Normalized();
                }

                result.Hit = true;
                result.HitUV = targetUV;
                result.WorldPosition = worldPos;
                result.WorldNormal = worldNormal;
                result.WorldTangent = worldTangent;
                result.WorldBitangent = worldBitangent;
                result.WorldUnitsPerU = worldUnitsPerU;
                result.WorldUnitsPerV = worldUnitsPerV;
                result.HitMesh = meshInstance;
                result.HitSurfaceIndex = tri.SurfaceIndex;
                return result;
            }

            return result;
        }
    }
}
