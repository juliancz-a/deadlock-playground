shader_type canvas_item;
render_mode blend_mix;

uniform vec4 u_brush_color : source_color = vec4(1.0, 1.0, 1.0, 1.0);
uniform float u_hardness : hint_range(0.0, 1.0) = 0.5;
uniform float u_flow : hint_range(0.0, 1.0) = 1.0;
uniform sampler2D u_brush_mask : hint_default_white;

void fragment() {
    vec2 center = UV - vec2(0.5);
    float dist = length(center) * 2.0;
    if (dist > 1.0) {
        discard;
    }

    float edge0 = clamp(u_hardness, 0.0, 0.999);
    float falloff = 1.0 - smoothstep(edge0, 1.0, dist);

    vec4 mask_val = texture(u_brush_mask, UV);
    float alpha = falloff * mask_val.a * mask_val.r * u_flow * u_brush_color.a;
    COLOR = vec4(u_brush_color.rgb, alpha);
}
