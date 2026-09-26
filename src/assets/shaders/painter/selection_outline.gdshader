shader_type spatial;
render_mode unshaded, cull_front;

uniform bool is_mesh_hidden = false;
uniform vec4 outline_color : source_color = vec4(1.0, 0.85, 0.35, 0.4);
uniform float outline_width : hint_range(0.0, 10.0, 0.1) = 0.7;

void vertex() {
	if (!is_mesh_hidden) {
		float dist = length(MODELVIEW_MATRIX[3].xyz);
		float scale = clamp(dist * 0.0015, 0.0006, 0.015);
		VERTEX += NORMAL * (outline_width * scale);
	}
}

void fragment() {
	if (is_mesh_hidden) {
		discard;
	} else {
		ALBEDO = outline_color.rgb;
		ALPHA = outline_color.a;
	}
}
