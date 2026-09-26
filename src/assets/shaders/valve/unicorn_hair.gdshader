shader_type spatial;
render_mode blend_mix, depth_draw_opaque, cull_back;

// Deadlock Celeste / Unicorn Hair Shader
// Delivers soft pastel lavender hair with crisp anime-style NPR diffuse stepping,
// subtle specular catch, and soft inner Fresnel self-illumination clamped to safe
// sub-bloom levels to prevent pure-white HDR blowout.

uniform sampler2D g_tColor : source_color, filter_linear_mipmap;
uniform vec4 g_vColorTint : source_color = vec4(1.0);
uniform vec4 g_vSelfIllumTint : source_color = vec4(0.31, 0.42, 0.91, 1.0);
uniform float g_flSelfIllumScale1 = 0.15;
uniform float roughness : hint_range(0.0, 1.0) = 0.95;
uniform float specular : hint_range(0.0, 1.0) = 0.0;

// NPR Stepped Shading Controls
uniform vec4 shadow_tint : source_color = vec4(0.18, 0.16, 0.26, 1.0);
uniform float shadow_tint_amount : hint_range(0.0, 1.0) = 0.40;
uniform float step_smoothness : hint_range(0.0, 1.0) = 0.30;

void fragment() {
	vec4 tex = texture(g_tColor, UV);
	vec3 base_color = tex.rgb * g_vColorTint.rgb;
	ALBEDO = base_color;
	ROUGHNESS = roughness;
	SPECULAR = specular;

	// Soft inner Fresnel tint clamped to safe LDR/sub-bloom threshold
	float ndotv = clamp(dot(normalize(NORMAL), normalize(VIEW)), 0.0, 1.0);
	float fresnel_mask = pow(1.0 - ndotv, 2.5);
	vec3 emission_tint = g_vSelfIllumTint.rgb * g_flSelfIllumScale1;
	EMISSION = clamp(emission_tint * fresnel_mask, vec3(0.0), vec3(0.35));
}

void light() {
	vec3 safe_norm = normalize(NORMAL);
	vec3 safe_light = normalize(LIGHT);
	float NdotL = dot(safe_norm, safe_light);
	float half_lambert = clamp(NdotL * 0.5 + 0.5, 0.0, 1.0);

	// Crisp anime two-tone diffuse step
	float shadow_threshold = 0.45;
	float shadow_feather = mix(0.04, 0.12, step_smoothness);
	float light_intensity = smoothstep(shadow_threshold - shadow_feather, shadow_threshold + shadow_feather, half_lambert);

	vec3 cool_ambient = vec3(0.75, 0.75, 0.85);
	vec3 shadow_ambient = ALBEDO * 0.45 * mix(cool_ambient, shadow_tint.rgb, shadow_tint_amount);
	vec3 lit_color = ALBEDO * LIGHT_COLOR;

	DIFFUSE_LIGHT += mix(shadow_ambient, lit_color, light_intensity) * clamp(ATTENUATION, 0.0, 1.0);
}
