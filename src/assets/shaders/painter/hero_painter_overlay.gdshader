shader_type spatial;
// Lit overlay: painted pixels are shaded exactly like the base material underneath (same BRDF, lights, ambient,
// normal map, AO, roughness/metallic/specular) so a colour picked from the raw albedo looks seamless in 3D.
// The camera-brush data pass (layer 21) stays exact: it writes UV/depth data through EMISSION with ALBEDO and
// SPECULAR forced to zero, and the brush viewport has ambient + reflected light disabled (camera_brush_viewport.tscn).
render_mode blend_mix, depth_draw_always, cull_disabled, diffuse_burley, specular_schlick_ggx;

uniform sampler2D overlay_texture : hint_default_transparent, filter_linear_mipmap_anisotropic, repeat_disable;
uniform sampler2D active_layer_texture : hint_default_transparent, filter_linear_mipmap_anisotropic, repeat_disable;
uniform sampler2D overlay_above_texture : hint_default_transparent, filter_linear_mipmap_anisotropic, repeat_disable;
uniform sampler2D base_atlas_texture : hint_default_transparent, filter_linear_mipmap_anisotropic, repeat_disable;
uniform sampler2D g_tColor : source_color, filter_linear_mipmap, hint_default_white; // Base diffuse/AO
uniform sampler2D selection_mask : hint_default_transparent, filter_linear, repeat_disable;
uniform vec2 position_in_atlas;
uniform vec2 size_in_atlas;
uniform uint atlas_index;
uniform float layer_opacity : hint_range(0.0, 1.0) = 1.0;
uniform bool active_layer_visible = true;
uniform float active_layer_opacity : hint_range(0.0, 1.0) = 1.0;
uniform int blend_mode = 0;
uniform bool front_faces_only = true;
uniform bool is_paint_target = true;
uniform bool show_selection_mask = false;
uniform bool show_stencil_pattern = true;
uniform bool preserve_base_alpha = true;
uniform bool has_edited_base_texture = false;
uniform bool overlay_renders_base_texture = true;
uniform bool enable_alpha_cutout = false;
uniform float alpha_cutout_threshold : hint_range(0.0, 1.0) = 0.5;

uniform bool smooth_bicubic = false;

// Base-material PBR mirror (filled by SkinLayerManager.ApplyBasePbrToOverlay from the authentic base material)
uniform sampler2D normal_texture : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D ao_texture : hint_default_white, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_normal_map = false;
uniform bool normal_is_source2 = false; // true: Source 2 hemi-octahedron RG + roughness in B (source2_pbr family)
uniform float normal_scale : hint_range(-16.0, 16.0) = 1.0;
uniform bool use_ao_texture = false;
uniform float ao_light_affect : hint_range(0.0, 1.0) = 0.0;
uniform float roughness : hint_range(0.0, 1.0) = 0.65;
uniform float metallic : hint_range(0.0, 1.0) = 0.0;
uniform float specular : hint_range(0.0, 1.0) = 0.5;
uniform vec3 uv1_scale = vec3(1.0);
uniform vec3 uv1_offset = vec3(0.0);
uniform bool use_vertex_color = false;

// Multi-channel interactive inspection & toggles
uniform sampler2D roughness_texture : hint_default_white, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D shadow_tint_texture : source_color, hint_default_white, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D emission_texture : source_color, hint_default_black, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D metallic_texture : hint_default_black, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D mask_preview_texture : hint_default_black, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D channel_color_override_texture : source_color, hint_default_white, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_channel_color_override = false;
uniform bool has_roughness_texture = false;
uniform bool has_shadow_tint_texture = false;
uniform bool has_metallic_texture = false;
uniform bool enable_color_map = true;
uniform bool enable_ao = true;
uniform bool enable_normal = true;
uniform bool enable_roughness = true;
uniform bool enable_shadow_map = true;
uniform bool enable_emission = true;
uniform int debug_view_channel = 0; // 0=Combined, 1=Color, 2=AO, 3=Normal, 4=Roughness, 5=Shadow, 6=SelfIllum, 7=Metalness, 8+=Masks
uniform int active_paint_channel = 0; // 0/1=Color, 2=AO, 3=Normal, 4=Roughness, 5=Shadow, 6=SelfIllum, 7=Metalness, 8+=Masks

// Live Material Parameter Overrides (synced from PaintTabUI.MaterialParams.cs)
uniform vec4 param_color_tint : source_color = vec4(1.0, 1.0, 1.0, 1.0);
uniform float param_vertex_color_strength : hint_range(0.0, 1.0) = 1.0;
uniform vec3 param_albedo_csb = vec3(1.0, 1.0, 1.0); // x=Contrast, y=Saturation, z=Brightness
uniform bool param_has_self_illum_override = false;
uniform bool param_has_self_illum_mask = false;
uniform vec4 param_self_illum_tint : source_color = vec4(1.0, 1.0, 1.0, 1.0);
uniform float param_self_illum_scale = 0.0;
uniform float param_self_illum_albedo_factor = 0.0;

varying vec2 v_atlas_uv;
varying vec2 v_mesh_uv;

vec3 overlay_linear_to_srgb(vec3 c) {
	vec3 clamped = clamp(c, vec3(0.0), vec3(1.0));
	return mix(
		12.92 * clamped,
		1.055 * pow(clamped, vec3(1.0 / 2.4)) - 0.055,
		step(vec3(0.0031308), clamped)
	);
}

vec3 decode_hemi_octahedron(vec2 e) {
	vec2 t = e * 2.0 - 1.0;
	vec3 n = vec3(t.x + t.y, t.x - t.y, 1.0 - abs(t.x) - abs(t.y));
	return normalize(n);
}

float get_luminance(vec3 c) {
	return dot(c, vec3(0.299, 0.587, 0.114));
}

vec3 set_luminance(vec3 c, float l) {
	float d = l - get_luminance(c);
	vec3 cr = c + vec3(d);
	float lum = get_luminance(cr);
	float min_c = min(cr.r, min(cr.g, cr.b));
	float max_c = max(cr.r, max(cr.g, cr.b));
	if (min_c < 0.0) {
		float denom = max(lum - min_c, 0.0001);
		cr = lum + ((cr - lum) * lum) / denom;
	}
	if (max_c > 1.0) {
		float denom = max(max_c - lum, 0.0001);
		cr = lum + ((cr - lum) * (1.0 - lum)) / denom;
	}
	return clamp(cr, vec3(0.0), vec3(1.0));
}

vec3 apply_blend_mode(vec3 base_col, vec3 src_col, int mode) {
	if (mode == 1) { // Multiply
		return base_col * src_col;
	} else if (mode == 2) { // Screen
		return vec3(1.0) - (vec3(1.0) - base_col) * (vec3(1.0) - src_col);
	} else if (mode == 3) { // Overlay
		vec3 less = 2.0 * base_col * src_col;
		vec3 more = vec3(1.0) - 2.0 * (vec3(1.0) - base_col) * (vec3(1.0) - src_col);
		return mix(more, less, step(base_col, vec3(0.5)));
	} else if (mode == 4) { // Darken
		return min(base_col, src_col);
	} else if (mode == 5) { // Lighten
		return max(base_col, src_col);
	} else if (mode == 6) { // ColorDodge
		return base_col / max(vec3(1.0) - src_col, vec3(0.001));
	} else if (mode == 7) { // Color (HSL)
		return set_luminance(src_col, get_luminance(base_col));
	}
	return src_col; // Normal (0)
}

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

vec2 wrap_mesh_uv(vec2 uv) {
	float wx = (uv.x < 0.0 || uv.x > 1.0) ? fract(uv.x) : uv.x;
	float wy = (uv.y < 0.0 || uv.y > 1.0) ? fract(uv.y) : uv.y;
	return vec2(wx, wy);
}

void vertex() {
	// Source 2 models in Deadlock often lack a dedicated UV2 stream.
	// Fall back to primary UV coordinates so each submesh properly samples its allocated rect in the atlas.
	v_mesh_uv = (length(UV2) > 0.0001) ? UV2 : UV;
	v_atlas_uv = wrap_mesh_uv(v_mesh_uv) * size_in_atlas + position_in_atlas;
}

void fragment() {
	// Wrap per-fragment so tiled UVs outside [0, 1] (e.g. V in [-1, 0]) map accurately into the [0, 1] paint atlas.
	vec2 wrapped_mesh_uv = wrap_mesh_uv(v_mesh_uv);
	vec2 atlas_uv = wrapped_mesh_uv * size_in_atlas + position_in_atlas;

	// Layer 21 (bit index 20) is used by camera brush to capture UVs and depths
	bool is_camera_brush = (CAMERA_VISIBLE_LAYERS & (1u << uint(20))) != 0u;
	if (is_camera_brush) {
		// Isolate painting strictly to the active target submesh/mesh
		if (!is_paint_target) {
			discard;
		}
		// Backface rejection: discard surfaces pointing away from the camera or at steep grazing angles.
		// NORMAL.z <= 0.05 or !FRONT_FACING rejects backfaces and grazing angles (> 87 deg), preventing bleed onto opposite or occluded walls.
		if (front_faces_only && (NORMAL.z <= 0.05 || !FRONT_FACING)) {
			discard;
		}
		uint exponential_bit = 1u << 30u;
		float distance = -VERTEX.z;
		uint distance_bits = (packHalf2x16(vec2(distance, 0)) & 0xFFE0u) << uint(13 - 5);
		uint index_sign_bit = (atlas_index & 0x4u) << uint(31 - 2);
		uint index_exp_bits = (atlas_index & 0x3u) << 24u;
		float b = uintBitsToFloat(exponential_bit | distance_bits | index_sign_bit | index_exp_bits);
		// Data travels through EMISSION only. ALBEDO/SPECULAR/METALLIC are zero so diffuse, specular, ambient and
		// reflection terms (all multiplied by albedo or F0) contribute exactly nothing.
		ALBEDO = vec3(0.0);
		METALLIC = 0.0;
		SPECULAR = 0.0;
		ROUGHNESS = 1.0;
		AO = 1.0;
		AO_LIGHT_AFFECT = 0.0;
		EMISSION = vec3(atlas_uv - vec2(0.5), b);
		// Alpha must be strictly 1.0 so the framebuffer data is never blended or scaled by the compositor
		ALPHA = 1.0;
	} else {
		// Clamp atlas UV within submesh rect with half-texel inset to prevent sampling neighbors
		vec2 half_texel = vec2(0.5) / vec2(textureSize(overlay_texture, 0));
		vec2 min_atlas = position_in_atlas + half_texel;
		vec2 max_atlas = position_in_atlas + size_in_atlas - half_texel;
		vec2 clamped_uv = clamp(atlas_uv, min_atlas, max_atlas);

		vec4 paint = smooth_bicubic
			? sample_bicubic_smooth(overlay_texture, atlas_uv, min_atlas, max_atlas)
			: sample_bilinear_smooth(overlay_texture, clamped_uv, min_atlas, max_atlas);

		vec4 active_paint = vec4(0.0);
		if (active_layer_visible) {
			active_paint = smooth_bicubic
				? sample_bicubic_smooth(active_layer_texture, atlas_uv, min_atlas, max_atlas)
				: sample_bilinear_smooth(active_layer_texture, clamped_uv, min_atlas, max_atlas);
			active_paint.a *= active_layer_opacity;
		}

		vec4 base_atlas_sample = use_channel_color_override
			? texture(channel_color_override_texture, wrapped_mesh_uv)
			: (has_edited_base_texture
				? texture(base_atlas_texture, clamped_uv)
				: texture(g_tColor, wrapped_mesh_uv));
		vec3 raw_base_rgb = clamp(base_atlas_sample.rgb, vec3(0.0), vec3(1.0));
		float base_alpha = clamp(base_atlas_sample.a, 0.0, 1.0);

		if (active_paint.a > 0.0001) {
			vec3 base_color = (paint.a > 0.0001)
				? mix(raw_base_rgb, paint.rgb, paint.a)
				: raw_base_rgb;

			vec3 blended_active = (blend_mode == 0)
				? active_paint.rgb
				: apply_blend_mode(base_color, active_paint.rgb, blend_mode);

			if (paint.a > 0.0001) {
				float out_a = clamp(active_paint.a + paint.a * (1.0 - active_paint.a), 0.0, 1.0);
				vec3 out_rgb = (out_a > 0.0001)
					? (blended_active * active_paint.a + paint.rgb * paint.a * (1.0 - active_paint.a)) / out_a
					: blended_active;
				paint = vec4(out_rgb, out_a);
			} else {
				paint = vec4(blended_active, active_paint.a);
			}
		}

		vec4 above_paint = smooth_bicubic
			? sample_bicubic_smooth(overlay_above_texture, atlas_uv, min_atlas, max_atlas)
			: sample_bilinear_smooth(overlay_above_texture, clamped_uv, min_atlas, max_atlas);

		if (above_paint.a > 0.0001) {
			if (paint.a > 0.0001) {
				float out_a = clamp(above_paint.a + paint.a * (1.0 - above_paint.a), 0.0, 1.0);
				vec3 out_rgb = (out_a > 0.0001)
					? (above_paint.rgb * above_paint.a + paint.rgb * paint.a * (1.0 - above_paint.a)) / out_a
					: above_paint.rgb;
				paint = vec4(out_rgb, out_a);
			} else {
				paint = above_paint;
			}
		}

		float stroke_alpha = clamp(paint.a, 0.0, 1.0);
		float eff_paint_a = preserve_base_alpha ? (stroke_alpha * base_alpha) : stroke_alpha;
		float final_alpha = eff_paint_a;
		// Straight RGB is preserved accurately by alpha-weighted filtering without division artifacts
		vec3 final_color = clamp(paint.rgb, vec3(0.0), vec3(1.0));

		if (has_edited_base_texture) {
			float combined_alpha = (preserve_base_alpha || active_paint_channel > 1)
				? base_alpha
				: clamp(base_alpha + stroke_alpha * (1.0 - base_alpha), 0.0, 1.0);
			if (enable_alpha_cutout && combined_alpha < alpha_cutout_threshold) {
				discard;
			}
			if (overlay_renders_base_texture) {
				if (active_paint_channel <= 1) {
					final_color = mix(raw_base_rgb, final_color, eff_paint_a);
				}
				final_alpha = combined_alpha;
			} else {
				final_alpha = eff_paint_a;
			}
		} else if (preserve_base_alpha) {
			if (enable_alpha_cutout && base_alpha < alpha_cutout_threshold) {
				discard;
			}
		}

		// Selection visuals (marching ants / stencil hatch) are editor aids: they are emitted unlit.
		vec3 emit_col = vec3(0.0);
		float lit_weight = 1.0;

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
					emit_col = (ant > 0.0) ? vec3(1.0, 1.0, 1.0) : vec3(0.05, 0.05, 0.05);
					lit_weight = 0.0;
					final_alpha = 1.0;
				} else if (m_center < 0.5 && show_stencil_pattern) {
					// UNSELECTED / UNPAINTABLE area has protective stencil overlay
					float stripe = sin((FRAGCOORD.x - FRAGCOORD.y) * 0.25);
					vec3 hatch_col = (stripe > 0.0) ? vec3(0.06, 0.06, 0.10) : vec3(0.01, 0.01, 0.03);
					float protect_alpha = 0.45;
					// Paint stays lit (scaled by the remaining weight); the hatch itself is emitted unlit.
					lit_weight = 1.0 - protect_alpha;
					emit_col = hatch_col * protect_alpha;
					final_alpha = max(final_alpha, protect_alpha);
				}
				// If m_center >= 0.5: Paintable area is 100% pristine, clear and untinted!
			}
		}

		if (final_alpha <= 0.0001 && debug_view_channel == 0) {
			discard;
		}

		// Mirror the base material's surface response so painted pixels light identically to unpainted ones.
		vec2 base_uv = UV * uv1_scale.xy + uv1_offset.xy;
		vec3 paint_encoded = overlay_linear_to_srgb(final_color);
		float rough = roughness;
		vec3 norm_preview = vec3(0.5, 0.5, 1.0);
		float rough_preview = roughness;
		if (use_normal_map) {
			vec4 n_sample = texture(normal_texture, base_uv);
			if (normal_is_source2) {
				norm_preview = decode_hemi_octahedron(n_sample.rg) * 0.5 + 0.5;
				rough_preview = n_sample.b;
				if (enable_normal) NORMAL_MAP = norm_preview;
				if (enable_roughness) rough = n_sample.b * roughness;
			} else {
				norm_preview = n_sample.rgb;
				if (enable_normal) NORMAL_MAP = norm_preview;
			}
			NORMAL_MAP_DEPTH = enable_normal ? normal_scale : 0.0;
		}
		if (has_roughness_texture) {
			rough_preview = texture(roughness_texture, base_uv).r;
			if (enable_roughness) rough = clamp(rough_preview * roughness, 0.02, 1.0);
		}
		float ao_sample = 1.0;
		if (use_ao_texture) {
			ao_sample = texture(ao_texture, base_uv).r;
			AO = enable_ao ? ao_sample : 1.0;
			AO_LIGHT_AFFECT = enable_ao ? ao_light_affect : 0.0;
		}

		// Live GPU preview of non-Color painted channels while brushing (zero CPU readback stall!)
		float base_metallic = has_metallic_texture ? texture(metallic_texture, base_uv).r : metallic;
		float eff_metallic = base_metallic;
		if (active_paint_channel == 2) {
			ao_sample = mix(ao_sample, paint_encoded.r, eff_paint_a);
			AO = enable_ao ? ao_sample : 1.0;
			AO_LIGHT_AFFECT = enable_ao ? max(ao_light_affect, 0.6) : 0.0;
		} else if (active_paint_channel == 3) {
			norm_preview = normalize(mix(norm_preview, paint_encoded, eff_paint_a));
			if (enable_normal) {
				NORMAL_MAP = norm_preview;
				NORMAL_MAP_DEPTH = abs(normal_scale) > 0.001 ? normal_scale : 1.0;
			}
		} else if (active_paint_channel == 4) {
			rough_preview = mix(rough_preview, paint_encoded.r, eff_paint_a);
			if (enable_roughness) rough = clamp(rough_preview, 0.02, 1.0);
		} else if (active_paint_channel == 7) {
			eff_metallic = mix(base_metallic, paint_encoded.r, eff_paint_a);
		}

		vec3 v_col = (use_vertex_color && length(COLOR.rgb) > 0.001) ? mix(vec3(1.0), COLOR.rgb, param_vertex_color_strength) : vec3(1.0);
		vec3 base_color_map = use_channel_color_override
			? texture(channel_color_override_texture, base_uv).rgb
			: raw_base_rgb;
		vec3 effective_color = (use_channel_color_override && debug_view_channel == 0)
			? base_color_map
			: ((has_edited_base_texture && overlay_renders_base_texture && active_paint_channel <= 1)
				? final_color
				: mix(base_color_map, final_color, (active_paint_channel <= 1) ? eff_paint_a : 0.0));
		vec3 base_albedo = enable_color_map ? (effective_color * lit_weight * v_col) : vec3(0.85 * lit_weight);
		base_albedo *= param_color_tint.rgb;
		float csb_lum = dot(base_albedo, vec3(0.2126, 0.7152, 0.0722));
		base_albedo = mix(vec3(csb_lum), base_albedo, param_albedo_csb.y);
		base_albedo = ((base_albedo - vec3(0.5)) * param_albedo_csb.x + vec3(0.5)) * param_albedo_csb.z;
		base_albedo = clamp(base_albedo, vec3(0.0), vec3(1.0));
		vec3 sh_tint = has_shadow_tint_texture ? texture(shadow_tint_texture, base_uv).rgb : vec3(1.0);
		if (active_paint_channel == 5) {
			sh_tint = mix(sh_tint, final_color, eff_paint_a);
			if (enable_shadow_map) {
				base_albedo *= mix(vec3(1.0), sh_tint, 0.35);
			}
		}
		vec3 emissive_sample = enable_emission ? texture(emission_texture, base_uv).rgb : vec3(0.0);
		if (active_paint_channel == 6) {
			emissive_sample = mix(emissive_sample, final_color, eff_paint_a);
			if (enable_emission) emit_col += final_color * eff_paint_a;
		}
		if (param_has_self_illum_override && enable_emission && param_self_illum_scale > 0.001) {
			float si_mask_val = param_has_self_illum_mask
				? max(emissive_sample.r, max(emissive_sample.g, emissive_sample.b))
				: 1.0;
			vec3 si_col = mix(param_self_illum_tint.rgb, base_albedo * param_self_illum_tint.rgb, param_self_illum_albedo_factor);
			emit_col += si_col * (si_mask_val * param_self_illum_scale);
		}

		ALBEDO = base_albedo;
		EMISSION = emit_col;
		ALPHA = final_alpha;
		ROUGHNESS = rough;
		METALLIC = eff_metallic;
		SPECULAR = specular;

		if (debug_view_channel > 0) {
			vec3 dbg = effective_color * v_col;
			if (debug_view_channel == 1) dbg = enable_color_map ? (effective_color * v_col) : vec3(0.85);
			else if (debug_view_channel == 2) dbg = vec3(enable_ao ? ao_sample : 1.0);
			else if (debug_view_channel == 3) dbg = enable_normal ? norm_preview : vec3(0.5, 0.5, 1.0);
			else if (debug_view_channel == 4) dbg = vec3(enable_roughness ? rough_preview : roughness);
			else if (debug_view_channel == 5) dbg = enable_shadow_map ? sh_tint : vec3(1.0);
			else if (debug_view_channel == 6) dbg = emissive_sample;
			else if (debug_view_channel == 7) dbg = vec3(eff_metallic);
			else if (debug_view_channel >= 8) {
				float mask_base = texture(mask_preview_texture, base_uv).r;
				dbg = vec3(mix(mask_base, paint_encoded.r, (active_paint_channel == debug_view_channel) ? eff_paint_a : 0.0));
			}
			ALBEDO = vec3(0.0);
			EMISSION = dbg * lit_weight + emit_col;
			ALPHA = 1.0;
			AO = 1.0;
			SPECULAR = 0.0;
			METALLIC = 0.0;
			ROUGHNESS = 1.0;
		}
	}
}
