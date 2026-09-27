shader_type canvas_item;
render_mode blend_mix;

uniform sampler2D TEXTURE : filter_nearest;

// Converts linear HDR half-float color from the GPU atlas composite buffer
// to sRGB for accurate presentation on the 2D canvas.
vec3 linear_to_srgb(vec3 c) {
    return mix(
        12.92 * c,
        1.055 * pow(clamp(c, 0.0, 1.0), vec3(1.0 / 2.4)) - 0.055,
        step(0.0031308, clamp(c, 0.0, 1.0))
    );
}

void fragment() {
    vec4 col = texture(TEXTURE, UV);
    // Discard transparent/empty pixels completely so they never affect or darken underlying base texture
    if (col.a <= 0.001) {
        discard;
    }
    COLOR = vec4(linear_to_srgb(col.rgb), col.a);
}
