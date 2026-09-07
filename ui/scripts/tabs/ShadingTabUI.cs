using Godot;
using System;
using System.Collections.Generic;

public partial class ShadingTabUI : VBoxContainer
{
    [ExportCategory("Overlay Shader Rects")]
    [Export] private ColorRect _crtRect;
    [Export] private ColorRect _glitchRect;

    [ExportCategory("Master Controls")]
    [Export] private CheckBox _masterToggle;
    [Export] private Button _btnResetDefaults;

    [ExportCategory("Toon Controls")]
    [Export] private CheckBox _checkToonEnable;
    [Export] private HSlider _sliderToonIntensity;
    [Export] private Label _lblToonIntensity;
    [Export] private HSlider _sliderToonSteps;
    [Export] private Label _lblToonSteps;
    [Export] private HSlider _sliderToonSmoothness;
    [Export] private Label _lblToonSmoothness;
    [Export] private CheckBox _checkToonOutline;
    [Export] private HSlider _sliderToonOutlineWidth;
    [Export] private Label _lblToonOutlineWidth;
    [Export] private ColorPickerButton _colorToonOutline;
    [Export] private HSlider _sliderToonShadowAmount;
    [Export] private Label _lblToonShadowAmount;
    [Export] private ColorPickerButton _colorToonShadow;

    [ExportCategory("CRT Controls")]
    [Export] private CheckBox _checkCrtEnable;
    [Export] private HSlider _sliderCrtScan1;
    [Export] private Label _lblCrtScan1;
    [Export] private HSlider _sliderCrtScan2;
    [Export] private Label _lblCrtScan2;
    [Export] private HSlider _sliderCrtScanReduction;
    [Export] private Label _lblCrtScanReduction;
    [Export] private HSlider _sliderCrtBlur;
    [Export] private Label _lblCrtBlur;
    [Export] private HSlider _sliderCrtDiffusion;
    [Export] private Label _lblCrtDiffusion;
    [Export] private HSlider _sliderCrtVignetteAlpha;
    [Export] private Label _lblCrtVignetteAlpha;
    [Export] private HSlider _sliderCrtVignetteRadius;
    [Export] private Label _lblCrtVignetteRadius;

    [ExportCategory("Glitch Controls")]
    [Export] private CheckBox _checkGlitchEnable;
    [Export] private HSlider _sliderGlitchIntensity;
    [Export] private Label _lblGlitchIntensity;
    [Export] private HSlider _sliderGlitchPixelSize;
    [Export] private Label _lblGlitchPixelSize;
    [Export] private HSlider _sliderGlitchSplit;
    [Export] private Label _lblGlitchSplit;
    [Export] private HSlider _sliderGlitchOpacity;
    [Export] private Label _lblGlitchOpacity;

    private bool _masterEnabled = true;
    private bool _toonEnabled = false;
    private bool _crtEnabled = false;
    private bool _glitchEnabled = false;
    private bool _isSyncing = false;

    // 3D Spatial Toon Shader and Mesh Material Management
    private Shader _toonShader;
    private Shader _toonOutlineShader;

    private class SurfaceRecord
    {
        public MeshInstance3D Mesh;
        public int SurfaceIndex;
        public Material OriginalMaterial;
        public ShaderMaterial ToonMaterial;
        public ShaderMaterial OutlineMaterial;
    }

    private readonly List<SurfaceRecord> _characterSurfaces = new();
    private Node3D _currentHero;

    public override void _Ready()
    {
        _toonShader = GD.Load<Shader>("res://assets/shaders/toon_shader.gdshader");
        _toonOutlineShader = GD.Load<Shader>("res://assets/shaders/toon_outline.gdshader");

        LinkRects();
        ConnectEvents();
        ResetToDefaults();

        // Connect to VPK loader if hero is already loaded or will load
        var loader = GetVpkLoader();
        if (loader != null)
        {
            loader.HeroLoaded += SetHero;
            loader.HeroUnloaded += ClearHero;
            if (loader.CurrentHeroNode != null)
            {
                SetHero(loader.CurrentHeroNode);
            }
        }
    }

    public override void _ExitTree()
    {
        var loader = GetVpkLoader();
        if (loader != null)
        {
            loader.HeroLoaded -= SetHero;
            loader.HeroUnloaded -= ClearHero;
        }
        ClearHero();
    }

    private VpkLoaderTest GetVpkLoader()
    {
        return GetNodeOrNull<VpkLoaderTest>("/root/Main/UIRoot/MainHUD/VBoxContainer/MainSplit/ViewportArea/SubViewportContainer/WorldViewport/VpkLoaderTest")
            ?? GetNodeOrNull<VpkLoaderTest>("/root/Main/VpkLoaderTest")
            ?? GetTree()?.Root?.FindChild("VpkLoaderTest", true, false) as VpkLoaderTest;
    }

    private void LinkRects()
    {
        _crtRect ??= GetNodeOrNull<ColorRect>("/root/Main/UIRoot/MainHUD/VBoxContainer/MainSplit/ViewportArea/ShaderOverlayStack/CRTRect")
                   ?? GetNodeOrNull<ColorRect>("/root/Main/ShaderOverlayStack/CRTRect")
                   ?? GetTree()?.Root?.FindChild("CRTRect", true, false) as ColorRect;

        _glitchRect ??= GetNodeOrNull<ColorRect>("/root/Main/UIRoot/MainHUD/VBoxContainer/MainSplit/ViewportArea/ShaderOverlayStack/GlitchRect")
                      ?? GetNodeOrNull<ColorRect>("/root/Main/ShaderOverlayStack/GlitchRect")
                      ?? GetTree()?.Root?.FindChild("GlitchRect", true, false) as ColorRect;
    }

    private void ConnectEvents()
    {
        // Master
        if (_masterToggle != null)
        {
            _masterToggle.Toggled += (pressed) =>
            {
                _masterEnabled = pressed;
                UpdateShaderVisibility();
            };
        }

        if (_btnResetDefaults != null)
        {
            _btnResetDefaults.Pressed += ResetToDefaults;
        }

        // Toon
        if (_checkToonEnable != null)
        {
            _checkToonEnable.Toggled += (pressed) =>
            {
                _toonEnabled = pressed;
                UpdateShaderVisibility();
            };
        }
        if (_sliderToonIntensity != null)
        {
            _sliderToonIntensity.ValueChanged += (v) =>
            {
                if (_lblToonIntensity != null) _lblToonIntensity.Text = $"{v:F2}";
                if (!_isSyncing) SetToonParam("rim_blend", (float)v);
            };
        }
        if (_sliderToonSteps != null)
        {
            _sliderToonSteps.ValueChanged += (v) =>
            {
                if (_lblToonSteps != null) _lblToonSteps.Text = $"{v:F0}";
                if (!_isSyncing) SetToonParam("steps", (float)v);
            };
        }
        if (_sliderToonSmoothness != null)
        {
            _sliderToonSmoothness.ValueChanged += (v) =>
            {
                if (_lblToonSmoothness != null) _lblToonSmoothness.Text = $"{v:F2}";
                if (!_isSyncing) SetToonParam("step_smoothness", (float)v);
            };
        }
        if (_checkToonOutline != null)
        {
            _checkToonOutline.Toggled += (pressed) =>
            {
                if (!_isSyncing) UpdateToonOutlines(pressed);
            };
        }
        if (_sliderToonOutlineWidth != null)
        {
            _sliderToonOutlineWidth.ValueChanged += (v) =>
            {
                if (_lblToonOutlineWidth != null) _lblToonOutlineWidth.Text = $"{v:F1}px";
                if (!_isSyncing) SetOutlineParam("outline_width", (float)v);
            };
        }
        if (_colorToonOutline != null)
        {
            _colorToonOutline.ColorChanged += (c) =>
            {
                if (!_isSyncing) SetOutlineParam("outline_color", c);
            };
        }
        if (_sliderToonShadowAmount != null)
        {
            _sliderToonShadowAmount.ValueChanged += (v) =>
            {
                if (_lblToonShadowAmount != null) _lblToonShadowAmount.Text = $"{v:F2}";
                if (!_isSyncing) SetToonParam("shadow_tint_amount", (float)v);
            };
        }
        if (_colorToonShadow != null)
        {
            _colorToonShadow.ColorChanged += (c) =>
            {
                if (!_isSyncing) SetToonParam("shadow_tint", c);
            };
        }

        // CRT
        if (_checkCrtEnable != null)
        {
            _checkCrtEnable.Toggled += (pressed) =>
            {
                _crtEnabled = pressed;
                UpdateShaderVisibility();
            };
        }
        if (_sliderCrtScan1 != null)
        {
            _sliderCrtScan1.ValueChanged += (v) =>
            {
                if (_lblCrtScan1 != null) _lblCrtScan1.Text = $"{v:F0}";
                if (!_isSyncing) SetShaderParam(_crtRect, "scanlines_1", (float)v);
            };
        }
        if (_sliderCrtScan2 != null)
        {
            _sliderCrtScan2.ValueChanged += (v) =>
            {
                if (_lblCrtScan2 != null) _lblCrtScan2.Text = $"{v:F0}";
                if (!_isSyncing) SetShaderParam(_crtRect, "scanlines_2", (float)v);
            };
        }
        if (_sliderCrtScanReduction != null)
        {
            _sliderCrtScanReduction.ValueChanged += (v) =>
            {
                if (_lblCrtScanReduction != null) _lblCrtScanReduction.Text = $"{v:F2}";
                if (!_isSyncing) SetShaderParam(_crtRect, "scan_reduction", (float)v);
            };
        }
        if (_sliderCrtBlur != null)
        {
            _sliderCrtBlur.ValueChanged += (v) =>
            {
                if (_lblCrtBlur != null) _lblCrtBlur.Text = $"{v:F2}";
                if (!_isSyncing) SetShaderParam(_crtRect, "blur", (float)v);
            };
        }
        if (_sliderCrtDiffusion != null)
        {
            _sliderCrtDiffusion.ValueChanged += (v) =>
            {
                if (_lblCrtDiffusion != null) _lblCrtDiffusion.Text = $"{v:F2}";
                if (!_isSyncing) SetShaderParam(_crtRect, "diffusion", (float)v);
            };
        }
        if (_sliderCrtVignetteAlpha != null)
        {
            _sliderCrtVignetteAlpha.ValueChanged += (v) =>
            {
                if (_lblCrtVignetteAlpha != null) _lblCrtVignetteAlpha.Text = $"{v:F2}";
                if (!_isSyncing) SetShaderParam(_crtRect, "vinnette_alpla", (float)v);
            };
        }
        if (_sliderCrtVignetteRadius != null)
        {
            _sliderCrtVignetteRadius.ValueChanged += (v) =>
            {
                if (_lblCrtVignetteRadius != null) _lblCrtVignetteRadius.Text = $"{v:F1}";
                if (!_isSyncing) SetShaderParam(_crtRect, "vinnette_inner_radius", (float)v);
            };
        }

        // Glitch
        if (_checkGlitchEnable != null)
        {
            _checkGlitchEnable.Toggled += (pressed) =>
            {
                _glitchEnabled = pressed;
                UpdateShaderVisibility();
            };
        }
        if (_sliderGlitchIntensity != null)
        {
            _sliderGlitchIntensity.ValueChanged += (v) =>
            {
                if (_lblGlitchIntensity != null) _lblGlitchIntensity.Text = $"{v:F1}";
                if (!_isSyncing) SetShaderParam(_glitchRect, "vhs_intensity", (float)v);
            };
        }
        if (_sliderGlitchPixelSize != null)
        {
            _sliderGlitchPixelSize.ValueChanged += (v) =>
            {
                if (_lblGlitchPixelSize != null) _lblGlitchPixelSize.Text = $"{v:F1}px";
                if (!_isSyncing) SetShaderParam(_glitchRect, "pixel_size", (float)v);
            };
        }
        if (_sliderGlitchSplit != null)
        {
            _sliderGlitchSplit.ValueChanged += (v) =>
            {
                if (_lblGlitchSplit != null) _lblGlitchSplit.Text = $"{v:F3}";
                if (!_isSyncing) SetShaderParam(_glitchRect, "double_vision_split", (float)v);
            };
        }
        if (_sliderGlitchOpacity != null)
        {
            _sliderGlitchOpacity.ValueChanged += (v) =>
            {
                if (_lblGlitchOpacity != null) _lblGlitchOpacity.Text = $"{v:F2}";
                if (!_isSyncing) SetShaderParam(_glitchRect, "opacity", (float)v);
            };
        }
    }

    #region Hero Character Mesh Tracking & Spatial Toon Application

    public void SetHero(Node3D heroNode)
    {
        ClearHero();
        _currentHero = heroNode;
        if (_currentHero == null) return;

        CollectMeshesRecursively(_currentHero);
        UpdateToonMaterialsUniforms();
        ApplyToonState();
    }

    public void ClearHero()
    {
        // Restore original materials before clearing
        foreach (var record in _characterSurfaces)
        {
            if (GodotObject.IsInstanceValid(record.Mesh))
            {
                record.Mesh.SetSurfaceOverrideMaterial(record.SurfaceIndex, record.OriginalMaterial);
            }
        }
        _characterSurfaces.Clear();
        _currentHero = null;
    }

    private void CollectMeshesRecursively(Node node)
    {
        if (node == null) return;

        // Skip gizmo managers, helpers or UI markers
        if (node.Name.ToString().Contains("Gizmo") || node.Name.ToString().Contains("Marker"))
            return;

        if (node is MeshInstance3D mi && mi.Mesh != null)
        {
            int surfaceCount = mi.Mesh.GetSurfaceCount();
            for (int i = 0; i < surfaceCount; i++)
            {
                Material origMat = mi.GetSurfaceOverrideMaterial(i) ?? mi.Mesh.SurfaceGetMaterial(i);

                var toonMat = new ShaderMaterial { Shader = _toonShader };
                var outlineMat = new ShaderMaterial { Shader = _toonOutlineShader };

                // Transfer diffuse and normal maps from original StandardMaterial3D
                if (origMat is StandardMaterial3D stdMat)
                {
                    if (stdMat.AlbedoTexture != null)
                        toonMat.SetShaderParameter("albedo_texture", stdMat.AlbedoTexture);
                    toonMat.SetShaderParameter("albedo_color", stdMat.AlbedoColor);

                    if (stdMat.NormalTexture != null)
                    {
                        toonMat.SetShaderParameter("normal_texture", stdMat.NormalTexture);
                        toonMat.SetShaderParameter("normal_strength", stdMat.NormalEnabled ? 1.0f : 0.0f);
                    }
                }

                _characterSurfaces.Add(new SurfaceRecord
                {
                    Mesh = mi,
                    SurfaceIndex = i,
                    OriginalMaterial = origMat,
                    ToonMaterial = toonMat,
                    OutlineMaterial = outlineMat
                });
            }
        }

        foreach (Node child in node.GetChildren())
        {
            CollectMeshesRecursively(child);
        }
    }

    private void ApplyToonState()
    {
        bool enableToon = _masterEnabled && _toonEnabled;
        foreach (var record in _characterSurfaces)
        {
            if (!GodotObject.IsInstanceValid(record.Mesh)) continue;

            if (enableToon)
            {
                record.Mesh.SetSurfaceOverrideMaterial(record.SurfaceIndex, record.ToonMaterial);
            }
            else
            {
                record.Mesh.SetSurfaceOverrideMaterial(record.SurfaceIndex, record.OriginalMaterial);
            }
        }
    }

    private void SetToonParam(string paramName, Variant value)
    {
        foreach (var record in _characterSurfaces)
        {
            record.ToonMaterial?.SetShaderParameter(paramName, value);
        }
    }

    private void SetOutlineParam(string paramName, Variant value)
    {
        foreach (var record in _characterSurfaces)
        {
            record.OutlineMaterial?.SetShaderParameter(paramName, value);
        }
    }

    private void UpdateToonOutlines(bool enableOutline)
    {
        foreach (var record in _characterSurfaces)
        {
            if (record.ToonMaterial != null)
            {
                record.ToonMaterial.NextPass = enableOutline ? record.OutlineMaterial : null;
            }
        }
    }

    private void UpdateToonMaterialsUniforms()
    {
        float steps = _sliderToonSteps != null ? (float)_sliderToonSteps.Value : 3.0f;
        float smooth = _sliderToonSmoothness != null ? (float)_sliderToonSmoothness.Value : 0.3f;
        float shadowAmount = _sliderToonShadowAmount != null ? (float)_sliderToonShadowAmount.Value : 0.4f;
        Color shadowColor = _colorToonShadow != null ? _colorToonShadow.Color : new Color(0.2f, 0.2f, 0.3f, 1.0f);
        float rimBlend = _sliderToonIntensity != null ? (float)_sliderToonIntensity.Value : 0.6f;
        bool outline = _checkToonOutline != null && _checkToonOutline.ButtonPressed;
        float outlineWidth = _sliderToonOutlineWidth != null ? (float)_sliderToonOutlineWidth.Value : 1.0f;
        Color outlineColor = _colorToonOutline != null ? _colorToonOutline.Color : new Color(0, 0, 0, 1);

        foreach (var record in _characterSurfaces)
        {
            if (record.ToonMaterial != null)
            {
                record.ToonMaterial.SetShaderParameter("use_stepped", true);
                record.ToonMaterial.SetShaderParameter("steps", steps);
                record.ToonMaterial.SetShaderParameter("step_smoothness", smooth);
                record.ToonMaterial.SetShaderParameter("shadow_tint", shadowColor);
                record.ToonMaterial.SetShaderParameter("shadow_tint_amount", shadowAmount);
                record.ToonMaterial.SetShaderParameter("use_rim", true);
                record.ToonMaterial.SetShaderParameter("rim_color", new Color(1.0f, 1.0f, 1.0f, 1.0f));
                record.ToonMaterial.SetShaderParameter("rim_amount", 2.0f);
                record.ToonMaterial.SetShaderParameter("rim_smoothness", 0.2f);
                record.ToonMaterial.SetShaderParameter("rim_blend", rimBlend);
                record.ToonMaterial.NextPass = outline ? record.OutlineMaterial : null;
            }

            if (record.OutlineMaterial != null)
            {
                record.OutlineMaterial.SetShaderParameter("outline_width", outlineWidth);
                record.OutlineMaterial.SetShaderParameter("outline_color", outlineColor);
            }
        }
    }

    #endregion

    public void UpdateShaderVisibility()
    {
        LinkRects();
        ApplyToonState();

        if (_crtRect != null) _crtRect.Visible = _masterEnabled && _crtEnabled;
        if (_glitchRect != null) _glitchRect.Visible = _masterEnabled && _glitchEnabled;
    }

    public void ResetToDefaults()
    {
        _isSyncing = true;

        _masterEnabled = true;
        if (_masterToggle != null) _masterToggle.ButtonPressed = true;

        // Toon Defaults
        _toonEnabled = false;
        if (_checkToonEnable != null) _checkToonEnable.ButtonPressed = false;
        if (_sliderToonIntensity != null) _sliderToonIntensity.Value = 0.6f;
        if (_lblToonIntensity != null) _lblToonIntensity.Text = "0.60";
        if (_sliderToonSteps != null) _sliderToonSteps.Value = 3.0f;
        if (_lblToonSteps != null) _lblToonSteps.Text = "3";
        if (_sliderToonSmoothness != null) _sliderToonSmoothness.Value = 0.3f;
        if (_lblToonSmoothness != null) _lblToonSmoothness.Text = "0.30";
        if (_checkToonOutline != null) _checkToonOutline.ButtonPressed = true;
        if (_sliderToonOutlineWidth != null) _sliderToonOutlineWidth.Value = 1.0f;
        if (_lblToonOutlineWidth != null) _lblToonOutlineWidth.Text = "1.0px";
        if (_colorToonOutline != null) _colorToonOutline.Color = new Color(0, 0, 0, 1);
        if (_sliderToonShadowAmount != null) _sliderToonShadowAmount.Value = 0.4f;
        if (_lblToonShadowAmount != null) _lblToonShadowAmount.Text = "0.40";
        if (_colorToonShadow != null) _colorToonShadow.Color = new Color(0.2f, 0.2f, 0.3f, 1);

        UpdateToonMaterialsUniforms();

        // CRT Defaults
        _crtEnabled = false;
        if (_checkCrtEnable != null) _checkCrtEnable.ButtonPressed = false;
        if (_sliderCrtScan1 != null) _sliderCrtScan1.Value = 500.0f;
        if (_lblCrtScan1 != null) _lblCrtScan1.Text = "500";
        if (_sliderCrtScan2 != null) _sliderCrtScan2.Value = 25.0f;
        if (_lblCrtScan2 != null) _lblCrtScan2.Text = "25";
        if (_sliderCrtScanReduction != null) _sliderCrtScanReduction.Value = 0.1f;
        if (_lblCrtScanReduction != null) _lblCrtScanReduction.Text = "0.10";
        if (_sliderCrtBlur != null) _sliderCrtBlur.Value = 0.35f;
        if (_lblCrtBlur != null) _lblCrtBlur.Text = "0.35";
        if (_sliderCrtDiffusion != null) _sliderCrtDiffusion.Value = 0.1f;
        if (_lblCrtDiffusion != null) _lblCrtDiffusion.Text = "0.10";
        if (_sliderCrtVignetteAlpha != null) _sliderCrtVignetteAlpha.Value = 0.8f;
        if (_lblCrtVignetteAlpha != null) _lblCrtVignetteAlpha.Text = "0.80";
        if (_sliderCrtVignetteRadius != null) _sliderCrtVignetteRadius.Value = 3.5f;
        if (_lblCrtVignetteRadius != null) _lblCrtVignetteRadius.Text = "3.5";

        SetShaderParam(_crtRect, "scanlines_1", 500.0f);
        SetShaderParam(_crtRect, "scanlines_2", 25.0f);
        SetShaderParam(_crtRect, "scan_reduction", 0.1f);
        SetShaderParam(_crtRect, "blur", 0.35f);
        SetShaderParam(_crtRect, "diffusion", 0.1f);
        SetShaderParam(_crtRect, "vinnette_alpla", 0.8f);
        SetShaderParam(_crtRect, "vinnette_inner_radius", 3.5f);

        // Glitch Defaults
        _glitchEnabled = false;
        if (_checkGlitchEnable != null) _checkGlitchEnable.ButtonPressed = false;
        if (_sliderGlitchIntensity != null) _sliderGlitchIntensity.Value = 3.0f;
        if (_lblGlitchIntensity != null) _lblGlitchIntensity.Text = "3.0";
        if (_sliderGlitchPixelSize != null) _sliderGlitchPixelSize.Value = 3.0f;
        if (_lblGlitchPixelSize != null) _lblGlitchPixelSize.Text = "3.0px";
        if (_sliderGlitchSplit != null) _sliderGlitchSplit.Value = 0.008f;
        if (_lblGlitchSplit != null) _lblGlitchSplit.Text = "0.008";
        if (_sliderGlitchOpacity != null) _sliderGlitchOpacity.Value = 0.5f;
        if (_lblGlitchOpacity != null) _lblGlitchOpacity.Text = "0.50";

        SetShaderParam(_glitchRect, "vhs_intensity", 3.0f);
        SetShaderParam(_glitchRect, "pixel_size", 3.0f);
        SetShaderParam(_glitchRect, "double_vision_split", 0.008f);
        SetShaderParam(_glitchRect, "opacity", 0.5f);

        _isSyncing = false;
        UpdateShaderVisibility();
    }

    private void SetShaderParam(ColorRect rect, string paramName, Variant value)
    {
        if (rect == null) return;
        if (rect.Material is ShaderMaterial mat)
        {
            mat.SetShaderParameter(paramName, value);
        }
    }
}
