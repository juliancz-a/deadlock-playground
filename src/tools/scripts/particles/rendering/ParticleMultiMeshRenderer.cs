using System;
using System.Collections.Generic;
using Godot;

namespace DeadlockPlayground.Particles;

/// <summary>
/// High-performance MultiMesh renderer for a single ParticleSystemInstance.
/// Dispatches alive particles into MultiMesh buffers for RenderSprites, RenderTrails, and RenderModels.
/// Strictly enforces Visual Layer 21 isolation (Rule B.1) and unshaded render modes (Rule B.4).
/// </summary>
public partial class ParticleMultiMeshRenderer : Node3D
{
    private readonly ParticleSystemInstance _instance;
    private MultiMeshInstance3D _multiMeshInstance;
    private MultiMesh _multiMesh;
    private ShaderMaterial _material;
    private IParticleRendererDef _rendererDef;
    private Vector4[] _sequenceRects;
    private bool _hasLoggedDebugChain = false;

    private static Shader _spriteShader;
    private static Shader _trailShader;
    private static Shader _modelShader;
    private static Shader _refractShader;

    public ParticleSystemInstance Instance => _instance;
    public ShaderMaterial Material => _material;
    public IParticleRendererDef RendererDef => _rendererDef;

    public ParticleMultiMeshRenderer(

        ParticleSystemInstance instance,
        IParticleRendererDef rendererDef = null,
        Texture2D texture = null,
        Vector4[] sequenceRects = null,
        Texture2D noiseTexture = null)
    {
        _instance = instance ?? throw new ArgumentNullException(nameof(instance));
        _rendererDef = rendererDef ?? (instance.Definition.Renderers.Count > 0 ? instance.Definition.Renderers[0] : null);
        _sequenceRects = sequenceRects;

        string rName = _rendererDef != null ? _rendererDef.ClassName : "Default";
        Name = $"Renderer_{instance.Definition.VpcfPath.GetFile().GetBaseName()}_{rName}";

        Color baseCol = instance.Definition.ConstantColor;
        CustomTintColor = new Color(baseCol.R, baseCol.G, baseCol.B, 1.0f);
        CustomAlphaScale = baseCol.A;

        BuildMultiMesh(texture, noiseTexture);
        SetCustomTint(baseCol);
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

        if (_rendererDef is RenderTrailsDef trailsDef)
        {
            _material.Shader = GetTrailShader();
            _material.SetShaderParameter("overbright_factor", trailsDef.OverbrightFactor > 0 ? trailsDef.OverbrightFactor : 2.0f);
            _material.SetShaderParameter("tail_alpha_scale", trailsDef.TailAlphaScale.LiteralValue);
            _material.SetShaderParameter("tail_color_scale", trailsDef.TailColorScale.LiteralValue);
            _material.SetShaderParameter("constant_color", _instance.Definition.ConstantColor);
            _material.SetShaderParameter("u_uv_rect", new Vector4(0f, 0f, 1f, 1f));
            _material.SetShaderParameter("u_uv_transform", trailsDef.UVTransform);

            if (texture != null)
            {
                _material.SetShaderParameter("albedo_texture", texture);
            }

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

            var sphere = new SphereMesh
            {
                Radius = 0.5f,
                Height = 1.0f,
                RadialSegments = 32,
                Rings = 16
            };
            _multiMesh.Mesh = sphere;
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
                }

                _material.SetShaderParameter("overbright_factor", overbright);
                _material.SetShaderParameter("constant_color", _instance.Definition.ConstantColor);
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

        _multiMeshInstance.MaterialOverride = _material;
        AddChild(_multiMeshInstance);

        // Debug Aid 3: Per-renderer log: blend mode, texture format and channel layout
        string rName = _rendererDef != null ? _rendererDef.ClassName : "Default";
        string texInfo = texture != null ? $"{texture.GetWidth()}x{texture.GetHeight()}" : "null";
        string blendDesc = (_rendererDef is RenderSpritesDef sd && sd.IsRefract) ? "REFRACT" : "ADD";
        GD.Print($"[ParticleRenderer] Bound '{Name}' ({rName}): Blend={blendDesc}, Texture={texInfo}, Sequences={_sequenceRects?.Length ?? 0}");
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

        if (!Visible)
        {
            if (_multiMesh.VisibleInstanceCount > 0)
            {
                _multiMesh.VisibleInstanceCount = 0;
            }
            return;
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

        if (_rendererDef is RenderTrailsDef trailsDef)
        {
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
            for (int i = 0; i < particles.Length; i++)
            {
                ref readonly var p = ref particles[i];
                if (!p.IsAlive) continue;

                float r = p.Radius;
                if (r < 1e-4f) continue; // Invisible at CP1=0 idle!

                float d = r * 2.0f;
                var xform = new Transform3D(Basis.Identity.Scaled(new Vector3(d, d, d)), p.Position);

                _multiMesh.SetInstanceTransform(activeCount, xform);
                var col = new Color(p.Color.R, p.Color.G, p.Color.B, p.Alpha).SrgbToLinear();
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
            var spritesDef = _rendererDef as RenderSpritesDef;
            float radScale = spritesDef != null ? spritesDef.RadiusScale.Evaluate(evalCtx) : 1.0f;
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
                var basis = billboardBasis.Rotated(billboardBasis.Z, p.Rotation).Scaled(new Vector3(size, size, size));
                var xform = new Transform3D(basis, p.Position);

                int seqIdx = (spritesDef != null && spritesDef.HasConstantSequenceNumber)
                    ? spritesDef.ConstantSequenceNumber
                    : p.SequenceIndex;

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
                    seqIdx,
                    p.GetField(26),
                    overbright
                ));

                activeCount++;
            }
        }

        _multiMesh.VisibleInstanceCount = activeCount;
    }
}
