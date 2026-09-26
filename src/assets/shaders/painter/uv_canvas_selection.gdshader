shader_type canvas_item;

uniform sampler2D selection_mask : hint_default_transparent, filter_linear, repeat_disable;
uniform vec2 atlas_size = vec2(2048.0, 2048.0);
uniform vec2 submesh_pos = vec2(0.0, 0.0);
uniform vec2 submesh_size = vec2(1.0, 1.0);
uniform bool has_submesh_rect = false;

void fragment() {
	vec2 uv = UV;

	if (uv.x < 0.0 || uv.y < 0.0 || uv.x > 1.0 || uv.y > 1.0) {
		discard;
	}

	float m_center = texture(selection_mask, uv).r;

	vec2 uv_dx = dFdx(uv);
	vec2 uv_dy = dFdy(uv);
	vec2 d_x = (length(uv_dx) > 1e-6) ? uv_dx : vec2(1.0 / atlas_size.x, 0.0);
	vec2 d_y = (length(uv_dy) > 1e-6) ? uv_dy : vec2(0.0, 1.0 / atlas_size.y);

	float m_r = texture(selection_mask, uv + d_x).r;
	float m_l = texture(selection_mask, uv - d_x).r;
	float m_u = texture(selection_mask, uv + d_y).r;
	float m_d = texture(selection_mask, uv - d_y).r;

	bool is_edge = (m_center >= 0.5) && (m_r < 0.5 || m_l < 0.5 || m_u < 0.5 || m_d < 0.5);

	if (is_edge) {
		// Animated marching ants outline (alternating black & white dashes matching 3D viewport)
		float ant = sin((FRAGCOORD.x + FRAGCOORD.y + TIME * 16.0) * 0.4);
		vec3 ant_color = (ant > 0.0) ? vec3(1.0, 1.0, 1.0) : vec3(0.05, 0.05, 0.05);
		COLOR = vec4(ant_color, 1.0);
	} else if (m_center < 0.5) {
		// UNSELECTED area: protective diagonal stencil hatch (matching 3D viewport)
		bool in_submesh = true;
		if (has_submesh_rect) {
			in_submesh = (uv.x >= submesh_pos.x && uv.x <= submesh_pos.x + submesh_size.x &&
			              uv.y >= submesh_pos.y && uv.y <= submesh_pos.y + submesh_size.y);
		}
		if (in_submesh) {
			float stripe = sin((FRAGCOORD.x - FRAGCOORD.y) * 0.25);
			vec3 hatch_col = (stripe > 0.0) ? vec3(0.06, 0.06, 0.10) : vec3(0.01, 0.01, 0.03);
			COLOR = vec4(hatch_col, 0.45);
		} else {
			COLOR = vec4(0.0);
		}
	} else {
		// SELECTED area: 100% pristine, clear and untinted!
		COLOR = vec4(0.0);
	}
}
