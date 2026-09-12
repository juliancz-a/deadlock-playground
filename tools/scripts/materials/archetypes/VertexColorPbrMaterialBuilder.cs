using System;
using System.Collections.Generic;
using Godot;
using SteamDatabase.ValvePak;
using ValveResourceFormat.ResourceTypes;
using VrfMaterial = ValveResourceFormat.ResourceTypes.Material;

namespace DeadlockPlayground.Materials.Archetypes;

/// <summary>
/// Builds ShaderMaterial instances using source2_vertcolor_pbr.gdshader for head, hair,
/// and turban meshes where pigmentation is baked into vertex colors (COLOR.rgb) rather than
/// traditional diffuse textures (e.g. Wraith head, Mirage turban/hair).
/// </summary>
public static class VertexColorPbrMaterialBuilder
{
    public static bool IsApplicable(VrfMaterial matResource, string vmatPath, string diffusePath, bool isFur)
    {
        if (matResource == null || isFur) return false;
        string vmatLower = vmatPath?.ToLowerInvariant() ?? "";

        bool isVertColorMat = matResource.IntParams.GetValueOrDefault("F_VERTEX_COLOR", 0) == 1 || vmatLower.Contains("vertcolor");
        bool isPaintVertexColors = matResource.IntParams.GetValueOrDefault("F_PAINT_VERTEX_COLORS", 0) == 1;

        bool isDevDummyTexture = string.IsNullOrEmpty(diffusePath) || diffusePath.Contains("498635a");
        bool isNprTransmissive = !string.IsNullOrEmpty(diffusePath) && diffusePath.Contains("nprtransmissive", StringComparison.OrdinalIgnoreCase);
        bool isMirageVertColor = vmatLower.Contains("miragev3_vertcolor");

        string nprTransTexturePath = Source2TextureLoader.GetTextureParam(matResource, "g_tNprTransmissiveColor")
                                  ?? Source2TextureLoader.GetTextureParam(matResource, "TextureNprTransmissiveColor")
                                  ?? Source2TextureLoader.GetTextureParam(matResource, "TextureNprTramsissiveColor");

        bool hasNprTransmissiveVector = matResource.VectorParams.ContainsKey("TextureNprTramsissiveColor1")
                                     || matResource.VectorParams.ContainsKey("TextureNprTransmissiveColor1")
                                     || matResource.VectorParams.ContainsKey("TextureNprTramsissiveColor")
                                     || matResource.VectorParams.ContainsKey("TextureNprTransmissiveColor");

        return isDevDummyTexture
            || isNprTransmissive
            || isMirageVertColor
            || ((isVertColorMat || isPaintVertexColors) && (!string.IsNullOrEmpty(nprTransTexturePath) || hasNprTransmissiveVector));
    }

    public static Godot.Material Build(Package package, VrfMaterial matResource, string vmatPath)
    {
        var vertColorShader = Source2ShaderRegistry.GetVertexColorPbrShader();
        if (vertColorShader != null)
        {
            var pbrMat = new ShaderMaterial { Shader = vertColorShader };
            pbrMat.ResourceName = vmatPath;

            // 1. Base skin / hair transmissive tint
            string nprTransTexturePath = Source2TextureLoader.GetTextureParam(matResource, "g_tNprTransmissiveColor")
                                      ?? Source2TextureLoader.GetTextureParam(matResource, "TextureNprTransmissiveColor")
                                      ?? Source2TextureLoader.GetTextureParam(matResource, "TextureNprTramsissiveColor");

            ImageTexture transTex = null;
            if (!string.IsNullOrEmpty(nprTransTexturePath))
            {
                transTex = Source2TextureLoader.GetOrLoadTexture(package, nprTransTexturePath, forceOpaque: true);
            }

            if (transTex != null)
            {
                pbrMat.SetShaderParameter("g_tNprTransmissiveColor", transTex);
                pbrMat.SetShaderParameter("has_transmissive_texture", true);
            }
            else
            {
                pbrMat.SetShaderParameter("has_transmissive_texture", false);
            }

            // Vector parameter fallback (e.g. Wraith TextureNprTramsissiveColor1 = [0.556, 0.361, 0.274, 1.0])
            Color skinColor = new Color(0.556f, 0.361f, 0.274f, 1.0f);
            System.Numerics.Vector4 nprVec = default;
            if (matResource.VectorParams.TryGetValue("TextureNprTramsissiveColor1", out nprVec)
                || matResource.VectorParams.TryGetValue("TextureNprTransmissiveColor1", out nprVec)
                || matResource.VectorParams.TryGetValue("TextureNprTramsissiveColor", out nprVec)
                || matResource.VectorParams.TryGetValue("TextureNprTransmissiveColor", out nprVec))
            {
                if (nprVec.X > 0.001f || nprVec.Y > 0.001f || nprVec.Z > 0.001f)
                {
                    skinColor = new Color(nprVec.X, nprVec.Y, nprVec.Z, 1.0f);
                }
            }
            pbrMat.SetShaderParameter("g_vSkinColor", skinColor);

            // CSB colour-correction matrix from g_vAlbedoContrastSaturationBrightness1
            Projection csbMatrix = Projection.Identity;
            System.Numerics.Vector4 csbVec = default;
            bool hasCsb = matResource.VectorParams.TryGetValue("g_vAlbedoContrastSaturationBrightness1", out csbVec)
                       || matResource.VectorParams.TryGetValue("g_vAlbedoContrastSaturationBrightness", out csbVec);
            if (hasCsb && (csbVec.X != 1.0f || csbVec.Y != 1.0f || csbVec.Z != 1.0f))
            {
                System.Numerics.Vector4 colorOffset = default;
                matResource.VectorParams.TryGetValue("g_vAlbedoColorOffset1", out colorOffset);
                csbMatrix = Source2ColorMatrix.CalculateAlbedoColorCorrectMatrix(
                    new System.Numerics.Vector3(csbVec.X, csbVec.Y, csbVec.Z),
                    new System.Numerics.Vector3(colorOffset.X, colorOffset.Y, colorOffset.Z)
                );
            }
            pbrMat.SetShaderParameter("g_mAlbedoColorCorrect", csbMatrix);

            // 2. Normal & Roughness map
            string nrPath = Source2TextureLoader.GetTextureParam(matResource, "g_tNormalRoughness")
                         ?? Source2TextureLoader.GetTextureParam(matResource, "TextureNormalRoughness")
                         ?? Source2TextureLoader.GetTextureParam(matResource, "g_tNormal")
                         ?? Source2TextureLoader.GetTextureParam(matResource, "TextureNormal");
            bool hasNormal = false;
            if (!string.IsNullOrEmpty(nrPath))
            {
                var nrTex = Source2TextureLoader.GetOrLoadTexture(package, nrPath, forceOpaque: true);
                if (nrTex != null)
                {
                    pbrMat.SetShaderParameter("g_tNormalRoughness", nrTex);
                    hasNormal = true;
                }
            }
            pbrMat.SetShaderParameter("normal_map_depth", hasNormal ? 1.0f : 0.0f);

            // 3. Roughness scalar
            pbrMat.SetShaderParameter("roughness", 1.0f);
            pbrMat.SetShaderParameter("metallic", 0.0f);

            // 4. Ambient Occlusion map
            string aoPbrPath = Source2TextureLoader.GetTextureParam(matResource, "g_tAmbientOcclusion")
                            ?? Source2TextureLoader.GetTextureParam(matResource, "TextureAmbientOcclusion");
            if (!string.IsNullOrEmpty(aoPbrPath))
            {
                var aoTex = Source2TextureLoader.GetOrLoadTexture(package, aoPbrPath, forceOpaque: false);
                if (aoTex != null)
                {
                    pbrMat.SetShaderParameter("g_tAmbientOcclusion", aoTex);
                }
            }

            return pbrMat;
        }

        // Fallback: StandardMaterial3D with neutral vertex colors
        var godotMat = new StandardMaterial3D();
        godotMat.AlbedoTexture = null;
        godotMat.AlbedoColor = Colors.White;
        godotMat.VertexColorUseAsAlbedo = true;
        godotMat.NormalEnabled = false;

        string aoFbPath = Source2TextureLoader.GetTextureParam(matResource, "g_tAmbientOcclusion")
                       ?? Source2TextureLoader.GetTextureParam(matResource, "TextureAmbientOcclusion");
        if (!string.IsNullOrEmpty(aoFbPath))
        {
            var aoTex = Source2TextureLoader.GetOrLoadTexture(package, aoFbPath, forceOpaque: false);
            if (aoTex != null)
            {
                godotMat.AOEnabled = true;
                godotMat.AOTexture = aoTex;
                godotMat.AOLightAffect = 0.7f;
            }
        }

        godotMat.Roughness = 0.94f;
        godotMat.Metallic = 0.0f;
        godotMat.MetallicSpecular = 0.05f;
        return godotMat;
    }
}
