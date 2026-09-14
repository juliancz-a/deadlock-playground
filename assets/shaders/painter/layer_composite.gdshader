shader_type canvas_item;
render_mode blend_mix;

uniform sampler2D screen_texture : hint_screen_texture, filter_linear_mipmap;
uniform int u_blend_mode = 0; // 0: Normal, 1: Multiply, 2: Screen, 3: Overlay
uniform float u_layer_opacity : hint_range(0.0, 1.0) = 1.0;

void fragment() {
    vec4 top = texture(TEXTURE, UV);
    vec4 bottom = textureLod(screen_texture, SCREEN_UV, 0.0);

    vec3 blended = top.rgb;
    if (u_blend_mode == 1) { // Multiply
        blended = bottom.rgb * top.rgb;
    } else if (u_blend_mode == 2) { // Screen / Add
        blended = 1.0 - (1.0 - bottom.rgb) * (1.0 - top.rgb);
    } else if (u_blend_mode == 3) { // Overlay
        blended = vec3(
            bottom.r < 0.5 ? (2.0 * bottom.r * top.r) : (1.0 - 2.0 * (1.0 - bottom.r) * (1.0 - top.r)),
            bottom.g < 0.5 ? (2.0 * bottom.g * top.g) : (1.0 - 2.0 * (1.0 - bottom.g) * (1.0 - top.g)),
            bottom.b < 0.5 ? (2.0 * bottom.b * top.b) : (1.0 - 2.0 * (1.0 - bottom.b) * (1.0 - top.b))
        );
    }

    float effective_alpha = top.a * u_layer_opacity;
    COLOR = vec4(mix(bottom.rgb, blended, effective_alpha), max(bottom.a, effective_alpha));
}
