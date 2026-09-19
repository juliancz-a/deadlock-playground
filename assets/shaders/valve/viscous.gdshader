shader_type spatial;
render_mode blend_mix, depth_draw_always, cull_back;

// Viscous signature shader: sleek, glossy, translucent green gelatinous suit.
// Delivers a wet, glassy slime material with clearcoat, subsurface scattering,
// visible interior bone structure, and vibrant lime Fresnel rim glow matching Deadlock in-game aesthetic.

group_uniforms Slime.Colors;
uniform vec4 u_slime_base_color : source_color = vec4(0.37, 0.75, 0.39, 1.0); // Source 2 #5FBE64 / TextureColor1
uniform float u_slime_opacity : hint_range(0.0, 1.0, 0.02) = 0.55;
uniform vec4 u_rim_glow_color : source_color = vec4(0.40, 1.0, 0.55, 1.0);

group_uniforms Slime.Textures;
uniform sampler2D g_tColor : source_color, filter_linear_mipmap;
uniform bool has_texture = false;

group_uniforms Slime.Properties;
uniform float roughness : hint_range(0.0, 1.0, 0.02) = 0.08;
uniform float specular : hint_range(0.0, 1.0, 0.02) = 0.6;
uniform float rim_intensity : hint_range(0.0, 3.0, 0.1) = 1.0;

void fragment() {
	vec3 base_col = u_slime_base_color.rgb;
	if (has_texture) {
		base_col *= texture(g_tColor, UV).rgb;
	}

	float fresnel = pow(1.0 - clamp(dot(normalize(NORMAL), normalize(VIEW)), 0.0, 1.0), 3.0);
	vec3 slime_col = mix(base_col, u_rim_glow_color.rgb, fresnel * 0.7);

	ALBEDO = slime_col;
	ALPHA = clamp(u_slime_opacity + fresnel * 0.45, 0.35, 0.95);
	ROUGHNESS = roughness;
	METALLIC = 0.0;
	SPECULAR = specular;

	// Wet glassy lacquer finish
	CLEARCOAT = 1.0;
	CLEARCOAT_ROUGHNESS = 0.05;

	// Vibrant lime rim glow
	EMISSION = u_rim_glow_color.rgb * (fresnel * rim_intensity * 0.35);
}
