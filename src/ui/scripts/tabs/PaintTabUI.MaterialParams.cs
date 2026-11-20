using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using DeadlockPlayground.Painter;
using DeadlockPlayground.Workspace;
using ValvePak;
using ValveResourceFormat;
using DeadlockPlayground.Materials;
using VrfMaterial = ValveResourceFormat.ResourceTypes.Material;

public partial class PaintTabUI
{
    private PanelContainer _matParamsCard;
    private Button _btnMatParamsToggle;
    private VBoxContainer _matParamsBody;
    private Label _lblMatParamsHint;
    private ColorPickerButton _cpMatColorTint;
    private HSlider _slMatVertColorStr;
    private Label _lblMatVertColorStr;
    private ColorPickerButton _cpMatSelfIllumTint;
    private HSlider _slMatSelfIllumScale;
    private Label _lblMatSelfIllumScale;
    private HSlider _slMatSelfIllumAlbedo;
    private Label _lblMatSelfIllumAlbedo;
    private HSlider _slMatContrast;
    private Label _lblMatContrast;
    private HSlider _slMatSaturation;
    private Label _lblMatSaturation;
    private HSlider _slMatBrightness;
    private Label _lblMatBrightness;
    private Button _btnMatParamsReset;

    private bool _matParamsExpanded = false;
    private bool _isSyncingMatParamsUI = false;
    private SubmeshNodeInfo _matParamsActiveSubmesh;

    private struct VanillaVmatDefaults
    {
        public Color ColorTint;
        public float VertexColorStrength;
        public bool HasVertexColor;
        public Color SelfIllumTint;
        public float SelfIllumScale;
        public float SelfIllumAlbedoFactor;
        public bool HasSelfIllum;
        public float Contrast;
        public float Saturation;
        public float Brightness;
    }

    private readonly Dictionary<string, VanillaVmatDefaults> _vanillaVmatDefaultsCache = new(StringComparer.OrdinalIgnoreCase);

    private void InitMaterialParamsPanel()
    {
        var submeshCard = GetNodeOrNull<Control>("SubmeshCard");
        var parentContainer = submeshCard?.GetParent();
        if (parentContainer == null) return;

        _matParamsCard = new PanelContainer
        {
            Name = "MaterialParamsCard",
            ThemeTypeVariation = "CardPanel",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 6);
        _matParamsCard.AddChild(vbox);

        _btnMatParamsToggle = new Button
        {
            Text = "▶  MATERIAL PARAMETERS (VMAT)",
            Alignment = HorizontalAlignment.Left,
            Flat = true,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            TooltipText = "Directly adjust Source 2 .vmat shader parameters (Color Tint, Vertex Color Strength, Self-Illum, CSB) with live 3D preview and .vmat_c export."
        };
        _btnMatParamsToggle.AddThemeFontSizeOverride("font_size", 11);
        _btnMatParamsToggle.Pressed += () =>
        {
            _matParamsExpanded = !_matParamsExpanded;
            UpdateMaterialParamsExpandState();
        };
        vbox.AddChild(_btnMatParamsToggle);

        _matParamsBody = new VBoxContainer { Visible = false };
        _matParamsBody.AddThemeConstantOverride("separation", 5);
        vbox.AddChild(_matParamsBody);

        _lblMatParamsHint = new Label
        {
            Text = "Select a material to adjust shader parameters.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        _lblMatParamsHint.AddThemeFontSizeOverride("font_size", 11);
        _lblMatParamsHint.AddThemeColorOverride("font_color", new Color(0.70f, 0.76f, 0.84f, 0.9f));
        _matParamsBody.AddChild(_lblMatParamsHint);

        // Helper for color row
        ColorPickerButton AddColorRow(string labelText, string tooltip)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 6);
            var lbl = new Label { Text = labelText, CustomMinimumSize = new Vector2(138, 0), TooltipText = tooltip };
            lbl.AddThemeFontSizeOverride("font_size", 11);
            var cp = new ColorPickerButton
            {
                Color = Colors.White,
                EditAlpha = false,
                CustomMinimumSize = new Vector2(64, 24),
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                TooltipText = tooltip
            };
            row.AddChild(lbl);
            row.AddChild(cp);
            _matParamsBody.AddChild(row);
            return cp;
        }

        // Helper for slider row
        (HSlider slider, Label valLbl) AddSliderRow(string labelText, double min, double max, double step, double defVal, string tooltip)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 6);
            var lbl = new Label { Text = labelText, CustomMinimumSize = new Vector2(138, 0), TooltipText = tooltip };
            lbl.AddThemeFontSizeOverride("font_size", 11);
            var sl = new HSlider
            {
                MinValue = min,
                MaxValue = max,
                Step = step,
                Value = defVal,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                TooltipText = tooltip
            };
            var vLbl = new Label { Text = defVal.ToString("F2"), CustomMinimumSize = new Vector2(36, 0), HorizontalAlignment = HorizontalAlignment.Right };
            vLbl.AddThemeFontSizeOverride("font_size", 11);
            row.AddChild(lbl);
            row.AddChild(sl);
            row.AddChild(vLbl);
            _matParamsBody.AddChild(row);
            return (sl, vLbl);
        }

        _cpMatColorTint = AddColorRow("Color Tint", "g_vColorTint1 — Direct RGB color multiplier on the material.");
        (_slMatVertColorStr, _lblMatVertColorStr) = AddSliderRow("Vertex Color Str", 0.0, 1.0, 0.01, 1.0, "g_fVertexColorStrength1 — Set to 0.0 to disable baked vertex colors (e.g. Vindicta's blue/cyan limbs) so your painted color controls 100% of the skin.");
        _cpMatSelfIllumTint = AddColorRow("Self-Illum Tint", "g_vSelfIllumTint1 — Emissive glow tint color (e.g. Vindicta's pupils / spectral limbs).");
        (_slMatSelfIllumScale, _lblMatSelfIllumScale) = AddSliderRow("Self-Illum Scale", 0.0, 10.0, 0.05, 1.0, "g_flSelfIllumScale1 — Emissive glow intensity. Set to 0.0 to disable self-illumination.");
        (_slMatSelfIllumAlbedo, _lblMatSelfIllumAlbedo) = AddSliderRow("SI Albedo Factor", 0.0, 1.0, 0.01, 1.0, "g_flSelfIllumAlbedoFactor1 — 1.0 makes self-illum follow your painted g_tColor; 0.0 ignores g_tColor.");
        (_slMatBrightness, _lblMatBrightness) = AddSliderRow("Brightness", 0.0, 2.0, 0.02, 1.0, "g_vAlbedoContrastSaturationBrightness1.z — Overall material brightness.");
        (_slMatSaturation, _lblMatSaturation) = AddSliderRow("Saturation", 0.0, 2.0, 0.02, 1.0, "g_vAlbedoContrastSaturationBrightness1.y — Overall material saturation.");
        (_slMatContrast, _lblMatContrast) = AddSliderRow("Contrast", 0.0, 2.0, 0.02, 1.0, "g_vAlbedoContrastSaturationBrightness1.x — Overall material contrast.");

        _btnMatParamsReset = new Button
        {
            Text = "Reset Material Parameters",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            TooltipText = "Clear parameter overrides for this material and restore vanilla .vmat_c values."
        };
        _btnMatParamsReset.AddThemeFontSizeOverride("font_size", 11);
        _matParamsBody.AddChild(_btnMatParamsReset);

        // Wire live change handlers
        _cpMatColorTint.ColorChanged += (_) => OnMaterialParamControlChanged();
        _cpMatSelfIllumTint.ColorChanged += (_) => OnMaterialParamControlChanged();
        _slMatVertColorStr.ValueChanged += (v) => { _lblMatVertColorStr.Text = v.ToString("F2"); OnMaterialParamControlChanged(); };
        _slMatSelfIllumScale.ValueChanged += (v) => { _lblMatSelfIllumScale.Text = v.ToString("F2"); OnMaterialParamControlChanged(); };
        _slMatSelfIllumAlbedo.ValueChanged += (v) => { _lblMatSelfIllumAlbedo.Text = v.ToString("F2"); OnMaterialParamControlChanged(); };
        _slMatBrightness.ValueChanged += (v) => { _lblMatBrightness.Text = v.ToString("F2"); OnMaterialParamControlChanged(); };
        _slMatSaturation.ValueChanged += (v) => { _lblMatSaturation.Text = v.ToString("F2"); OnMaterialParamControlChanged(); };
        _slMatContrast.ValueChanged += (v) => { _lblMatContrast.Text = v.ToString("F2"); OnMaterialParamControlChanged(); };
        _btnMatParamsReset.Pressed += ResetActiveMaterialParamsToVanilla;

        parentContainer.AddChild(_matParamsCard);
        parentContainer.MoveChild(_matParamsCard, submeshCard.GetIndex() + 1);
    }

    private void UpdateMaterialParamsExpandState()
    {
        if (_matParamsBody != null) _matParamsBody.Visible = _matParamsExpanded;
        if (_btnMatParamsToggle != null)
        {
            string arrow = _matParamsExpanded ? "▼" : "▶";
            string suffix = "";
            if (_matParamsActiveSubmesh != null && !string.IsNullOrEmpty(_currentHeroId))
            {
                var ov = ModWorkspaceSession.Instance.GetMaterialParamOverrides(_currentHeroId, _matParamsActiveSubmesh.OriginalVmatPath);
                if (ov != null && ov.HasAnyOverride()) suffix = "  ●";
            }
            _btnMatParamsToggle.Text = $"{arrow}  MATERIAL PARAMETERS (VMAT){suffix}";
        }
    }

    private VanillaVmatDefaults ReadVanillaVmatDefaults(SubmeshNodeInfo sub)
    {
        var def = new VanillaVmatDefaults
        {
            ColorTint = Colors.White,
            VertexColorStrength = 1.0f,
            HasVertexColor = false,
            SelfIllumTint = Colors.White,
            SelfIllumScale = 0.0f,
            SelfIllumAlbedoFactor = 0.0f,
            HasSelfIllum = false,
            Contrast = 1.0f,
            Saturation = 1.0f,
            Brightness = 1.0f
        };
        if (sub == null || string.IsNullOrEmpty(sub.OriginalVmatPath)) return def;

        string cacheKey = $"{_currentHeroId}:{sub.OriginalVmatPath}";
        if (_vanillaVmatDefaultsCache.TryGetValue(cacheKey, out var cached)) return cached;

        try
        {
            var vpkLoader = GetVpkLoader();
            Package addonPkg = vpkLoader?.AddonPackage;
            Package basePkg = vpkLoader?.GetOrCreateBasePackage();

            Package ownerPkg = null;
            PackageEntry found = null;
            foreach (var pkg in new[] { addonPkg, basePkg })
            {
                if (pkg == null) continue;
                found = FindVmatEntryForSubmesh(pkg, sub);
                if (found != null) { ownerPkg = pkg; break; }
            }

            if (found != null && ownerPkg != null)
            {
                using var stream = ownerPkg.GetMemoryMappedStreamIfPossible(found);
                using var res = new ValveResourceFormat.Resource();
                res.Read(stream, leaveOpen: true, mode: ReadMode.Deferred);
                if (res.ResourceType == ResourceType.Material && res.DataBlock is VrfMaterial vmat)
                {
                    if (vmat.IntParams != null)
                    {
                        def.HasVertexColor = vmat.IntParams.TryGetValue("F_VERTEX_COLOR", out long vc) && vc != 0;
                        def.HasSelfIllum = vmat.IntParams.TryGetValue("F_SELF_ILLUM", out long si) && si != 0;
                    }
                    if (vmat.FloatParams != null)
                    {
                        if (vmat.FloatParams.TryGetValue("g_fVertexColorStrength1", out float vcs)) def.VertexColorStrength = vcs;
                        if (vmat.FloatParams.TryGetValue("g_flSelfIllumScale1", out float sis)) def.SelfIllumScale = sis;
                        if (vmat.FloatParams.TryGetValue("g_flSelfIllumAlbedoFactor1", out float siaf)) def.SelfIllumAlbedoFactor = siaf;
                    }
                    if (vmat.VectorParams != null)
                    {
                        if (vmat.VectorParams.TryGetValue("g_vColorTint1", out var ct))
                            def.ColorTint = new Color(Mathf.Clamp(ct.X, 0f, 1f), Mathf.Clamp(ct.Y, 0f, 1f), Mathf.Clamp(ct.Z, 0f, 1f), 1f);
                        if (vmat.VectorParams.TryGetValue("g_vSelfIllumTint1", out var sit))
                            def.SelfIllumTint = new Color(Mathf.Clamp(sit.X, 0f, 1f), Mathf.Clamp(sit.Y, 0f, 1f), Mathf.Clamp(sit.Z, 0f, 1f), 1f);
                        if (vmat.VectorParams.TryGetValue("g_vAlbedoContrastSaturationBrightness1", out var csb))
                        {
                            def.Contrast = csb.X > 0.001f ? csb.X : 1.0f;
                            def.Saturation = csb.Y >= 0f ? csb.Y : 1.0f;
                            def.Brightness = csb.Z > 0.001f ? csb.Z : 1.0f;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            GD.Print($"[PaintTabUI] Non-fatal error reading vanilla vmat defaults for '{sub.OriginalVmatPath}': {ex.Message}");
        }

        _vanillaVmatDefaultsCache[cacheKey] = def;
        return def;
    }

    private PackageEntry FindVmatEntryForSubmesh(Package pkg, SubmeshNodeInfo sub)
    {
        if (pkg == null || sub == null) return null;
        if (sub.Mesh != null && GodotObject.IsInstanceValid(sub.Mesh) && sub.Mesh.HasMeta("FullVmatPath"))
        {
            string full = sub.Mesh.GetMeta("FullVmatPath").AsString().Replace('\\', '/').TrimStart('/');
            if (!string.IsNullOrEmpty(full))
            {
                if (!full.EndsWith("_c", StringComparison.OrdinalIgnoreCase)) full += "_c";
                var exactFull = pkg.FindEntry(full);
                if (exactFull != null) return exactFull;
            }
        }
        if (!string.IsNullOrEmpty(sub.OriginalVmatPath))
        {
            string norm = sub.OriginalVmatPath.Replace('\\', '/').TrimStart('/');
            if (!norm.EndsWith("_c", StringComparison.OrdinalIgnoreCase)) norm += "_c";
            var exact = pkg.FindEntry(norm);
            if (exact != null) return exact;
        }
        string matShort = !string.IsNullOrEmpty(sub.MaterialName)
            ? sub.MaterialName
            : (!string.IsNullOrEmpty(sub.OriginalVmatPath) ? System.IO.Path.GetFileNameWithoutExtension(sub.OriginalVmatPath) : "");
        if (!string.IsNullOrEmpty(matShort) && pkg.Entries.TryGetValue("vmat_c", out var vmats))
        {
            // Prefer hero's active model folder (e.g. hornet_v3 over legacy vindicta)
            string heroDir = !string.IsNullOrEmpty(_currentHeroVmdlPath)
                ? System.IO.Path.GetDirectoryName(_currentHeroVmdlPath)?.Replace('\\', '/')?.TrimStart('/')
                : null;
            if (!string.IsNullOrEmpty(heroDir))
            {
                var inHeroDir = vmats.FirstOrDefault(e =>
                    e.FileName.Equals(matShort, StringComparison.OrdinalIgnoreCase) &&
                    e.GetFullPath().Replace('\\', '/').StartsWith(heroDir + "/", StringComparison.OrdinalIgnoreCase));
                if (inHeroDir != null) return inHeroDir;
            }
            return vmats.FirstOrDefault(e => e.FileName.Equals(matShort, StringComparison.OrdinalIgnoreCase));
        }
        return null;
    }

    private void RefreshMaterialParamsForTarget(MeshInstance3D mesh, int surfaceIndex)
    {
        if (_matParamsCard == null) return;
        var sub = _meshHierarchy?.ActiveTarget ?? (mesh != null ? _meshHierarchy?.FindSubmesh(mesh, surfaceIndex) : null);
        _matParamsActiveSubmesh = sub;

        if (sub == null)
        {
            if (_lblMatParamsHint != null) _lblMatParamsHint.Text = "Select a material to adjust shader parameters.";
            UpdateMaterialParamsExpandState();
            return;
        }

        var def = ReadVanillaVmatDefaults(sub);
        var ov = !string.IsNullOrEmpty(_currentHeroId)
            ? ModWorkspaceSession.Instance.GetMaterialParamOverrides(_currentHeroId, sub.OriginalVmatPath)
            : null;

        _isSyncingMatParamsUI = true;
        try
        {
            Color colorTint = (ov?.ColorTintRgb is { Length: >= 3 })
                ? new Color(ov.ColorTintRgb[0], ov.ColorTintRgb[1], ov.ColorTintRgb[2], 1f)
                : def.ColorTint;
            float vertStr = ov?.VertexColorStrength ?? def.VertexColorStrength;
            Color siTint = (ov?.SelfIllumTintRgb is { Length: >= 3 })
                ? new Color(ov.SelfIllumTintRgb[0], ov.SelfIllumTintRgb[1], ov.SelfIllumTintRgb[2], 1f)
                : def.SelfIllumTint;
            float siScale = ov?.SelfIllumScale ?? (def.HasSelfIllum ? def.SelfIllumScale : 0.0f);
            float siAlbedo = ov?.SelfIllumAlbedoFactor ?? def.SelfIllumAlbedoFactor;
            float contrast = ov?.AlbedoContrast ?? def.Contrast;
            float sat = ov?.AlbedoSaturation ?? def.Saturation;
            float bright = ov?.AlbedoBrightness ?? def.Brightness;

            _cpMatColorTint.Color = colorTint;
            _slMatVertColorStr.Value = vertStr;
            _lblMatVertColorStr.Text = vertStr.ToString("F2");
            _cpMatSelfIllumTint.Color = siTint;
            _slMatSelfIllumScale.Value = siScale;
            _lblMatSelfIllumScale.Text = siScale.ToString("F2");
            _slMatSelfIllumAlbedo.Value = siAlbedo;
            _lblMatSelfIllumAlbedo.Text = siAlbedo.ToString("F2");
            _slMatContrast.Value = contrast;
            _lblMatContrast.Text = contrast.ToString("F2");
            _slMatSaturation.Value = sat;
            _lblMatSaturation.Text = sat.ToString("F2");
            _slMatBrightness.Value = bright;
            _lblMatBrightness.Text = bright.ToString("F2");

            string tags = "";
            if (def.HasVertexColor) tags += " [VertexColor]";
            if (def.HasSelfIllum) tags += " [SelfIllum]";
            _lblMatParamsHint.Text = $"{sub.DisplayName}{tags}";

            if (ov != null && ov.HasAnyOverride())
            {
                ApplyMaterialParamOverridesTo3D(sub, ov);
            }
        }
        finally
        {
            _isSyncingMatParamsUI = false;
        }

        UpdateMaterialParamsExpandState();
    }

    private void OnMaterialParamControlChanged()
    {
        if (_isSyncingMatParamsUI || _matParamsActiveSubmesh == null) return;
        var sub = _matParamsActiveSubmesh;
        var def = ReadVanillaVmatDefaults(sub);

        var ov = new WorkspaceMaterialParamOverrides();
        Color ct = _cpMatColorTint.Color;
        if (MathF.Abs(ct.R - def.ColorTint.R) > 0.005f || MathF.Abs(ct.G - def.ColorTint.G) > 0.005f || MathF.Abs(ct.B - def.ColorTint.B) > 0.005f)
            ov.ColorTintRgb = new[] { ct.R, ct.G, ct.B };

        float vcs = (float)_slMatVertColorStr.Value;
        if (MathF.Abs(vcs - def.VertexColorStrength) > 0.005f)
            ov.VertexColorStrength = vcs;

        Color sit = _cpMatSelfIllumTint.Color;
        if (MathF.Abs(sit.R - def.SelfIllumTint.R) > 0.005f || MathF.Abs(sit.G - def.SelfIllumTint.G) > 0.005f || MathF.Abs(sit.B - def.SelfIllumTint.B) > 0.005f)
            ov.SelfIllumTintRgb = new[] { sit.R, sit.G, sit.B };

        float sis = (float)_slMatSelfIllumScale.Value;
        float defSis = def.HasSelfIllum ? def.SelfIllumScale : 0.0f;
        if (MathF.Abs(sis - defSis) > 0.005f)
            ov.SelfIllumScale = sis;

        float sia = (float)_slMatSelfIllumAlbedo.Value;
        if (MathF.Abs(sia - def.SelfIllumAlbedoFactor) > 0.005f)
            ov.SelfIllumAlbedoFactor = sia;

        float con = (float)_slMatContrast.Value;
        if (MathF.Abs(con - def.Contrast) > 0.005f)
            ov.AlbedoContrast = con;

        float sat = (float)_slMatSaturation.Value;
        if (MathF.Abs(sat - def.Saturation) > 0.005f)
            ov.AlbedoSaturation = sat;

        float bri = (float)_slMatBrightness.Value;
        if (MathF.Abs(bri - def.Brightness) > 0.005f)
            ov.AlbedoBrightness = bri;

        ApplyMaterialParamOverridesTo3D(sub, ov, def);

        string heroId = !string.IsNullOrEmpty(_currentHeroId) ? _currentHeroId : "hero";
        string heroDisp = !string.IsNullOrEmpty(_currentHeroDisplayName) ? _currentHeroDisplayName : heroId;
        string category = _currentHeroIsExtra ? "ExtraModel" : "CharacterModel";
        string ownerPath = !string.IsNullOrEmpty(_currentHeroVmdlPath) ? _currentHeroVmdlPath : $"models/heroes_staging/{heroId}/{heroId}.vmdl_c";

        ModWorkspaceSession.Instance.StageMaterialParamOverrides(
            heroId, heroDisp, category, ownerPath, sub.OriginalVmatPath, sub.DisplayName, ov.HasAnyOverride() ? ov : null);

        sub.IsDirty = true;
        UpdateMaterialParamsExpandState();
    }

    private void ResetActiveMaterialParamsToVanilla()
    {
        if (_matParamsActiveSubmesh == null) return;
        var sub = _matParamsActiveSubmesh;
        var def = ReadVanillaVmatDefaults(sub);
        ApplyMaterialParamOverridesTo3D(sub, null, def);

        string heroId = !string.IsNullOrEmpty(_currentHeroId) ? _currentHeroId : "hero";
        string heroDisp = !string.IsNullOrEmpty(_currentHeroDisplayName) ? _currentHeroDisplayName : heroId;
        string category = _currentHeroIsExtra ? "ExtraModel" : "CharacterModel";
        string ownerPath = !string.IsNullOrEmpty(_currentHeroVmdlPath) ? _currentHeroVmdlPath : $"models/heroes_staging/{heroId}/{heroId}.vmdl_c";
        ModWorkspaceSession.Instance.StageMaterialParamOverrides(heroId, heroDisp, category, ownerPath, sub.OriginalVmatPath, sub.DisplayName, null);

        RefreshMaterialParamsForTarget(sub.Mesh, sub.SurfaceIndex);
        _studioUI?.ShowToast($"Reset parameters: {sub.DisplayName}");
    }

    private void ApplyMaterialParamOverridesTo3D(SubmeshNodeInfo targetSub, WorkspaceMaterialParamOverrides ov, VanillaVmatDefaults? knownDef = null)
    {
        if (targetSub == null || _meshHierarchy == null) return;
        var def = knownDef ?? ReadVanillaVmatDefaults(targetSub);

        Color colorTint = (ov?.ColorTintRgb is { Length: >= 3 })
            ? new Color(ov.ColorTintRgb[0], ov.ColorTintRgb[1], ov.ColorTintRgb[2], 1f)
            : def.ColorTint;
        float vertStr = ov?.VertexColorStrength ?? def.VertexColorStrength;
        Color siTint = (ov?.SelfIllumTintRgb is { Length: >= 3 })
            ? new Color(ov.SelfIllumTintRgb[0], ov.SelfIllumTintRgb[1], ov.SelfIllumTintRgb[2], 1f)
            : def.SelfIllumTint;
        float siScale = ov?.SelfIllumScale ?? (def.HasSelfIllum ? def.SelfIllumScale : 0.0f);
        float siAlbedo = ov?.SelfIllumAlbedoFactor ?? def.SelfIllumAlbedoFactor;
        float contrast = ov?.AlbedoContrast ?? def.Contrast;
        float sat = ov?.AlbedoSaturation ?? def.Saturation;
        float bright = ov?.AlbedoBrightness ?? def.Brightness;

        string targetKey = targetSub.OriginalVmatPath ?? targetSub.MaterialName ?? "";
        foreach (var sub in _meshHierarchy.Submeshes)
        {
            if (sub?.Mesh == null || !GodotObject.IsInstanceValid(sub.Mesh)) continue;
            string sKey = sub.OriginalVmatPath ?? sub.MaterialName ?? "";
            if (!SkinLayerManager.AreMaterialKeysEquivalent(sKey, targetKey) && sub != targetSub) continue;

            var mats = new List<Material>();
            if (sub.OriginalMaterial != null) mats.Add(sub.OriginalMaterial);
            var surf = sub.Mesh.GetSurfaceOverrideMaterial(sub.SurfaceIndex);
            if (surf != null && !mats.Contains(surf)) mats.Add(surf);
            var meshMat = sub.Mesh.Mesh?.SurfaceGetMaterial(sub.SurfaceIndex);
            if (meshMat != null && !mats.Contains(meshMat)) mats.Add(meshMat);

            foreach (var mat in mats)
            {
                if (mat is ShaderMaterial sm)
                {
                    sm.SetShaderParameter("color_tint", colorTint);
                    sm.SetShaderParameter("vertex_color_strength", vertStr);
                    sm.SetShaderParameter("albedo_csb", new Vector3(contrast, sat, bright));
                    sm.SetShaderParameter("enable_self_illum", siScale > 0.001f);
                    sm.SetShaderParameter("self_illum_tint", siTint);
                    sm.SetShaderParameter("self_illum_scale", siScale);
                    sm.SetShaderParameter("self_illum_albedo_factor", siAlbedo);
                }
                else if (mat is StandardMaterial3D std)
                {
                    if (ov?.VertexColorStrength.HasValue == true)
                    {
                        std.VertexColorUseAsAlbedo = vertStr > 0.05f;
                    }
                    std.AlbedoColor = new Color(
                        Mathf.Clamp(colorTint.R * bright, 0f, 2f),
                        Mathf.Clamp(colorTint.G * bright, 0f, 2f),
                        Mathf.Clamp(colorTint.B * bright, 0f, 2f),
                        std.AlbedoColor.A);
                    if (ov?.SelfIllumScale != null || ov?.SelfIllumTintRgb != null)
                    {
                        std.EmissionEnabled = siScale > 0.001f;
                        std.Emission = siTint;
                        std.EmissionEnergyMultiplier = siScale;
                    }
                }
            }

            // Also apply live parameter preview to MaterialOverlay (hero_painter_overlay.gdshader),
            // which renders on top of the base material while in the Paint tab!
           if (sub.Mesh.MaterialOverlay is ShaderMaterial overlaySm)
            {
                bool hasOv = ov != null && ov.HasAnyOverride();
                var siCh = sub.Channels?.GetChannel(MaterialTextureChannel.SelfIllum);
                siCh?.EnsureMaterialized();
                bool hasSiMask = siCh != null && siCh.HasValidTexture && !siCh.IsDefaultFallback;
                if (hasSiMask)
                {
                    overlaySm.SetShaderParameter("emission_texture", siCh.Texture);
                }

                overlaySm.SetShaderParameter("param_color_tint", colorTint);
                overlaySm.SetShaderParameter("param_vertex_color_strength", vertStr);
                overlaySm.SetShaderParameter("param_albedo_csb", new Vector3(contrast, sat, bright));
                overlaySm.SetShaderParameter("param_has_self_illum_override", hasOv && (ov.SelfIllumScale.HasValue || ov.SelfIllumTintRgb != null || ov.SelfIllumAlbedoFactor.HasValue));
                overlaySm.SetShaderParameter("param_has_self_illum_mask", hasSiMask);
                overlaySm.SetShaderParameter("param_self_illum_tint", siTint);
                overlaySm.SetShaderParameter("param_self_illum_scale", siScale);
                overlaySm.SetShaderParameter("param_self_illum_albedo_factor", siAlbedo);
            }
        }
    }

    private void RestoreMaterialParamOverridesFromWorkspace(string heroId)
    {
        if (string.IsNullOrEmpty(heroId) || _meshHierarchy == null) return;
        if (!ModWorkspaceSession.Instance.Manifest.Heroes.TryGetValue(heroId, out var heroEntry) || heroEntry == null) return;

        foreach (var asset in heroEntry.Assets)
        {
            if (asset.Materials == null) continue;
            foreach (var matEntry in asset.Materials)
            {
                if (matEntry?.ParamOverrides == null || !matEntry.ParamOverrides.HasAnyOverride()) continue;
                var targetSub = _meshHierarchy.Submeshes.FirstOrDefault(s =>
                    SkinLayerManager.AreMaterialKeysEquivalent(s.OriginalVmatPath, matEntry.MaterialPath) ||
                    SkinLayerManager.AreMaterialKeysEquivalent(s.MaterialName, matEntry.MaterialPath));
                if (targetSub != null)
                {
                    ApplyMaterialParamOverridesTo3D(targetSub, matEntry.ParamOverrides);
                    targetSub.IsDirty = true;
                }
            }
        }

        if (_meshHierarchy.ActiveTarget != null)
        {
            RefreshMaterialParamsForTarget(_meshHierarchy.ActiveTarget.Mesh, _meshHierarchy.ActiveTarget.SurfaceIndex);
        }
    }
}

