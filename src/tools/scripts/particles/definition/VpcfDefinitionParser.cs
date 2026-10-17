using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;
using ValveKeyValue;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using VrfResource = ValveResourceFormat.Resource;

namespace DeadlockPlayground.Particles;

/// <summary>
/// Strongly typed Source 2 .vpcf parser. Decodes KV3 tree structures into
/// ParticleSystemDefinition models without swallowing unknown classes.
/// </summary>
public static class VpcfDefinitionParser
{
    public static Action<string> LogCallback { get; set; } = Console.WriteLine;

    private static void Log(string msg)
    {
        try { LogCallback?.Invoke(msg); } catch { Console.WriteLine(msg); }
    }

    public static ParticleSystemDefinition Parse(string vpcfPath, VrfResource res)
    {
        if (res == null) return null;

        KVObject data = null;
        if (res.DataBlock is ParticleSystem ps)
        {
            data = ps.Data;
        }
        else if (res.DataBlock is BinaryKV3 bkv)
        {
            data = bkv.Data;
        }

        if (data == null)
        {
            Log($"[VpcfDefinitionParser] No KV3 data block in {vpcfPath}");
            return null;
        }

        return ParseFromKv(vpcfPath, data);
    }

    public static ParticleSystemDefinition ParseFromKv(string vpcfPath, KVObject data)
    {
        if (data == null) return null;

        var def = new ParticleSystemDefinition
        {
            VpcfPath = vpcfPath,
            MaxParticles = (int)ExtractLong(data, "m_nMaxParticles", 64),
            ConstantRadius = ExtractFloat(data, "m_flConstantRadius", 1.0f),
            ConstantLifetime = ExtractFloat(data, "m_flConstantLifespan", 1.0f),
            BehaviorVersion = (int)ExtractLong(data, "m_nBehaviorVersion", 12),
            ConstantColor = ParseColor(data, "m_ConstantColor", Colors.White),
            ConstantSequenceNumber = (int)ExtractLong(data, "m_nConstantSequenceNumber", 0),
            HasConstantSequenceNumber = data.TryGetValue("m_nConstantSequenceNumber", out _)
        };

        // 1. Control Point Drivers
        ParseControlPointDrivers(data, def);

        // 2. Emitters
        if (data.TryGetValue("m_Emitters", out var emittersObj) && emittersObj != null)
        {
            foreach (var item in emittersObj.Children)
            {
                var eDef = ParseEmitter(item.Value, vpcfPath);
                if (eDef != null) def.Emitters.Add(eDef);
            }
        }

        // 3. Initializers
        if (data.TryGetValue("m_Initializers", out var initsObj) && initsObj != null)
        {
            foreach (var item in initsObj.Children)
            {
                var iDef = ParseInitializer(item.Value, vpcfPath);
                if (iDef != null) def.Initializers.Add(iDef);
            }
        }

        // 4. Operators
        if (data.TryGetValue("m_Operators", out var opsObj) && opsObj != null)
        {
            foreach (var item in opsObj.Children)
            {
                var opDef = ParseOperator(item.Value, vpcfPath);
                if (opDef != null) def.Operators.Add(opDef);
            }
        }

        // 5. Renderers
        if (data.TryGetValue("m_Renderers", out var renderersObj) && renderersObj != null)
        {
            foreach (var item in renderersObj.Children)
            {
                var rDef = ParseRenderer(item.Value, vpcfPath);
                if (rDef != null) def.Renderers.Add(rDef);
            }
        }

        // 6. Children
        if (data.TryGetValue("m_Children", out var childrenObj) && childrenObj != null)
        {
            foreach (var item in childrenObj.Children)
            {
                var cVal = item.Value;
                if (cVal == null) continue;

                string childRef = ExtractString(cVal, "m_ChildRef");
                if (string.IsNullOrEmpty(childRef)) continue;

                bool isEndCap = ExtractBool(cVal, "m_bEndCap", false);
                float delay = ExtractFloat(cVal, "m_flDelay", 0.0f);

                def.Children.Add(new ChildParticleSystemRef
                {
                    ChildVpcfPath = childRef,
                    IsEndCap = isEndCap,
                    Delay = delay
                });
            }
        }

        return def;
    }

    #region Drivers

    private static void ParseControlPointDrivers(KVObject data, ParticleSystemDefinition def)
    {
        void ParseDriverArray(KVObject arr)
        {
            if (arr == null) return;
            foreach (var item in arr.Children)
            {
                var drv = item.Value;
                if (drv == null) continue;

                var driverDef = new ControlPointDriverDef
                {
                    ControlPointNumber = (int)ExtractLong(drv, "m_iControlPoint", 0),
                    AttachType = ExtractString(drv, "m_iAttachType", "PATTACH_WORLDORIGIN"),
                    AttachmentName = ExtractString(drv, "m_attachmentName", string.Empty),
                    EntityName = ExtractString(drv, "m_entityName", string.Empty),
                    Offset = ParseVector3Offset(drv, "m_vecOffset")
                };
                def.ControlPointDrivers.Add(driverDef);
            }
        }

        if (data.TryGetValue("m_drivers", out var rootDrivers))
        {
            ParseDriverArray(rootDrivers);
        }

        if (data.TryGetValue("m_controlPointConfigurations", out var configs))
        {
            foreach (var cfg in configs.Children)
            {
                if (cfg.Value.TryGetValue("m_drivers", out var cfgDrivers))
                {
                    ParseDriverArray(cfgDrivers);
                }
            }
        }
    }

    #endregion

    #region Emitters

    private static IParticleEmitterDef ParseEmitter(KVObject kv, string vpcfPath)
    {
        if (kv == null) return null;
        string cls = ExtractString(kv, "_class");

        switch (cls)
        {
            case "C_OP_ContinuousEmitter":
                return new ContinuousEmitterDef
                {
                    EmitRate = ParseFloatInput(kv, "m_flEmitRate", 10.0f),
                    EmissionDuration = ParseFloatInput(kv, "m_flEmissionDuration", 0.0f)
                };

            case "C_OP_InstantaneousEmitter":
                return new InstantaneousEmitterDef
                {
                    ParticlesToEmit = ParseFloatInput(kv, "m_nParticlesToEmit", 1.0f),
                    Delay = ExtractFloat(kv, "m_flStartTime", 0.0f)
                };

            default:
                Log($"[VpcfDefinitionParser] Unsupported Emitter class: '{cls}' in '{vpcfPath}'");
                return null;
        }
    }

    #endregion

    #region Initializers

    private static IParticleInitializerDef ParseInitializer(KVObject kv, string vpcfPath)
    {
        if (kv == null) return null;
        string cls = ExtractString(kv, "_class");

        switch (cls)
        {
            case "C_INIT_RingWave":
                int rwCp = 0;
                if (kv.TryGetValue("m_TransformInput", out var rwtf) && rwtf != null)
                {
                    rwCp = (int)ExtractLong(rwtf, "m_nControlPoint", 0);
                }
                else
                {
                    rwCp = (int)ExtractLong(kv, "m_nControlPointNumber", 0);
                }

                return new RingWaveInitializerDef
                {
                    Radius = ParseFloatInput(kv, "m_flInitialRadius", 5.0f),
                    Thickness = ParseFloatInput(kv, "m_flThickness", 0.0f),
                    SpeedMin = ParseFloatInput(kv, "m_flInitialSpeedMin", 0.0f),
                    SpeedMax = ParseFloatInput(kv, "m_flInitialSpeedMax", 0.0f),
                    Pitch = ParseFloatInput(kv, "m_flPitch", 0.0f),
                    Roll = ParseFloatInput(kv, "m_flRoll", 0.0f),
                    Yaw = ParseFloatInput(kv, "m_flYaw", 0.0f),
                    XYVelocityOnly = ExtractBool(kv, "m_bXYVelocityOnly", false),
                    EvenDistribution = ExtractBool(kv, "m_bEvenDistribution", false),
                    ParticlesPerOrbit = ParseFloatInput(kv, "m_flParticlesPerOrbit", -1.0f).LiteralValue,
                    ControlPointNumber = rwCp
                };

            case "C_INIT_CreateWithinSphereTransform":
                int cpNum = 0;
                if (kv.TryGetValue("m_TransformInput", out var tf) && tf != null)
                {
                    cpNum = (int)ExtractLong(tf, "m_nControlPoint", 0);
                }
                else
                {
                    cpNum = (int)ExtractLong(kv, "m_nControlPointNumber", 0);
                }

                return new CreateWithinSphereTransformDef
                {
                    ControlPointNumber = cpNum,
                    SpeedMin = ParseFloatInput(kv, "m_fSpeedMin", 0.0f),
                    SpeedMax = ParseFloatInput(kv, "m_fSpeedMax", 0.0f),
                    RadiusMin = ParseFloatInput(kv, "m_fRadiusMin", 0.0f),
                    RadiusMax = ParseFloatInput(kv, "m_fRadiusMax", 0.0f),
                    LocalCoordinateSystemSpeed = ExtractBool(kv, "m_bLocalCoords", false)
                };

            case "C_INIT_InitFloat":
                return new InitFloatDef
                {
                    OutputField = (int)ExtractLong(kv, "m_nOutputField", 3), // Schema default is 3 (RADIUS)
                    InputValue = ParseFloatInput(kv, "m_InputValue", 1.0f),
                    SetMethod = ParseSetMethod(kv, "m_nSetMethod")
                };

            case "C_INIT_RandomColor":
                return new RandomColorInitializerDef
                {
                    ColorMin = ParseColor(kv, "m_ColorMin", Colors.White),
                    ColorMax = ParseColor(kv, "m_ColorMax", Colors.White)
                };

            case "C_INIT_RandomYawFlip":
                return new RandomYawFlipInitializerDef
                {
                    Percent = ExtractFloat(kv, "m_flPercent", 0.5f)
                };

            case "C_INIT_PositionOffset":
                return new PositionOffsetInitializerDef
                {
                    OffsetMin = ParseVector3Offset(kv, "m_OffsetMin"),
                    OffsetMax = ParseVector3Offset(kv, "m_OffsetMax"),
                    LocalCoords = ExtractBool(kv, "m_bLocalCoords", false),
                    ControlPointNumber = (int)ExtractLong(kv, "m_nControlPointNumber", 0)
                };

            case "C_INIT_RandomSequence":
                return new RandomSequenceInitializerDef
                {
                    SequenceMin = (int)ExtractLong(kv, "m_nSequenceMin", 0),
                    SequenceMax = (int)ExtractLong(kv, "m_nSequenceMax", 0)
                };

            default:
                Log($"[VpcfDefinitionParser] Unsupported Initializer class: '{cls}' in '{vpcfPath}'");
                return null;
        }
    }

    #endregion

    #region Operators

    private static IParticleOperatorDef ParseOperator(KVObject kv, string vpcfPath)
    {
        if (kv == null) return null;
        string cls = ExtractString(kv, "_class");
        var endCapState = ParseEndCapState(kv, "m_nOpEndCapState");

        switch (cls)
        {
            case "C_OP_BasicMovement":
                return new BasicMovementOperatorDef
                {
                    EndCapState = endCapState,
                    Drag = ExtractFloat(kv, "m_fDrag", 0.0f),
                    Gravity = ParseVector3Offset(kv, "m_Gravity")
                };

            case "C_OP_PositionLock":
                int cpNum = 0;
                if (kv.TryGetValue("m_TransformInput", out var tf) && tf != null)
                {
                    cpNum = (int)ExtractLong(tf, "m_nControlPoint", 0);
                }
                else
                {
                    cpNum = (int)ExtractLong(kv, "m_nControlPointNumber", 0);
                }

                return new PositionLockOperatorDef
                {
                    EndCapState = endCapState,
                    ControlPointNumber = cpNum,
                    LockRotation = ExtractBool(kv, "m_bLockRot", false),
                    PrevPosScale = ExtractFloat(kv, "m_flPrevPosScale", 1.0f)
                };

            case "C_OP_Decay":
                return new DecayOperatorDef
                {
                    EndCapState = endCapState
                };

            case "C_OP_InterpolateRadius":
                return new InterpolateRadiusOperatorDef
                {
                    EndCapState = endCapState,
                    StartScale = ExtractFloat(kv, "m_flStartScale", 1.0f),
                    EndScale = ExtractFloat(kv, "m_flEndScale", 1.0f),
                    Bias = ExtractFloat(kv, "m_flBias", 0.0f),
                    StartTime = ExtractFloat(kv, "m_flStartTime", 0.0f),
                    EndTime = ExtractFloat(kv, "m_flEndTime", 1.0f)
                };

            case "C_OP_ColorInterpolate":
                return new ColorInterpolateOperatorDef
                {
                    EndCapState = endCapState,
                    ColorFade = ParseColor(kv, "m_ColorFade", Colors.White),
                    FadeStartTime = ExtractFloat(kv, "m_flFadeStartTime", 0.0f),
                    FadeEndTime = ExtractFloat(kv, "m_flFadeEndTime", 1.0f),
                    EaseInOut = ExtractBool(kv, "m_bEaseInOut", false)
                };

            case "C_OP_SetFloat":
                return new SetFloatOperatorDef
                {
                    EndCapState = endCapState,
                    OutputField = (int)ExtractLong(kv, "m_nOutputField", 3), // Schema default is 3 (RADIUS)
                    InputValue = ParseFloatInput(kv, "m_InputValue", 0.0f),
                    SetMethod = ParseSetMethod(kv, "m_nSetMethod")
                };

            case "C_OP_RampScalarLinearSimple":
                return new RampScalarLinearSimpleOperatorDef
                {
                    EndCapState = endCapState,
                    Rate = ExtractFloat(kv, "m_Rate", 0.0f),
                    StartTime = ExtractFloat(kv, "m_flStartTime", 0.0f),
                    EndTime = ExtractFloat(kv, "m_flEndTime", 999999.0f),
                    Field = (int)ExtractLong(kv, "m_nField", 3)
                };

            case "C_OP_FadeOutSimple":
                return new FadeOutSimpleOperatorDef
                {
                    EndCapState = endCapState,
                    FadeOutTime = ExtractFloat(kv, "m_flFadeOutTime", 0.25f),
                    Field = (int)ExtractLong(kv, "m_nField", 7)
                };

            case "C_OP_FadeInSimple":
                return new FadeInSimpleOperatorDef
                {
                    EndCapState = endCapState,
                    FadeInTime = ExtractFloat(kv, "m_flFadeInTime", 0.25f),
                    Field = (int)ExtractLong(kv, "m_nField", 7)
                };

            case "C_OP_SpinUpdate":
                return new SpinUpdateOperatorDef
                {
                    EndCapState = endCapState
                };

            case "C_OP_SetVec":
                return new SetVecOperatorDef
                {
                    EndCapState = endCapState,
                    OutputField = (int)ExtractLong(kv, "m_nOutputField", 6), // Schema default is 6 (COLOR/TINT)
                    InputValue = ParseVecInput(kv, "m_InputValue", Vector3.Zero)
                };

            default:
                Log($"[VpcfDefinitionParser] Unsupported Operator class: '{cls}' in '{vpcfPath}'");
                return null;
        }
    }

    #endregion

    #region Renderers

    private static IParticleRendererDef ParseRenderer(KVObject kv, string vpcfPath)
    {
        if (kv == null) return null;
        string cls = ExtractString(kv, "_class");
        string blendMode = ExtractString(kv, "m_nOutputBlendMode", "PARTICLE_OUTPUT_BLEND_MODE_ALPHA");

        switch (cls)
        {
            case "C_OP_RenderTrails":
                var trails = new RenderTrailsDef
                {
                    BlendMode = blendMode,
                    OverbrightFactor = ParseFloatInput(kv, "m_flOverbrightFactor", 1.0f).LiteralValue,
                    AddSelfAmount = ParseFloatInput(kv, "m_flAddSelfAmount", 0.0f).LiteralValue,
                    SelfIllumAmount = ParseFloatInput(kv, "m_flSelfIllumAmount", 0.0f).LiteralValue,
                    LengthScale = ParseFloatInput(kv, "m_flLengthScale", 1.0f).LiteralValue,
                    MaxLength = ParseFloatInput(kv, "m_flMaxLength", 2000.0f).LiteralValue,
                    MinLength = ParseFloatInput(kv, "m_flMinLength", 0.0f).LiteralValue,
                    LengthFadeInTime = ParseFloatInput(kv, "m_flLengthFadeInTime", 0.0f).LiteralValue,
                    TailAlphaScale = ParseFloatInput(kv, "m_flTailAlphaScale", 1.0f),
                    TailColorScale = ParseVecInput(kv, "m_vecTailColorScale", Vector3.One),
                    IgnoreDT = ExtractBool(kv, "m_bIgnoreDT", false),
                    RadiusScale = ParseFloatInput(kv, "m_flRadiusScale", 1.0f),
                    ConstrainRadiusToLengthRatio = ParseFloatInput(kv, "m_flConstrainRadiusToLengthRatio", 1.0f).LiteralValue
                };
                ParseTextureInputs(kv, trails, out var tPath1, out var uv1);
                trails.PrimaryTexturePath = tPath1;
                trails.UVTransform = uv1;
                return trails;

            case "C_OP_RenderSprites":
                var sprites = new RenderSpritesDef
                {
                    BlendMode = blendMode,
                    OverbrightFactor = ParseFloatInput(kv, "m_flOverbrightFactor", 1.0f).LiteralValue,
                    AddSelfAmount = ParseFloatInput(kv, "m_flAddSelfAmount", 0.0f).LiteralValue,
                    SelfIllumAmount = ParseFloatInput(kv, "m_flSelfIllumAmount", 0.0f).LiteralValue,
                    RadiusScale = ParseFloatInput(kv, "m_flRadiusScale", 1.0f),
                    IsRefract = ExtractBool(kv, "m_bRefract", false),
                    RefractAmount = ParseFloatInput(kv, "m_flRefractAmount", 0.1f).LiteralValue,
                    RefractBlurRadius = ExtractFloat(kv, "m_nRefractBlurRadius", 3.0f)
                };
                if (kv.TryGetValue("m_nConstantSequenceNumber", out var cSeq) && cSeq != null)
                {
                    sprites.ConstantSequenceNumber = (int)Convert.ToInt64(cSeq.ToString());
                    sprites.HasConstantSequenceNumber = true;
                }
                ParseTextureInputs(kv, sprites, out var tPath2, out var uv2);
                sprites.PrimaryTexturePath = tPath2;
                sprites.UVTransform = uv2;
                return sprites;

            case "C_OP_RenderModels":
                var models = new RenderModelsDef
                {
                    BlendMode = blendMode,
                    OverrideMaterialPath = ExtractString(kv, "m_hOverrideMaterial"),
                    OrientZ = ExtractBool(kv, "m_bOrientZ", true)
                };

                if (kv.TryGetValue("m_ModelList", out var mdlList) && mdlList != null)
                {
                    foreach (var mItem in mdlList.Children)
                    {
                        string m = ExtractString(mItem.Value, "m_model");
                        if (!string.IsNullOrEmpty(m))
                        {
                            models.ModelPath = m;
                            break;
                        }
                    }
                }

                if (kv.TryGetValue("m_MaterialVars", out var matVars) && matVars != null)
                {
                    foreach (var mv in matVars.Children)
                    {
                        var val = mv.Value;
                        if (val == null) continue;
                        string varName = ExtractString(val, "m_strVariable");
                        int varField = (int)ExtractLong(val, "m_nVariableField", 0);
                        models.MaterialVars.Add(new MaterialVarMapping
                        {
                            VariableName = varName,
                            VariableField = varField
                        });
                    }
                }
                return models;

            case "C_OP_RenderRopes":
                var ropes = new RenderRopesDef
                {
                    BlendMode = blendMode,
                    OverbrightFactor = ParseFloatInput(kv, "m_flOverbrightFactor", 1.0f).LiteralValue,
                    AddSelfAmount = ParseFloatInput(kv, "m_flAddSelfAmount", 0.0f).LiteralValue,
                    SelfIllumAmount = ParseFloatInput(kv, "m_flSelfIllumAmount", 0.0f).LiteralValue,
                    AlphaScale = ParseFloatInput(kv, "m_flAlphaScale", 1.0f),
                    RadiusScale = ParseFloatInput(kv, "m_flRadiusScale", 1.0f),
                    OnlyRenderInEffectsBloomPass = ExtractBool(kv, "m_bOnlyRenderInEffectsBloomPass", false),
                    MinTesselation = (int)ExtractLong(kv, "m_nMinTesselation", 4),
                    MaxTesselation = (int)ExtractLong(kv, "m_nMaxTesselation", 4),
                    ClosedLoop = ExtractBool(kv, "m_bClosedLoop", false),
                    UseScalarForTextureCoordinate = ExtractBool(kv, "m_bUseScalarForTextureCoordinate", false),
                    ScalarFieldForTextureCoordinate = (int)ExtractLong(kv, "m_nScalarFieldForTextureCoordinate", 0)
                };
                ParseTextureInputs(kv, ropes, out var tPathRopes, out var uvRopes);
                ropes.PrimaryTexturePath = tPathRopes;
                ropes.UVTransform = uvRopes;
                return ropes;

            default:
                Log($"[VpcfDefinitionParser] Unsupported Renderer class: '{cls}' in '{vpcfPath}'");
                return null;
        }
    }

    private static void ParseTextureInputs(KVObject kv, IParticleRendererDef renderer, out string texturePath, out Vector4 uvTransform)
    {
        texturePath = string.Empty;
        uvTransform = new Vector4(1f, 1f, 0f, 0f);

        if (kv.TryGetValue("m_vecTexturesInput", out var texIn) && texIn != null)
        {
            foreach (var ti in texIn.Children)
            {
                var tVal = ti.Value;
                if (tVal == null) continue;

                if (tVal.TryGetValue("m_bEnabled", out var en) && en != null && en.ToString().Equals("false", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string tex = ExtractString(tVal, "m_hTexture");
                if (!string.IsNullOrEmpty(tex))
                {
                    texturePath = tex;
                }

                if (tVal.TryGetValue("m_TextureControls", out var tc) && tc != null)
                {
                    float scaleU = ExtractFloat(tc, "m_flFinalTextureScaleU", 1.0f);
                    float scaleV = ExtractFloat(tc, "m_flFinalTextureScaleV", 1.0f);
                    float offsetU = ExtractFloat(tc, "m_flFinalTextureOffsetU", 0.0f);
                    float offsetV = ExtractFloat(tc, "m_flFinalTextureOffsetV", 0.0f);
                    uvTransform = new Vector4(scaleU, scaleV, offsetU, offsetV);
                }

                if (!string.IsNullOrEmpty(texturePath)) break;
            }
        }

        if (string.IsNullOrEmpty(texturePath))
        {
            string directTex = ExtractString(kv, "m_hTexture");
            if (!string.IsNullOrEmpty(directTex))
            {
                texturePath = directTex;
            }
        }
    }

    #endregion

    #region Float & Vector Input Parsers

    public static ParticleFloatInput ParseFloatInput(KVObject parent, string key, float fallback = 0.0f)
    {
        if (parent == null || !parent.TryGetValue(key, out var obj) || obj == null)
        {
            return ParticleFloatInput.FromLiteral(fallback);
        }

        return ParseFloatInputObject(obj, fallback);
    }

    private static ParticleFloatInput ParseFloatInputObject(KVObject obj, float fallback)
    {
        // Direct literal number
        if (obj.Count == 0 && float.TryParse(obj.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out float directNum))
        {
            return ParticleFloatInput.FromLiteral(directNum);
        }

        var input = new ParticleFloatInput
        {
            Type = ExtractString(obj, "m_nType", "PF_TYPE_LITERAL"),
            MapType = ExtractString(obj, "m_nMapType", "PF_MAP_TYPE_DIRECT"),
            LiteralValue = ExtractFloat(obj, "m_flLiteralValue", fallback),
            ControlPoint = (int)ExtractLong(obj, "m_nControlPoint", 0),
            VectorComponent = (int)ExtractLong(obj, "m_nVectorComponent", 0),
            ScalarAttribute = (int)ExtractLong(obj, "m_nScalarAttribute", 3),
            RandomMin = ExtractFloat(obj, "m_flRandomMin", 0.0f),
            RandomMax = ExtractFloat(obj, "m_flRandomMax", 1.0f),
            HasRandomSignFlip = ExtractBool(obj, "m_bHasRandomSignFlip", false),
            RandomMode = ExtractString(obj, "m_nRandomMode", "PF_RANDOM_MODE_CONSTANT"),
            Input0 = ExtractFloat(obj, "m_flInput0", 0.0f),
            Input1 = ExtractFloat(obj, "m_flInput1", 1.0f),
            Output0 = ExtractFloat(obj, "m_flOutput0", 0.0f),
            Output1 = ExtractFloat(obj, "m_flOutput1", 1.0f),
            MultFactor = ExtractFloat(obj, "m_flMultFactor", 1.0f),
            BiasParameter = ExtractFloat(obj, "m_flBiasParameter", 0.0f),
            NoiseOutputMin = ExtractFloat(obj, "m_flNoiseOutputMin", 0.0f),
            NoiseOutputMax = ExtractFloat(obj, "m_flNoiseOutputMax", 1.0f),
            NoiseScale = ExtractFloat(obj, "m_flNoiseScale", 0.1f),
            NoiseOffsetRate = ParseVector3Offset(obj, "m_vecNoiseOffsetRate")
        };

        // Parse Curve spline if present
        if (obj.TryGetValue("m_Curve", out var curveObj) && curveObj != null)
        {
            if (curveObj.TryGetValue("m_spline", out var splineObj) && splineObj != null)
            {
                var points = new List<SplinePoint>();
                foreach (var p in splineObj.Children)
                {
                    float px = ExtractFloat(p.Value, "x", 0.0f);
                    float py = ExtractFloat(p.Value, "y", 0.0f);
                    float sIn = ExtractFloat(p.Value, "m_flSlopeIncoming", 0.0f);
                    float sOut = ExtractFloat(p.Value, "m_flSlopeOutgoing", 0.0f);
                    points.Add(new SplinePoint(px, py, sIn, sOut));
                }

                Vector2 dMin = Vector2.Zero;
                Vector2 dMax = Vector2.One;
                if (curveObj.TryGetValue("m_vDomainMins", out var minObj) && minObj != null && minObj.IsArray)
                {
                    var items = minObj.Children.ToArray();
                    if (items.Length >= 2)
                    {
                        dMin = new Vector2(Convert.ToSingle(items[0].Value), Convert.ToSingle(items[1].Value));
                    }
                }
                if (curveObj.TryGetValue("m_vDomainMaxs", out var maxObj) && maxObj != null && maxObj.IsArray)
                {
                    var items = maxObj.Children.ToArray();
                    if (items.Length >= 2)
                    {
                        dMax = new Vector2(Convert.ToSingle(items[0].Value), Convert.ToSingle(items[1].Value));
                    }
                }

                input.Curve = new SplineCurve(points, dMin, dMax);
            }
        }

        return input;
    }

    public static ParticleVecInput ParseVecInput(KVObject parent, string key, Vector3 fallback)
    {
        if (parent == null || !parent.TryGetValue(key, out var obj) || obj == null)
        {
            return ParticleVecInput.FromLiteral(fallback);
        }

        var vec = new ParticleVecInput
        {
            Type = ExtractString(obj, "m_nType", "PVEC_TYPE_LITERAL"),
            ControlPoint = (int)ExtractLong(obj, "m_nControlPoint", 0)
        };

        if (obj.TryGetValue("m_vLiteralValue", out var lit) && lit != null && lit.IsArray)
        {
            var arr = lit.Children.ToArray();
            if (arr.Length >= 3)
            {
                vec.LiteralValue = new Vector3(
                    Convert.ToSingle(arr[0].Value),
                    Convert.ToSingle(arr[1].Value),
                    Convert.ToSingle(arr[2].Value)
                );
            }
        }
        else if (obj.TryGetValue("m_LiteralColor", out var col) && col != null && col.IsArray)
        {
            var arr = col.Children.ToArray();
            if (arr.Length >= 3)
            {
                vec.LiteralValue = new Vector3(
                    Convert.ToSingle(arr[0].Value) / 255.0f,
                    Convert.ToSingle(arr[1].Value) / 255.0f,
                    Convert.ToSingle(arr[2].Value) / 255.0f
                );
            }
        }
        if (obj.TryGetValue("m_FloatInterp", out var fiObj) && fiObj != null)
        {
            vec.FloatInterp = ParseFloatInputObject(fiObj, 0.0f);
        }

        if (vec.Type == "PVEC_TYPE_FLOAT_INTERP_GRADIENT")
        {
            if (obj.TryGetValue("m_Gradient", out var gradObj) && gradObj != null && gradObj.TryGetValue("m_Stops", out var stopsObj) && stopsObj != null)
            {
                foreach (var stopItem in stopsObj.Children)
                {
                    var stopVal = stopItem.Value;
                    if (stopVal == null) continue;
                    float pos = ExtractFloat(stopVal, "m_flPosition", 0.0f);
                    Color stopCol = ParseColor(stopVal, "m_Color", Colors.White);
                    vec.GradientStops.Add(new GradientStopDef
                    {
                        Position = pos,
                        Color = new Vector3(stopCol.R, stopCol.G, stopCol.B)
                    });
                }
            }
        }

        if (obj.TryGetValue("m_FloatComponentX", out var fcX) && fcX != null) vec.ComponentX = ParseFloatInputObject(fcX, 0f);
        if (obj.TryGetValue("m_FloatComponentY", out var fcY) && fcY != null) vec.ComponentY = ParseFloatInputObject(fcY, 0f);
        if (obj.TryGetValue("m_FloatComponentZ", out var fcZ) && fcZ != null) vec.ComponentZ = ParseFloatInputObject(fcZ, 0f);

        return vec;
    }

    #endregion

    #region Primitive KV Helpers

    private static float ExtractFloat(KVObject kv, string key, float fallback)
    {
        if (kv == null || !kv.TryGetValue(key, out var v) || v == null) return fallback;
        if (v.TryGetValue("m_flLiteralValue", out var lit) && lit != null)
        {
            if (float.TryParse(lit.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out float litVal))
            {
                return litVal;
            }
        }
        if (float.TryParse(v.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out float res))
        {
            return res;
        }
        return fallback;
    }

    public static long ExtractLong(KVObject kv, string key, long fallback)
    {
        if (kv == null || !kv.TryGetValue(key, out var v) || v == null) return fallback;
        if (long.TryParse(v.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long res))
        {
            return res;
        }
        return fallback;
    }

    public static string ExtractString(KVObject kv, string key, string fallback = "")
    {
        if (kv == null || !kv.TryGetValue(key, out var v) || v == null) return fallback;
        string s = v.ToString() ?? fallback;
        if (s.StartsWith("resource:", StringComparison.OrdinalIgnoreCase))
        {
            s = s.Substring("resource:".Length);
        }
        return s.Trim();
    }

    private static bool ExtractBool(KVObject kv, string key, bool fallback)
    {
        if (kv == null || !kv.TryGetValue(key, out var v) || v == null) return fallback;
        string s = v.ToString()?.Trim() ?? "";
        return s.Equals("1") || s.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    private static Color ParseColor(KVObject kv, string key, Color fallback)
    {
        if (kv == null || !kv.TryGetValue(key, out var obj) || obj == null || !obj.IsArray) return fallback;
        var arr = obj.Children.ToArray();
        if (arr.Length >= 3)
        {
            float r = Convert.ToSingle(arr[0].Value) / 255.0f;
            float g = Convert.ToSingle(arr[1].Value) / 255.0f;
            float b = Convert.ToSingle(arr[2].Value) / 255.0f;
            float a = arr.Length > 3 ? Convert.ToSingle(arr[3].Value) / 255.0f : 1.0f;
            return new Color(r, g, b, a);
        }
        return fallback;
    }

    private static Vector3 ParseVector3Offset(KVObject kv, string key)
    {
        if (kv == null || !kv.TryGetValue(key, out var obj) || obj == null) return Vector3.Zero;

        KVObject target = obj;
        if (!target.IsArray && target.TryGetValue("m_vLiteralValue", out var lit) && lit != null && lit.IsArray)
        {
            target = lit;
        }

        if (!target.IsArray) return Vector3.Zero;

        var arr = target.Children.ToArray();
        if (arr.Length >= 3)
        {
            float sx = Convert.ToSingle(arr[0].Value, CultureInfo.InvariantCulture);
            float sy = Convert.ToSingle(arr[1].Value, CultureInfo.InvariantCulture);
            float sz = Convert.ToSingle(arr[2].Value, CultureInfo.InvariantCulture);
            // Source 2 (inches, Z-up: X fwd, Y left, Z up) to Godot (meters, Y-up: X right, Y up, Z back)
            const float inToM = 0.0254f;
            return new Vector3(sx * inToM, sz * inToM, -sy * inToM);
        }
        return Vector3.Zero;
    }

    private static ParticleSetMethod ParseSetMethod(KVObject kv, string key)
    {
        string s = ExtractString(kv, key);
        return s switch
        {
            "PARTICLE_SET_SCALE_INITIAL_VALUE" => ParticleSetMethod.ScaleInitial,
            "PARTICLE_SET_ADD_TO_INITIAL_VALUE" => ParticleSetMethod.AddInitial,
            _ => ParticleSetMethod.SetDirect
        };
    }

    private static ParticleEndCapState ParseEndCapState(KVObject kv, string key)
    {
        string s = ExtractString(kv, key);
        return s switch
        {
            "PARTICLE_ENDCAP_ENDCAP_ON" => ParticleEndCapState.EndCapOn,
            "PARTICLE_ENDCAP_ENDCAP_OFF" => ParticleEndCapState.EndCapOff,
            _ => ParticleEndCapState.Any
        };
    }

    #endregion
}
