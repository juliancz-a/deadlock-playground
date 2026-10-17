using System;
using System.Collections.Generic;
using Godot;

namespace DeadlockPlayground.Particles;

public enum ParticleEndCapState
{
    Any = 0,
    EndCapOff = 1, // Runs only during active life
    EndCapOn = 2   // Runs only after system stop / release
}

public enum ParticleSetMethod
{
    SetDirect = 0,
    ScaleInitial = 1,
    AddInitial = 2
}

#region Interfaces

public interface IParticleEmitterDef
{
    string ClassName { get; }
}

public interface IParticleInitializerDef
{
    string ClassName { get; }
}

public interface IParticleOperatorDef
{
    string ClassName { get; }
    ParticleEndCapState EndCapState { get; set; }
}

public interface IParticleRendererDef
{
    string ClassName { get; }
    string BlendMode { get; set; }
    string PrimaryTexturePath { get; set; }
}

#endregion

#region Emitters

public class ContinuousEmitterDef : IParticleEmitterDef
{
    public string ClassName => "C_OP_ContinuousEmitter";
    public ParticleFloatInput EmitRate { get; set; } = ParticleFloatInput.FromLiteral(10.0f);
    public ParticleFloatInput EmissionDuration { get; set; } = ParticleFloatInput.FromLiteral(0.0f);
}

public class InstantaneousEmitterDef : IParticleEmitterDef
{
    public string ClassName => "C_OP_InstantaneousEmitter";
    public ParticleFloatInput ParticlesToEmit { get; set; } = ParticleFloatInput.FromLiteral(1.0f);
    public float Delay { get; set; } = 0.0f;
}

#endregion

#region Initializers

public class RingWaveInitializerDef : IParticleInitializerDef
{
    public string ClassName => "C_INIT_RingWave";
    public ParticleFloatInput Radius { get; set; } = ParticleFloatInput.FromLiteral(5.0f); // in inches
    public ParticleFloatInput Thickness { get; set; } = ParticleFloatInput.FromLiteral(0.0f);
    public ParticleFloatInput SpeedMin { get; set; } = ParticleFloatInput.FromLiteral(0.0f); // in/s
    public ParticleFloatInput SpeedMax { get; set; } = ParticleFloatInput.FromLiteral(0.0f); // in/s
    public ParticleFloatInput Pitch { get; set; } = ParticleFloatInput.FromLiteral(0.0f); // degrees
    public ParticleFloatInput Roll { get; set; } = ParticleFloatInput.FromLiteral(0.0f);
    public ParticleFloatInput Yaw { get; set; } = ParticleFloatInput.FromLiteral(0.0f);
    public bool XYVelocityOnly { get; set; } = false;
    public bool EvenDistribution { get; set; } = false;
    public float ParticlesPerOrbit { get; set; } = -1.0f;
    public int ControlPointNumber { get; set; } = 0;
}

public class CreateWithinSphereTransformDef : IParticleInitializerDef
{
    public string ClassName => "C_INIT_CreateWithinSphereTransform";
    public int ControlPointNumber { get; set; } = 0;
    public ParticleFloatInput SpeedMin { get; set; } = ParticleFloatInput.FromLiteral(0.0f);
    public ParticleFloatInput SpeedMax { get; set; } = ParticleFloatInput.FromLiteral(0.0f);
    public ParticleFloatInput RadiusMin { get; set; } = ParticleFloatInput.FromLiteral(0.0f);
    public ParticleFloatInput RadiusMax { get; set; } = ParticleFloatInput.FromLiteral(0.0f);
    public bool LocalCoordinateSystemSpeed { get; set; } = false;
}

public class InitFloatDef : IParticleInitializerDef
{
    public string ClassName => "C_INIT_InitFloat";
    public int OutputField { get; set; } = 1; // 1 = Lifetime, 3 = Radius, 7 = Alpha, 10 = Trail Length
    public ParticleFloatInput InputValue { get; set; } = ParticleFloatInput.FromLiteral(1.0f);
    public ParticleSetMethod SetMethod { get; set; } = ParticleSetMethod.SetDirect;
}

public class RandomColorInitializerDef : IParticleInitializerDef
{
    public string ClassName => "C_INIT_RandomColor";
    public Color ColorMin { get; set; } = Colors.White;
    public Color ColorMax { get; set; } = Colors.White;
}

public class RandomYawFlipInitializerDef : IParticleInitializerDef
{
    public string ClassName => "C_INIT_RandomYawFlip";
    public float Percent { get; set; } = 0.5f;
}

public class PositionOffsetInitializerDef : IParticleInitializerDef
{
    public string ClassName => "C_INIT_PositionOffset";
    public Vector3 OffsetMin { get; set; } = Vector3.Zero;
    public Vector3 OffsetMax { get; set; } = Vector3.Zero;
    public bool LocalCoords { get; set; } = false;
    public int ControlPointNumber { get; set; } = 0;
}

public class RandomSequenceInitializerDef : IParticleInitializerDef
{
    public string ClassName => "C_INIT_RandomSequence";
    public int SequenceMin { get; set; } = 0;
    public int SequenceMax { get; set; } = 0;
}

#endregion

#region Operators

public class BasicMovementOperatorDef : IParticleOperatorDef
{
    public string ClassName => "C_OP_BasicMovement";
    public ParticleEndCapState EndCapState { get; set; } = ParticleEndCapState.Any;
    public float Drag { get; set; } = 0.0f;
    public Vector3 Gravity { get; set; } = Vector3.Zero;
}

public class PositionLockOperatorDef : IParticleOperatorDef
{
    public string ClassName => "C_OP_PositionLock";
    public ParticleEndCapState EndCapState { get; set; } = ParticleEndCapState.Any;
    public int ControlPointNumber { get; set; } = 0;
    public bool LockRotation { get; set; } = false;
    public float PrevPosScale { get; set; } = 1.0f;
}

public class DecayOperatorDef : IParticleOperatorDef
{
    public string ClassName => "C_OP_Decay";
    public ParticleEndCapState EndCapState { get; set; } = ParticleEndCapState.EndCapOff;
}

public class InterpolateRadiusOperatorDef : IParticleOperatorDef
{
    public string ClassName => "C_OP_InterpolateRadius";
    public ParticleEndCapState EndCapState { get; set; } = ParticleEndCapState.EndCapOff;
    public float StartScale { get; set; } = 1.0f;
    public float EndScale { get; set; } = 1.0f;
    public float Bias { get; set; } = 0.0f;
    public float StartTime { get; set; } = 0.0f;
    public float EndTime { get; set; } = 1.0f;
}

public class ColorInterpolateOperatorDef : IParticleOperatorDef
{
    public string ClassName => "C_OP_ColorInterpolate";
    public ParticleEndCapState EndCapState { get; set; } = ParticleEndCapState.Any;
    public Color ColorFade { get; set; } = Colors.White;
    public float FadeStartTime { get; set; } = 0.0f;
    public float FadeEndTime { get; set; } = 1.0f;
    public bool EaseInOut { get; set; } = false;
}

public class SetFloatOperatorDef : IParticleOperatorDef
{
    public string ClassName => "C_OP_SetFloat";
    public ParticleEndCapState EndCapState { get; set; } = ParticleEndCapState.Any;
    public int OutputField { get; set; } = 0;
    public ParticleFloatInput InputValue { get; set; } = ParticleFloatInput.FromLiteral(0.0f);
    public ParticleSetMethod SetMethod { get; set; } = ParticleSetMethod.SetDirect;
}

public class SetVecOperatorDef : IParticleOperatorDef
{
    public string ClassName => "C_OP_SetVec";
    public ParticleEndCapState EndCapState { get; set; } = ParticleEndCapState.Any;
    public int OutputField { get; set; } = 0;
    public ParticleVecInput InputValue { get; set; } = ParticleVecInput.FromLiteral(Vector3.Zero);
}

public class RampScalarLinearSimpleOperatorDef : IParticleOperatorDef
{
    public string ClassName => "C_OP_RampScalarLinearSimple";
    public ParticleEndCapState EndCapState { get; set; } = ParticleEndCapState.EndCapOn;
    public float Rate { get; set; } = 0.0f;
    public float StartTime { get; set; } = 0.0f;
    public float EndTime { get; set; } = 999999.0f;
    public int Field { get; set; } = 3; // 3 = Radius, 10 = Trail Length
}

public class FadeOutSimpleOperatorDef : IParticleOperatorDef
{
    public string ClassName => "C_OP_FadeOutSimple";
    public ParticleEndCapState EndCapState { get; set; } = ParticleEndCapState.Any;
    public float FadeOutTime { get; set; } = 0.25f;
    public int Field { get; set; } = 7; // Alpha
}

public class FadeInSimpleOperatorDef : IParticleOperatorDef
{
    public string ClassName => "C_OP_FadeInSimple";
    public ParticleEndCapState EndCapState { get; set; } = ParticleEndCapState.Any;
    public float FadeInTime { get; set; } = 0.25f;
    public int Field { get; set; } = 7; // Alpha
}

public class SpinUpdateOperatorDef : IParticleOperatorDef
{
    public string ClassName => "C_OP_SpinUpdate";
    public ParticleEndCapState EndCapState { get; set; } = ParticleEndCapState.Any;
}

#endregion

#region Renderers

public class RenderTrailsDef : IParticleRendererDef
{
    public string ClassName => "C_OP_RenderTrails";
    public string BlendMode { get; set; } = "PARTICLE_OUTPUT_BLEND_MODE_ALPHA";
    public string PrimaryTexturePath { get; set; } = string.Empty;
    public float OverbrightFactor { get; set; } = 1.0f;
    public float LengthScale { get; set; } = 1.0f;
    public float MaxLength { get; set; } = 2000.0f; // inches
    public float MinLength { get; set; } = 0.0f;
    public float LengthFadeInTime { get; set; } = 0.0f;
    public ParticleFloatInput TailAlphaScale { get; set; } = ParticleFloatInput.FromLiteral(1.0f);
    public ParticleVecInput TailColorScale { get; set; } = ParticleVecInput.FromLiteral(Vector3.One);
    public bool IgnoreDT { get; set; } = false;
    public ParticleFloatInput RadiusScale { get; set; } = ParticleFloatInput.FromLiteral(1.0f);
    public float ConstrainRadiusToLengthRatio { get; set; } = 1.0f;
    public float AddSelfAmount { get; set; } = 0.0f;
    public float SelfIllumAmount { get; set; } = 0.0f;
    public Vector4 UVTransform { get; set; } = new(1f, 1f, 0f, 0f);
}

public class RenderSpritesDef : IParticleRendererDef
{
    public string ClassName => "C_OP_RenderSprites";
    public string BlendMode { get; set; } = "PARTICLE_OUTPUT_BLEND_MODE_ALPHA";
    public string PrimaryTexturePath { get; set; } = string.Empty;
    public float OverbrightFactor { get; set; } = 1.0f;
    public float AddSelfAmount { get; set; } = 0.0f;
    public float SelfIllumAmount { get; set; } = 0.0f;
    public ParticleFloatInput RadiusScale { get; set; } = ParticleFloatInput.FromLiteral(1.0f);
    public int ConstantSequenceNumber { get; set; } = 0;
    public bool HasConstantSequenceNumber { get; set; } = false;
    public Vector4 UVTransform { get; set; } = new(1f, 1f, 0f, 0f);
    public Vector4 UVRect { get; set; } = new(0f, 0f, 1f, 1f);
    public bool IsRefract { get; set; } = false;
    public float RefractAmount { get; set; } = 0.1f;
    public float RefractBlurRadius { get; set; } = 3.0f;
}

public class MaterialVarMapping
{
    public string VariableName { get; set; } = string.Empty;
    public int VariableField { get; set; } = 0; // Particle attribute slot
}

public class RenderModelsDef : IParticleRendererDef
{
    public string ClassName => "C_OP_RenderModels";
    public string BlendMode { get; set; } = "PARTICLE_OUTPUT_BLEND_MODE_ALPHA";
    public string PrimaryTexturePath { get; set; } = string.Empty;
    public string ModelPath { get; set; } = string.Empty;
    public string OverrideMaterialPath { get; set; } = string.Empty;
    public bool OrientZ { get; set; } = true;
    public List<MaterialVarMapping> MaterialVars { get; } = new();
}

public class RenderRopesDef : IParticleRendererDef
{
    public string ClassName => "C_OP_RenderRopes";
    public string BlendMode { get; set; } = "PARTICLE_OUTPUT_BLEND_MODE_ALPHA";
    public string PrimaryTexturePath { get; set; } = string.Empty;
    public float OverbrightFactor { get; set; } = 1.0f;
    public float AddSelfAmount { get; set; } = 0.0f;
    public float SelfIllumAmount { get; set; } = 0.0f;
    public ParticleFloatInput AlphaScale { get; set; } = ParticleFloatInput.FromLiteral(1.0f);
    public ParticleFloatInput RadiusScale { get; set; } = ParticleFloatInput.FromLiteral(1.0f);
    public Vector4 UVTransform { get; set; } = new(1f, 1f, 0f, 0f);
    public bool OnlyRenderInEffectsBloomPass { get; set; } = false;
    public int MinTesselation { get; set; } = 4;
    public int MaxTesselation { get; set; } = 4;
    public bool ClosedLoop { get; set; } = false;
    public bool UseScalarForTextureCoordinate { get; set; } = false;
    public int ScalarFieldForTextureCoordinate { get; set; } = 0;
}

#endregion

#region Hierarchy & Root System Definition

public class ChildParticleSystemRef
{
    public string ChildVpcfPath { get; set; } = string.Empty;
    public float Delay { get; set; } = 0.0f;
    public bool IsEndCap { get; set; } = false;
    public ParticleSystemDefinition ChildDefinition { get; set; }
}

public class ControlPointDriverDef
{
    public int ControlPointNumber { get; set; } = 0;
    public string AttachType { get; set; } = "PATTACH_WORLDORIGIN";
    public string AttachmentName { get; set; } = string.Empty;
    public Vector3 Offset { get; set; } = Vector3.Zero;
    public string EntityName { get; set; } = string.Empty;
}

/// <summary>
/// Root strongly typed representation of a Source 2 .vpcf particle definition.
/// </summary>
public class ParticleSystemDefinition
{
    public string VpcfPath { get; set; } = string.Empty;
    public int MaxParticles { get; set; } = 64;
    public float ConstantRadius { get; set; } = 1.0f;
    public float ConstantLifetime { get; set; } = 1.0f;
    public Color ConstantColor { get; set; } = Colors.White;
    public int ConstantSequenceNumber { get; set; } = 0;
    public bool HasConstantSequenceNumber { get; set; } = false;
    public int BehaviorVersion { get; set; } = 12;

    public List<ControlPointDriverDef> ControlPointDrivers { get; } = new();
    public List<IParticleEmitterDef> Emitters { get; } = new();
    public List<IParticleInitializerDef> Initializers { get; } = new();
    public List<IParticleOperatorDef> Operators { get; } = new();
    public List<IParticleRendererDef> Renderers { get; } = new();
    public List<ChildParticleSystemRef> Children { get; } = new();
}

#endregion
