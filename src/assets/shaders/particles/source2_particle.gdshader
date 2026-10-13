shader_type spatial;
render_mode blend_add, depth_draw_never, cull_disabled, unshaded;

uniform sampler2D albedo_texture : source_color, filter_linear_mipmap, hint_default_transparent;
uniform vec4 constant_color : source_color = vec4(1.0);
uniform float overbright_factor : hint_range(1.0, 32.0) = 1.0;

// Sub-rect UV bounds in normalized texture space (Min.x, Min.y, Max.x, Max.y)
uniform vec4 u_uv_rect = vec4(0.0, 0.0, 1.0, 1.0);

// Multi-sequence sprite sheet bounding rectangles (up to 32 sequences)
uniform vec4 u_seq_rects[32];
uniform int u_seq_count = 0;

// UV transform controls from m_TextureControls (ScaleU, ScaleV, OffsetU, OffsetV)
uniform vec4 u_uv_transform = vec4(1.0, 1.0, 0.0, 0.0);

uniform bool is_refract = false;

// Debug mode: 0=normal, 1=alpha (vec3(a)), 2=raw tex.rgb, 3=raw tex.a, 4=vtx color, 5=zero albedo (decisive test)
uniform int u_debug_mode = 0;

varying vec4 v_custom;

void vertex() {
    v_custom = INSTANCE_CUSTOM;
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
    // 1. Sprite card UV
    vec2 card_uv = UV;

    // 2. Active atlas sub-rectangle
    vec4 active_rect = u_uv_rect;
    if (u_seq_count > 0) {
        int seq_idx = clamp(int(v_custom.y + 0.5), 0, u_seq_count - 1);
        active_rect = u_seq_rects[seq_idx];
    }

    // 3. Map transformed card UV into active atlas sequence rect
    vec2 uv = place_corner(card_uv, u_uv_transform, active_rect.xy, active_rect.zw);

    vec4 tex = texture(albedo_texture, uv);

    float inst_overbright = (v_custom.w > 0.0) ? v_custom.w : overbright_factor;
    vec3 vtx_linear = srgb_to_linear(COLOR.rgb);
    vec3 const_linear = srgb_to_linear(constant_color.rgb);

    if (is_refract) {
        // Source 2 Refraction Shockwave (warp_noise10 normal map ripple in explode_warp).
        // Tangent-space normal map has flat unperturbed surface at (0.5, 0.5, 1.0).
        // Filter out DXT compression noise (disp < 0.06) and extract expanding ripple wave:
        vec2 disp = tex.rg - vec2(0.5);
        float ripple = smoothstep(0.06, 0.25, length(disp) * 2.5);
        float a = ripple * constant_color.a * COLOR.a;
        if (a <= 0.005) {
            discard;
        }
        vec3 rgb = const_linear * vtx_linear * inst_overbright;
        ALBEDO = rgb;
        ALPHA = a;
    } else {
        // Compute texture luminance (Rec. 601, matching Source 2 LUMA_601)
        float tex_luma = dot(tex.rgb, vec3(0.299, 0.587, 0.114));

        // For additive textures where alpha is constant 1.0 (e.g. DXT1 flares, lightning arcs with opaque alpha),
        // derive alpha from luminance so the black quad background is completely transparent and adds 0 light.
        float effective_alpha = tex.a;
        if (tex.a > 0.98 && tex_luma < 0.98) {
            effective_alpha = smoothstep(0.05, 0.30, tex_luma);
        }

        vec3 rgb = tex.rgb * const_linear * vtx_linear * inst_overbright;
        float a = effective_alpha * constant_color.a * COLOR.a;

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
            ALBEDO = COLOR.rgb;
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
}
