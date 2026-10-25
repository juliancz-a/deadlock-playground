shader_type spatial;
render_mode blend_mix, depth_draw_opaque, cull_disabled, diffuse_burley, specular_schlick_ggx;

// --- Textures ---
uniform sampler2D texture_albedo : source_color, filter_linear_mipmap, repeat_enable;
uniform sampler2D texture_albedo_b : source_color, filter_linear_mipmap, repeat_enable;
uniform sampler2D texture_normal_roughness : hint_default_black, filter_linear_mipmap, repeat_enable;
uniform sampler2D texture_self_illum : hint_default_black, filter_linear_mipmap, repeat_enable;
uniform sampler2D texture_tint_mask : hint_default_white, filter_linear_mipmap, repeat_enable;

// --- Parameters ---
uniform vec4 albedo_tint : source_color = vec4(1.0);
uniform vec4 albedo_tint_b : source_color = vec4(1.0);
instance uniform vec4 instance_tint : source_color = vec4(1.0);
uniform vec3 self_illum_tint : source_color = vec3(1.0);
uniform float self_illum_scale = 0.0;
uniform float roughness_mult = 1.0;
uniform float metallic = 0.0;

// --- Feature Flags ---
uniform bool has_albedo_texture = true;
uniform bool has_albedo_b = false;
uniform bool has_tint_mask = false;
uniform bool has_dedicated_tint_mask = false;
uniform bool use_vertex_color = false;
uniform bool has_normal_texture = false;
uniform bool has_self_illum_texture = false;
uniform bool is_alpha_tested = false;
uniform float alpha_scissor_threshold = 0.5;
uniform bool is_overlay = false;

varying float v_has_tangent;

void vertex() {
	v_has_tangent = length(TANGENT) > 0.01 ? 1.0 : 0.0;
	if (is_overlay) {
		// Subtle polygon bias along normal to prevent coplanar depth collisions
		VERTEX += NORMAL * 0.0012;
	}
}

void fragment() {
	vec4 raw_sample = has_albedo_texture ? texture(texture_albedo, UV) : vec4(1.0);
	vec3 eff_tint = albedo_tint.rgb * instance_tint.rgb;
	vec3 base_col = raw_sample.rgb;

	// Tint Mask (Source 2 F_TINT_MASK / g_tTintMask)
	if (has_tint_mask) {
		float mask = has_dedicated_tint_mask ? texture(texture_tint_mask, UV).r : texture(texture_normal_roughness, UV).a;
		base_col = mix(base_col, base_col * eff_tint, mask);
	} else {
		base_col *= eff_tint;
	}

	vec4 colA = vec4(base_col, raw_sample.a * albedo_tint.a * instance_tint.a);

	// Alpha-tested cutout without ever assigning to ALPHA
	// (Writing to ALPHA triggers Godot's transparent queue and breaks hardware Z-buffer occlusion)
	if (is_alpha_tested) {
		if (colA.a < alpha_scissor_threshold) {
			discard;
		}
	}

	vec3 final_albedo = colA.rgb;

	// Two-layer material blend (Source 2 multiblend/environment_blend)
	if (has_albedo_b) {
		vec4 raw_b = texture(texture_albedo_b, UV);
		vec3 colB = raw_b.rgb * albedo_tint_b.rgb * instance_tint.rgb;
		float blend_weight = clamp(COLOR.r, 0.0, 1.0);
		final_albedo = mix(colA.rgb, colB, blend_weight);
	} else if (use_vertex_color) {
		// Single-layer surfaces where vertex COLOR stores baked AO / vertex tint:
		// Sign of dot product detects non-zero painted vertices (all-zero in Source 2 means unpainted)
		if (length(COLOR.rgb) > 0.05) {
			final_albedo *= COLOR.rgb;
		}
	}

	ALBEDO = final_albedo;

	// Decode Source 2 Hemi-Octahedral Normal & Extract Roughness from Channel B
	if (has_normal_texture) {
		vec4 nr = texture(texture_normal_roughness, UV);
		if (v_has_tangent > 0.5) {
			// Valve HemiOctRB projection decode (matches utils.slang & pbr.frag.slang)
			vec2 temp = vec2(nr.r + nr.g - 1.003922, nr.r - nr.g);
			float nz = 1.0 - abs(temp.x) - abs(temp.y);
			vec3 normal_ts = normalize(vec3(temp.x, -temp.y, nz));
			NORMAL_MAP = normal_ts * 0.5 + 0.5;
		}

		// Channel B is authored surface roughness - clamp to sensible architectural range
		ROUGHNESS = clamp(nr.b * roughness_mult, 0.25, 0.95);
		METALLIC = metallic;
	} else {
		ROUGHNESS = 0.75;
		METALLIC = 0.0;
	}

	SPECULAR = 0.2;

	// Source 2 Self-Illumination modulation & emission
	if (has_self_illum_texture && self_illum_scale > 0.01) {
		float mask = texture(texture_self_illum, UV).r;
		float illum_amt = mask * clamp(self_illum_scale, 0.0, 1.0);
		// Modulate diffuse albedo where illuminated
		ALBEDO = mix(ALBEDO, self_illum_tint * ALBEDO, illum_amt);
		// Emit radiant light strictly on masked pixels
		EMISSION = self_illum_tint * (mask * clamp(self_illum_scale * 0.5, 0.0, 0.5));
	}
}
