using Godot;
using System;
using System.Collections.Generic;
using DeadlockPlayground.Materials;

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

    [ExportCategory("Hero Signature Effect")]
    [Export] private CheckBox _checkHeroSignature;
    [Export] private Label _lblHeroSignature;
    [Export] private VBoxContainer _dynamicHeroControls;

    private bool _masterEnabled = true;
    private bool _toonEnabled = false;
    private bool _crtEnabled = false;
    private bool _glitchEnabled = false;
    private bool _heroSignatureEnabled = true;
    private bool _isSyncing = false;
    private string _currentHeroName = string.Empty;

    // 3D Spatial Shaders
    private Shader _toonShader;
    private Shader _toonOutlineShader;

    private class SurfaceRecord
    {
        public MeshInstance3D Mesh;
        public int SurfaceIndex;
        public Material OriginalMaterial;
        public string MaterialPath = string.Empty;
        public bool IsWeapon = false;
        public bool IsBuiltinOutline = false;
        public bool IsAdditive = false;
        public bool IsTranslucent = false;

        public ShaderMaterial ToonMaterial;
        public ShaderMaterial OutlineMaterial;
        public ShaderMaterial SignatureMaterial;
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
    }

    private VpkLoaderTest GetVpkLoader()
    {
        return GetNodeOrNull<VpkLoaderTest>("/root/Main/UIRoot/MainHUD/VBoxContainer/MainSplit/ViewportArea/SubViewportContainer/WorldViewport/VpkLoaderTest")
            ?? GetNodeOrNull<VpkLoaderTest>("/root/Main/VpkLoaderTest")
            ?? GetTree().Root.FindChild("VpkLoaderTest", true, false) as VpkLoaderTest;
    }

    private void LinkRects()
    {
        if (_crtRect == null)
            _crtRect = GetNodeOrNull<ColorRect>("/root/Main/UIRoot/MainHUD/VBoxContainer/MainSplit/ViewportArea/OverlayShaders/CRT")
                ?? GetTree().Root.FindChild("CRT", true, false) as ColorRect;

        if (_glitchRect == null)
            _glitchRect = GetNodeOrNull<ColorRect>("/root/Main/UIRoot/MainHUD/VBoxContainer/MainSplit/ViewportArea/OverlayShaders/Glitch")
                ?? GetTree().Root.FindChild("Glitch", true, false) as ColorRect;
    }

    private void ConnectEvents()
    {
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

        // Toon Events
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
                if (!_isSyncing) UpdateToonMaterialsUniforms();
            };
        }
        if (_sliderToonSteps != null)
        {
            _sliderToonSteps.ValueChanged += (v) =>
            {
                if (_lblToonSteps != null) _lblToonSteps.Text = $"{(int)v}";
                if (!_isSyncing) UpdateToonMaterialsUniforms();
            };
        }
        if (_sliderToonSmoothness != null)
        {
            _sliderToonSmoothness.ValueChanged += (v) =>
            {
                if (_lblToonSmoothness != null) _lblToonSmoothness.Text = $"{v:F2}";
                if (!_isSyncing) UpdateToonMaterialsUniforms();
            };
        }
        if (_checkToonOutline != null)
        {
            _checkToonOutline.Toggled += (pressed) =>
            {
                if (!_isSyncing) ApplyToonState();
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
                if (!_isSyncing) UpdateToonMaterialsUniforms();
            };
        }
        if (_colorToonShadow != null)
        {
            _colorToonShadow.ColorChanged += (c) =>
            {
                if (!_isSyncing) UpdateToonMaterialsUniforms();
            };
        }

        // CRT Events
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

        // Glitch Events
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

        // Hero Signature Events
        if (_checkHeroSignature != null)
        {
            _checkHeroSignature.Toggled += (pressed) =>
            {
                _heroSignatureEnabled = pressed;
                UpdateShaderVisibility();
            };
        }
    }

    #region Hero Character Mesh Tracking & Spatial Toon Application

    public void SetHero(Node3D heroNode)
    {
        ClearHero();
        _currentHero = heroNode;
        if (_currentHero == null) return;

        string heroName = heroNode.Name.ToString().ToLowerInvariant();
        var vpk = GetVpkLoader();
        if (vpk != null && !string.IsNullOrEmpty(vpk.HeroName)) heroName += " " + vpk.HeroName.ToLowerInvariant();
        _currentHeroName = heroName;

        // Clear dynamic controls container
        if (_dynamicHeroControls != null)
        {
            foreach (Node child in _dynamicHeroControls.GetChildren())
            {
                child.QueueFree();
            }
        }

        // Build dynamic UI for hero config
        var heroConfig = HeroMaterialManager.GetConfigForHero(_currentHeroName);
        if (heroConfig != null)
        {
            if (_lblHeroSignature != null) _lblHeroSignature.Text = heroConfig.DisplayName;
            if (_checkHeroSignature != null)
            {
                _checkHeroSignature.Visible = true;
                _checkHeroSignature.ButtonPressed = true;
                _checkHeroSignature.Text = "Enable Signature Shader";
            }

            var dynamicUI = heroConfig.BuildUI((paramName, value) => SetSignatureParam(paramName, value));
            if (dynamicUI != null && _dynamicHeroControls != null)
            {
                _dynamicHeroControls.AddChild(dynamicUI);
            }
        }
        else
        {
            if (_lblHeroSignature != null) _lblHeroSignature.Text = "Standard PBR / Toon shading";
            if (_checkHeroSignature != null)
            {
                _checkHeroSignature.Visible = false;
                _checkHeroSignature.ButtonPressed = false;
            }
        }

        CollectMeshesRecursively(_currentHero);
        UpdateToonMaterialsUniforms();
        ApplyToonState();
    }

    public void ClearHero()
    {
        foreach (var record in _characterSurfaces)
        {
            if (GodotObject.IsInstanceValid(record.Mesh))
            {
                record.Mesh.SetSurfaceOverrideMaterial(record.SurfaceIndex, record.OriginalMaterial);
            }
        }
        _characterSurfaces.Clear();
        _currentHero = null;
        _currentHeroName = string.Empty;

        if (_dynamicHeroControls != null)
        {
            foreach (Node child in _dynamicHeroControls.GetChildren())
            {
                child.QueueFree();
            }
        }
    }

    private void CollectMeshesRecursively(Node node)
    {
        if (node == null) return;

        // Skip gizmo managers, helpers or UI markers
        if (node.Name.ToString().Contains("Gizmo") || node.Name.ToString().Contains("Marker"))
            return;

        if (node is MeshInstance3D mi && mi.Mesh != null)
        {
            string meshNameLower = mi.Name.ToString().ToLowerInvariant();
            bool isWeapon = meshNameLower.Contains("gun") || meshNameLower.Contains("weapon") ||
                            meshNameLower.Contains("sword") || meshNameLower.Contains("katana") ||
                            meshNameLower.Contains("bow") || meshNameLower.Contains("shortsword") ||
                            meshNameLower.Contains("longhilt") || meshNameLower.Contains("beltscabbard");
            bool isBuiltinOutline = meshNameLower.Contains("bodyoutline") || meshNameLower.Contains("outline");

            int surfaceCount = mi.Mesh.GetSurfaceCount();
            for (int i = 0; i < surfaceCount; i++)
            {
                Material origMat = mi.GetSurfaceOverrideMaterial(i) ?? mi.Mesh.SurfaceGetMaterial(i);
                string matPath = origMat?.ResourceName?.ToLowerInvariant() ?? "";

                if (matPath.Contains("outline")) isBuiltinOutline = true;

                bool isGlass = matPath.Contains("glass") || matPath.Contains("lens") || matPath.Contains("specs") || matPath.Contains("spectacle");
                bool isAdditive = false;
                bool isTranslucent = isGlass;
                if (origMat is StandardMaterial3D sm)
                {
                    isAdditive = sm.BlendMode == BaseMaterial3D.BlendModeEnum.Add;
                    isTranslucent = isTranslucent || sm.Transparency == BaseMaterial3D.TransparencyEnum.Alpha;
                }
                else if (origMat is ShaderMaterial)
                {
                    // Procedural dynamic glow or energy layer or glass: treat as VFX
                    isAdditive = true;
                }

                // 1. Signature material from HeroMaterialManager
                ShaderMaterial sigMat = HeroMaterialManager.GetSignatureMaterial(_currentHeroName, meshNameLower, i, matPath, origMat as StandardMaterial3D);

                // 2. Toon material
                var toonMat = new ShaderMaterial { Shader = _toonShader };
                var outlineMat = new ShaderMaterial { Shader = _toonOutlineShader };
                outlineMat.SetShaderParameter("outline_width", _sliderToonOutlineWidth != null ? (float)_sliderToonOutlineWidth.Value : 1.0f);
                outlineMat.SetShaderParameter("outline_color", _colorToonOutline != null ? _colorToonOutline.Color : new Color(0.08f, 0.08f, 0.08f, 1.0f));

                // Transfer properties from original StandardMaterial3D
                if (origMat is StandardMaterial3D stdMat)
                {
                    toonMat.SetShaderParameter("albedo_color", stdMat.AlbedoColor);

                    if (stdMat.AlbedoTexture != null)
                    {
                        toonMat.SetShaderParameter("albedo_texture", stdMat.AlbedoTexture);
                    }

                    if (stdMat.NormalTexture != null)
                    {
                        toonMat.SetShaderParameter("normal_texture", stdMat.NormalTexture);
                        toonMat.SetShaderParameter("normal_strength", stdMat.NormalEnabled ? 1.0f : 0.0f);
                    }

                    toonMat.SetShaderParameter("metallic", stdMat.Metallic);
                    float spec = stdMat.Metallic > 0.5f ? 0.45f : 0.30f;
                    toonMat.SetShaderParameter("specular", spec);
                    toonMat.SetShaderParameter("uv1_scale", stdMat.Uv1Scale);
                    toonMat.SetShaderParameter("uv1_offset", stdMat.Uv1Offset);

                    if (stdMat.Transparency == BaseMaterial3D.TransparencyEnum.AlphaScissor)
                    {
                        toonMat.SetShaderParameter("alpha_scissor_threshold", stdMat.AlphaScissorThreshold > 0.01f ? stdMat.AlphaScissorThreshold : 0.5f);
                    }

                    if (stdMat.EmissionEnabled)
                    {
                        toonMat.SetShaderParameter("emission_color", stdMat.Emission);
                        toonMat.SetShaderParameter("emission_energy", stdMat.EmissionEnergyMultiplier);
                        if (stdMat.EmissionTexture != null)
                        {
                            toonMat.SetShaderParameter("emission_texture", stdMat.EmissionTexture);
                        }
                    }
                }

                _characterSurfaces.Add(new SurfaceRecord
                {
                    Mesh = mi,
                    SurfaceIndex = i,
                    OriginalMaterial = origMat,
                    MaterialPath = matPath,
                    IsWeapon = isWeapon,
                    IsBuiltinOutline = isBuiltinOutline,
                    IsAdditive = isAdditive,
                    IsTranslucent = isTranslucent,
                    ToonMaterial = toonMat,
                    OutlineMaterial = outlineMat,
                    SignatureMaterial = sigMat
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
        bool master = _masterEnabled;
        bool enableToon = master && _toonEnabled;
        bool enableSignature = master && _heroSignatureEnabled;
        bool enableOutline = enableToon && _checkToonOutline != null && _checkToonOutline.ButtonPressed;

        foreach (var record in _characterSurfaces)
        {
            if (!GodotObject.IsInstanceValid(record.Mesh)) continue;

            // Built-in outline meshes (like Viscous's bodyoutline) maintain their original material
            if (record.IsBuiltinOutline ||
                record.MaterialPath.Contains("vertcolor_pbr_basic", StringComparison.OrdinalIgnoreCase) ||
                record.MaterialPath.Contains("materials/dev/", StringComparison.OrdinalIgnoreCase))
            {
                record.Mesh.SetSurfaceOverrideMaterial(record.SurfaceIndex, record.OriginalMaterial);
                continue;
            }

            // Lash sparkles and Wraith playing cards: strictly preserve original material, never overwrite with Toon or NextPass
            if (record.MaterialPath.Contains("lash_sparkles", StringComparison.OrdinalIgnoreCase) ||
                record.MaterialPath.Contains("wraith_cards", StringComparison.OrdinalIgnoreCase) ||
                record.MaterialPath.Contains("wraith_hat_card", StringComparison.OrdinalIgnoreCase) ||
                record.MaterialPath.Contains("handcards", StringComparison.OrdinalIgnoreCase) ||
                record.MaterialPath.Contains("card", StringComparison.OrdinalIgnoreCase) ||
                record.Mesh.Name.ToString().Contains("sparkle", StringComparison.OrdinalIgnoreCase) ||
                record.Mesh.Name.ToString().Contains("card", StringComparison.OrdinalIgnoreCase) ||
                record.OriginalMaterial?.ResourceName?.Contains("card", StringComparison.OrdinalIgnoreCase) == true ||
                (record.OriginalMaterial is ShaderMaterial smSpecial && (
                    smSpecial.Shader?.ResourcePath?.Contains("lash_sparkles") == true ||
                    smSpecial.Shader?.ResourcePath?.Contains("source2_cards") == true ||
                    smSpecial.Shader?.ResourcePath?.Contains("wraith_card") == true)))
            {
                record.Mesh.SetSurfaceOverrideMaterial(record.SurfaceIndex, record.OriginalMaterial);
                continue;
            }

            Material baseMat = record.OriginalMaterial;

            if (enableSignature && record.SignatureMaterial != null)
            {
                baseMat = record.SignatureMaterial;
            }
            else if (enableToon)
            {
                // Preserve additive VFX, dynamic glow shaders, and translucent glass surfaces.
                // EXCEPTION: source2_pbr.gdshader is a structural NPR head shader — preserve it
                // as-is (it already has NPR wrapped N·L) but do NOT swap it for toon_shader.
                bool isPbrHead = record.OriginalMaterial is ShaderMaterial smPbr &&
                                 smPbr.Shader?.ResourcePath?.Contains("source2_pbr") == true;
                if (record.IsAdditive || record.IsTranslucent || (record.OriginalMaterial is ShaderMaterial && !isPbrHead))
                {
                    baseMat = record.OriginalMaterial;
                }
                else
                {
                    baseMat = isPbrHead ? record.OriginalMaterial : record.ToonMaterial;
                }
            }
            else
            {
                baseMat = record.OriginalMaterial;
            }

            // Outline NextPass for Toon: ONLY on structural body/clothing meshes using DeadlockMaterialResolver selective filter
            // Automatically excludes eyes, mouth interior, teeth, decals, glasses/lenses, fur layers, and particle/fire meshes.
            if (baseMat is ShaderMaterial shMat)
            {
                string meshCleanName = record.Mesh.Name.ToString().TrimStart('.', '_');

                // source2_pbr head shader: treat as structural (not additive) for outline eligibility.
                bool isNprHead = shMat.Shader?.ResourcePath?.Contains("source2_pbr") == true;
                bool isAdditiveForOutline = !isNprHead && (record.IsAdditive || record.OriginalMaterial is ShaderMaterial);

                bool isEligibleForOutline = DeadlockMaterialResolver.ShouldApplyOutline(
                    meshCleanName,
                    record.MaterialPath,
                    isAdditiveForOutline,
                    record.IsTranslucent
                );

                // Allow outline on: toon material, signature material, OR the NPR head shader itself
                bool canHaveOutline = baseMat == record.ToonMaterial
                                   || baseMat == record.SignatureMaterial
                                   || isNprHead;

                if (enableOutline && canHaveOutline && isEligibleForOutline)
                {
                    shMat.NextPass = record.OutlineMaterial;
                }
                else
                {
                    shMat.NextPass = null;
                }
            }

            record.Mesh.SetSurfaceOverrideMaterial(record.SurfaceIndex, baseMat);
        }
    }

    private void SetSignatureParam(string paramName, Variant value)
    {
        GD.Print($"[ShadingTabUI] SetSignatureParam called: {paramName} = {value}");
        foreach (var record in _characterSurfaces)
        {
            record.SignatureMaterial?.SetShaderParameter(paramName, value);

            var activeMat = record.Mesh?.GetSurfaceOverrideMaterial(record.SurfaceIndex) ?? record.OriginalMaterial;
            if (activeMat is ShaderMaterial shMat)
            {
                shMat.SetShaderParameter(paramName, value);

                if (paramName == "g_flSelfIllumScale" || paramName == "emission_scale")
                {
                    shMat.SetShaderParameter("g_flSelfIllumScale", value);
                    shMat.SetShaderParameter("emission_scale", value);
                }
                else if (paramName == "aura_color" || paramName == "arm_color" || paramName == "glow_color" || paramName == "g_vSelfIllumTint")
                {
                    shMat.SetShaderParameter("color_tint", value);
                    shMat.SetShaderParameter("self_illum_tint", value);
                    shMat.SetShaderParameter("glow_color", value);
                    shMat.SetShaderParameter("g_vSelfIllumTint", value);
                }
                else if (paramName == "emission_energy")
                {
                    shMat.SetShaderParameter("opacity_scale", (float)value / 2.4f);
                    shMat.SetShaderParameter("self_illum_scale", value);
                }
                else if (paramName == "noise_speed")
                {
                    shMat.SetShaderParameter("self_illum_scroll_speed", new Vector2(0.0f, -(float)value * 0.3f));
                    shMat.SetShaderParameter("albedo_scroll_speed", new Vector2(0.0f, -(float)value * 0.3f));
                    shMat.SetShaderParameter("scroll_speed", new Vector2(0.0f, -(float)value * 0.3f));
                }
            }
            else if (activeMat is StandardMaterial3D stdMat &&
                     (record.MaterialPath.Contains("card", StringComparison.OrdinalIgnoreCase) ||
                      record.Mesh?.Name.ToString().Contains("card", StringComparison.OrdinalIgnoreCase) == true ||
                      stdMat.ResourceName.Contains("card", StringComparison.OrdinalIgnoreCase)))
            {
                if (paramName == "g_vSelfIllumTint" || paramName == "glow_color")
                {
                    stdMat.Emission = (Color)value;
                }
                else if (paramName == "g_flSelfIllumScale" || paramName == "emission_scale")
                {
                    stdMat.EmissionEnergyMultiplier = (float)value * 0.6f;
                }
            }
        }
    }

    private void SetOutlineParam(string paramName, Variant value)
    {
        foreach (var record in _characterSurfaces)
        {
            record.OutlineMaterial?.SetShaderParameter(paramName, value);
        }
    }

    private void UpdateToonMaterialsUniforms()
    {
        float intensity = _sliderToonIntensity != null ? (float)_sliderToonIntensity.Value : 1.0f;
        float steps = _sliderToonSteps != null ? (float)_sliderToonSteps.Value : 3.0f;
        float smooth = _sliderToonSmoothness != null ? (float)_sliderToonSmoothness.Value : 0.30f;
        float shadowAmt = _sliderToonShadowAmount != null ? (float)_sliderToonShadowAmount.Value : 0.40f;
        Color shadowCol = _colorToonShadow != null ? _colorToonShadow.Color : new Color(0.18f, 0.16f, 0.26f, 1.0f);

        float outWidth = _sliderToonOutlineWidth != null ? (float)_sliderToonOutlineWidth.Value : 1.0f;
        Color outCol = _colorToonOutline != null ? _colorToonOutline.Color : new Color(0.08f, 0.08f, 0.08f, 1.0f);

        foreach (var record in _characterSurfaces)
        {
            if (record.ToonMaterial != null)
            {
                record.ToonMaterial.SetShaderParameter("toon_intensity", intensity);
                record.ToonMaterial.SetShaderParameter("use_stepped", true);
                record.ToonMaterial.SetShaderParameter("steps", steps);
                record.ToonMaterial.SetShaderParameter("step_smoothness", smooth);
                record.ToonMaterial.SetShaderParameter("shadow_tint", shadowCol);
                record.ToonMaterial.SetShaderParameter("shadow_tint_amount", shadowAmt);
                record.ToonMaterial.SetShaderParameter("specular", record.IsWeapon ? 0.45f : 0.30f);
                record.ToonMaterial.SetShaderParameter("use_rim", true);
                record.ToonMaterial.SetShaderParameter("rim_color", new Color(1.0f, 1.0f, 1.0f, 1.0f));
                record.ToonMaterial.SetShaderParameter("rim_amount", 2.0f);
                record.ToonMaterial.SetShaderParameter("rim_smoothness", 0.2f);
                record.ToonMaterial.SetShaderParameter("rim_blend", 1.0f);
                record.ToonMaterial.SetShaderParameter("rim_mask_shadow", 1.0f);
            }

            if (record.OutlineMaterial != null)
            {
                record.OutlineMaterial.SetShaderParameter("outline_width", outWidth);
                record.OutlineMaterial.SetShaderParameter("outline_color", outCol);
            }

            // ── NPR head shader sync ─────────────────────────────────────────
            // source2_pbr.gdshader is always-on (never swapped for toon_shader),
            // but it shares the same stepped-diffuse + shadow-tint + rim uniforms.
            // Push the slider values here so the head stays visually in sync.
            if (record.OriginalMaterial is ShaderMaterial smPbr &&
                smPbr.Shader?.ResourcePath?.Contains("source2_pbr") == true)
            {
                smPbr.SetShaderParameter("toon_intensity",    intensity);
                smPbr.SetShaderParameter("steps",             steps);
                smPbr.SetShaderParameter("step_smoothness",   smooth);
                smPbr.SetShaderParameter("shadow_tint",       shadowCol);
                smPbr.SetShaderParameter("shadow_tint_amount", shadowAmt);
                smPbr.SetShaderParameter("use_rim",           true);
                smPbr.SetShaderParameter("rim_color",         new Color(1.0f, 1.0f, 1.0f, 1.0f));
                smPbr.SetShaderParameter("rim_amount",        2.0f);
                smPbr.SetShaderParameter("rim_smoothness",    0.2f);
                smPbr.SetShaderParameter("rim_blend",         1.0f);
            }
        }
    }

    #endregion

    private void UpdateShaderVisibility()
    {
        LinkRects();

        if (_crtRect != null)
        {
            _crtRect.Visible = _masterEnabled && _crtEnabled;
        }

        if (_glitchRect != null)
        {
            _glitchRect.Visible = _masterEnabled && _glitchEnabled;
        }

        ApplyToonState();
    }

    public void ResetToDefaults()
    {
        _isSyncing = true;

        // Master
        _masterEnabled = true;
        if (_masterToggle != null) _masterToggle.ButtonPressed = true;

        // Toon Defaults
        _toonEnabled = false;
        if (_checkToonEnable != null) _checkToonEnable.ButtonPressed = false;
        if (_sliderToonIntensity != null) _sliderToonIntensity.Value = 1.0;
        if (_lblToonIntensity != null) _lblToonIntensity.Text = "1.00";
        if (_sliderToonSteps != null) _sliderToonSteps.Value = 3.0;
        if (_lblToonSteps != null) _lblToonSteps.Text = "3";
        if (_sliderToonSmoothness != null) _sliderToonSmoothness.Value = 0.30;
        if (_lblToonSmoothness != null) _lblToonSmoothness.Text = "0.30";
        if (_checkToonOutline != null) _checkToonOutline.ButtonPressed = true;
        if (_sliderToonOutlineWidth != null) _sliderToonOutlineWidth.Value = 1.0;
        if (_lblToonOutlineWidth != null) _lblToonOutlineWidth.Text = "1.0px";
        if (_colorToonOutline != null) _colorToonOutline.Color = new Color(0.08f, 0.08f, 0.08f, 1.0f);
        if (_sliderToonShadowAmount != null) _sliderToonShadowAmount.Value = 0.40;
        if (_lblToonShadowAmount != null) _lblToonShadowAmount.Text = "0.40";
        if (_colorToonShadow != null) _colorToonShadow.Color = new Color(0.18f, 0.16f, 0.26f, 1.0f);

        UpdateToonMaterialsUniforms();

        // CRT Defaults
        _crtEnabled = false;
        if (_checkCrtEnable != null) _checkCrtEnable.ButtonPressed = false;
        if (_sliderCrtScan1 != null) _sliderCrtScan1.Value = 500.0;
        if (_lblCrtScan1 != null) _lblCrtScan1.Text = "500";
        if (_sliderCrtScan2 != null) _sliderCrtScan2.Value = 25.0;
        if (_lblCrtScan2 != null) _lblCrtScan2.Text = "25";
        if (_sliderCrtScanReduction != null) _sliderCrtScanReduction.Value = 0.1;
        if (_lblCrtScanReduction != null) _lblCrtScanReduction.Text = "0.10";
        if (_sliderCrtBlur != null) _sliderCrtBlur.Value = 0.35;
        if (_lblCrtBlur != null) _lblCrtBlur.Text = "0.35";
        if (_sliderCrtDiffusion != null) _sliderCrtDiffusion.Value = 0.1;
        if (_lblCrtDiffusion != null) _lblCrtDiffusion.Text = "0.10";
        if (_sliderCrtVignetteAlpha != null) _sliderCrtVignetteAlpha.Value = 0.8;
        if (_lblCrtVignetteAlpha != null) _lblCrtVignetteAlpha.Text = "0.80";
        if (_sliderCrtVignetteRadius != null) _sliderCrtVignetteRadius.Value = 3.5;
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
        if (_sliderGlitchIntensity != null) _sliderGlitchIntensity.Value = 3.0;
        if (_lblGlitchIntensity != null) _lblGlitchIntensity.Text = "3.0";
        if (_sliderGlitchPixelSize != null) _sliderGlitchPixelSize.Value = 3.0;
        if (_lblGlitchPixelSize != null) _lblGlitchPixelSize.Text = "3.0px";
        if (_sliderGlitchSplit != null) _sliderGlitchSplit.Value = 0.008f;
        if (_lblGlitchSplit != null) _lblGlitchSplit.Text = "0.008";
        if (_sliderGlitchOpacity != null) _sliderGlitchOpacity.Value = 0.5f;
        if (_lblGlitchOpacity != null) _lblGlitchOpacity.Text = "0.50";

        SetShaderParam(_glitchRect, "vhs_intensity", 3.0f);
        SetShaderParam(_glitchRect, "pixel_size", 3.0f);
        SetShaderParam(_glitchRect, "double_vision_split", 0.008f);
        SetShaderParam(_glitchRect, "opacity", 0.5f);

        // Signature Effects Defaults
        _heroSignatureEnabled = true;
        if (_checkHeroSignature != null) _checkHeroSignature.ButtonPressed = true;

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
