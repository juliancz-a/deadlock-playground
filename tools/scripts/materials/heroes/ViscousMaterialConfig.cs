using System;
using Godot;

namespace DeadlockPlayground.Materials.Heroes;

public class ViscousMaterialConfig : IHeroMaterialConfig
{
    public string HeroKey => "viscous";
    public string DisplayName => "Viscous";

    public Color? SignatureGlowColor => new Color(0.12f, 0.72f, 0.32f, 1.0f); // Viscous slime green

    private Color? _dynamicOutlineColor;

    private Shader _slimeShader;

    public ViscousMaterialConfig()
    {
        if (ResourceLoader.Exists("res://assets/shaders/valve/viscous.gdshader"))
            _slimeShader = GD.Load<Shader>("res://assets/shaders/valve/viscous.gdshader");
        else if (ResourceLoader.Exists("res://assets/shaders/viscous.gdshader"))
            _slimeShader = GD.Load<Shader>("res://assets/shaders/viscous.gdshader");
    }

    public void ConfigureBaseMaterial(string meshName, int surfaceIndex, string vmatPath, StandardMaterial3D material)
    {
        // Base materials are accurately parsed and configured directly from VPK attributes by Source2MaterialHelper
    }

    public bool ShouldPreserveMaterial(string meshName, string vmatPath, Godot.Material material)
    {
        string vmatLower = vmatPath?.ToLowerInvariant() ?? "";
        string meshLower = meshName?.ToLowerInvariant() ?? "";

        // Preserve viscous slime body, ball, and inverted-hull outline
        if (vmatLower.Contains("viscous_body") || vmatLower.Contains("viscous_ball") ||
            vmatLower.Contains("viscous_outline") ||
            meshLower.Contains("body_outline") || meshLower.Contains("bodyoutline"))
        {
            return true;
        }

        return false;
    }

    public Godot.Material TryCreateCustomMaterial(SteamDatabase.ValvePak.Package package, string vmatPath, string meshName)
    {
        return TryCreateCustomMaterial(package, vmatPath, meshName, null);
    }

    public Godot.Material TryCreateCustomMaterial(SteamDatabase.ValvePak.Package package, string vmatPath, string meshName, SteamDatabase.ValvePak.Package addonPackage)
    {
        string vmatLower = vmatPath?.ToLowerInvariant() ?? "";
        string meshLower = meshName?.ToLowerInvariant() ?? "";

        // 1. Viscous Inverted-Hull Outline
        if (vmatLower.Contains("viscous_outline") || meshLower.Contains("body_outline") || meshLower.Contains("bodyoutline"))
        {
            var outlineShader = Source2ShaderRegistry.GetViscousOutlineShader();
            if (outlineShader == null) return null;

            var mat = new ShaderMaterial { Shader = outlineShader };
            Color tint = new Color(0.08f, 0.08f, 0.08f, 1.0f);
            float thickness = 0.004f;

            var vrfMat = Source2MaterialHelper.TryReadVmat(package, vmatPath, addonPackage);
            if (vrfMat != null)
            {
                if (vrfMat.VectorParams.TryGetValue("TextureColor1", out var tc1) && (tc1.X > 0.001f || tc1.Y > 0.001f || tc1.Z > 0.001f) && !Source2ColorMatrix.IsNeutralWhiteOrBlack(tc1))
                    tint = new Color(tc1.X, tc1.Y, tc1.Z, 1.0f);
                else if (vrfMat.VectorParams.TryGetValue("TextureColor", out var tc0) && (tc0.X > 0.001f || tc0.Y > 0.001f || tc0.Z > 0.001f) && !Source2ColorMatrix.IsNeutralWhiteOrBlack(tc0))
                    tint = new Color(tc0.X, tc0.Y, tc0.Z, 1.0f);
                else if (vrfMat.VectorParams.TryGetValue("g_vColorTint1", out var ct1) && !Source2ColorMatrix.IsNeutralWhiteOrBlack(ct1))
                    tint = new Color(ct1.X, ct1.Y, ct1.Z, 1.0f);
                else if (vrfMat.VectorParams.TryGetValue("g_vColorTint", out var ct0) && !Source2ColorMatrix.IsNeutralWhiteOrBlack(ct0))
                    tint = new Color(ct0.X, ct0.Y, ct0.Z, 1.0f);
                else if (vrfMat.VectorParams.TryGetValue("g_vSolidOutlineTint1", out var ot1) && !Source2ColorMatrix.IsNeutralWhiteOrBlack(ot1))
                    tint = new Color(ot1.X, ot1.Y, ot1.Z, 1.0f);
                else if (vrfMat.VectorParams.TryGetValue("g_vSolidOutlineTint", out var ot0) && !Source2ColorMatrix.IsNeutralWhiteOrBlack(ot0))
                    tint = new Color(ot0.X, ot0.Y, ot0.Z, 1.0f);

                if (vrfMat.FloatParams.TryGetValue("g_flOutlineThickness1", out var th1)) thickness = th1;
                else if (vrfMat.FloatParams.TryGetValue("g_flOutlineThickness", out var th0)) thickness = th0;
            }

            _dynamicOutlineColor = tint;
            mat.SetShaderParameter("g_vSolidOutlineTint", tint);
            mat.SetShaderParameter("outline_color", tint);
            mat.SetShaderParameter("outline_thickness", thickness);
            mat.RenderPriority = 0;
            mat.SetMeta("PreserveShading", true);
            return mat;
        }

        // 2. Viscous Slime Body / Ball
        if (vmatLower.Contains("viscous_body") || vmatLower.Contains("viscous_ball"))
        {
            if (_slimeShader == null) return null;

            var mat = new ShaderMaterial { Shader = _slimeShader };
            mat.RenderPriority = 1;

            // Extract dynamic properties from VPK VMAT data without hardcoding
            Color slimeBase = new Color(0.37f, 0.75f, 0.39f, 1.0f); // Default #5FBE64
            Color rimGlow = new Color(0.40f, 1.0f, 0.55f, 1.0f);
            float opacity = 0.55f;
            ImageTexture colorTex = null;

            var vrfMat = Source2MaterialHelper.TryReadVmat(package, vmatPath, addonPackage);
            if (vrfMat != null)
            {
                if (vrfMat.VectorParams.TryGetValue("TextureColor1", out var tc1) && (tc1.X > 0.001f || tc1.Y > 0.001f || tc1.Z > 0.001f))
                    slimeBase = new Color(tc1.X, tc1.Y, tc1.Z, 1.0f);
                else if (vrfMat.VectorParams.TryGetValue("TextureColor", out var tc0) && (tc0.X > 0.001f || tc0.Y > 0.001f || tc0.Z > 0.001f))
                    slimeBase = new Color(tc0.X, tc0.Y, tc0.Z, 1.0f);
                else if (vrfMat.VectorParams.TryGetValue("g_vColorTint1", out var vt1) && !Source2ColorMatrix.IsNeutralWhiteOrBlack(vt1))
                    slimeBase = new Color(vt1.X, vt1.Y, vt1.Z, 1.0f);
                else if (vrfMat.VectorParams.TryGetValue("g_vColorTint", out var vt0) && !Source2ColorMatrix.IsNeutralWhiteOrBlack(vt0))
                    slimeBase = new Color(vt0.X, vt0.Y, vt0.Z, 1.0f);

                if (vrfMat.VectorParams.TryGetValue("g_vSelfIllumTint1", out var si1) && (si1.X > 0.001f || si1.Y > 0.001f || si1.Z > 0.001f))
                    rimGlow = new Color(si1.X, si1.Y, si1.Z, 1.0f);
                else if (vrfMat.VectorParams.TryGetValue("g_vSelfIllumTint", out var si0) && (si0.X > 0.001f || si0.Y > 0.001f || si0.Z > 0.001f))
                    rimGlow = new Color(si0.X, si0.Y, si0.Z, 1.0f);

                if (vrfMat.FloatParams.TryGetValue("g_flCloakFactor1", out var cf1) && cf1 > 0.01f)
                    opacity = Mathf.Clamp(cf1 * 0.55f, 0.35f, 0.85f);
                else if (vrfMat.FloatParams.TryGetValue("g_flOpacityScale1", out var os1) && os1 > 0.01f)
                    opacity = Mathf.Clamp(os1, 0.35f, 0.85f);

                string colorTexPath = Source2TextureLoader.GetTextureParam(vrfMat, "g_tColor")
                                   ?? Source2TextureLoader.GetTextureParam(vrfMat, "TextureColor")
                                   ?? Source2TextureLoader.GetTextureParam(vrfMat, "g_tColor1");
                if (!string.IsNullOrEmpty(colorTexPath))
                {
                    colorTex = Source2TextureLoader.GetOrLoadTexture(package, colorTexPath, forceOpaque: false, addonPackage: addonPackage);
                }
            }

            mat.SetShaderParameter("u_slime_base_color", slimeBase);
            mat.SetShaderParameter("u_slime_opacity", opacity);
            mat.SetShaderParameter("u_rim_glow_color", rimGlow);
            mat.SetShaderParameter("roughness", 0.08f);
            mat.SetShaderParameter("specular", 0.6f);
            mat.SetShaderParameter("rim_intensity", 1.0f);

            // Legacy uniform aliases
            mat.SetShaderParameter("slime_color", slimeBase);
            mat.SetShaderParameter("rim_glow_color", rimGlow);

            if (colorTex != null)
            {
                mat.SetShaderParameter("g_tColor", colorTex);
                mat.SetShaderParameter("has_texture", true);
            }
            else
            {
                mat.SetShaderParameter("has_texture", false);
            }

            mat.SetMeta("PreserveShading", true);
            return mat;
        }

        return null;
    }

    public ShaderMaterial GetSignatureMaterial(string meshName, int surfaceIndex, string vmatPath, StandardMaterial3D baseMat)
    {
        string meshLower = meshName.ToLowerInvariant();
        string vmatLower = vmatPath.ToLowerInvariant();

        // Guns, details, boots, outline and glass do NOT get the slime shader
        if (meshLower.Contains("gun") || vmatLower.Contains("black") ||
            meshLower.Contains("bodyoutline") || vmatLower.Contains("viscous_outline") ||
            vmatLower.Contains("viscous_glass") || vmatLower.Contains("viscous_swatches"))
        {
            return null;
        }

        // Apply slime shader ONLY to slime body parts:
        // inflated[2] (viscous_ball) and body[1] (viscous_body)
        if (vmatLower.Contains("viscous_ball") || vmatLower.Contains("viscous_body"))
        {
            if (_slimeShader == null) return null;

            var mat = new ShaderMaterial { Shader = _slimeShader };
            Color slimeBase = baseMat?.AlbedoColor ?? new Color(0.37f, 0.75f, 0.39f, 1.0f);
            Color rimGlow = new Color(0.40f, 1.0f, 0.55f, 1.0f);

            mat.SetShaderParameter("u_slime_base_color", slimeBase);
            mat.SetShaderParameter("u_slime_opacity", 0.55f);
            mat.SetShaderParameter("u_rim_glow_color", rimGlow);
            mat.SetShaderParameter("roughness", 0.08f);
            mat.SetShaderParameter("specular", 0.6f);
            mat.SetShaderParameter("rim_intensity", 1.0f);

            // Legacy aliases
            mat.SetShaderParameter("slime_color", slimeBase);
            mat.SetShaderParameter("rim_glow_color", rimGlow);

            if (baseMat?.AlbedoTexture != null)
            {
                mat.SetShaderParameter("g_tColor", baseMat.AlbedoTexture);
                mat.SetShaderParameter("has_texture", true);
            }
            else
            {
                mat.SetShaderParameter("has_texture", false);
            }

            mat.RenderPriority = 1;
            mat.SetMeta("PreserveShading", true);
            return mat;
        }

        return null;
    }

    public Control BuildUI(Action<string, Variant> onParameterChanged)
    {
        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 8);

        // Slime Color
        var rowColor = new HBoxContainer();
        var lblColor = new Label { Text = "Slime Base Color:", CustomMinimumSize = new Vector2(130, 0) };
        var cpColor = new ColorPickerButton { Color = new Color(0.12f, 0.72f, 0.32f, 0.70f), CustomMinimumSize = new Vector2(60, 26) };
        rowColor.AddChild(lblColor);
        rowColor.AddChild(cpColor);
        vbox.AddChild(rowColor);

        // Slime Opacity
        var rowOp = new HBoxContainer();
        var lblOp = new Label { Text = "Slime Opacity:", CustomMinimumSize = new Vector2(130, 0) };
        var sliderOp = new HSlider { MinValue = 0.2, MaxValue = 1.0, Step = 0.05, Value = 0.70, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        var valOp = new Label { Text = "0.70", CustomMinimumSize = new Vector2(45, 0) };
        rowOp.AddChild(lblOp);
        rowOp.AddChild(sliderOp);
        rowOp.AddChild(valOp);
        vbox.AddChild(rowOp);

        // Roughness
        var rowRough = new HBoxContainer();
        var lblRough = new Label { Text = "Gloss / Roughness:", CustomMinimumSize = new Vector2(130, 0) };
        var sliderRough = new HSlider { MinValue = 0.0, MaxValue = 0.5, Step = 0.01, Value = 0.06, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        var valRough = new Label { Text = "0.06", CustomMinimumSize = new Vector2(45, 0) };
        rowRough.AddChild(lblRough);
        rowRough.AddChild(sliderRough);
        rowRough.AddChild(valRough);
        vbox.AddChild(rowRough);

        // Rim Glow Color
        var rowRimCol = new HBoxContainer();
        var lblRimCol = new Label { Text = "Rim Glow Color:", CustomMinimumSize = new Vector2(130, 0) };
        var cpRimCol = new ColorPickerButton { Color = new Color(0.40f, 1.0f, 0.55f, 1.0f), CustomMinimumSize = new Vector2(60, 26) };
        rowRimCol.AddChild(lblRimCol);
        rowRimCol.AddChild(cpRimCol);
        vbox.AddChild(rowRimCol);

        // Rim Glow Intensity
        var rowRimInt = new HBoxContainer();
        var lblRimInt = new Label { Text = "Rim Glow Intensity:", CustomMinimumSize = new Vector2(130, 0) };
        var sliderRimInt = new HSlider { MinValue = 0.0, MaxValue = 3.0, Step = 0.1, Value = 1.2, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        var valRimInt = new Label { Text = "1.20", CustomMinimumSize = new Vector2(45, 0) };
        rowRimInt.AddChild(lblRimInt);
        rowRimInt.AddChild(sliderRimInt);
        rowRimInt.AddChild(valRimInt);
        vbox.AddChild(rowRimInt);

        // Slime Outline Tint
        var rowOutCol = new HBoxContainer();
        var lblOutCol = new Label { Text = "Outline Tint:", CustomMinimumSize = new Vector2(130, 0) };
        var cpOutCol = new ColorPickerButton { Color = _dynamicOutlineColor ?? new Color(0.08f, 0.08f, 0.08f, 1.0f), CustomMinimumSize = new Vector2(60, 26) };
        rowOutCol.AddChild(lblOutCol);
        rowOutCol.AddChild(cpOutCol);
        vbox.AddChild(rowOutCol);

        // Wire events
        cpColor.ColorChanged += col =>
        {
            float op = (float)sliderOp.Value;
            onParameterChanged?.Invoke("u_slime_base_color", new Color(col.R, col.G, col.B, 1.0f));
            onParameterChanged?.Invoke("slime_color", new Color(col.R, col.G, col.B, op));
        };

        sliderOp.ValueChanged += v =>
        {
            valOp.Text = v.ToString("F2");
            Color col = cpColor.Color;
            onParameterChanged?.Invoke("u_slime_opacity", (float)v);
            onParameterChanged?.Invoke("slime_color", new Color(col.R, col.G, col.B, (float)v));
        };

        sliderRough.ValueChanged += v =>
        {
            valRough.Text = v.ToString("F2");
            onParameterChanged?.Invoke("roughness", (float)v);
        };

        cpRimCol.ColorChanged += col =>
        {
            onParameterChanged?.Invoke("u_rim_glow_color", col);
            onParameterChanged?.Invoke("rim_glow_color", col);
        };

        sliderRimInt.ValueChanged += v =>
        {
            valRimInt.Text = v.ToString("F2");
            onParameterChanged?.Invoke("rim_intensity", (float)v);
        };

        cpOutCol.ColorChanged += col =>
        {
            _dynamicOutlineColor = col;
            onParameterChanged?.Invoke("g_vSolidOutlineTint", col);
            onParameterChanged?.Invoke("outline_color", col);
        };

        return vbox;
    }
}
