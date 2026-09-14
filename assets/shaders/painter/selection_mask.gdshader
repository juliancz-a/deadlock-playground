shader_type spatial;
render_mode unshaded, cull_back;

uniform bool is_mesh_hidden = false;
uniform vec4 outline_color : source_color = vec4(1.0, 0.85, 0.35, 0.4);

void fragment() {
	if (!is_mesh_hidden) {
		discard;
	}

	float rim = 1.0 - clamp(dot(NORMAL, VIEW), 0.0, 1.0);
	float edge = smoothstep(0.68, 0.92, rim);
	if (edge <= 0.02) {
		discard;
	}
	ALBEDO = outline_color.rgb;
	ALPHA = edge * outline_color.a;
}
