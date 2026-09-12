shader_type spatial;
render_mode blend_add, depth_draw_never, cull_disabled, unshaded;

// Source 2 Dynamic Glow & Energy Shader
// Replicates Deadlock's animated flow, ectoplasm, and energy layers
// (Vindicta aura, Geist arm shimmer, Infernus arm flame, Mirage cyclonewrap)

uniform vec4 glow_color : source_color = vec4(0.835294, 0.431373, 0.0, 1.0);
uniform vec2 scroll_speed = vec2(0.0, 0.0);
uniform float fresnel_exponent = 2.0;
uniform float opacity_scale = 1.0;
uniform sampler2D noise_mask : hint_default_white;
uniform bool enable_vertex_jitter = false;
uniform vec3 jitter_amplitude = vec3(0.015, 0.015, 0.015);
uniform vec3 jitter_frequency = vec3(10.0, 10.0, 10.0);

void vertex() {
	if (enable_vertex_jitter) {
		VERTEX += sin(TIME * jitter_frequency + VERTEX) * jitter_amplitude;
	}
}

void fragment() {
	// Animate texture UV coordinates along scroll direction
	vec2 moving_uv = UV + scroll_speed * TIME;

	// Grazing Fresnel rim mask (using abs for two-sided cull_disabled geometry)
	vec3 norm = normalize(NORMAL);
	vec3 view_dir = normalize(VIEW);
	float n_dot_v = clamp(abs(dot(norm, view_dir)), 0.0, 1.0);
	float exp_val = max(fresnel_exponent, 0.001);
	float fresnel = pow(1.0 - n_dot_v, exp_val);

	// Ensure direct-facing surfaces retain a solid glowing core (35%) rather than going 100% invisible
	float rim = mix(0.35, 1.0, fresnel);

	// Ensure the glow color dominates the chromatic output:
	vec3 final_glow = glow_color.rgb;

	// If a mask exists, modulate the intensity, NEVER desaturate the color:
	float mask = texture(noise_mask, moving_uv).r;

	// If noise_mask is null or white, fallback to standard rim:
	if (mask < 0.001) mask = 1.0;

	ALBEDO = final_glow * (mask * rim * 2.5);
	ALPHA = clamp(rim * mask * opacity_scale, 0.0, 1.0);

	// DEBUG: If this shows solid orange in the viewport, the C# extraction and uniform binding WORKED,
	// and the issue was previous math/texture multiplications washing it out.
	ALBEDO = glow_color.rgb * 2.0;
	ALPHA = 1.0;
}
