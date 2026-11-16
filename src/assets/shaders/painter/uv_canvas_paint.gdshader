shader_type canvas_item;
render_mode blend_mix;

uniform bool show_alpha_mask = false;
uniform bool use_live_layers = false;
uniform sampler2D below_texture : hint_default_transparent, filter_nearest, repeat_disable;
uniform sampler2D active_layer_texture : hint_default_transparent, filter_nearest, repeat_disable;
uniform sampler2D above_texture : hint_default_transparent, filter_nearest, repeat_disable;
uniform sampler2D base_atlas_texture : hint_default_transparent, filter_nearest, repeat_disable;
uniform bool active_layer_visible = true;
uniform float active_layer_opacity : hint_range(0.0, 1.0) = 1.0;
uniform int blend_mode = 0;
uniform bool clip_active_to_base_alpha = true;
uniform bool has_base_atlas_texture = false;

// Converts linear HDR half-float color from the GPU atlas composite buffer
// to sRGB for accurate presentation on the 2D canvas.
vec3 linear_to_srgb(vec3 c) {
    return mix(
        12.92 * c,
        1.055 * pow(clamp(c, 0.0, 1.0), vec3(1.0 / 2.4)) - 0.055,
        step(0.0031308, clamp(c, 0.0, 1.0))
    );
}

float get_luminance(vec3 c) {
    return dot(c, vec3(0.299, 0.587, 0.114));
}

vec3 set_luminance(vec3 c, float l) {
    float d = l - get_luminance(c);
    vec3 cr = c + vec3(d);
    float lum = get_luminance(cr);
    float min_c = min(cr.r, min(cr.g, cr.b));
    float max_c = max(cr.r, max(cr.g, cr.b));
    if (min_c < 0.0) {
        float denom = max(lum - min_c, 0.0001);
        cr = lum + ((cr - lum) * lum) / denom;
    }
    if (max_c > 1.0) {
        float denom = max(max_c - lum, 0.0001);
        cr = lum + ((cr - lum) * (1.0 - lum)) / denom;
    }
    return clamp(cr, vec3(0.0), vec3(1.0));
}

vec3 apply_blend_mode(vec3 base_col, vec3 src_col, int mode) {
    if (mode == 1) { // Multiply
        return base_col * src_col;
    } else if (mode == 2) { // Screen
        return vec3(1.0) - (vec3(1.0) - base_col) * (vec3(1.0) - src_col);
    } else if (mode == 3) { // Overlay
        vec3 less = 2.0 * base_col * src_col;
        vec3 more = vec3(1.0) - 2.0 * (vec3(1.0) - base_col) * (vec3(1.0) - src_col);
        return mix(more, less, step(base_col, vec3(0.5)));
    } else if (mode == 4) { // Darken
        return min(base_col, src_col);
    } else if (mode == 5) { // Lighten
        return max(base_col, src_col);
    } else if (mode == 6) { // ColorDodge
        return base_col / max(vec3(1.0) - src_col, vec3(0.001));
    } else if (mode == 7) { // Color (HSL)
        return set_luminance(src_col, get_luminance(base_col));
    }
    return src_col; // Normal (0)
}

void fragment() {
    vec4 col;
    if (use_live_layers) {
        vec4 paint = texture(below_texture, UV);
        vec4 base_sample = has_base_atlas_texture ? texture(base_atlas_texture, UV) : vec4(1.0);
        float base_alpha = has_base_atlas_texture ? clamp(base_sample.a, 0.0, 1.0) : 1.0;

        if (active_layer_visible) {
            vec4 active_paint = texture(active_layer_texture, UV);
            active_paint.a = clamp(active_paint.a * active_layer_opacity, 0.0, 1.0);
            if (clip_active_to_base_alpha && has_base_atlas_texture) {
                active_paint.a *= base_alpha;
            }

            if (active_paint.a > 0.0001) {
                vec3 blended_active = active_paint.rgb;
                if (blend_mode != 0 && (paint.a > 0.0001 || (has_base_atlas_texture && base_alpha > 0.001))) {
                    vec3 base_color = (paint.a > 0.0001)
                        ? mix(base_sample.rgb, paint.rgb, paint.a)
                        : base_sample.rgb;
                    blended_active = apply_blend_mode(base_color, active_paint.rgb, blend_mode);
                }

                if (paint.a > 0.0001) {
                    float out_a = clamp(active_paint.a + paint.a * (1.0 - active_paint.a), 0.0, 1.0);
                    vec3 out_rgb = (out_a > 0.0001)
                        ? (blended_active * active_paint.a + paint.rgb * paint.a * (1.0 - active_paint.a)) / out_a
                        : blended_active;
                    paint = vec4(out_rgb, out_a);
                } else {
                    paint = vec4(blended_active, active_paint.a);
                }
            }
        }

        vec4 above_paint = texture(above_texture, UV);
        if (above_paint.a > 0.0001) {
            if (paint.a > 0.0001) {
                float out_a = clamp(above_paint.a + paint.a * (1.0 - above_paint.a), 0.0, 1.0);
                vec3 out_rgb = (out_a > 0.0001)
                    ? (above_paint.rgb * above_paint.a + paint.rgb * paint.a * (1.0 - above_paint.a)) / out_a
                    : above_paint.rgb;
                paint = vec4(out_rgb, out_a);
            } else {
                paint = above_paint;
            }
        }

        col = paint;
    } else {
        col = texture(TEXTURE, UV);
    }

    if (show_alpha_mask) {
        COLOR = vec4(vec3(clamp(col.a, 0.0, 1.0)), clamp(col.a, 0.0, 1.0));
    } else {
        // Discard transparent/empty pixels completely so they never affect or darken underlying base texture
        if (col.a <= 0.001) {
            discard;
        }
        COLOR = vec4(linear_to_srgb(col.rgb), clamp(col.a, 0.0, 1.0));
    }
}
