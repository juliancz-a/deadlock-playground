shader_type spatial;
render_mode blend_mix, depth_draw_opaque, cull_disabled;

group_uniforms Shader.Albedo;
uniform vec4 albedo_color : source_color = vec4(1.0);
uniform sampler2D albedo_texture : source_color, hint_default_white, filter_linear_mipmap;
uniform vec3 uv1_scale = vec3(1.0, 1.0, 1.0);
uniform vec3 uv1_offset = vec3(0.0, 0.0, 0.0);
uniform float alpha_scissor_threshold : hint_range(0.0, 1.0, 0.05) = 0.0;

group_uniforms Shader.Emission;
uniform vec4 emission_color : source_color = vec4(0.0, 0.0, 0.0, 1.0);
uniform float emission_energy = 0.0;
uniform sampler2D emission_texture : source_color, hint_default_white, filter_linear_mipmap;

group_uniforms Shader.Light;
uniform float toon_intensity : hint_range(0.0, 1.0, 0.05) = 1.0;
uniform bool use_stepped = true;
uniform float steps = 3.0;
uniform float step_smoothness : hint_range(0.0, 1.0, 0.05) = 0.3;
uniform vec4 shadow_tint : source_color = vec4(0.18, 0.16, 0.26, 1.0);
uniform float shadow_tint_amount : hint_range(0.0, 1.0, 0.05) = 0.4;

group_uniforms Shader.Specular;
uniform float metallic : hint_range(0.0, 1.0, 0.05) = 0.0;
uniform float roughness : hint_range(0.0, 1.0, 0.05) = 0.65;
uniform float specular : hint_range(0.0, 1.0, 0.05) = 0.3;

group_uniforms Shader.Normal;
uniform float normal_strength : hint_range(0.0, 1.0, 0.05) = 0.0;
uniform sampler2D normal_texture : hint_normal, filter_linear_mipmap;

group_uniforms Shader.Rim;
uniform bool use_rim = true;
uniform vec4 rim_color : source_color = vec4(1.0);
uniform float rim_amount = 2.0;
uniform float rim_smoothness = 0.2;
uniform float rim_mask_shadow : hint_range(0.0, 1.0, 0.05) = 1.0;
uniform float rim_blend : hint_range(0.0, 1.0, 0.05) = 1.0;

group_uniforms Shader.ValveNPR;
uniform bool use_valve_npr = true;
uniform float npr_diffuse_step_sharpness : hint_range(0.0, 0.99, 0.01) = 0.9;
uniform float npr_direct_light_wrap : hint_range(0.1, 1.0, 0.05) = 0.8;
uniform float npr_diffuse_pbr_blend : hint_range(0.0, 1.0, 0.05) = 0.25;
uniform float npr_rim_light_falloff : hint_range(0.1, 4.0, 0.05) = 0.7;
uniform float npr_rim_light_wrap : hint_range(0.0, 1.0, 0.05) = 0.7;
uniform float npr_rim_light_strength : hint_range(0.0, 5.0, 0.1) = 2.0;
uniform vec2 npr_rim_light_up_ramp = vec2(0.2, 0.2);
uniform sampler2D rim_mask_texture : hint_default_white, filter_linear_mipmap;
uniform bool rim_mask_from_green_channel = false;
uniform float self_illum_albedo_factor : hint_range(0.0, 1.0, 0.05) = 1.0;
group_uniforms;

group_uniforms Shader.Pattern;
uniform bool use_pattern = false;
uniform sampler2D pattern_texture : hint_default_white, filter_linear_mipmap;
uniform int pattern_uv_mode : hint_enum("UV", "Screen", "Local Screen") = 2;
uniform float pattern_tiling = 32.0;
uniform float pattern_amount : hint_range(0.0, 1.0, 0.05) = 0.5;
uniform float pattern_smoothness : hint_range(0.0, 1.0, 0.05) = 0.5;
uniform float pattern_blend : hint_range(0.0, 1.0, 0.05) = 0.2;

varying vec4 node_position;

float fresnel(float amount, vec3 normal, vec3 view)
{
	return pow((1.0 - clamp(dot(normalize(normal), normalize(view)), 0.0, 1.0 )), amount);
}

vec2 pattern_uv(vec2 uv, vec2 screen_uv){
	vec2 p_uv = uv;
	if(pattern_uv_mode == 1) { p_uv = screen_uv; }
	if(pattern_uv_mode == 2) { p_uv = (screen_uv * 2.0 - 1.0) * node_position.w - node_position.xy; }
	return p_uv;
}

float shape_value(float value, vec2 uv, vec2 screen_size){
	float aspect = screen_size.y / screen_size.x;
	vec2 p_uv = uv * pattern_tiling * vec2(1.0, aspect);
	float shadow_pattern = texture(pattern_texture, p_uv).r;
	shadow_pattern = clamp(smoothstep(shadow_pattern - pattern_smoothness, shadow_pattern + pattern_smoothness, value + pattern_amount), 0.0, value + pattern_amount);
	return shadow_pattern;
}

void vertex() {
	node_position = (PROJECTION_MATRIX * vec4(NODE_POSITION_VIEW, 1.0));
}

// Deadlock NPR helpers (VRF 21.1 lighting_npr.slang / citadel.slang parity)
float npr_step(float x, float sharpness) {
	float clamped_sharpness = clamp(sharpness, 0.0, 0.99);
	float whole = floor(x);
	float fraction = x - whole;
	float exponent = 1.0 / (1.0 - clamped_sharpness);
	bool upper_half = fraction > 0.5;
	float distance_to_whole = max(upper_half ? 1.0 - fraction : fraction, 0.0);
	float bent = (distance_to_whole > 0.0)
		? (pow(0.5, 1.0 - exponent) * pow(distance_to_whole, exponent))
		: 0.0;
	return whole + (upper_half ? 1.0 - bent : bent);
}

float npr_direct_diffuse(float NoL, float sharpness) {
	float wrap = max(npr_direct_light_wrap, 0.05);
	float normalization = 1.0 / (2.0 * wrap);
	float wrapped = clamp(0.5 + ((wrap - 0.5 + NoL) - 0.5) * 2.0, 0.0, 1.0);
	float raw = mix(normalization * npr_step(wrapped, sharpness), clamp(NoL, 0.0, 1.0), npr_diffuse_pbr_blend);
	float peak = max(mix(normalization, 1.0, npr_diffuse_pbr_blend), 0.001);
	return clamp(raw / peak, 0.0, 1.0);
}

float npr_rim_up_ramp(float world_normal_y) {
	float range = npr_rim_light_up_ramp.y - npr_rim_light_up_ramp.x;
	if (abs(range) < 1e-5) {
		return (world_normal_y > npr_rim_light_up_ramp.x) ? 1.0 : 0.0;
	}
	return clamp((world_normal_y - npr_rim_light_up_ramp.x) / range, 0.0, 1.0);
}

float npr_rim_factor(vec3 view_normal, vec3 view_dir, float world_normal_y, float rim_mask) {
	if (rim_mask <= 0.0) {
		return 0.0;
	}
	float wrap = npr_rim_light_wrap;
	float denom = max((1.0 + wrap) * (1.0 + wrap), 1e-5);
	float facing_base = clamp((wrap - dot(view_normal, view_dir)) / denom, 0.0, 1.0);
	float facing = (facing_base > 0.0) ? pow(facing_base, npr_rim_light_falloff) : 0.0;
	return facing * npr_rim_up_ramp(world_normal_y) * npr_rim_light_strength * rim_mask;
}

void fragment() {
	vec2 uv = UV * uv1_scale.xy + uv1_offset.xy;
	vec3 safe_albedo_col = (dot(albedo_color.rgb, vec3(1.0)) > 0.001) ? albedo_color.rgb : vec3(1.0);
	vec4 tex_col = texture(albedo_texture, uv);
	vec4 albedo = vec4(tex_col.rgb * safe_albedo_col, tex_col.a * albedo_color.a);

	if (alpha_scissor_threshold > 0.0) {
		if (albedo.a < alpha_scissor_threshold) {
			discard;
		}
	}

	vec4 normal_map = texture(normal_texture, uv);

	ALBEDO = albedo.rgb;
	NORMAL_MAP = mix(vec3(0.5, 0.5, 1.0), normal_map.rgb, normal_strength);
	METALLIC = metallic;
	ROUGHNESS = clamp(roughness * normal_map.a, 0.02, 1.0);

	if (emission_energy > 0.0 && dot(emission_color.rgb, vec3(1.0)) > 1e-4) {
		vec4 em_tex = texture(emission_texture, uv);
		if (use_valve_npr) {
			float illum_mask = max(em_tex.r, max(em_tex.g, em_tex.b));
			vec3 em_chroma = (illum_mask > 1e-4) ? (em_tex.rgb / illum_mask) : vec3(0.0);
			vec3 base_illum = emission_color.rgb * em_chroma;
			vec3 illum_tint = mix(base_illum, base_illum * ALBEDO, self_illum_albedo_factor);
			float illum_amount = illum_mask * clamp(emission_energy, 0.0, 1.0);
			ALBEDO = mix(ALBEDO, illum_tint, illum_amount);
			EMISSION = ALBEDO * (illum_mask * emission_energy);
		} else {
			EMISSION = emission_color.rgb * em_tex.rgb * emission_energy;
		}
	}

	// Citadel near-black albedo specular suppression (citadel.slang GetSpecularColorCitadel)
	float albedo_max = max(ALBEDO.r, max(ALBEDO.g, ALBEDO.b));
	float albedo_spec_gate = use_valve_npr ? clamp(albedo_max * 25.0, 0.0, 1.0) : 1.0;
	SPECULAR = specular * albedo_spec_gate;
}

void light() {
	vec3 safe_norm = normalize(NORMAL);
	vec3 safe_view = normalize(VIEW);
	vec3 safe_light = normalize(LIGHT);
	float atten = clamp(ATTENUATION, 0.0, 1.0);

	// 1. Stylized Diffuse (Valve Deadlock NPR parity or Half-Lambert fallback)
	float NdotL = dot(safe_norm, safe_light);
	float half_lambert = clamp(NdotL * 0.5 + 0.5, 0.0, 1.0);

	float light_intensity;
	if (use_valve_npr) {
		float eff_sharpness = clamp(npr_diffuse_step_sharpness * clamp(1.3 - step_smoothness, 0.0, 1.1), 0.0, 0.99);
		float npr_val = npr_direct_diffuse(NdotL, eff_sharpness);
		light_intensity = use_stepped ? mix(half_lambert, npr_val, toon_intensity) : half_lambert;
	} else {
		float shadow_threshold = 0.45;
		float shadow_feather = mix(0.04, 0.12, step_smoothness);
		light_intensity = smoothstep(shadow_threshold - shadow_feather, shadow_threshold + shadow_feather, half_lambert);
		if (use_stepped) {
			light_intensity = mix(half_lambert, light_intensity, toon_intensity);
		}
	}

	if (use_pattern) {
		vec2 p_uv = pattern_uv(UV, SCREEN_UV);
		light_intensity = mix(light_intensity, shape_value(light_intensity, p_uv, VIEWPORT_SIZE), pattern_blend);
		light_intensity = clamp(light_intensity, 0.0, 1.0);
	}

	// Ambient shadow floor so shaded areas retain detail, warmth and user tint:
	vec3 cool_ambient = vec3(0.75, 0.75, 0.85);
	vec3 base_shadow = ALBEDO * 0.35 * cool_ambient;
	vec3 tinted_shadow = ALBEDO * shadow_tint.rgb * 1.5;
	vec3 shadow_ambient = mix(base_shadow, tinted_shadow, shadow_tint_amount);
	vec3 lit_color = ALBEDO * LIGHT_COLOR;

	vec3 diffuse_result = mix(shadow_ambient, lit_color, light_intensity);

	// 2. Stylized Rim Light (Valve Deadlock NPR wrap + world-up ramp + g_tTintMaskRimLightMask)
	if (use_rim) {
		float rim = 0.0;
		if (use_valve_npr) {
			vec2 rim_uv = UV * uv1_scale.xy + uv1_offset.xy;
			vec4 rim_mask_sample = texture(rim_mask_texture, rim_uv);
			float rim_mask = rim_mask_from_green_channel ? rim_mask_sample.g : rim_mask_sample.r;
			float world_normal_y = normalize((INV_VIEW_MATRIX * vec4(safe_norm, 0.0)).xyz).y;
			float raw_rim = npr_rim_factor(safe_norm, safe_view, world_normal_y, rim_mask);
			float shadow_gate = mix(1.0, light_intensity, rim_mask_shadow * 0.85);
			float rim_scale = max(rim_amount, 0.0) * 0.5;
			rim = raw_rim * shadow_gate * 0.45 * rim_scale * rim_blend;
		} else {
			float NdotV = clamp(dot(safe_norm, safe_view), 0.0, 1.0);
			rim = pow(1.0 - NdotV, 4.0) * clamp(NdotL, 0.0, 1.0);
			rim = smoothstep(0.4, 0.7, rim) * 0.25 * rim_blend;
		}
		diffuse_result += rim_color.rgb * LIGHT_COLOR * rim;
	}

	DIFFUSE_LIGHT += clamp(diffuse_result * atten, vec3(0.0), vec3(1.0));

	// 3. Respect Material Roughness & Kill Cloth Specular
	// Suppress specular on matte surfaces (cloth, leather, skin):
	float roughness_factor = clamp(1.0 - ROUGHNESS, 0.0, 1.0);
	roughness_factor = pow(roughness_factor, 2.0); // Harsh falloff for non-metals

	// Smooth, subtle specular rather than a harsh binary cut:
	vec3 half_vec = normalize(safe_view + safe_light);
	float NdotH = clamp(dot(safe_norm, half_vec), 0.0, 1.0);
	float spec_power = mix(8.0, 128.0, 1.0 - ROUGHNESS);
	float raw_spec = pow(NdotH, spec_power);

	// Soft threshold modulated by roughness + Citadel near-black albedo specular suppression:
	float albedo_max = max(ALBEDO.r, max(ALBEDO.g, ALBEDO.b));
	float albedo_spec_gate = use_valve_npr ? clamp(albedo_max * 25.0, 0.0, 1.0) : 1.0;
	float toon_spec = smoothstep(0.7, 0.85, raw_spec) * roughness_factor * 0.35 * (specular / 0.3) * albedo_spec_gate;

	// 4. Color-Modulated Specular (No Blown-Out White Highlights)
	vec3 spec_tint = mix(LIGHT_COLOR, ALBEDO, 0.3) * METALLIC + LIGHT_COLOR * (1.0 - METALLIC) * 0.2;
	SPECULAR_LIGHT += clamp(spec_tint * toon_spec * atten, vec3(0.0), vec3(1.0));
}