using System;
using Godot;
using SteamDatabase.ValvePak;
using ValveResourceFormat.ResourceTypes;
using VrfMaterial = ValveResourceFormat.ResourceTypes.Material;

namespace DeadlockPlayground.Materials.Heroes;

public class CelesteMaterialConfig : IHeroMaterialConfig
{
    public string HeroKey => "unicorn";
    public string DisplayName => "Celeste";

    public Color? SignatureGlowColor => new Color(0.85f, 0.70f, 1.0f, 1.0f); // Pastel lilac

    public void ConfigureBaseMaterial(string meshName, int surfaceIndex, string vmatPath, StandardMaterial3D material)
    {
        string vmatLower = vmatPath?.ToLowerInvariant() ?? "";
        if (vmatLower.Contains("hair"))
        {
            material.Roughness = 0.95f;
            material.Metallic = 0.0f;
            material.MetallicSpecular = 0.0f;
            if (material.EmissionEnabled)
            {
                material.EmissionEnergyMultiplier = Mathf.Clamp(material.EmissionEnergyMultiplier, 0.0f, 0.35f);
            }
        }
    }

    public bool ShouldPreserveMaterial(string meshName, string vmatPath, Godot.Material material)
    {
        string vmatLower = vmatPath?.ToLowerInvariant() ?? "";
        string meshLower = meshName?.ToLowerInvariant() ?? "";

        // Preserve additive pulsing horn glow from Toon material replacement
        if (vmatLower.Contains("unicorn_hornglow") || vmatLower.Contains("unicorn_glow") ||
            meshLower.Contains("hornglow") || meshLower.Contains("unicorn_glow"))
        {
            return true;
        }

        return false;
    }

    public Godot.Material TryCreateCustomMaterial(Package package, string vmatPath, string meshName)
    {
        return TryCreateCustomMaterial(package, vmatPath, meshName, null);
    }

    public Godot.Material TryCreateCustomMaterial(Package package, string vmatPath, string meshName, Package addonPackage)
    {
        string vmatLower = vmatPath?.ToLowerInvariant() ?? "";
        string meshLower = meshName?.ToLowerInvariant() ?? "";

        // 1. Celeste Hair: Soft pastel lavender NPR shading with clamped Fresnel self-illumination
        if (vmatLower.Contains("unicorn_hair"))
        {
            var hairShader = Source2ShaderRegistry.GetUnicornHairShader();
            if (hairShader == null) return null;

            var mat = new ShaderMaterial { Shader = hairShader };
            Color colorTint = Colors.White;
            Color selfIllumTint = new Color(0.31f, 0.42f, 0.91f, 1.0f);
            float selfIllumScale = 0.15f;
            ImageTexture colorTex = null;

            var vrfMat = Source2MaterialHelper.TryReadVmat(package, vmatPath, addonPackage);
            if (vrfMat != null)
            {
                if (vrfMat.VectorParams.TryGetValue("g_vColorTint1", out var ct1) && !Source2ColorMatrix.IsNeutralWhiteOrBlack(ct1))
                    colorTint = new Color(ct1.X, ct1.Y, ct1.Z, 1.0f);
                else if (vrfMat.VectorParams.TryGetValue("g_vColorTint", out var ct0) && !Source2ColorMatrix.IsNeutralWhiteOrBlack(ct0))
                    colorTint = new Color(ct0.X, ct0.Y, ct0.Z, 1.0f);

                if (vrfMat.VectorParams.TryGetValue("g_vSelfIllumTint1", out var si1))
                    selfIllumTint = new Color(si1.X, si1.Y, si1.Z, 1.0f);
                else if (vrfMat.VectorParams.TryGetValue("g_vSelfIllumTint", out var si0))
                    selfIllumTint = new Color(si0.X, si0.Y, si0.Z, 1.0f);

                if (vrfMat.FloatParams.TryGetValue("g_flSelfIllumScale1", out var sc1))
                    selfIllumScale = sc1;
                else if (vrfMat.FloatParams.TryGetValue("g_flSelfIllumScale", out var sc0))
                    selfIllumScale = sc0;

                string colorTexPath = Source2TextureLoader.GetTextureParam(vrfMat, "g_tColor")
                                   ?? Source2TextureLoader.GetTextureParam(vrfMat, "TextureColor")
                                   ?? Source2TextureLoader.GetTextureParam(vrfMat, "g_tColor1");
                if (!string.IsNullOrEmpty(colorTexPath))
                {
                    colorTex = Source2TextureLoader.GetOrLoadTexture(package, colorTexPath, forceOpaque: true, addonPackage: addonPackage);
                }
            }

            if (colorTex != null)
            {
                mat.SetShaderParameter("g_tColor", colorTex);
            }
            mat.SetShaderParameter("g_vColorTint", colorTint);
            mat.SetShaderParameter("g_vSelfIllumTint", selfIllumTint);
            mat.SetShaderParameter("g_flSelfIllumScale1", selfIllumScale);
            mat.SetShaderParameter("roughness", 0.95f);
            mat.SetShaderParameter("specular", 0.0f);
            mat.RenderPriority = 0;
            return mat;
        }

        // 2. Celeste Horn Glow: Additive pulsing spiral core (DynamicParams 2 * sin(3 * time) + 2)
        if (vmatLower.Contains("unicorn_hornglow") || vmatLower.Contains("unicorn_glow") ||
            meshLower.Contains("hornglow") || meshLower.Contains("unicorn_glow"))
        {
            var hornShader = Source2ShaderRegistry.GetUnicornHornGlowShader();
            if (hornShader == null) return null;

            var mat = new ShaderMaterial { Shader = hornShader };
            Color selfIllumTint = new Color(0.5137f, 0.3765f, 0.8941f, 1.0f); // Fallback soft lilac
            float selfIllumScale = 1.0f;
            ImageTexture colorTex = null;
            ImageTexture tintMaskTex = null;

            if (package != null)
            {
                var entry = Source2MaterialHelper.FindVmatEntry(package, vmatPath);
                if (entry != null)
                {
                    package.ReadEntry(entry, out byte[] data);
                    using var res = new ValveResourceFormat.Resource();
                    using var ms = new System.IO.MemoryStream(data);
                    res.Read(ms);
                    if (res.DataBlock is VrfMaterial vrfMat)
                    {
                        if (vrfMat.VectorParams.TryGetValue("g_vSelfIllumTint1", out var si1))
                            selfIllumTint = new Color(si1.X, si1.Y, si1.Z, 1.0f);
                        else if (vrfMat.VectorParams.TryGetValue("g_vSelfIllumTint", out var si0))
                            selfIllumTint = new Color(si0.X, si0.Y, si0.Z, 1.0f);

                        if (vrfMat.FloatParams.TryGetValue("g_flSelfIllumScale1", out var sc1))
                            selfIllumScale = sc1;
                        else if (vrfMat.FloatParams.TryGetValue("g_flSelfIllumScale", out var sc0))
                            selfIllumScale = sc0;

                        string colorTexPath = Source2TextureLoader.GetTextureParam(vrfMat, "g_tColor")
                                           ?? Source2TextureLoader.GetTextureParam(vrfMat, "TextureColor")
                                           ?? Source2TextureLoader.GetTextureParam(vrfMat, "g_tColor1");
                        if (!string.IsNullOrEmpty(colorTexPath))
                        {
                            colorTex = Source2TextureLoader.GetOrLoadTexture(package, colorTexPath, forceOpaque: false);
                        }

                        string tintMaskPath = Source2TextureLoader.GetTextureParam(vrfMat, "g_tTintMask")
                                           ?? Source2TextureLoader.GetTextureParam(vrfMat, "TextureTintMask");
                        if (!string.IsNullOrEmpty(tintMaskPath))
                        {
                            tintMaskTex = Source2TextureLoader.GetOrLoadTexture(package, tintMaskPath, forceOpaque: false);
                        }
                    }
                }
            }

            if (colorTex != null)
            {
                mat.SetShaderParameter("g_tColor", colorTex);
            }
            if (tintMaskTex != null)
            {
                mat.SetShaderParameter("g_tTintMask", tintMaskTex);
                mat.SetShaderParameter("has_tint_mask", true);
            }
            mat.SetShaderParameter("g_vSelfIllumTint", selfIllumTint);
            mat.SetShaderParameter("g_flSelfIllumScale", selfIllumScale);
            mat.RenderPriority = 2;
            mat.SetMeta("PreserveShading", true);
            return mat;
        }

        return null;
    }

    public ShaderMaterial GetSignatureMaterial(string meshName, int surfaceIndex, string vmatPath, StandardMaterial3D baseMat)
    {
        return null;
    }

    public Control BuildUI(Action<string, Variant> onParameterChanged)
    {
        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 8);

        // Horn Glow Intensity
        var rowHorn = new HBoxContainer();
        var lblHorn = new Label { Text = "Horn Glow Scale:", CustomMinimumSize = new Vector2(130, 0) };
        var sliderHorn = new HSlider { MinValue = 0.0, MaxValue = 3.0, Step = 0.1, Value = 1.0, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        var valHorn = new Label { Text = "1.00", CustomMinimumSize = new Vector2(45, 0) };
        rowHorn.AddChild(lblHorn);
        rowHorn.AddChild(sliderHorn);
        rowHorn.AddChild(valHorn);
        vbox.AddChild(rowHorn);

        // Hair Fresnel Tint Intensity
        var rowHair = new HBoxContainer();
        var lblHair = new Label { Text = "Hair Fresnel Scale:", CustomMinimumSize = new Vector2(130, 0) };
        var sliderHair = new HSlider { MinValue = 0.0, MaxValue = 0.5, Step = 0.02, Value = 0.15, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        var valHair = new Label { Text = "0.15", CustomMinimumSize = new Vector2(45, 0) };
        rowHair.AddChild(lblHair);
        rowHair.AddChild(sliderHair);
        rowHair.AddChild(valHair);
        vbox.AddChild(rowHair);

        sliderHorn.ValueChanged += v =>
        {
            valHorn.Text = v.ToString("F2");
            onParameterChanged?.Invoke("g_flSelfIllumScale", (float)v);
        };

        sliderHair.ValueChanged += v =>
        {
            valHair.Text = v.ToString("F2");
            onParameterChanged?.Invoke("g_flSelfIllumScale1", (float)v);
        };

        return vbox;
    }
}
