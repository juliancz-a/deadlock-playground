shader_type spatial;
render_mode unshaded, blend_mix, depth_draw_opaque, cull_disabled;

uniform sampler2D overlay_texture : hint_default_transparent, filter_linear_mipmap_anisotropic, repeat_disable;
uniform sampler2D active_layer_texture : hint_default_transparent, filter_linear_mipmap_anisotropic, repeat_disable;
uniform sampler2D g_tColor : source_color, filter_linear_mipmap, hint_default_white; // Base diffuse/AO
uniform sampler2D selection_mask : hint_default_transparent, filter_linear, repeat_disable;
uniform vec2 position_in_atlas;
uniform vec2 size_in_atlas;
uniform uint atlas_index;
uniform float layer_opacity : hint_range(0.0, 1.0) = 1.0;
uniform bool active_layer_visible = true;
uniform float active_layer_opacity : hint_range(0.0, 1.0) = 1.0;
uniform bool front_faces_only = true;
uniform bool is_paint_target = true;
uniform bool show_selection_mask = false;
uniform bool show_stencil_pattern = true;

uniform bool smooth_bicubic = false;

varying vec2 v_atlas_uv;
varying vec2 v_mesh_uv;

// High-fidelity Cubic B-spline smooth texture filtering (strictly non-negative weights, zero ringing or dark outlines)
vec4 sample_bicubic_smooth(sampler2D tex, vec2 uv, vec2 min_bounds, vec2 max_bounds) {
	ivec2 itex_size = textureSize(tex, 0);
	if (itex_size.x <= 2 || itex_size.y <= 2) {
		return texture(tex, clamp(uv, min_bounds, max_bounds));
	}
	vec2 tex_size = vec2(itex_size);
	vec2 inv_size = 1.0 / tex_size;
	vec2 tc = uv * tex_size - 0.5;
	vec2 f = fract(tc);
	vec2 f2 = f * f;
	vec2 f3 = f2 * f;

	// Cubic B-spline basis weights (strictly non-negative, sum to 1.0, monotonic continuous transitions)
	vec2 w0 = (-f3 + 3.0 * f2 - 3.0 * f + 1.0) / 6.0;
	vec2 w1 = ( 3.0 * f3 - 6.0 * f2 + 4.0) / 6.0;
	vec2 w2 = (-3.0 * f3 + 3.0 * f2 + 3.0 * f + 1.0) / 6.0;
	vec2 w3 = f3 / 6.0;

	vec2 s0 = w0 + w1;
	vec2 s1 = w2 + w3;

	vec2 f0 = w1 / max(s0, vec2(1e-5));
	vec2 f1 = w3 / max(s1, vec2(1e-5));

	vec2 t0 = (floor(tc) - 0.5 + f0) * inv_size;
	vec2 t1 = (floor(tc) + 1.5 + f1) * inv_size;

	vec2 c0 = clamp(vec2(t0.x, t0.y), min_bounds, max_bounds);
	vec2 c1 = clamp(vec2(t1.x, t0.y), min_bounds, max_bounds);
	vec2 c2 = clamp(vec2(t0.x, t1.y), min_bounds, max_bounds);
	vec2 c3 = clamp(vec2(t1.x, t1.y), min_bounds, max_bounds);

	vec4 s00 = texture(tex, c0);
	vec4 s10 = texture(tex, c1);
	vec4 s01 = texture(tex, c2);
	vec4 s11 = texture(tex, c3);

	float w_00 = s0.x * s0.y;
	float w_10 = s1.x * s0.y;
	float w_01 = s0.x * s1.y;
	float w_11 = s1.x * s1.y;

	// Alpha-weighted color reconstruction: eliminates dark fringes near empty (0,0,0,0) texels
	// without erroneously scaling straight RGB by 1/alpha when stroke opacity is low.
	float aw00 = w_00 * s00.a;
	float aw10 = w_10 * s10.a;
	float aw01 = w_01 * s01.a;
	float aw11 = w_11 * s11.a;
	float total_aw = aw00 + aw10 + aw01 + aw11;

	vec3 rgb = (total_aw > 1e-5)
		? (s00.rgb * aw00 + s10.rgb * aw10 + s01.rgb * aw01 + s11.rgb * aw11) / total_aw
		: (s00.rgb * w_00 + s10.rgb * w_10 + s01.rgb * w_01 + s11.rgb * w_11);

	float a = s00.a * w_00 + s10.a * w_10 + s01.a * w_01 + s11.a * w_11;
	return vec4(rgb, clamp(a, 0.0, 1.0));
}

// Alpha-weighted bilinear filtering fallback for when bicubic filtering is disabled
vec4 sample_bilinear_smooth(sampler2D tex, vec2 uv, vec2 min_bounds, vec2 max_bounds) {
	ivec2 itex_size = textureSize(tex, 0);
	if (itex_size.x <= 2 || itex_size.y <= 2) {
		return texture(tex, clamp(uv, min_bounds, max_bounds));
	}
	vec2 tex_size = vec2(itex_size);
	vec2 inv_size = 1.0 / tex_size;
	vec2 tc = uv * tex_size - 0.5;
	vec2 f = fract(tc);
	vec2 base_pos = floor(tc);

	vec2 c0 = clamp((base_pos + vec2(0.5, 0.5)) * inv_size, min_bounds, max_bounds);
	vec2 c1 = clamp((base_pos + vec2(1.5, 0.5)) * inv_size, min_bounds, max_bounds);
	vec2 c2 = clamp((base_pos + vec2(0.5, 1.5)) * inv_size, min_bounds, max_bounds);
	vec2 c3 = clamp((base_pos + vec2(1.5, 1.5)) * inv_size, min_bounds, max_bounds);

	vec4 s00 = texture(tex, c0);
	vec4 s10 = texture(tex, c1);
	vec4 s01 = texture(tex, c2);
	vec4 s11 = texture(tex, c3);

	float w_00 = (1.0 - f.x) * (1.0 - f.y);
	float w_10 = f.x * (1.0 - f.y);
	float w_01 = (1.0 - f.x) * f.y;
	float w_11 = f.x * f.y;

	float aw00 = w_00 * s00.a;
	float aw10 = w_10 * s10.a;
	float aw01 = w_01 * s01.a;
	float aw11 = w_11 * s11.a;
	float total_aw = aw00 + aw10 + aw01 + aw11;

	vec3 rgb = (total_aw > 1e-5)
		? (s00.rgb * aw00 + s10.rgb * aw10 + s01.rgb * aw01 + s11.rgb * aw11) / total_aw
		: (s00.rgb * w_00 + s10.rgb * w_10 + s01.rgb * w_01 + s11.rgb * w_11);

	float a = s00.a * w_00 + s10.a * w_10 + s01.a * w_01 + s11.a * w_11;
	return vec4(rgb, clamp(a, 0.0, 1.0));
}

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
		// Backface rejection: discard surfaces pointing away from the camera.
		// Uses camera-space normal z component with threshold rather than !FRONT_FACING,
		// allowing single-sided/ribbon geometry (hair, skirts, belts) to be painted.
		if (front_faces_only && NORMAL.z < -0.1) {
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
		// Alpha must be strictly 1.0 so the framebuffer data is never blended or scaled by the compositor
		ALPHA = 1.0;
	} else {
		// Clamp atlas UV within submesh rect with half-texel inset to prevent sampling neighbors
		vec2 half_texel = vec2(0.5) / vec2(textureSize(overlay_texture, 0));
		vec2 min_atlas = position_in_atlas + half_texel;
		vec2 max_atlas = position_in_atlas + size_in_atlas - half_texel;
		vec2 clamped_uv = clamp(v_atlas_uv, min_atlas, max_atlas);

		vec4 paint = smooth_bicubic
			? sample_bicubic_smooth(overlay_texture, v_atlas_uv, min_atlas, max_atlas)
			: sample_bilinear_smooth(overlay_texture, clamped_uv, min_atlas, max_atlas);

		vec4 active_paint = vec4(0.0);
		if (active_layer_visible) {
			active_paint = smooth_bicubic
				? sample_bicubic_smooth(active_layer_texture, v_atlas_uv, min_atlas, max_atlas)
				: sample_bilinear_smooth(active_layer_texture, clamped_uv, min_atlas, max_atlas);
			active_paint.a *= active_layer_opacity;
		}

		if (active_paint.a > 0.0001) {
			float out_a = clamp(active_paint.a + paint.a * (1.0 - active_paint.a), 0.0, 1.0);
			vec3 out_rgb = (out_a > 0.0001)
				? (active_paint.rgb * active_paint.a + paint.rgb * paint.a * (1.0 - active_paint.a)) / out_a
				: active_paint.rgb;
			paint = vec4(out_rgb, out_a);
		}

		float final_alpha = clamp(paint.a, 0.0, 1.0);
		// Straight RGB is preserved accurately by alpha-weighted filtering without division artifacts
		vec3 final_color = clamp(paint.rgb, vec3(0.0), vec3(1.0));

		if (show_selection_mask) {
			ivec2 mask_size = textureSize(selection_mask, 0);
			if (mask_size.x > 1) {
				vec2 uv_dx = dFdx(clamped_uv);
				vec2 uv_dy = dFdy(clamped_uv);
				float texel_step = 1.0 / float(mask_size.x);
				vec2 d_x = (length(uv_dx) > 1e-6 && length(uv_dx) < 0.05) ? uv_dx : vec2(texel_step, 0.0);
				vec2 d_y = (length(uv_dy) > 1e-6 && length(uv_dy) < 0.05) ? uv_dy : vec2(0.0, texel_step);

				float m_center = texture(selection_mask, clamped_uv).r;
				float m_r = texture(selection_mask, clamped_uv + d_x).r;
				float m_l = texture(selection_mask, clamped_uv - d_x).r;
				float m_u = texture(selection_mask, clamped_uv + d_y).r;
				float m_d = texture(selection_mask, clamped_uv - d_y).r;

				bool is_edge = ((m_center >= 0.5) && (m_r < 0.5 || m_l < 0.5 || m_u < 0.5 || m_d < 0.5))
				            || ((m_center < 0.5) && (m_r >= 0.5 || m_l >= 0.5 || m_u >= 0.5 || m_d >= 0.5));

				if (is_edge) {
					// Animated marching ants outline (alternating black & white dashes)
					float ant = sin((FRAGCOORD.x + FRAGCOORD.y + TIME * 16.0) * 0.4);
					vec3 ant_color = (ant > 0.0) ? vec3(1.0, 1.0, 1.0) : vec3(0.05, 0.05, 0.05);
					final_color = ant_color;
					final_alpha = 1.0;
				} else if (m_center < 0.5 && show_stencil_pattern) {
					// UNSELECTED / UNPAINTABLE area has protective stencil overlay
					float stripe = sin((FRAGCOORD.x - FRAGCOORD.y) * 0.25);
					vec3 hatch_col = (stripe > 0.0) ? vec3(0.06, 0.06, 0.10) : vec3(0.01, 0.01, 0.03);
					float protect_alpha = 0.45;
					final_color = mix(final_color, hatch_col, protect_alpha);
					final_alpha = max(final_alpha, protect_alpha);
				}
				// If m_center >= 0.5: Paintable area is 100% pristine, clear and untinted!
			}
		}

		if (final_alpha <= 0.0001) {
			discard;
		}

		ALBEDO = final_color;
		ALPHA = final_alpha;
	}
}
