shader_type canvas_item;
render_mode blend_mix;

// Base hero color map texture (e.g. _color.vtex).
// In Valve's Source 2, the alpha channel contains packed shader masks (tint, cavity AO, or fabric specularity)
// rather than transparency. This canvas shader forces opaque RGB evaluation so low-alpha areas are never crushed.
uniform sampler2D texture_base : hint_default_transparent, filter_linear_mipmap;

vec3 linear_to_srgb(vec3 c) {
    return mix(
        12.92 * c,
        1.055 * pow(clamp(c, 0.0, 1.0), vec3(1.0 / 2.4)) - 0.055,
        step(0.0031308, clamp(c, 0.0, 1.0))
    );
}

void fragment() {
    vec4 base_sample = texture(TEXTURE, UV);
    // If TEXTURE is not set or empty, check texture_base uniform fallback
    if (base_sample.a <= 0.001) {
        vec4 alt = texture(texture_base, UV);
        if (alt.a > 0.001) {
            base_sample = alt;
        }
    }

    vec3 rgb = base_sample.rgb;
    // Unpremultiply / unscale if color was scaled by alpha in texture
    if (base_sample.a > 0.001 && base_sample.a < 0.999) {
        rgb = rgb / max(0.01, base_sample.a);
    }

    // Convert from linear HDR half-float to sRGB for accurate 2D canvas presentation
    COLOR = vec4(linear_to_srgb(rgb), 1.0);
}
