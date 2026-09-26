shader_type spatial;
render_mode blend_add, cull_disabled, unshaded;

// Deadlock Celeste / Unicorn Horn Glow Shader
// Additive emissive pulsing spiral core overlay matching Valve in-game aesthetic

uniform sampler2D g_tColor : source_color, filter_linear_mipmap;
uniform sampler2D g_tTintMask : source_color, filter_linear_mipmap;
uniform bool has_tint_mask = false;
uniform vec4 g_vSelfIllumTint : source_color = vec4(0.5137, 0.3765, 0.8941, 1.0);
uniform float g_flSelfIllumScale = 1.0;

void fragment() {
	vec4 glow_tex = texture(g_tColor, UV);
	vec3 mask_col = vec3(1.0);
	if (has_tint_mask) {
		mask_col = texture(g_tTintMask, UV).rgb;
	}
	float pulse = 0.5 * sin(3.0 * TIME) + 0.5;
	float intensity = (0.25 + 0.35 * pulse) * clamp(g_flSelfIllumScale * 0.1, 0.2, 1.0);
	ALBEDO = g_vSelfIllumTint.rgb * COLOR.rgb * glow_tex.rgb * mask_col * intensity;
}
