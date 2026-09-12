shader_type spatial;
render_mode blend_mix, cull_back;

// Viscous signature shader: sleek, glossy, translucent green gelatinous suit.
// Delivers a wet, glassy slime material with clearcoat, subsurface scattering,
// and vibrant lime rim glow matching Deadlock's signature in-game aesthetic.

group_uniforms Slime.Colors;
uniform vec4 slime_color : source_color = vec4(0.12, 0.72, 0.32, 0.70);
uniform vec4 core_color : source_color = vec4(0.04, 0.35, 0.14, 1.0);
uniform vec4 rim_glow_color : source_color = vec4(0.40, 1.0, 0.55, 1.0);
uniform vec4 subsurface_color : source_color = vec4(0.15, 0.85, 0.40, 1.0);

group_uniforms Slime.Properties;
uniform float roughness : hint_range(0.0, 1.0, 0.02) = 0.06;
uniform float rim_power : hint_range(1.0, 6.0, 0.1) = 2.8;
uniform float rim_intensity : hint_range(0.0, 3.0, 0.1) = 1.2;

void fragment() {
	vec3 view_dir = normalize(VIEW);
	vec3 norm = normalize(NORMAL);

	// Smooth grazing Fresnel for gelatinous depth and edge glow
	float n_dot_v = clamp(dot(norm, view_dir), 0.0, 1.0);
	float fresnel = pow(1.0 - n_dot_v, rim_power);

	// Deep gelatinous color blend: rich inner volume graduating to translucent green
	vec3 col = mix(core_color.rgb, slime_color.rgb, 0.75);

	ALBEDO = col;
	ALPHA = clamp(slime_color.a + fresnel * 0.22, 0.0, 1.0);
	ROUGHNESS = roughness;
	METALLIC = 0.0;
	SPECULAR = 0.95;

	// Wet glassy lacquer finish
	CLEARCOAT = 1.0;
	CLEARCOAT_ROUGHNESS = 0.05;

	// Internal subsurface light scattering
	BACKLIGHT = subsurface_color.rgb * 0.5;

	// Vibrant toxic lime rim glow
	EMISSION = rim_glow_color.rgb * (fresnel * rim_intensity * 0.6);
}
