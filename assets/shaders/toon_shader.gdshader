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
uniform sampler2D emission_texture : hint_default_white, filter_linear_mipmap;

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

void fragment() {
	vec2 uv = UV * uv1_scale.xy + uv1_offset.xy;
	vec4 albedo = texture(albedo_texture, uv) * albedo_color * COLOR;

	if (alpha_scissor_threshold > 0.0) {
		if (albedo.a < alpha_scissor_threshold) {
			discard;
		}
	}

	vec4 normal_map = texture(normal_texture, uv);

	ALBEDO = albedo.rgb;
	NORMAL_MAP = mix(vec3(0.5, 0.5, 1.0), normal_map.rgb, normal_strength);
	SPECULAR = specular;
	ROUGHNESS = roughness;

	if (emission_energy > 0.0) {
		vec4 em_tex = texture(emission_texture, uv);
		EMISSION = emission_color.rgb * em_tex.rgb * emission_energy;
	}
}

void light() {
	vec3 safe_norm = normalize(NORMAL);
	vec3 safe_view = normalize(VIEW);
	vec3 safe_light = normalize(LIGHT);
	float atten = clamp(ATTENUATION, 0.0, 1.0);

	// 1. Half-Lambert Stylized Diffuse (Deadlock Style Shading)
	// Half-Lambert wrap to soften transitions on curved surfaces
	float NdotL = dot(safe_norm, safe_light);
	float half_lambert = clamp(NdotL * 0.5 + 0.5, 0.0, 1.0);

	// Two-tone soft step (illuminated vs ambient shadow)
	float shadow_threshold = 0.45;
	float shadow_feather = mix(0.04, 0.12, step_smoothness);
	float light_intensity = smoothstep(shadow_threshold - shadow_feather, shadow_threshold + shadow_feather, half_lambert);

	if (use_stepped) {
		light_intensity = mix(half_lambert, light_intensity, toon_intensity);
	}

	if (use_pattern) {
		vec2 p_uv = pattern_uv(UV, SCREEN_UV);
		light_intensity = mix(light_intensity, shape_value(light_intensity, p_uv, VIEWPORT_SIZE), pattern_blend);
		light_intensity = clamp(light_intensity, 0.0, 1.0);
	}

	// Ambient shadow floor so shaded areas retain detail and warmth:
	vec3 cool_ambient = vec3(0.75, 0.75, 0.85);
	vec3 shadow_ambient = ALBEDO * 0.4 * mix(cool_ambient, shadow_tint.rgb, shadow_tint_amount);
	vec3 lit_color = ALBEDO * LIGHT_COLOR;

	vec3 diffuse_result = mix(shadow_ambient, lit_color, light_intensity);

	// 2. Restrict Rim Light to Grazing Edges Only (Subtle edge catch without white halos)
	if (use_rim) {
		float NdotV = clamp(dot(safe_norm, safe_view), 0.0, 1.0);
		float rim = pow(1.0 - NdotV, 4.0) * clamp(NdotL, 0.0, 1.0);
		rim = smoothstep(0.4, 0.7, rim) * 0.25 * rim_blend;
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

	// Soft threshold modulated by roughness:
	float toon_spec = smoothstep(0.7, 0.85, raw_spec) * roughness_factor * 0.35 * (specular / 0.3);

	// 4. Color-Modulated Specular (No Blown-Out White Highlights)
	vec3 spec_tint = mix(LIGHT_COLOR, ALBEDO, 0.3) * METALLIC + LIGHT_COLOR * (1.0 - METALLIC) * 0.2;
	SPECULAR_LIGHT += clamp(spec_tint * toon_spec * atten, vec3(0.0), vec3(1.0));
}