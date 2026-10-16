shader_type spatial;
render_mode unshaded, cull_front;

uniform bool is_mesh_hidden = false;
uniform vec4 outline_color : source_color = vec4(1.0, 0.85, 0.35, 0.85);
uniform float outline_width : hint_range(0.0, 10.0, 0.1) = 0.7;

void vertex() {
	if (!is_mesh_hidden) {
		float dist = length(MODELVIEW_MATRIX[3].xyz);
		// Tighten world expansion scale so silhouette stays crisp without inflating or ballooning thin cloth/fingers
		float scale = clamp(dist * 0.00045, 0.0002, 0.0028);
		VERTEX += NORMAL * (outline_width * scale);
	}
}

void fragment() {
	if (is_mesh_hidden) {
		discard;
	} else {
		// Only render silhouette edge backfaces; discard interior backfaces that pop through thin cloth (coats, skirts, hair)
		float view_dot = dot(NORMAL, VIEW);
		if (abs(view_dot) > 0.45) {
			discard;
		}
		ALBEDO = outline_color.rgb;
		ALPHA = outline_color.a;
	}
}

