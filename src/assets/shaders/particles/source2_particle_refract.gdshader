shader_type spatial;
render_mode unshaded, depth_draw_never, cull_disabled;

uniform sampler2D albedo_texture : filter_linear_mipmap, hint_default_transparent;
uniform sampler2D screen_tex : hint_screen_texture, repeat_disable, filter_linear_mipmap;
uniform float refract_amount = 0.1;
uniform float blur_lod : hint_range(0.0, 5.0) = 1.5;

// Sub-rect UV bounds in normalized texture space (Min.x, Min.y, Max.x, Max.y)
uniform vec4 u_uv_rect = vec4(0.0, 0.0, 1.0, 1.0);

// Multi-sequence sprite sheet bounding rectangles (up to 32 sequences)
uniform vec4 u_seq_rects[32];
uniform int u_seq_count = 0;

// UV transform controls from m_TextureControls (ScaleU, ScaleV, OffsetU, OffsetV)
uniform vec4 u_uv_transform = vec4(1.0, 1.0, 0.0, 0.0);

varying vec4 v_custom;

void vertex() {
    v_custom = INSTANCE_CUSTOM;
}

vec2 place_corner(vec2 card_uv, vec4 scale_offset, vec2 rect_min, vec2 rect_max) {
    vec2 centered = card_uv - vec2(0.5);
    vec2 scale = scale_offset.xy;
    if (abs(scale.x) < 1e-5) scale.x = 1.0;
    if (abs(scale.y) < 1e-5) scale.y = 1.0;

    vec2 origin = scale_offset.zw + vec2(0.5);
    vec2 wrapped = origin - floor(origin);

    vec2 placed = (centered / scale) + wrapped;
    placed = clamp(placed, 0.0, 1.0);

    return mix(rect_min, rect_max, placed);
}

void fragment() {
    vec2 card_uv = UV;

    vec4 active_rect = u_uv_rect;
    if (u_seq_count > 0) {
        int seq_idx = clamp(int(v_custom.y + 0.5), 0, u_seq_count - 1);
        active_rect = u_seq_rects[seq_idx];
    }

    vec2 uv = place_corner(card_uv, u_uv_transform, active_rect.xy, active_rect.zw);
    vec4 tex = texture(albedo_texture, uv);

    // Circular size falloff so quad corners never show as square edges
    float dist_from_center = length(UV - vec2(0.5)) * 2.0;
    float size_falloff = clamp(1.0 - dist_from_center, 0.0, 1.0);
    size_falloff = smoothstep(0.0, 1.0, size_falloff);

    // Screen-space displacement offset derived from distortion normal map (rg in [-1, 1])
    vec2 disp = (tex.rg * 2.0 - 1.0);
    vec2 offset = disp * refract_amount * tex.a * COLOR.a * size_falloff;

    vec2 screen_uv = SCREEN_UV + offset;
    ALBEDO = textureLod(screen_tex, screen_uv, blur_lod).rgb;

    // Fade alpha with particle alpha (COLOR.a) and distortion magnitude
    float disp_mag = length(disp);
    float warp_alpha = clamp(disp_mag * 2.0, 0.0, 1.0) * tex.a * COLOR.a * size_falloff;

    if (warp_alpha <= 0.002) {
        discard;
    }
    ALPHA = warp_alpha;
}
