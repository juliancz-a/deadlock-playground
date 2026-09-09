shader_type spatial;
render_mode cull_front, unshaded;

// Deadlock Inverted Hull Outline
// Scaled by camera distance to provide consistent outline thickness across hero meshes

uniform vec4 outline_color : source_color = vec4(0.08, 0.08, 0.08, 1.0);
uniform float outline_width : hint_range(0.1, 10.0, 0.1) = 1.5;

void vertex() {
	float dist = length(MODELVIEW_MATRIX[3].xyz);
	float scale = clamp(dist * 0.002, 0.001, 0.03);
	VERTEX += NORMAL * (outline_width * scale);
}

void fragment() {
	ALBEDO = outline_color.rgb;
}
