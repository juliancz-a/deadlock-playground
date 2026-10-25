using System;
using System.Collections.Generic;
using Godot;
using ValveResourceFormat;
using ValveResourceFormat.Blocks;
using ValveKeyValue;
using VrfMesh = ValveResourceFormat.ResourceTypes.Mesh;

namespace DeadlockPlayground.Maps;

/// <summary>
/// Direct binary decoder that converts Source 2 VBIB (Vertex Buffer & Index Buffer)
/// geometry into native Godot 4 ArrayMesh instances without glTF or disk intermediate files.
/// </summary>
public static class VbibMeshDecoder
{
    /// <summary>
    /// Decodes a single draw call from a Source 2 Mesh into a Godot ArrayMesh.
    /// Returns null if the draw call contains no valid geometry.
    /// </summary>
    public static ArrayMesh DecodeDrawCallToArrayMesh(VrfMesh vrfMesh, KVObject drawCall, out string materialPath)
    {
        materialPath = string.Empty;
        if (vrfMesh?.VBIB == null || drawCall == null) return null;

        materialPath = drawCall.TryGetValue("m_material", out var mVal) && mVal != null ? mVal.ToString() : string.Empty;

        int startIndex = GetKvInt(drawCall, "m_nStartIndex", 0);
        int indexCount = GetKvInt(drawCall, "m_nIndexCount", 0);
        int baseVertex = GetKvInt(drawCall, "m_nBaseVertex", 0);
        int vertexCount = GetKvInt(drawCall, "m_nVertexCount", 0);

        if (indexCount <= 0 || vertexCount <= 0) return null;

        // Resolve IndexBuffer
        int indexBufferIdx = GetIndexBufferIndex(drawCall);
        if (indexBufferIdx < 0 || indexBufferIdx >= vrfMesh.VBIB.IndexBuffers.Count) return null;
        var ib = vrfMesh.VBIB.IndexBuffers[indexBufferIdx];
        if (ib.Data == null || ib.Data.Length == 0) return null;

        // Resolve VertexBuffers associated with this draw call
        var boundBuffers = GetBoundVertexBuffers(vrfMesh.VBIB, drawCall);
        if (boundBuffers.Count == 0) return null;

        // Find attribute layout descriptors across bound vertex buffers
        VBIB.RenderInputLayoutField? posField = null;
        VBIB.OnDiskBufferData? posBuffer = null;

        VBIB.RenderInputLayoutField? uvField = null;
        VBIB.OnDiskBufferData? uvBuffer = null;

        VBIB.RenderInputLayoutField? normField = null;
        VBIB.OnDiskBufferData? normBuffer = null;

        VBIB.RenderInputLayoutField? tanField = null;
        VBIB.OnDiskBufferData? tanBuffer = null;

        VBIB.RenderInputLayoutField? colField = null;
        VBIB.OnDiskBufferData? colBuffer = null;

        foreach (var vb in boundBuffers)
        {
            if (vb.InputLayoutFields == null) continue;
            foreach (var layout in vb.InputLayoutFields)
            {
                string sem = layout.SemanticName;
                if (!posField.HasValue && sem.Equals("POSITION", StringComparison.OrdinalIgnoreCase))
                {
                    posField = layout;
                    posBuffer = vb;
                }
                else if (!uvField.HasValue && sem.Equals("TEXCOORD", StringComparison.OrdinalIgnoreCase) && layout.SemanticIndex == 0)
                {
                    uvField = layout;
                    uvBuffer = vb;
                }
                else if (!normField.HasValue && sem.Equals("NORMAL", StringComparison.OrdinalIgnoreCase))
                {
                    normField = layout;
                    normBuffer = vb;
                }
                else if (!tanField.HasValue && sem.Equals("TANGENT", StringComparison.OrdinalIgnoreCase))
                {
                    tanField = layout;
                    tanBuffer = vb;
                }
                else if (!colField.HasValue && sem.Equals("COLOR", StringComparison.OrdinalIgnoreCase))
                {
                    colField = layout;
                    colBuffer = vb;
                }
            }
        }

        if (!posField.HasValue || !posBuffer.HasValue || posBuffer.Value.Data == null) return null;

        var pField = posField.Value;
        var pBuffer = posBuffer.Value;

        // Decompress Normals and Tangents via VRF's official VBIB decoder
        System.Numerics.Vector3[] vrfNormals = null;
        System.Numerics.Vector4[] vrfTangents = null;

        if (normField.HasValue && normBuffer.HasValue && normBuffer.Value.Data != null)
        {
            try
            {
                var (nArr, tArr) = VBIB.GetNormalTangentArray(normBuffer.Value, normField.Value);
                vrfNormals = nArr;
                if (tArr != null && tArr.Length > 0)
                {
                    vrfTangents = tArr;
                }
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[VbibMeshDecoder] Error decompressing normals via VBIB: {ex.Message}");
            }
        }

        // If tangents weren't packed with normals, check for dedicated TANGENT semantic
        if ((vrfTangents == null || vrfTangents.Length == 0) && tanField.HasValue && tanBuffer.HasValue && tanBuffer.Value.Data != null)
        {
            try
            {
                vrfTangents = VBIB.GetVector4AttributeArray(tanBuffer.Value, tanField.Value);
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[VbibMeshDecoder] Error reading separate TANGENT attribute: {ex.Message}");
            }
        }

        // Allocate Godot vertex arrays
        var vertices = new Vector3[vertexCount];
        var uvs = uvField.HasValue ? new Vector2[vertexCount] : null;
        var normals = (vrfNormals != null && vrfNormals.Length > 0) ? new Vector3[vertexCount] : null;
        var tangents = (vrfTangents != null && vrfTangents.Length > 0) ? new float[vertexCount * 4] : null;
        var colors = colField.HasValue ? new Color[vertexCount] : null;

        // 1. Decode Vertex Attributes
        for (int i = 0; i < vertexCount; i++)
        {
            int globalVertexIdx = baseVertex + i;

            // Position
            int posOffset = (int)(globalVertexIdx * pBuffer.ElementSizeInBytes + pField.Offset);
            if (posOffset + 12 <= pBuffer.Data.Length)
            {
                float px = BitConverter.ToSingle(pBuffer.Data, posOffset);
                float py = BitConverter.ToSingle(pBuffer.Data, posOffset + 4);
                float pz = BitConverter.ToSingle(pBuffer.Data, posOffset + 8);
                vertices[i] = MapCoordinateUtils.SourceToGodotPoint(px, py, pz);
            }

            // UV0
            if (uvField.HasValue && uvBuffer.HasValue && uvBuffer.Value.Data != null)
            {
                var uField = uvField.Value;
                var uBuf = uvBuffer.Value;
                int uvOffset = (int)(globalVertexIdx * uBuf.ElementSizeInBytes + uField.Offset);
                if (uField.Format == DXGI_FORMAT.R16G16_FLOAT && uvOffset + 4 <= uBuf.Data.Length)
                {
                    float u = (float)BitConverter.ToHalf(uBuf.Data, uvOffset);
                    float v = (float)BitConverter.ToHalf(uBuf.Data, uvOffset + 2);
                    uvs[i] = new Vector2(u, v);
                }
                else if (uField.Format == DXGI_FORMAT.R32G32_FLOAT && uvOffset + 8 <= uBuf.Data.Length)
                {
                    float u = BitConverter.ToSingle(uBuf.Data, uvOffset);
                    float v = BitConverter.ToSingle(uBuf.Data, uvOffset + 4);
                    uvs[i] = new Vector2(u, v);
                }
            }

            // Normal
            if (normals != null && globalVertexIdx < vrfNormals.Length)
            {
                var sn = vrfNormals[globalVertexIdx];
                normals[i] = MapCoordinateUtils.SourceToGodotNormal(sn.X, sn.Y, sn.Z);
            }

            // Tangent
            if (tangents != null && globalVertexIdx < vrfTangents.Length)
            {
                var st = vrfTangents[globalVertexIdx];
                var gt = MapCoordinateUtils.SourceToGodotNormal(st.X, st.Y, st.Z);
                tangents[i * 4 + 0] = gt.X;
                tangents[i * 4 + 1] = gt.Y;
                tangents[i * 4 + 2] = gt.Z;
                tangents[i * 4 + 3] = st.W;
            }

            // Color (Vertex Color blending)
            if (colField.HasValue && colBuffer.HasValue && colBuffer.Value.Data != null)
            {
                var cField = colField.Value;
                var cBuf = colBuffer.Value;
                int colOffset = (int)(globalVertexIdx * cBuf.ElementSizeInBytes + cField.Offset);
                if (colOffset + 4 <= cBuf.Data.Length)
                {
                    byte r = cBuf.Data[colOffset + 0];
                    byte g = cBuf.Data[colOffset + 1];
                    byte b = cBuf.Data[colOffset + 2];
                    byte a = cBuf.Data[colOffset + 3];
                    colors[i] = new Color(r / 255f, g / 255f, b / 255f, a / 255f);
                }
            }
        }

        // 2. Decode Index Buffer
        var indices = new int[indexCount];
        int elemSize = (int)ib.ElementSizeInBytes;

        for (int i = 0; i < indexCount; i++)
        {
            int byteOffset = (startIndex + i) * elemSize;
            if (byteOffset + elemSize > ib.Data.Length) break;

            int rawIndex = elemSize == 2
                ? BitConverter.ToUInt16(ib.Data, byteOffset)
                : (int)BitConverter.ToUInt32(ib.Data, byteOffset);

            // Remap global vertex index to local slice [0, vertexCount - 1]
            int localIdx = rawIndex;
            if (localIdx >= baseVertex && localIdx < baseVertex + vertexCount)
            {
                localIdx -= baseVertex;
            }
            else if (localIdx >= vertexCount)
            {
                localIdx %= vertexCount;
            }

            indices[i] = localIdx;
        }

        // Reverse triangle index winding order (swap i1 and i2) to match Godot CCW front-facing winding
        // Coordinate conversion (Source Z-Up -> Godot Y-Up with negative Y) inverts the geometric orientation
        for (int i = 0; i + 2 < indexCount; i += 3)
        {
            int temp = indices[i + 1];
            indices[i + 1] = indices[i + 2];
            indices[i + 2] = temp;
        }

        // 3. Assemble Godot ArrayMesh
        var surfaceArrays = new Godot.Collections.Array();
        surfaceArrays.Resize((int)Mesh.ArrayType.Max);

        surfaceArrays[(int)Mesh.ArrayType.Vertex] = vertices;
        surfaceArrays[(int)Mesh.ArrayType.Index] = indices;

        if (uvs != null) surfaceArrays[(int)Mesh.ArrayType.TexUV] = uvs;
        if (normals != null) surfaceArrays[(int)Mesh.ArrayType.Normal] = normals;
        if (tangents != null) surfaceArrays[(int)Mesh.ArrayType.Tangent] = tangents;
        if (colors != null) surfaceArrays[(int)Mesh.ArrayType.Color] = colors;

        var arrayMesh = new ArrayMesh();
        arrayMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, surfaceArrays);

        return arrayMesh;
    }

    private static int GetIndexBufferIndex(KVObject drawCall)
    {
        if (drawCall.TryGetValue("m_indexBuffer", out var ibVal) && ibVal != null)
        {
            if (ibVal.ValueType == KVValueType.Int32 || ibVal.ValueType == KVValueType.Int64 || ibVal.ValueType == KVValueType.UInt32)
            {
                if (int.TryParse(ibVal.ToString(), out int directInt)) return directInt;
            }
            if (ibVal.TryGetValue("m_hBuffer", out var hbVal) && hbVal != null && int.TryParse(hbVal.ToString(), out int hIdx))
            {
                return hIdx;
            }
            if (ibVal.TryGetValue("m_nBuffer", out var nbVal) && nbVal != null && int.TryParse(nbVal.ToString(), out int nIdx))
            {
                return nIdx;
            }
            if (ibVal.ValueType != KVValueType.Collection && int.TryParse(ibVal.ToString(), out int fallbackIdx))
            {
                return fallbackIdx;
            }
        }
        return 0;
    }

    private static List<VBIB.OnDiskBufferData> GetBoundVertexBuffers(VBIB vbib, KVObject drawCall)
    {
        var list = new List<VBIB.OnDiskBufferData>();
        if (drawCall.TryGetValue("m_vertexBuffers", out var vbArr) && vbArr != null && vbArr.IsArray)
        {
            for (int i = 0; i < vbArr.Count; i++)
            {
                var elem = vbArr[i];
                int bufferIdx = 0;
                if (elem.TryGetValue("m_hBuffer", out var hb) && hb != null && int.TryParse(hb.ToString(), out int hIdx))
                {
                    bufferIdx = hIdx;
                }
                else if (elem.TryGetValue("m_nBuffer", out var nb) && nb != null && int.TryParse(nb.ToString(), out int nIdx))
                {
                    bufferIdx = nIdx;
                }
                else if (elem.ValueType != KVValueType.Collection && int.TryParse(elem.ToString(), out int parsedIdx))
                {
                    bufferIdx = parsedIdx;
                }

                if (bufferIdx >= 0 && bufferIdx < vbib.VertexBuffers.Count)
                {
                    list.Add(vbib.VertexBuffers[bufferIdx]);
                }
            }
        }

        if (list.Count == 0 && vbib.VertexBuffers.Count > 0)
        {
            list.AddRange(vbib.VertexBuffers);
        }

        return list;
    }

    private static int GetKvInt(KVObject obj, string key, int fallback)
    {
        if (obj != null && obj.TryGetValue(key, out var val) && val != null)
        {
            if (int.TryParse(val.ToString(), out int r)) return r;
        }
        return fallback;
    }
}
