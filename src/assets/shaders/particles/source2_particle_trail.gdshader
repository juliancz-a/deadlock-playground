shader_type spatial;
render_mode blend_add, depth_draw_never, cull_disabled, unshaded;

uniform sampler2D albedo_texture : source_color, filter_linear_mipmap, hint_default_transparent;
uniform vec4 constant_color : source_color = vec4(1.0);
uniform float overbright_factor : hint_range(1.0, 32.0) = 2.0;
uniform float tail_alpha_scale : hint_range(0.0, 1.0) = 0.0;
uniform vec3 tail_color_scale : source_color = vec3(0.0, 0.0, 1.0);

// Sub-rect UV bounds in normalized texture space (Min.x, Min.y, Max.x, Max.y)
// Default fallback when no sprite sheet sequence table is provided
uniform vec4 u_uv_rect = vec4(0.0, 0.0, 1.0, 1.0);

// Multi-sequence sprite sheet bounding rectangles (up to 32 sequences)
uniform vec4 u_seq_rects[32];
uniform int u_seq_count = 0;

// UV transform controls from m_TextureControls (ScaleU, ScaleV, OffsetU, OffsetV)
uniform vec4 u_uv_transform = vec4(1.0, -1.0, 0.0, 1.0);

// Debug mode: 0=normal, 1=alpha (vec3(a)), 2=raw tex.rgb, 3=raw tex.a, 4=vtx color, 5=zero albedo (decisive test)
uniform int u_debug_mode = 0;

varying vec4 v_custom;
varying float v_ribbon_t;

void vertex() {
    v_custom = INSTANCE_CUSTOM;
    // On Godot QuadMesh with CenterOffset Y=0.5:
    // VERTEX.y = 0.0 at base (head), VERTEX.y = 1.0 at tip (tail)
    v_ribbon_t = clamp(VERTEX.y, 0.0, 1.0);
}

// Places one card coordinate into a layer's texture, matching Source 2 / VRF PlaceCorner
vec2 place_corner(vec2 card_uv, vec4 scale_offset, vec2 rect_min, vec2 rect_max) {
    vec2 centered = card_uv - vec2(0.5);

    // In Source 2 / VRF: placed = (rotated / scale) + wrapped. SCALE IS A DIVIDE!
    vec2 scale = scale_offset.xy;
    if (abs(scale.x) < 1e-5) scale.x = 1.0;
    if (abs(scale.y) < 1e-5) scale.y = 1.0;

    vec2 origin = scale_offset.zw + vec2(0.5);
    vec2 wrapped = origin - floor(origin);

    vec2 placed = (centered / scale) + wrapped;
    placed = clamp(placed, 0.0, 1.0);

    return mix(rect_min, rect_max, placed);
}

// Converts authored sRGB vertex color to linear space (Fix 5.1, matching VRF SrgbGammaToLinear)
vec3 srgb_to_linear(vec3 c) {
    return mix(
        c / 12.92,
        pow((c + vec3(0.055)) / 1.055, vec3(2.4)),
        step(vec3(0.04045), c)
    );
}

void fragment() {
    // 1. Trail path card UV: v along ribbon from center tail (0.0) to outward tip head (1.0).
    // Inverted so texture peak alpha (row 120, narrow 13px point) aligns with the outward needle tip,
    // and soft fanning base (rows 0-48) aligns with the center socket.
    vec2 card_uv = vec2(UV.x, 1.0 - v_ribbon_t);

    // 2. Active atlas sub-rectangle
    vec4 active_rect = u_uv_rect;
    if (u_seq_count > 0) {
        int seq_idx = clamp(int(v_custom.y + 0.5), 0, u_seq_count - 1);
        active_rect = u_seq_rects[seq_idx];
    }

    // 3. Map transformed card UV into active atlas sequence rect
    vec2 uv = place_corner(card_uv, u_uv_transform, active_rect.xy, active_rect.zw);

    vec4 tex = texture(albedo_texture, uv);

    // 4. Trail ribbon length gradient:
    // At center (v_ribbon_t = 0.0): alpha scales by tail_alpha_scale (mostly translucent, soft center).
    // At tip (v_ribbon_t = 1.0): alpha is 1.0 (sharp solid needles), color shifts to pure tail_color_scale (saturated blue).
    vec3 vtx_linear = srgb_to_linear(COLOR.rgb);
    vec3 ribbon_color = mix(vtx_linear, vtx_linear * tail_color_scale, v_ribbon_t);
    float ribbon_alpha = mix(COLOR.a * tail_alpha_scale, COLOR.a, v_ribbon_t);

    float inst_overbright = (v_custom.w > 0.0) ? v_custom.w : overbright_factor;

    // Strict Ground-Truth Output formula:
    // In Godot 4 Forward+, setting dynamic ALPHA < 1.0 routes the material to the Transparent Pass.
    // In the transparent pass with render_mode blend_add, hardware blending uses SRC_ALPHA, ONE:
    // Framebuffer += ALBEDO * ALPHA.
    // Discarding fragments where a <= 0.005 guarantees zero background occlusion.
    vec3 rgb = tex.rgb * ribbon_color * inst_overbright;
    float a = tex.a * ribbon_alpha;

    if (u_debug_mode == 1) {
        ALBEDO = vec3(a);
        ALPHA = 1.0;
    } else if (u_debug_mode == 2) {
        ALBEDO = tex.rgb;
        ALPHA = 1.0;
    } else if (u_debug_mode == 3) {
        ALBEDO = vec3(tex.a);
        ALPHA = 1.0;
    } else if (u_debug_mode == 4) {
        ALBEDO = ribbon_color;
        ALPHA = 1.0;
    } else if (u_debug_mode == 5) {
        discard;
    } else {
        if (a <= 0.005) {
            discard;
        }
        ALBEDO = rgb;
        ALPHA = a;
    }
}
