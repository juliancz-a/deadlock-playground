shader_type spatial;
render_mode cull_front, unshaded;

uniform vec4 outline_color : source_color = vec4(0.0, 0.0, 0.0, 1.0);
uniform float outline_width : hint_range(0.0, 10.0, 0.1) = 0.5;
uniform bool has_painter_base_override = false;
uniform sampler2D painter_base_texture : hint_default_transparent, filter_linear, repeat_enable;
uniform float alpha_scissor_threshold = 0.0;

void vertex() {
	float dist = length(MODELVIEW_MATRIX[3].xyz);
	float scale = clamp(dist * 0.002, 0.001, 0.03);
	VERTEX += NORMAL * (outline_width * scale);
}

void fragment() {
	if (has_painter_base_override && alpha_scissor_threshold > 0.0) {
		if (texture(painter_base_texture, fract(UV)).a < alpha_scissor_threshold) {
			discard;
		}
	}
	ALBEDO = outline_color.rgb;
}
