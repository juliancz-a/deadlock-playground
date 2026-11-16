using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using ValvePak;
using ValveResourceFormat.IO;

namespace DeadlockPlayground.Particles;

/// <summary>
/// High-performance MultiMesh &amp; dynamic mesh/light renderer for a single ParticleSystemInstance.
/// Dispatches alive particles for RenderSprites, RenderTrails, RenderModels, RenderRopes,
/// RenderCables, RenderLightBeam, and RenderStandardLight / Omni2Light / DeferredLight.
/// Strictly enforces Visual Layer 21 isolation (Rule B.1) and unshaded render modes (Rule B.4).
/// </summary>
public partial class ParticleMultiMeshRenderer : Node3D
{
    private const int MaxPooledLights = 16;

    private readonly ParticleSystemInstance _instance;
    private readonly IFileLoader _fileLoader;
    private readonly Package _basePackage;
    private readonly Package _addonPackage;
    private MultiMeshInstance3D _multiMeshInstance;
    private MultiMesh _multiMesh;
    private MeshInstance3D _ropeMeshInstance;
    private ArrayMesh _ropeMesh;
    private MeshInstance3D _beamMeshInstance;
    private ArrayMesh _beamMesh;
    private ShaderMaterial _material;
    private StandardMaterial3D _modelStandardMaterial;
    private IParticleRendererDef _rendererDef;
    private Vector4[] _sequenceRects;
    private Texture2D _secondaryTexture;
    private bool _isLoadedVmdlMesh = false;
    private bool _hasLoggedDebugChain = false;
    private readonly List<OmniLight3D> _lightPool = new();

    private static readonly Dictionary<string, Mesh> _vmdlMeshCache = new(StringComparer.OrdinalIgnoreCase);

    private static Shader _spriteShader;
    private static Shader _trailShader;
    private static Shader _modelShader;
    private static Shader _refractShader;
    private static Shader _ropeShader;
    private static Shader _ropeAdditiveShader;

    public ParticleSystemInstance Instance => _instance;
    public ShaderMaterial Material => _material;
    public IParticleRendererDef RendererDef => _rendererDef;

    public ParticleMultiMeshRenderer(
        ParticleSystemInstance instance,
        IParticleRendererDef rendererDef = null,
        Texture2D texture = null,
        Vector4[] sequenceRects = null,
        Texture2D noiseTexture = null,
        Texture2D secondaryTexture = null,
        IFileLoader fileLoader = null,
        Package basePackage = null,
        Package addonPackage = null)
    {
        _instance = instance ?? throw new ArgumentNullException(nameof(instance));
        _rendererDef = rendererDef ?? (instance.Definition.Renderers.Count > 0 ? instance.Definition.Renderers[0] : null);
        _sequenceRects = sequenceRects;
        _secondaryTexture = secondaryTexture;
        _fileLoader = fileLoader;
        _basePackage = basePackage;
        _addonPackage = addonPackage;

        string rName = _rendererDef != null ? _rendererDef.ClassName : "Default";
        Name = $"Renderer_{instance.Definition.VpcfPath.GetFile().GetBaseName()}_{rName}";

        Color baseCol = instance.Definition.ConstantColor;
        CustomTintColor = new Color(baseCol.R, baseCol.G, baseCol.B, 1.0f);
        CustomAlphaScale = baseCol.A;

        BuildMultiMesh(texture, noiseTexture);
        SetCustomTint(baseCol);
    }

    public override void _ExitTree()
    {
        for (int i = 0; i < _lightPool.Count; i++)
        {
            if (IsInstanceValid(_lightPool[i]))
            {
                _lightPool[i].QueueFree();
            }
        }
        _lightPool.Clear();
    }

    private static Shader GetSpriteShader()
    {
        return _spriteShader ??= GD.Load<Shader>("res://assets/shaders/particles/source2_particle.gdshader");
    }

    private static Shader GetRefractShader()
    {
        return _refractShader ??= GD.Load<Shader>("res://assets/shaders/particles/source2_particle_refract.gdshader");
    }

    private static Shader GetTrailShader()
    {
        return _trailShader ??= GD.Load<Shader>("res://assets/shaders/particles/source2_particle_trail.gdshader");
    }

    private static Shader GetModelShader()
    {
        return _modelShader ??= GD.Load<Shader>("res://assets/shaders/particles/source2_model_additive.gdshader");
    }

    private static Shader GetRopeShader(bool isAdditive = false)
    {
        if (isAdditive)
        {
            return _ropeAdditiveShader ??= GD.Load<Shader>("res://assets/shaders/particles/source2_particle_rope_additive.gdshader");
        }
        return _ropeShader ??= GD.Load<Shader>("res://assets/shaders/particles/source2_particle_rope.gdshader");
    }

    private void BuildMultiMesh(Texture2D texture, Texture2D noiseTexture)
    {
        int maxCapacity = Math.Max(1, _instance.Particles.Length);

        _multiMesh = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true,
            UseCustomData = true,
            InstanceCount = maxCapacity,
            VisibleInstanceCount = 0
        };

        _multiMeshInstance = new MultiMeshInstance3D
        {
            Name = "MultiMeshInstance",
            Multimesh = _multiMesh,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            CustomAabb = new Aabb(new Vector3(-50, -50, -50), new Vector3(100, 100, 100))
        };


        // Strict Rule B.1: Clear Layer 21 (CameraBrush culling mask)
        _multiMeshInstance.Layers &= ~(1u << 20);

        _material = new ShaderMaterial();

        // Pass sequence rect table if available (up to 32 sequences)
        if (_sequenceRects != null && _sequenceRects.Length > 0)
        {
            var rectArray = new Godot.Collections.Array<Vector4>();
            for (int i = 0; i < 32; i++)
            {
                if (i < _sequenceRects.Length)
                    rectArray.Add(_sequenceRects[i]);
                else
                    rectArray.Add(Vector4.Zero);
            }
            _material.SetShaderParameter("u_seq_rects", rectArray);
            _material.SetShaderParameter("u_seq_count", _sequenceRects.Length);
        }
        else
        {
            _material.SetShaderParameter("u_seq_count", 0);
        }

        if (_rendererDef is RenderLightDef)
        {
            _multiMeshInstance.Visible = false;
            _material.Shader = GetSpriteShader();
        }
        else if (_rendererDef is RenderLightBeamDef beamDef)
        {
            _multiMeshInstance.Visible = false;
            _beamMesh = new ArrayMesh();
            _beamMeshInstance = new MeshInstance3D
            {
                Name = "LightBeamMeshInstance",
                Mesh = _beamMesh,
                MaterialOverride = _material,
                TopLevel = true,
                Transform = Transform3D.Identity,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                CustomAabb = new Aabb(new Vector3(-50, -50, -50), new Vector3(100, 100, 100))
            };
            _beamMeshInstance.Layers &= ~(1u << 20); // Strict Rule B.1
            AddChild(_beamMeshInstance);

            _material.Shader = GetRopeShader(isAdditive: true);
            _material.SetShaderParameter("overbright_factor", Mathf.Clamp(beamDef.BrightnessLumensPerMeter / 500.0f, 1.0f, 8.0f));
            _material.SetShaderParameter("alpha_scale", beamDef.AlphaScale);
            _material.SetShaderParameter("constant_color", _instance.Definition.ConstantColor);
            _material.SetShaderParameter("u_uv_transform", new Vector4(1f, 1f, 0f, 0f));

            if (texture != null)
            {
                _material.SetShaderParameter("albedo_texture", texture);
            }
        }
        else if (_rendererDef is RenderRopesDef ropesDef)
        {
            _multiMeshInstance.Visible = false;
            _ropeMesh = new ArrayMesh();
            _ropeMeshInstance = new MeshInstance3D
            {
                Name = "RopeMeshInstance",
                Mesh = _ropeMesh,
                MaterialOverride = _material,
                TopLevel = true,
                Transform = Transform3D.Identity,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                CustomAabb = new Aabb(new Vector3(-50, -50, -50), new Vector3(100, 100, 100))
            };
            _ropeMeshInstance.Layers &= ~(1u << 20); // Strict Rule B.1: Clear Layer 21 (CameraBrush culling mask)
            AddChild(_ropeMeshInstance);

            bool isAdditive = ropesDef.BlendMode != null && ropesDef.BlendMode.Equals("PARTICLE_OUTPUT_BLEND_MODE_ADD", StringComparison.OrdinalIgnoreCase);
            _material.Shader = GetRopeShader(isAdditive);
            _material.SetShaderParameter("overbright_factor", ropesDef.OverbrightFactor > 0 ? ropesDef.OverbrightFactor : 1.0f);
            _material.SetShaderParameter("alpha_scale", ropesDef.AlphaScale.LiteralValue > 0 ? ropesDef.AlphaScale.LiteralValue : 1.0f);
            _material.SetShaderParameter("constant_color", _instance.Definition.ConstantColor);
            _material.SetShaderParameter("u_uv_transform", ropesDef.UVTransform);

            if (texture != null)
            {
                _material.SetShaderParameter("albedo_texture", texture);
            }
        }
        else if (_rendererDef is RenderTrailsDef trailsDef)
        {
            _material.Shader = GetTrailShader();
            _material.SetShaderParameter("overbright_factor", trailsDef.OverbrightFactor > 0 ? trailsDef.OverbrightFactor : 2.0f);
            _material.SetShaderParameter("tail_alpha_scale", trailsDef.TailAlphaScale.LiteralValue);
            _material.SetShaderParameter("tail_color_scale", trailsDef.TailColorScale.LiteralValue);
            _material.SetShaderParameter("constant_color", _instance.Definition.ConstantColor);
            _material.SetShaderParameter("u_uv_rect", new Vector4(0f, 0f, 1f, 1f));
            _material.SetShaderParameter("u_uv_transform", trailsDef.UVTransform);
            _material.SetShaderParameter("u_sec_uv_transform", trailsDef.SecondaryUVTransform);
            _material.SetShaderParameter("u_crop_frames", trailsDef.CropsFrames ? 1.0f : 0.0f);

            if (texture != null)
            {
                _material.SetShaderParameter("albedo_texture", texture);
            }
            ApplySecondaryTextureParams(_secondaryTexture);

            // Quad mesh anchored at particle origin (CenterOffset Y = 0.5)
            var quad = new QuadMesh
            {
                Size = new Vector2(1.0f, 1.0f),
                CenterOffset = new Vector3(0.0f, 0.5f, 0.0f)
            };
            _multiMesh.Mesh = quad;
        }
        else if (_rendererDef is RenderModelsDef modelsDef)
        {
            string modelPathToLoad = modelsDef.ModelPaths.Count > 0 ? modelsDef.ModelPaths[0] : modelsDef.ModelPath;
            Mesh vmdlMesh = TryLoadVmdlMesh(modelPathToLoad);
            _isLoadedVmdlMesh = vmdlMesh != null;

            bool hasNoiseTex = noiseTexture != null && !string.IsNullOrEmpty(modelsDef.PrimaryTexturePath);
            bool isAdditive = string.Equals(modelsDef.BlendMode, "PARTICLE_OUTPUT_BLEND_MODE_ADD", StringComparison.OrdinalIgnoreCase);

            if (_isLoadedVmdlMesh && !hasNoiseTex && !isAdditive)
            {
                _modelStandardMaterial = new StandardMaterial3D
                {
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    VertexColorUseAsAlbedo = true,
                    Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                    CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                    AlbedoColor = Colors.White
                };
            }
            else
            {
                _material.Shader = GetModelShader();
                _material.SetShaderParameter("illum_color", new Color(0.255f, 0.663f, 1.0f, 1.0f));
                _material.SetShaderParameter("overbright_factor", 2.5f);
                _material.SetShaderParameter("fresnel_power", 2.5f);

                if (noiseTexture != null)
                {
                    _material.SetShaderParameter("noise_texture", noiseTexture);
                }
                else if (texture != null)
                {
                    _material.SetShaderParameter("noise_texture", texture);
                }
            }

            _multiMesh.Mesh = vmdlMesh ?? new SphereMesh
            {
                Radius = 0.5f,
                Height = 1.0f,
                RadialSegments = 32,
                Rings = 16
            };
        }
        else // RenderSprites (default)
        {
            var spritesDef = _rendererDef as RenderSpritesDef;
            bool isRefract = spritesDef != null && spritesDef.IsRefract;
            if (isRefract)
            {
                _material.Shader = GetRefractShader();
                _material.SetShaderParameter("refract_amount", spritesDef.RefractAmount > 0f ? spritesDef.RefractAmount : 0.1f);
                _material.SetShaderParameter("blur_lod", 1.5f);
                _material.SetShaderParameter("u_uv_rect", spritesDef.UVRect);
                _material.SetShaderParameter("u_uv_transform", spritesDef.UVTransform);
            }
            else
            {
                _material.Shader = GetSpriteShader();
                float overbright = 1.0f;
                if (spritesDef != null)
                {
                    overbright = spritesDef.OverbrightFactor > 0 ? spritesDef.OverbrightFactor : 1.0f;
                    _material.SetShaderParameter("u_uv_rect", spritesDef.UVRect);
                    _material.SetShaderParameter("u_uv_transform", spritesDef.UVTransform);
                    _material.SetShaderParameter("u_sec_uv_transform", spritesDef.SecondaryUVTransform);
                    _material.SetShaderParameter("u_crop_frames", spritesDef.CropsFrames ? 1.0f : 0.0f);
                }

                _material.SetShaderParameter("overbright_factor", overbright);
                _material.SetShaderParameter("constant_color", _instance.Definition.ConstantColor);
                ApplySecondaryTextureParams(_secondaryTexture);
            }

            if (texture != null)
            {
                _material.SetShaderParameter("albedo_texture", texture);
            }

            var quad = new QuadMesh
            {
                Size = new Vector2(1.0f, 1.0f),
                CenterOffset = Vector3.Zero
            };
            _multiMesh.Mesh = quad;
        }

        _multiMeshInstance.MaterialOverride = _modelStandardMaterial ?? (Material)_material;
        AddChild(_multiMeshInstance);

        // Debug Aid 3: Per-renderer log: blend mode, texture format and channel layout
        string rName = _rendererDef != null ? _rendererDef.ClassName : "Default";
        string texInfo = texture != null ? $"{texture.GetWidth()}x{texture.GetHeight()}" : "null";
        string blendDesc = _rendererDef switch
        {
            RenderLightDef => "LIGHT",
            RenderLightBeamDef => "LIGHTBEAM",
            RenderCablesDef => "CABLE",
            RenderRopesDef => "ROPE",
            RenderModelsDef => _isLoadedVmdlMesh ? "VMDL" : "SPHERE",
            RenderSpritesDef sd when sd.IsRefract => "REFRACT",
            _ => "ADD"
        };
        GD.Print($"[ParticleRenderer] Bound '{Name}' ({rName}): Type={blendDesc}, Texture={texInfo}, Sequences={_sequenceRects?.Length ?? 0}");
    }

    public void SetTexture(Texture2D texture)
    {
        if (texture == null || _material == null) return;
        if (_rendererDef is RenderModelsDef)
        {
            _material.SetShaderParameter("noise_texture", texture);
        }
        else
        {
            _material.SetShaderParameter("albedo_texture", texture);
        }
    }

    public void SetSecondaryTexture(Texture2D secondaryTexture)
    {
        _secondaryTexture = secondaryTexture;
        ApplySecondaryTextureParams(secondaryTexture);
    }

    private void ApplySecondaryTextureParams(Texture2D secondaryTexture)
    {
        if (_material == null) return;
        if (secondaryTexture != null)
        {
            _material.SetShaderParameter("secondary_texture", secondaryTexture);
            _material.SetShaderParameter("u_has_secondary", true);
        }
        else
        {
            _material.SetShaderParameter("u_has_secondary", false);
        }
    }

    public Color CustomTintColor { get; set; } = Colors.White;
    public float CustomAlphaScale { get; set; } = 1.0f;
    public float CustomSizeScale { get; set; } = 1.0f;

    public void SetCustomTint(Color color)
    {
        CustomTintColor = new Color(color.R, color.G, color.B, 1.0f);
        CustomAlphaScale = color.A;
        if (_material != null)
        {
            _material.SetShaderParameter("constant_color", color);
            _material.SetShaderParameter("tail_color_scale", new Vector3(color.R, color.G, color.B));
        }
        if (_modelStandardMaterial != null)
        {
            _modelStandardMaterial.AlbedoColor = color;
        }
    }

    public void SetConstantColor(Color color)
    {
        SetCustomTint(color);
    }

    public bool VisibleAndEmitting
    {
        get => Visible;
        set
        {
            Visible = value;
            if (_instance != null)
            {
                _instance.IsEmissionEnabled = value;
            }
        }
    }

    public void Render(Camera3D camera, ControlPointRegistry cpRegistry)
    {
        if (_multiMesh == null) return;

        if (!Visible || (_rendererDef is RenderRopesDef rBloom && rBloom.OnlyRenderInEffectsBloomPass))
        {
            if (_multiMesh.VisibleInstanceCount > 0)
            {
                _multiMesh.VisibleInstanceCount = 0;
            }
            if (_ropeMesh != null && _ropeMesh.GetSurfaceCount() > 0)
            {
                _ropeMesh.ClearSurfaces();
            }
            if (_ropeMeshInstance != null)
            {
                _ropeMeshInstance.Visible = false;
            }
            if (_beamMesh != null && _beamMesh.GetSurfaceCount() > 0)
            {
                _beamMesh.ClearSurfaces();
            }
            if (_beamMeshInstance != null)
            {
                _beamMeshInstance.Visible = false;
            }
            HidePooledLights(0);
            return;
        }

        if (_ropeMeshInstance != null)
        {
            _ropeMeshInstance.Visible = true;
            _ropeMeshInstance.Transform = Transform3D.Identity;
        }
        if (_beamMeshInstance != null)
        {
            _beamMeshInstance.Visible = true;
            _beamMeshInstance.Transform = Transform3D.Identity;
        }

        var particles = _instance.Particles;
        int activeCount = 0;
        Vector3 camPos = camera != null ? camera.GlobalPosition : new Vector3(0, 1.5f, 3.0f);

        var evalCtx = new ParticleEvaluationContext
        {
            CollectionAge = _instance.CollectionAge,
            ParticleAge = 0.0f,
            Lifetime = 1.0f,
            ControlPoints = cpRegistry
        };

        if (_rendererDef is RenderLightDef lightDef)
        {
            int lightIdx = 0;
            for (int i = 0; i < particles.Length && lightIdx < MaxPooledLights; i++)
            {
                ref readonly var p = ref particles[i];
                if (!p.IsAlive) continue;

                var light = GetOrCreatePooledLight(lightIdx, lightDef.CastShadows);
                light.Position = p.Position;
                light.ShadowEnabled = lightDef.CastShadows;
                light.LightColor = new Color(
                    Mathf.Clamp(p.Color.R * lightDef.ColorScale.X * CustomTintColor.R, 0f, 4f),
                    Mathf.Clamp(p.Color.G * lightDef.ColorScale.Y * CustomTintColor.G, 0f, 4f),
                    Mathf.Clamp(p.Color.B * lightDef.ColorScale.Z * CustomTintColor.B, 0f, 4f),
                    1.0f);

                float rangeM = p.Radius > 1e-4f
                    ? p.Radius * lightDef.RadiusMultiplier * lightDef.RadiusScale * CustomSizeScale
                    : lightDef.RangeInches * ParticleCoordUtils.InchesToMeters * lightDef.RadiusScale * CustomSizeScale;
                if (lightDef.RangeInches > 0f && p.Radius <= 1e-4f)
                {
                    rangeM = lightDef.RangeInches * ParticleCoordUtils.InchesToMeters * lightDef.RadiusScale * CustomSizeScale;
                }
                light.OmniRange = Mathf.Max(0.25f, rangeM);

                float baseBrightness = string.Equals(lightDef.BrightnessUnit, "PARTICLE_LIGHT_UNIT_CANDELAS", StringComparison.OrdinalIgnoreCase)
                    ? lightDef.BrightnessCandelas / 50.0f
                    : (lightDef.BrightnessLumens > 0f ? lightDef.BrightnessLumens / 250.0f : lightDef.Intensity);
                float energy = Mathf.Clamp(baseBrightness * p.Alpha * lightDef.AlphaScale * CustomAlphaScale, 0.0f, 16.0f);
                light.LightEnergy = energy;
                light.Visible = energy > 0.001f;

                lightIdx++;
            }

            HidePooledLights(lightIdx);
            return;
        }
        else if (_rendererDef is RenderLightBeamDef beamDef)
        {
            RenderLightBeam(particles, beamDef, camPos);
            return;
        }
        else if (_rendererDef is RenderRopesDef ropesDef)
        {
            float radScale = ropesDef.RadiusScale.Evaluate(evalCtx);
            float overbright = ropesDef.OverbrightFactor > 0 ? ropesDef.OverbrightFactor : 1.0f;
            float addSelf = 1.0f + ropesDef.AddSelfAmount;
            float selfIllum = ropesDef.SelfIllumAmount > 0 ? ropesDef.SelfIllumAmount : 1.0f;
            float totalBrightness = overbright * addSelf * selfIllum;
            float alphaScale = ropesDef.AlphaScale.Evaluate(evalCtx);

            _material.SetShaderParameter("overbright_factor", totalBrightness);
            _material.SetShaderParameter("alpha_scale", alphaScale);
            _material.SetShaderParameter("u_animated_v_offset", -_instance.CollectionAge);
            _material.SetShaderParameter("u_uv_transform", ropesDef.UVTransform);

            var aliveIndices = new List<int>();
            for (int i = 0; i < particles.Length; i++)
            {
                if (particles[i].IsAlive) aliveIndices.Add(i);
            }

            int count = aliveIndices.Count;
            if (count < 2)
            {
                if (_ropeMesh != null && _ropeMesh.GetSurfaceCount() > 0)
                {
                    _ropeMesh.ClearSurfaces();
                }
                return;
            }

            int minTess = ropesDef is RenderCablesDef cableDef
                ? Math.Max(ropesDef.MinTesselation, cableDef.Roundness + 1)
                : ropesDef.MinTesselation;
            int subdivisions = Math.Clamp(minTess, 1, 8);
            bool closedLoop = ropesDef.ClosedLoop && count >= 3;
            int segmentCount = closedLoop ? count : count - 1;
            int totalSamples = segmentCount * subdivisions + 1;
            int totalVerts = totalSamples * 2;
            int totalIndices = segmentCount * subdivisions * 6;

            var vertArray = new Vector3[totalVerts];
            var uvArray = new Vector2[totalVerts];
            var colorArray = new Color[totalVerts];
            var indexArray = new int[totalIndices];

            int sampleIdx = 0;
            int quadIdx = 0;
            Vector3 stripAxis = Vector3.Zero;

            for (int seg = 0; seg < segmentCount; seg++)
            {
                int idx1 = aliveIndices[seg];
                int idx2 = closedLoop ? aliveIndices[(seg + 1) % count] : aliveIndices[seg + 1];
                int idx0 = closedLoop
                    ? aliveIndices[(seg - 1 + count) % count]
                    : (seg > 0 ? aliveIndices[seg - 1] : idx1);
                int idx3 = closedLoop
                    ? aliveIndices[(seg + 2) % count]
                    : (seg + 2 < count ? aliveIndices[seg + 2] : idx2);

                ref readonly var p0 = ref particles[idx0];
                ref readonly var p1 = ref particles[idx1];
                ref readonly var p2 = ref particles[idx2];
                ref readonly var p3 = ref particles[idx3];

                float v1 = ropesDef.UseScalarForTextureCoordinate
                    ? p1.GetField(ropesDef.ScalarFieldForTextureCoordinate)
                    : ((float)seg / segmentCount);
                float v2 = ropesDef.UseScalarForTextureCoordinate
                    ? p2.GetField(ropesDef.ScalarFieldForTextureCoordinate)
                    : ((float)(seg + 1) / segmentCount);

                float r0 = p0.Radius * radScale * CustomSizeScale;
                float r1 = p1.Radius * radScale * CustomSizeScale;
                float r2 = p2.Radius * radScale * CustomSizeScale;
                float r3 = p3.Radius * radScale * CustomSizeScale;

                Color c1 = new Color(
                    p1.Color.R * CustomTintColor.R,
                    p1.Color.G * CustomTintColor.G,
                    p1.Color.B * CustomTintColor.B,
                    p1.Alpha * CustomAlphaScale
                );
                Color c2 = new Color(
                    p2.Color.R * CustomTintColor.R,
                    p2.Color.G * CustomTintColor.G,
                    p2.Color.B * CustomTintColor.B,
                    p2.Alpha * CustomAlphaScale
                );

                int startStep = (seg == 0) ? 0 : 1;
                for (int step = startStep; step <= subdivisions; step++)
                {
                    float t = (float)step / subdivisions;

                    Vector3 pos = CatmullRom(p0.Position, p1.Position, p2.Position, p3.Position, t);
                    float rad = Mathf.Max(0.005f, CatmullRom(r0, r1, r2, r3, t));
                    Vector3 tangent = CatmullRomTangent(p0.Position, p1.Position, p2.Position, p3.Position, t);
                    if (tangent.LengthSquared() < 1e-5f)
                    {
                        tangent = p2.Position - p1.Position;
                    }
                    tangent = tangent.LengthSquared() > 1e-5f ? tangent.Normalized() : Vector3.Forward;

                    Vector3 planeNormal = camPos - pos;
                    if (planeNormal.LengthSquared() < 1e-5f) planeNormal = Vector3.Back;
                    planeNormal = planeNormal.Normalized();

                    Vector3 widthAxis = tangent.Cross(planeNormal);
                    if (widthAxis.LengthSquared() < 1e-5f)
                    {
                        widthAxis = tangent.Cross(MathF.Abs(tangent.Y) < 0.99f ? Vector3.Up : Vector3.Right);
                    }
                    widthAxis = widthAxis.Normalized();

                    if (sampleIdx == 0)
                    {
                        stripAxis = widthAxis;
                    }
                    else
                    {
                        if (widthAxis.Dot(stripAxis) < 0f)
                        {
                            widthAxis = -widthAxis;
                        }
                        stripAxis = widthAxis;
                    }

                    Color col = c1.Lerp(c2, t);
                    float v = Mathf.Lerp(v1, v2, t);

                    int vLeft = sampleIdx * 2;
                    int vRight = sampleIdx * 2 + 1;

                    vertArray[vLeft] = pos - widthAxis * rad;
                    vertArray[vRight] = pos + widthAxis * rad;

                    uvArray[vLeft] = new Vector2(0f, v);
                    uvArray[vRight] = new Vector2(1f, v);

                    colorArray[vLeft] = col;
                    colorArray[vRight] = col;

                    if (sampleIdx > 0)
                    {
                        int prevLeft = (sampleIdx - 1) * 2;
                        int prevRight = (sampleIdx - 1) * 2 + 1;

                        int iBase = quadIdx * 6;
                        indexArray[iBase + 0] = prevLeft;
                        indexArray[iBase + 1] = vLeft;
                        indexArray[iBase + 2] = prevRight;

                        indexArray[iBase + 3] = prevRight;
                        indexArray[iBase + 4] = vLeft;
                        indexArray[iBase + 5] = vRight;

                        quadIdx++;
                    }

                    sampleIdx++;
                }
            }

            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = vertArray;
            arrays[(int)Mesh.ArrayType.TexUV] = uvArray;
            arrays[(int)Mesh.ArrayType.Color] = colorArray;
            arrays[(int)Mesh.ArrayType.Index] = indexArray;

            _ropeMesh.ClearSurfaces();
            _ropeMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        }
        else if (_rendererDef is RenderTrailsDef trailsDef)
        {
            EnsureMultiMeshCapacity(particles.Length);
            float lenScale = trailsDef.LengthScale > 0 ? trailsDef.LengthScale : 1.0f;
            float maxLenM = trailsDef.MaxLength * 0.0254f;
            float minLenM = trailsDef.MinLength * 0.0254f;
            float radScale = trailsDef.RadiusScale.Evaluate(evalCtx);

            // Ground-Truth: Total brightness factor = Overbright * (1 + AddSelfAmount) * SelfIllum
            float overbright = trailsDef.OverbrightFactor > 0 ? trailsDef.OverbrightFactor : 1.0f;
            float addSelf = 1.0f + trailsDef.AddSelfAmount;
            float selfIllum = trailsDef.SelfIllumAmount > 0 ? trailsDef.SelfIllumAmount : 1.0f;
            float totalBrightness = overbright * addSelf * selfIllum;

            // Update tail alpha and color uniform parameters
            float tailAlpha = trailsDef.TailAlphaScale.Evaluate(evalCtx);
            Vector3 tailColor = trailsDef.TailColorScale.Evaluate(evalCtx);
            _material.SetShaderParameter("overbright_factor", totalBrightness);
            _material.SetShaderParameter("tail_alpha_scale", tailAlpha);
            _material.SetShaderParameter("tail_color_scale", tailColor);

            float dt = _instance.CurrentFrameTime > 0f ? _instance.CurrentFrameTime : (1.0f / 60.0f);
            float oneOverDt = trailsDef.IgnoreDT ? 1.0f : (1.0f / dt);

            for (int i = 0; i < particles.Length; i++)
            {
                ref readonly var p = ref particles[i];
                if (!p.IsAlive) continue;

                // Section B: Trail direction radiates OUTWARD along motion:
                // p.Velocity points radially outward from CP0 (RingWave).
                // motionDir = Position - PrevPosition = +Velocity * dt points outward into the world.
                // This anchors the base (head, v=0) at p.Position (near chest/socket) and radiates
                // the pointed needle tip (tail, v=1) outward into the world along +motionDir.
                Vector3 motionDir = p.Position - p.PrevPosition;
                float diffLen = motionDir.Length();
                if (diffLen < 1e-5f)
                {
                    if (p.Velocity.LengthSquared() > 1e-5f)
                    {
                        motionDir = p.Velocity;
                        diffLen = p.Velocity.Length() * (1.0f / 60.0f);
                    }
                    else continue;
                }

                Vector3 dir = motionDir.Normalized(); // dir points OUTWARD into the world

                // Length evaluated from scratch field 10 (trail length attr)
                float trailAttr = p.GetField(10);
                if (trailAttr <= 0.0001f) continue;

                // LengthFadeInTime ramps trail length up from birth
                float lengthFade = trailsDef.LengthFadeInTime > 1e-5f
                    ? MathF.Min(1.0f, p.Age / trailsDef.LengthFadeInTime)
                    : 1.0f;

                // In Source 2 / VRF RenderTrails:
                // When ignoreDT is true, the step displacement is evaluated at the standard simulation tick (1/60s).
                // In Godot, p.Velocity is in m/s (~150 m/s for beams). The authored step displacement is Velocity * (1/60s) = ~2.5m.
                // When ignoreDT is false, diffLen is scaled by (1/dt) to convert back to full velocity.
                float velM = p.Velocity.Length();
                float stepDistM = trailsDef.IgnoreDT
                    ? (velM > 1e-5f ? velM * (1.0f / 60.0f) : diffLen)
                    : (diffLen * oneOverDt);

                float rawLen = lenScale * trailAttr * stepDistM * lengthFade;
                float len = Mathf.Clamp(rawLen, minLenM, maxLenM);
                if (len <= 0.0001f) continue;

                // VRF RenderTrails behavior:
                // radius = min(particleRadius, constrainRadiusToLengthRatio * length)
                float particleRadius = p.Radius * radScale;
                float radius = MathF.Min(particleRadius, trailsDef.ConstrainRadiusToLengthRatio * len);
                float width = Mathf.Max(0.005f, radius * 2.0f * CustomSizeScale);

                // Ribbon axis AxisU = normalize(cross(dir, camPos - center)), evaluated per instance
                Vector3 center = p.Position + (dir * (len * 0.5f));
                Vector3 toCam = (camPos - center).Normalized();
                Vector3 xAxis = dir.Cross(toCam);
                if (xAxis.LengthSquared() < 1e-5f)
                {
                    xAxis = dir.Cross(MathF.Abs(dir.Y) < 0.99f ? Vector3.Up : Vector3.Right);
                }
                xAxis = xAxis.Normalized();
                Vector3 zAxis = xAxis.Cross(dir).Normalized();

                // Quad mesh has CenterOffset Y = 0.5:
                // Vertex Y=0 is head at p.Position (base at socket), Y=1 is tail at p.Position + dir * len (pointing outward)
                var basis = new Basis(xAxis * width, dir * len, zAxis * width);
                var xform = new Transform3D(basis, p.Position);

                _multiMesh.SetInstanceTransform(activeCount, xform);
                // Modulate vertex colors by custom emitter tint and alpha
                var col = new Color(
                    p.Color.R * CustomTintColor.R,
                    p.Color.G * CustomTintColor.G,
                    p.Color.B * CustomTintColor.B,
                    p.Alpha * CustomAlphaScale
                );
                _multiMesh.SetInstanceColor(activeCount, col);
                _multiMesh.SetInstanceCustomData(activeCount, new Color(
                    p.Age / p.Lifetime,
                    p.SequenceIndex,
                    0.0f,
                    totalBrightness
                ));

                // Debug Aid 4: Log length & width chain for first 5 beam particles
                if (!_hasLoggedDebugChain && activeCount < 5 && _instance.Definition.VpcfPath.Contains("beams"))
                {
                    GD.Print($"[ParticleDebug] Beam P[{i}]: dt={dt:F4}, ignoreDT={trailsDef.IgnoreDT}, stepDist={stepDistM:F3}m, trailAttr={trailAttr:F2}, lengthFade={lengthFade:F2}, finalLen={len:F2}m, pRad={p.Radius:F3}m, radScale={radScale:F2}, halfWidth={radius:F3}m, width={width:F3}m");
                }

                activeCount++;
            }

            if (!_hasLoggedDebugChain && activeCount > 0 && _instance.Definition.VpcfPath.Contains("beams"))
            {
                _hasLoggedDebugChain = true;
            }
        }
        else if (_rendererDef is RenderModelsDef modelsDef)
        {
            EnsureMultiMeshCapacity(particles.Length);
            float radScale = modelsDef.RadiusScale.Evaluate(evalCtx);
            Vector3 compScaleS = modelsDef.ComponentScale.Evaluate(evalCtx);
            float alphaScale = modelsDef.AlphaScale.Evaluate(evalCtx);

            for (int i = 0; i < particles.Length; i++)
            {
                ref readonly var p = ref particles[i];
                if (!p.IsAlive) continue;

                float r = p.Radius;
                if (r < 1e-4f) continue; // Invisible at CP1=0 idle!

                Basis modelBasis = ComputeModelBasis(modelsDef, p, radScale, compScaleS, out Vector3 worldOffset);
                var xform = new Transform3D(modelBasis, p.Position + worldOffset);

                _multiMesh.SetInstanceTransform(activeCount, xform);
                var col = new Color(
                    p.Color.R * CustomTintColor.R,
                    p.Color.G * CustomTintColor.G,
                    p.Color.B * CustomTintColor.B,
                    p.Alpha * alphaScale * CustomAlphaScale
                ).SrgbToLinear();
                _multiMesh.SetInstanceColor(activeCount, col);
                _multiMesh.SetInstanceCustomData(activeCount, new Color(
                    p.GetField(10), // ScrollX
                    p.GetField(27), // ScrollY
                    p.GetField(26), // AlphaTest
                    p.GetField(17)  // IllumColor
                ));

                activeCount++;
            }
        }
        else // RenderSprites
        {
            EnsureMultiMeshCapacity(particles.Length);
            var spritesDef = _rendererDef as RenderSpritesDef;
            float radScale = spritesDef != null ? spritesDef.RadiusScale.Evaluate(evalCtx) : 1.0f;
            float alphaScale = spritesDef != null ? spritesDef.AlphaScale.Evaluate(evalCtx) : 1.0f;
            float overbright = 1.0f;
            if (spritesDef != null)
            {
                float ob = spritesDef.OverbrightFactor > 0 ? spritesDef.OverbrightFactor : 1.0f;
                float addSelf = 1.0f + spritesDef.AddSelfAmount;
                float selfIllum = spritesDef.SelfIllumAmount > 0 ? spritesDef.SelfIllumAmount : 1.0f;
                overbright = ob * addSelf * selfIllum;
            }

            Basis billboardBasis = camera != null ? camera.GlobalBasis : Basis.Identity;

            for (int i = 0; i < particles.Length; i++)
            {
                ref readonly var p = ref particles[i];
                if (!p.IsAlive) continue;

                float size = Mathf.Max(0.005f, p.Radius * 2.0f * radScale * CustomSizeScale);
                Basis basis = ComputeSpriteBasis(spritesDef, billboardBasis, camPos, p).Scaled(new Vector3(size, size, size));
                var xform = new Transform3D(basis, p.Position);

                int seqIdx = (spritesDef != null && spritesDef.SequenceOverride >= 0)
                    ? spritesDef.SequenceOverride
                    : (spritesDef != null && spritesDef.HasConstantSequenceNumber)
                        ? spritesDef.ConstantSequenceNumber
                        : p.SequenceIndex;

                _multiMesh.SetInstanceTransform(activeCount, xform);
                // Modulate vertex colors by custom emitter tint and alpha
                var col = new Color(
                    p.Color.R * CustomTintColor.R,
                    p.Color.G * CustomTintColor.G,
                    p.Color.B * CustomTintColor.B,
                    p.Alpha * alphaScale * CustomAlphaScale
                );
                _multiMesh.SetInstanceColor(activeCount, col);
                _multiMesh.SetInstanceCustomData(activeCount, new Color(
                    p.Age / p.Lifetime,
                    seqIdx,
                    p.GetField(26),
                    overbright
                ));

                activeCount++;
            }
        }

        _multiMesh.VisibleInstanceCount = activeCount;
    }

    private void EnsureMultiMeshCapacity(int requiredCount)
    {
        if (_multiMesh != null && requiredCount > _multiMesh.InstanceCount)
        {
            _multiMesh.InstanceCount = Math.Max(requiredCount, _multiMesh.InstanceCount * 2);
        }
    }

    private OmniLight3D GetOrCreatePooledLight(int index, bool castShadows)
    {
        while (_lightPool.Count <= index)
        {
            var light = new OmniLight3D
            {
                Name = $"ParticleLight_{_lightPool.Count}",
                ShadowEnabled = castShadows,
                OmniAttenuation = 1.5f
            };
            light.Layers &= ~(1u << 20); // Strict Rule B.1
            light.LightCullMask &= ~(1u << 20);
            AddChild(light);
            _lightPool.Add(light);
        }
        return _lightPool[index];
    }

    private void HidePooledLights(int startIndex)
    {
        for (int i = startIndex; i < _lightPool.Count; i++)
        {
            if (IsInstanceValid(_lightPool[i]))
            {
                _lightPool[i].Visible = false;
            }
        }
    }

    private void RenderLightBeam(ReadOnlySpan<CpuParticle> particles, RenderLightBeamDef beamDef, Vector3 camPos)
    {
        var aliveIndices = new List<int>();
        for (int i = 0; i < particles.Length; i++)
        {
            if (particles[i].IsAlive) aliveIndices.Add(i);
        }

        int count = aliveIndices.Count;
        if (count < 2 || _beamMesh == null)
        {
            if (_beamMesh != null && _beamMesh.GetSurfaceCount() > 0)
            {
                _beamMesh.ClearSurfaces();
            }
            HidePooledLights(0);
            return;
        }

        int segmentCount = count - 1;
        var vertArray = new Vector3[count * 2];
        var uvArray = new Vector2[count * 2];
        var colorArray = new Color[count * 2];
        var indexArray = new int[segmentCount * 6];

        Vector3 stripAxis = Vector3.Zero;
        for (int s = 0; s < count; s++)
        {
            ref readonly var p = ref particles[aliveIndices[s]];
            Vector3 pPrev = s > 0 ? particles[aliveIndices[s - 1]].Position : p.Position;
            Vector3 pNext = s + 1 < count ? particles[aliveIndices[s + 1]].Position : p.Position;
            Vector3 tangent = pNext - pPrev;
            if (tangent.LengthSquared() < 1e-6f) tangent = Vector3.Forward;
            tangent = tangent.Normalized();

            Vector3 toCam = (camPos - p.Position);
            if (toCam.LengthSquared() < 1e-6f) toCam = Vector3.Back;
            toCam = toCam.Normalized();

            Vector3 widthAxis = tangent.Cross(toCam);
            if (widthAxis.LengthSquared() < 1e-6f)
            {
                widthAxis = tangent.Cross(MathF.Abs(tangent.Y) < 0.99f ? Vector3.Up : Vector3.Right);
            }
            widthAxis = widthAxis.Normalized();

            if (s == 0)
            {
                stripAxis = widthAxis;
            }
            else
            {
                if (widthAxis.Dot(stripAxis) < 0f) widthAxis = -widthAxis;
                stripAxis = widthAxis;
            }

            float halfWidth = Mathf.Max(
                0.015f,
                MathF.Max(p.Radius, beamDef.Thickness * ParticleCoordUtils.InchesToMeters * 0.5f) * beamDef.RadiusScale * CustomSizeScale);

            int vL = s * 2;
            int vR = s * 2 + 1;
            vertArray[vL] = p.Position - widthAxis * halfWidth;
            vertArray[vR] = p.Position + widthAxis * halfWidth;

            float vCoord = (float)s / (count - 1);
            uvArray[vL] = new Vector2(0f, vCoord);
            uvArray[vR] = new Vector2(1f, vCoord);

            var col = new Color(
                p.Color.R * beamDef.ColorBlend.X * CustomTintColor.R,
                p.Color.G * beamDef.ColorBlend.Y * CustomTintColor.G,
                p.Color.B * beamDef.ColorBlend.Z * CustomTintColor.B,
                p.Alpha * beamDef.AlphaScale * CustomAlphaScale);
            colorArray[vL] = col;
            colorArray[vR] = col;

            if (s > 0)
            {
                int prevL = (s - 1) * 2;
                int prevR = (s - 1) * 2 + 1;
                int ib = (s - 1) * 6;
                indexArray[ib + 0] = prevL;
                indexArray[ib + 1] = vL;
                indexArray[ib + 2] = prevR;
                indexArray[ib + 3] = prevR;
                indexArray[ib + 4] = vL;
                indexArray[ib + 5] = vR;
            }
        }

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertArray;
        arrays[(int)Mesh.ArrayType.TexUV] = uvArray;
        arrays[(int)Mesh.ArrayType.Color] = colorArray;
        arrays[(int)Mesh.ArrayType.Index] = indexArray;

        _beamMesh.ClearSurfaces();
        _beamMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);

        int numLights = Math.Clamp(beamDef.NumberOfLightsToCreate, 0, Math.Min(MaxPooledLights, count));
        for (int l = 0; l < numLights; l++)
        {
            float frac = numLights == 1 ? 0.5f : (float)l / (numLights - 1);
            int pIdx = aliveIndices[Math.Clamp((int)MathF.Round(frac * (count - 1)), 0, count - 1)];
            ref readonly var p = ref particles[pIdx];

            var light = GetOrCreatePooledLight(l, beamDef.CastShadows);
            light.Position = p.Position;
            light.ShadowEnabled = beamDef.CastShadows;
            light.LightColor = new Color(
                Mathf.Clamp(p.Color.R * beamDef.ColorBlend.X * CustomTintColor.R, 0f, 4f),
                Mathf.Clamp(p.Color.G * beamDef.ColorBlend.Y * CustomTintColor.G, 0f, 4f),
                Mathf.Clamp(p.Color.B * beamDef.ColorBlend.Z * CustomTintColor.B, 0f, 4f),
                1.0f);
            light.OmniRange = Mathf.Max(0.5f, beamDef.Range * ParticleCoordUtils.InchesToMeters);
            light.LightEnergy = Mathf.Clamp((beamDef.BrightnessLumensPerMeter / 400.0f) * p.Alpha * beamDef.AlphaScale * CustomAlphaScale, 0f, 12.0f);
            light.Visible = light.LightEnergy > 0.001f;
        }
        HidePooledLights(numLights);
    }

    private Basis ComputeSpriteBasis(RenderSpritesDef spritesDef, Basis billboardBasis, Vector3 camPos, in CpuParticle p)
    {
        string orient = spritesDef?.OrientationType ?? "PARTICLE_ORIENTATION_SCREEN_ALIGNED";
        switch (orient)
        {
            case "PARTICLE_ORIENTATION_SCREEN_Z_ALIGNED":
            {
                Vector3 up = Vector3.Up;
                Vector3 toCam = camPos - p.Position;
                toCam.Y = 0f;
                Vector3 normal = toCam.LengthSquared() > 1e-6f ? toCam.Normalized() : billboardBasis.Z;
                Vector3 right = up.Cross(normal).Normalized();
                return new Basis(right, up, normal).Rotated(normal, p.Rotation);
            }
            case "PARTICLE_ORIENTATION_WORLD_Z_ALIGNED":
            {
                var flat = new Basis(Vector3.Right, Vector3.Back, Vector3.Up);
                return flat.Rotated(Vector3.Up, p.Yaw + p.Rotation);
            }
            case "PARTICLE_ORIENTATION_ALIGN_TO_PARTICLE_NORMAL":
            {
                Vector3 normal = p.Normal.LengthSquared() > 1e-6f ? p.Normal.Normalized() : Vector3.Up;
                Vector3 reference = MathF.Abs(normal.Y) > 0.9f ? Vector3.Forward : Vector3.Up;
                Vector3 right = reference.Cross(normal).Normalized();
                Vector3 up = normal.Cross(right).Normalized();
                float twist = (spritesDef != null && spritesDef.UseYawWithNormalAligned) ? (p.Rotation + p.Yaw) : p.Rotation;
                return new Basis(right, up, normal).Rotated(normal, twist);
            }
            case "PARTICLE_ORIENTATION_SCREENALIGN_TO_PARTICLE_NORMAL":
            {
                Vector3 right = p.Normal.LengthSquared() > 1e-6f ? p.Normal.Normalized() : billboardBasis.X;
                Vector3 toCam = camPos - p.Position;
                Vector3 up = toCam.Cross(right);
                if (up.LengthSquared() < 1e-6f)
                {
                    up = billboardBasis.Y;
                }
                else
                {
                    up = up.Normalized();
                }
                Vector3 normal = right.Cross(up).Normalized();
                right = up.Cross(normal).Normalized();
                return new Basis(right, up, normal).Rotated(normal, p.Rotation);
            }
            case "PARTICLE_ORIENTATION_FULL_3AXIS_ROTATION":
            {
                var flat = new Basis(Vector3.Right, Vector3.Back, Vector3.Up);
                return ParticleCoordUtils.Source2EulerRadiansToGodotModelBasis(p.Pitch, p.Yaw, p.Rotation) * flat;
            }
            default: // PARTICLE_ORIENTATION_SCREEN_ALIGNED
                return billboardBasis.Rotated(billboardBasis.Z, p.Rotation);
        }
    }

    private Basis ComputeModelBasis(
        RenderModelsDef modelsDef,
        in CpuParticle p,
        float radScale,
        Vector3 compScaleS,
        out Vector3 worldOffset)
    {
        const float deg2Rad = MathF.PI / 180.0f;
        var localTurn = ParticleCoordUtils.Source2EulerRadiansToMatrix(
            modelsDef.LocalRotationDeg.X * deg2Rad,
            modelsDef.LocalRotationDeg.Y * deg2Rad,
            modelsDef.LocalRotationDeg.Z * deg2Rad);
        var particleTurn = ParticleCoordUtils.Source2EulerRadiansToMatrix(p.Pitch, p.Yaw, p.Roll);
        var turnBasis = ParticleCoordUtils.Source2MatrixToGodotModelBasis(localTurn * particleTurn);

        Basis normalBasis = Basis.Identity;
        if (!modelsDef.IgnoreNormal && p.Normal.LengthSquared() > 1e-6f)
        {
            Vector3 n = p.Normal.Normalized();
            Vector3 up = MathF.Abs(n.Y) < 0.99f ? Vector3.Up : Vector3.Right;
            if (modelsDef.OrientZ)
            {
                Vector3 x = up.Cross(n).Normalized();
                Vector3 z = x.Cross(n).Normalized();
                normalBasis = new Basis(x, n, z);
            }
            else
            {
                Vector3 x = up.Cross(n).Normalized();
                Vector3 y = n.Cross(x).Normalized();
                normalBasis = new Basis(x, y, n);
            }
        }

        Vector3 scaleG;
        if (_isLoadedVmdlMesh)
        {
            float scaleIn = MathF.Max(1e-4f, p.Radius * ParticleCoordUtils.MetersToInches * radScale * CustomSizeScale);
            scaleG = new Vector3(compScaleS.Y, compScaleS.Z, compScaleS.X) * scaleIn;
        }
        else
        {
            float d = MathF.Max(1e-4f, p.Radius * 2.0f * radScale * CustomSizeScale);
            scaleG = new Vector3(d * compScaleS.Y, d * compScaleS.Z, d * compScaleS.X);
        }

        Basis rot = normalBasis * turnBasis;
        Basis scaling = Basis.Identity.Scaled(scaleG);
        Basis finalBasis = modelsDef.LocalScale ? (rot * scaling) : (normalBasis * scaling * turnBasis);
        Vector3 localOffsetG = modelsDef.LocalOffset;
        worldOffset = finalBasis * localOffsetG;
        return finalBasis;
    }

    private Mesh TryLoadVmdlMesh(string modelPath)
    {
        if (string.IsNullOrWhiteSpace(modelPath)) return null;

        string normalized = modelPath.Replace('\\', '/').Trim().TrimStart('/');
        string compiledPath = normalized.EndsWith("_c", StringComparison.OrdinalIgnoreCase)
            ? normalized
            : normalized + "_c";

        if (_vmdlMeshCache.TryGetValue(compiledPath, out var cachedMesh))
        {
            return cachedMesh;
        }

        string tempGlbPath = null;
        try
        {
            var loader = _fileLoader as CompositeParticleFileLoader
                ?? new CompositeParticleFileLoader(_fileLoader, _basePackage, _addonPackage);

            using var res = loader.LoadFileCompiled(compiledPath);
            if (res?.DataBlock is not ValveResourceFormat.ResourceTypes.Model) return null;

            var exporter = new GltfModelExporter(loader)
            {
                ExportAnimations = false,
                ExportMaterials = false,
                ExportExtras = false
            };

            tempGlbPath = Path.Combine(Path.GetTempPath(), $"dp_ptcl_mdl_{Guid.NewGuid():N}.glb");
            exporter.Export(res, tempGlbPath);
            if (!File.Exists(tempGlbPath)) return null;

            byte[] glbBytes = File.ReadAllBytes(tempGlbPath);
            var gltfDoc = new GltfDocument();
            var gltfState = new GltfState();
            if (gltfDoc.AppendFromBuffer(glbBytes, string.Empty, gltfState) != Error.Ok)
            {
                return null;
            }

            Node3D rootScene = gltfDoc.GenerateScene(gltfState) as Node3D;
            if (rootScene == null) return null;

            Mesh foundMesh = FindFirstMesh(rootScene);
            rootScene.QueueFree();

            if (foundMesh != null)
            {
                _vmdlMeshCache[compiledPath] = foundMesh;
            }
            return foundMesh;
        }
        catch (Exception ex)
        {
            GD.Print($"[ParticleMultiMeshRenderer] Could not load .vmdl_c '{compiledPath}' (using fallback sphere): {ex.Message}");
            return null;
        }
        finally
        {
            if (!string.IsNullOrEmpty(tempGlbPath) && File.Exists(tempGlbPath))
            {
                try { File.Delete(tempGlbPath); } catch { }
            }
        }
    }

    private static Mesh FindFirstMesh(Node node)
    {
        if (node is MeshInstance3D mi && mi.Mesh != null)
        {
            return mi.Mesh;
        }

        foreach (Node child in node.GetChildren())
        {
            var m = FindFirstMesh(child);
            if (m != null) return m;
        }

        return null;
    }

    private static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
    {
        float t2 = t * t;
        float t3 = t2 * t;
        return 0.5f * (
            (2f * p1) +
            (-p0 + p2) * t +
            (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 +
            (-p0 + 3f * p1 - 3f * p2 + p3) * t3
        );
    }

    private static float CatmullRom(float p0, float p1, float p2, float p3, float t)
    {
        float t2 = t * t;
        float t3 = t2 * t;
        return 0.5f * (
            (2f * p1) +
            (-p0 + p2) * t +
            (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 +
            (-p0 + 3f * p1 - 3f * p2 + p3) * t3
        );
    }

    private static Vector3 CatmullRomTangent(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
    {
        float t2 = t * t;
        return 0.5f * (
            (-p0 + p2) +
            2f * (2f * p0 - 5f * p1 + 4f * p2 - p3) * t +
            3f * (-p0 + 3f * p1 - 3f * p2 + p3) * t2
        );
    }
}
