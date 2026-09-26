shader_type spatial;
render_mode blend_mix, depth_draw_opaque, cull_disabled;

uniform sampler2D g_tColor : source_color, filter_linear_mipmap;
uniform sampler2D g_tSelfIllumMask : hint_default_black, filter_linear_mipmap;
uniform sampler2D g_tNormalRoughness : hint_normal, filter_linear_mipmap;

// Tint multiplier for card glow (default vec4(1.0) preserves the authentic lilac color authored in g_tColor)
uniform vec4 g_vSelfIllumTint : source_color = vec4(1.0, 1.0, 1.0, 1.0);
// Balanced emission energy (default 2.0: soft, radiant psychic glow without blowing out)
uniform float g_flSelfIllumScale : hint_range(0.0, 10.0) = 2.0;
// Soft, subtle halo opacity for the exterior psychic envelope (0.10)
uniform float g_flHaloAlpha : hint_range(0.0, 1.0) = 0.10;
// In wraith_cards_color, card bodies occupy V in [0.0, 0.79].
// The exterior aura halo envelope geometry strictly occupies the bottom purple strip V in [0.85, 1.0].
// Threshold 0.82 ensures cards (including the hat card) are 100% solid, while only the outer halo is translucent.
uniform float g_flHaloVThreshold : hint_range(0.0, 1.0) = 0.82;
uniform bool invert_v = false;

void fragment() {
    vec4 baseTex = texture(g_tColor, UV);
    float maskR = texture(g_tSelfIllumMask, UV).r;

    // 1. Pristine diffuse albedo: card paper and artwork from the texture atlas
    ALBEDO = baseTex.rgb;

    // 2. Selective opacity: Cards are solid (UV.y <= 0.82), outer halo envelope is translucent (UV.y > 0.82)
    bool isHaloMesh = invert_v ? (UV.y < (1.0 - g_flHaloVThreshold)) : (UV.y > g_flHaloVThreshold);
    ALPHA = isHaloMesh ? g_flHaloAlpha : 1.0;

    // 3. Emission: Uses the authentic texture color (bottom strip aura & card glyphs) modulated by mask and scale
    EMISSION = baseTex.rgb * g_vSelfIllumTint.rgb * maskR * g_flSelfIllumScale;

    // 4. Roughness from normal texture Blue channel, smooth mesh normals (no tangent artifacts on curved hat card)
    vec4 normSample = texture(g_tNormalRoughness, UV);
    ROUGHNESS = mix(0.7, normSample.b, 0.5);
    METALLIC = 0.0;
}
