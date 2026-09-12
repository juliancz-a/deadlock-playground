using System;
using Godot;

namespace DeadlockPlayground.Materials;

/// <summary>
/// Central registry and cache for Valve Source 2 custom shaders.
/// Handles resolution from valve subfolder or assets root.
/// </summary>
public static class Source2ShaderRegistry
{
    private static Shader _heroOutlineShader;
    private static Shader _vertColorPbrShader;
    private static Shader _flameHairShader;
    private static Shader _dynamicFxShader;
    private static Shader _dynamicFxAddShader;
    private static Shader _dynamicJitterShader;
    private static Shader _dynamicGlowShader;
    private static Shader _glassShader;
    private static Shader _cardsShader;
    private static Shader _lashSparklesShader;
    private static Shader _viscousShader;
    private static Shader _ladyGeistShader;
    private static Shader _vindictaShader;

    public static Shader LoadValveShader(string shaderName)
    {
        string valvePath = $"res://assets/shaders/valve/{shaderName}";
        if (ResourceLoader.Exists(valvePath)) return GD.Load<Shader>(valvePath);
        string fallbackPath = $"res://assets/shaders/{shaderName}";
        if (ResourceLoader.Exists(fallbackPath)) return GD.Load<Shader>(fallbackPath);
        return null;
    }

    public static Shader GetHeroOutlineShader()
    {
        _heroOutlineShader ??= LoadValveShader("source2_hero_outline.gdshader");
        return _heroOutlineShader;
    }

    /// <summary>
    /// Shader for vertex-colored head/hair surfaces (Wraith head, Mirage turban/hair, etc.)
    /// </summary>
    public static Shader GetVertexColorPbrShader()
    {
        _vertColorPbrShader ??= LoadValveShader("source2_vertcolor_pbr.gdshader") 
                             ?? LoadValveShader("source2_pbr.gdshader");
        return _vertColorPbrShader;
    }

    /// <summary>
    /// Backward-compatible alias for GetVertexColorPbrShader.
    /// </summary>
    public static Shader GetPbrHeadShader() => GetVertexColorPbrShader();

    public static Shader GetFlameHairShader()
    {
        _flameHairShader ??= LoadValveShader("source2_flame_hair.gdshader");
        return _flameHairShader ?? GetDynamicFxShader(false);
    }

    public static Shader GetDynamicFxShader(bool isAdditive)
    {
        if (isAdditive)
        {
            _dynamicFxAddShader ??= LoadValveShader("source2_dynamic_fx_add.gdshader");
            if (_dynamicFxAddShader != null) return _dynamicFxAddShader;
        }

        _dynamicFxShader ??= LoadValveShader("source2_dynamic_fx.gdshader");
        return _dynamicFxShader;
    }

    public static Shader GetDynamicJitterShader()
    {
        _dynamicJitterShader ??= LoadValveShader("source2_dynamic_jitter.gdshader");
        return _dynamicJitterShader ?? GetDynamicFxShader(false);
    }

    public static Shader GetDynamicGlowShader()
    {
        _dynamicGlowShader ??= LoadValveShader("source2_dynamic_glow.gdshader");
        return _dynamicGlowShader;
    }

    public static Shader GetGlassShader()
    {
        _glassShader ??= LoadValveShader("source2_glass.gdshader");
        return _glassShader;
    }

    public static Shader GetCardsShader()
    {
        _cardsShader ??= LoadValveShader("source2_cards.gdshader") ?? LoadValveShader("source2_wraith_card.gdshader");
        return _cardsShader;
    }

    public static Shader GetWraithCardShader() => GetCardsShader();

    public static Shader GetLashSparklesShader()
    {
        _lashSparklesShader ??= LoadValveShader("lash_sparkles.gdshader");
        return _lashSparklesShader;
    }

    public static Shader GetViscousShader()
    {
        _viscousShader ??= LoadValveShader("viscous.gdshader");
        return _viscousShader;
    }

    public static Shader GetLadyGeistShader()
    {
        _ladyGeistShader ??= LoadValveShader("lady_geist.gdshader");
        return _ladyGeistShader;
    }

    public static Shader GetVindictaShader()
    {
        _vindictaShader ??= LoadValveShader("vindicta.gdshader");
        return _vindictaShader;
    }
}
