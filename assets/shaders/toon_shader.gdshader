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
	ROUGHNESS = 0.35;

	if (emission_energy > 0.0) {
		vec4 em_tex = texture(emission_texture, uv);
		EMISSION = emission_color.rgb * em_tex.rgb * emission_energy;
	}
}

void light() {
	float ndotl = clamp(dot(NORMAL, LIGHT), 0.0, 1.0) * ATTENUATION;
	float light = ndotl;

	if (use_stepped) {
		float light_mult = light * steps;
		float step_base = floor(light_mult);
		float light_factor = light_mult - step_base;

		light_factor = smoothstep(0.5 - step_smoothness, 0.5 + step_smoothness, light_factor);
		float stepped_light = (step_base + light_factor) / steps;
		light = mix(ndotl, stepped_light, toon_intensity);
	}

	if (use_pattern) {
		vec2 p_uv = pattern_uv(UV, SCREEN_UV);
		light = mix(light, shape_value(light, p_uv, VIEWPORT_SIZE), pattern_blend);
	}

	vec3 diffuse = vec3(light) * (LIGHT_COLOR / PI);
	diffuse = (shadow_tint.rgb * (1.0 - light) * shadow_tint_amount) + diffuse * light;

	if (use_rim) {
		float rim_value = fresnel(rim_amount, NORMAL, VIEW);
		rim_value = smoothstep(0.5 - (rim_smoothness / 2.0), 0.5 + (rim_smoothness / 2.0), rim_value);
		rim_value = mix(rim_value, rim_value * light, rim_mask_shadow);
		diffuse += rim_color.rgb * rim_value * rim_blend;
	}

	DIFFUSE_LIGHT += ALBEDO * diffuse;

	// Specular reflection highlights (preserves form and depth on blacks and greys)
	vec3 half_vec = normalize(VIEW + LIGHT);
	float NdotH = max(dot(normalize(NORMAL), half_vec), 0.0);
	float spec_term = pow(max(light, 0.0), 50.0) * 0.7 + pow(NdotH, 32.0) * 0.3;
	vec3 spec_tint = mix(vec3(1.0), ALBEDO, metallic);
	SPECULAR_LIGHT += (LIGHT_COLOR / PI) * spec_term * specular * spec_tint;
}