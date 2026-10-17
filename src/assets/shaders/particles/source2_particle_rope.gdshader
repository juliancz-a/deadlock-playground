shader_type spatial;
render_mode blend_mix, depth_draw_never, cull_disabled, unshaded;

uniform sampler2D albedo_texture : source_color, filter_linear_mipmap, repeat_enable, hint_default_transparent;
uniform vec4 constant_color : source_color = vec4(1.0);
uniform float overbright_factor : hint_range(0.1, 32.0) = 1.0;
uniform float alpha_scale : hint_range(0.0, 4.0) = 1.0;

// UV transform controls from m_TextureControls (ScaleU, ScaleV, OffsetU, OffsetV)
uniform vec4 u_uv_transform = vec4(1.0, 1.0, 0.0, 0.0);

// Dynamic animated V offset (e.g. from collection age)
uniform float u_animated_v_offset = 0.0;

// Places card UV coordinates into texture space, matching Source 2 / VRF PlaceCorner
// Uses continuous wrapped offset without fract() on V to prevent mipmap seam derivative spikes
vec2 place_corner(vec2 card_uv, vec4 scale_offset, float anim_offset_v) {
    vec2 centered = card_uv - vec2(0.5);

    vec2 scale = scale_offset.xy;
    if (abs(scale.x) < 1e-5) scale.x = 1.0;
    if (abs(scale.y) < 1e-5) scale.y = 1.0;

    vec2 origin = scale_offset.zw + vec2(0.0, anim_offset_v) + vec2(0.5);
    vec2 wrapped = origin - floor(origin);
    vec2 placed = (centered / scale) + wrapped;
    placed.x = clamp(placed.x, 0.0, 1.0);

    return placed;
}

// Converts authored sRGB vertex color to linear space
vec3 srgb_to_linear(vec3 c) {
    return mix(
        c / 12.92,
        pow((c + vec3(0.055)) / 1.055, vec3(2.4)),
        step(vec3(0.04045), c)
    );
}

void fragment() {
    vec2 uv = place_corner(UV, u_uv_transform, u_animated_v_offset);
    vec4 tex = texture(albedo_texture, uv);

    vec3 vtx_linear = srgb_to_linear(COLOR.rgb);
    vec3 const_linear = srgb_to_linear(constant_color.rgb);

    vec3 rgb = tex.rgb * const_linear * vtx_linear * overbright_factor;
    float a = tex.a * constant_color.a * COLOR.a * alpha_scale;

    if (a <= 0.005) {
        discard;
    }

    ALBEDO = rgb;
    ALPHA = a;
}
