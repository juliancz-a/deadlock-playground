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

        private readonly List<Triangle> _triangles = new();
        private Aabb _localBounds;
        private bool _isInitialized = false;

        public bool IsInitialized => _isInitialized;
        public int TriangleCount => _triangles.Count;

        /// <summary>
        /// Extracts triangle geometry and UV maps from the specified MeshInstance3D surface(s).
        /// If targetSurfaceIndex == -1, processes all surfaces of the mesh.
        /// </summary>
        public bool BuildFromMesh(MeshInstance3D meshInstance, int targetSurfaceIndex = -1)
        {
            _triangles.Clear();
            _isInitialized = false;

            if (meshInstance == null || meshInstance.Mesh == null) return false;

            int surfaceCount = meshInstance.Mesh.GetSurfaceCount();
            if (surfaceCount == 0) return false;

            bool firstBound = true;
            int startSurface = (targetSurfaceIndex >= 0) ? targetSurfaceIndex : 0;
            int endSurface = (targetSurfaceIndex >= 0) ? targetSurfaceIndex + 1 : surfaceCount;

            if (startSurface >= surfaceCount) return false;

            for (int s = startSurface; s < endSurface; s++)
            {
                var arrays = meshInstance.Mesh.SurfaceGetArrays(s);
                if (arrays == null || arrays.Count == 0) continue;

                var vertices = (Vector3[])arrays[(int)Mesh.ArrayType.Vertex];
                var uvs = (Vector2[])arrays[(int)Mesh.ArrayType.TexUV];
                var indices = (int[])arrays[(int)Mesh.ArrayType.Index];

                if (vertices == null || vertices.Length == 0) continue;

                if (firstBound)
                {
                    _localBounds = new Aabb(vertices[0], Vector3.Zero);
                    firstBound = false;
                }

                for (int i = 0; i < vertices.Length; i++)
                {
                    _localBounds = _localBounds.Expand(vertices[i]);
                }

                bool hasUVs = uvs != null && uvs.Length == vertices.Length;

                if (indices != null && indices.Length >= 3)
                {
                    for (int i = 0; i < indices.Length; i += 3)
                    {
                        int i0 = indices[i];
                        int i1 = indices[i + 1];
                        int i2 = indices[i + 2];

                        if (i0 >= vertices.Length || i1 >= vertices.Length || i2 >= vertices.Length) continue;

                        Vector3 v0 = vertices[i0];
                        Vector3 v1 = vertices[i1];
                        Vector3 v2 = vertices[i2];

                        Vector2 uv0 = hasUVs ? uvs[i0] : Vector2.Zero;
                        Vector2 uv1 = hasUVs ? uvs[i1] : Vector2.Zero;
                        Vector2 uv2 = hasUVs ? uvs[i2] : Vector2.Zero;

                        Vector3 normal = (v1 - v0).Cross(v2 - v0).Normalized();

                        _triangles.Add(new Triangle
                        {
                            V0 = v0, V1 = v1, V2 = v2,
                            UV0 = uv0, UV1 = uv1, UV2 = uv2,
                            Normal = normal,
                            SurfaceIndex = s
                        });
                    }
                }
                else
                {
                    for (int i = 0; i < vertices.Length - 2; i += 3)
                    {
                        Vector3 v0 = vertices[i];
                        Vector3 v1 = vertices[i + 1];
                        Vector3 v2 = vertices[i + 2];

                        Vector2 uv0 = hasUVs ? uvs[i] : Vector2.Zero;
                        Vector2 uv1 = hasUVs ? uvs[i + 1] : Vector2.Zero;
                        Vector2 uv2 = hasUVs ? uvs[i + 2] : Vector2.Zero;

                        Vector3 normal = (v1 - v0).Cross(v2 - v0).Normalized();

                        _triangles.Add(new Triangle
                        {
                            V0 = v0, V1 = v1, V2 = v2,
                            UV0 = uv0, UV1 = uv1, UV2 = uv2,
                            Normal = normal,
                            SurfaceIndex = s
                        });
                    }
                }
            }

            _isInitialized = _triangles.Count > 0;
            return _isInitialized;
        }

        /// <summary>
        /// Casts a ray against the mesh geometry and returns the closest hit point, normal, surface index, and interpolated UV.
        /// </summary>
        public RaycastHitResult IntersectRay(MeshInstance3D meshInstance, Vector3 worldRayOrigin, Vector3 worldRayDir, bool cullBackfaces = false)
        {
            var result = new RaycastHitResult { Hit = false, Distance = float.MaxValue, HitSurfaceIndex = -1 };
            if (!_isInitialized || meshInstance == null) return result;

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

            for (int i = 0; i < _triangles.Count; i++)
            {
                var tri = _triangles[i];

                if (cullBackfaces && tri.Normal.Dot(localDir) > 0)
                {
                    continue; // Skip back-facing triangle
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
                result.HitUV = interpolatedUV;
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
    }
}
