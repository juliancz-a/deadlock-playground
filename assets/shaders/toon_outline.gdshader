shader_type spatial;
render_mode cull_front, unshaded;

uniform vec4 outline_color : source_color = vec4(0.0, 0.0, 0.0, 1.0);
uniform float outline_width : hint_range(0.0, 10.0, 0.1) = 1.8;

void vertex() {
	float dist = length(MODELVIEW_MATRIX[3].xyz);
	float scale = clamp(dist * 0.002, 0.001, 0.03);
	VERTEX += NORMAL * (outline_width * scale);
}

void fragment() {
	ALBEDO = outline_color.rgb;
}
