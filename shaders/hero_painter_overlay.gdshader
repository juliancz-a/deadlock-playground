shader_type spatial;
render_mode unshaded, blend_mix, depth_draw_opaque, cull_back;

uniform sampler2D overlay_texture : hint_default_transparent, filter_linear, repeat_disable;
uniform sampler2D g_tColor : source_color, filter_linear_mipmap, hint_default_white; // Base diffuse/AO
uniform sampler2D selection_mask : hint_default_transparent, filter_linear, repeat_disable;
uniform vec2 position_in_atlas;
uniform vec2 size_in_atlas;
uniform uint atlas_index;
uniform float layer_opacity : hint_range(0.0, 1.0) = 1.0;
uniform bool is_paint_target = true;
uniform bool show_selection_mask = false;

varying vec2 v_atlas_uv;
varying vec2 v_mesh_uv;

void vertex() {
	// Source 2 models in Deadlock often lack a dedicated UV2 stream.
	// Fall back to primary UV coordinates so each submesh properly samples its allocated rect in the atlas.
	v_mesh_uv = (length(UV2) > 0.0001) ? UV2 : UV;
	v_atlas_uv = v_mesh_uv * size_in_atlas + position_in_atlas;
}

void fragment() {
	// Layer 21 (bit index 20) is used by camera brush to capture UVs and depths
	bool is_camera_brush = (CAMERA_VISIBLE_LAYERS & (1u << uint(20))) != 0u;
	if (is_camera_brush) {
		// Isolate painting strictly to the active target submesh/mesh
		if (!is_paint_target) {
			discard;
		}
		// Reject backfaces or grazing angles (> 89 degrees relative to brush camera)
		if (NORMAL.z <= 0.02) {
			discard;
		}
		uint exponential_bit = 1u << 30u;
		float distance = -VERTEX.z;
		uint distance_bits = (packHalf2x16(vec2(distance, 0)) & 0xFFE0u) << uint(13 - 5);
		uint index_sign_bit = (atlas_index & 0x4u) << uint(31 - 2);
		uint index_exp_bits = (atlas_index & 0x3u) << 24u;
		float b = uintBitsToFloat(exponential_bit | distance_bits | index_sign_bit | index_exp_bits);
		ALBEDO = vec3(v_atlas_uv - vec2(0.5), b);
		SPECULAR = 0.0;
		EMISSION = vec3(v_atlas_uv - vec2(0.5), b);
	} else {
		// Clamp atlas UV within submesh rect with half-texel inset to prevent sampling neighbors
		vec2 half_texel = vec2(0.5) / vec2(textureSize(overlay_texture, 0));
		vec2 min_atlas = position_in_atlas + half_texel;
		vec2 max_atlas = position_in_atlas + size_in_atlas - half_texel;
		vec2 clamped_uv = clamp(v_atlas_uv, min_atlas, max_atlas);

		vec4 paint = texture(overlay_texture, clamped_uv);

		vec3 final_color = paint.rgb;
		float final_alpha = paint.a;

		if (show_selection_mask) {
			float mask_val = texture(selection_mask, clamped_uv).r;

			ivec2 mask_size = textureSize(selection_mask, 0);
			if (mask_size.x > 1) {
				// Screen-space derivatives ensure the boundary outline is razor-sharp and strictly 1 screen pixel wide
				vec2 uv_dx = dFdx(clamped_uv);
				vec2 uv_dy = dFdy(clamped_uv);
				vec2 d_x = (length(uv_dx) > 1e-6) ? uv_dx : vec2(1.0 / float(mask_size.x), 0.0);
				vec2 d_y = (length(uv_dy) > 1e-6) ? uv_dy : vec2(0.0, 1.0 / float(mask_size.y));

				float is_sel = step(0.5, mask_val);
				float sel_right = step(0.5, texture(selection_mask, clamped_uv + d_x).r);
				float sel_left  = step(0.5, texture(selection_mask, clamped_uv - d_x).r);
				float sel_down  = step(0.5, texture(selection_mask, clamped_uv + d_y).r);
				float sel_up    = step(0.5, texture(selection_mask, clamped_uv - d_y).r);

				bool is_edge = (is_sel > 0.5) && (sel_right < 0.5 || sel_left < 0.5 || sel_down < 0.5 || sel_up < 0.5);

				if (is_edge) {
					// Razor-thin 1-pixel animated marching ants boundary outline
					float ant = sin((FRAGCOORD.x + FRAGCOORD.y + TIME * 16.0) * 0.4);
					vec3 ant_color = (ant > 0.0) ? vec3(1.0, 1.0, 1.0) : vec3(0.05, 0.05, 0.05);
					final_color = ant_color;
					final_alpha = 1.0;
				} else if (is_sel < 0.5) {
					// Inverted: UNSELECTED / UNPAINTABLE area has protective stencil overlay
					// Subtle dark protective tint with subtle diagonal hatch
					float stripe = sin((FRAGCOORD.x - FRAGCOORD.y) * 0.25);
					vec3 hatch_col = (stripe > 0.0) ? vec3(0.06, 0.06, 0.10) : vec3(0.01, 0.01, 0.03);
					float protect_alpha = 0.45;
					final_color = mix(final_color, hatch_col, protect_alpha);
					final_alpha = max(final_alpha, protect_alpha);
				}
				// If is_sel >= 0.5: Paintable area is 100% pristine, clear and untinted!
			}
		}

		if (final_alpha <= 0.0001) {
			discard;
		}

		ALBEDO = final_color;
		ALPHA = final_alpha;
	}
}
