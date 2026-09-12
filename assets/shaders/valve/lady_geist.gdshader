shader_type spatial;
render_mode blend_mix, cull_disabled;

// Lady Geist signature shader: crystalline translucent emerald green spectral arm.
// Features deep green crystal translucency, bright green grazing Fresnel rim,
// internal scattering, and vibrant emerald glow matching in-game reference.

group_uniforms Crystal.Colors;
uniform vec4 arm_color : source_color = vec4(0.04, 0.78, 0.40, 0.80);
uniform vec4 core_color : source_color = vec4(0.12, 0.95, 0.52, 1.0);
uniform vec4 rim_color : source_color = vec4(0.35, 1.35, 0.75, 1.0);

group_uniforms Crystal.Properties;
uniform float rim_power : hint_range(1.0, 5.0, 0.1) = 2.4;
uniform float emission_energy : hint_range(0.5, 5.0, 0.1) = 2.4;
uniform float roughness : hint_range(0.0, 1.0, 0.02) = 0.10;

uniform sampler2D albedo_texture : source_color, hint_default_white, filter_linear_mipmap;
uniform sampler2D normal_texture : hint_normal, filter_linear_mipmap;

void fragment() {
	vec3 view_dir = normalize(VIEW);
	vec3 norm = normalize(NORMAL);

	// Grazing angle Fresnel for glowing edge
	float n_dot_v = clamp(abs(dot(norm, view_dir)), 0.0, 1.0);
	float fresnel = pow(1.0 - n_dot_v, rim_power);

	vec4 base_tex = texture(albedo_texture, UV);
	vec3 base_tint = mix(arm_color.rgb, core_color.rgb, 0.35);

	vec3 final_albedo = mix(base_tint, arm_color.rgb, 0.6);
	final_albedo = mix(final_albedo, rim_color.rgb, fresnel * 0.5);

	ALBEDO = final_albedo;
	ALPHA = clamp(arm_color.a + fresnel * 0.30, 0.0, 1.0);
	ROUGHNESS = roughness;
	METALLIC = 0.05;
	SPECULAR = 0.9;
	CLEARCOAT = 0.8;
	CLEARCOAT_ROUGHNESS = 0.1;

	// Internal backlit scattering gives ethereal crystal depth
	BACKLIGHT = core_color.rgb * 0.7;

	// Glowing emerald rim outline
	EMISSION = rim_color.rgb * (fresnel * emission_energy);
}
