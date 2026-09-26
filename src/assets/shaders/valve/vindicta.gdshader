shader_type spatial;
render_mode blend_add, depth_draw_never, cull_disabled, unshaded;

// Vindicta signature shader: spectral ghostly cyan aura.
// Applied to her ghost_glow mesh and spectral extremities.
// Features inverted grazing Fresnel, animated ectoplasmic FBM noise, and vivid cyan HDR glow.

uniform vec4 aura_color : source_color = vec4(0.18, 0.78, 1.0, 0.9);
uniform vec4 inner_core_color : source_color = vec4(0.65, 0.94, 1.0, 1.0);
uniform float emission_energy : hint_range(0.5, 5.0, 0.1) = 2.4;
uniform float fresnel_power : hint_range(0.5, 4.0, 0.1) = 2.0;
uniform float noise_speed : hint_range(0.1, 3.0, 0.1) = 0.8;

// Procedural pseudo-random 2D noise generator
float hash(vec2 p) {
	vec3 p3 = fract(vec3(p.xyx) * 0.1031);
	p3 += dot(p3, p3.yzx + 33.33);
	return fract((p3.x + p3.y) * p3.z);
}

float noise(vec2 p) {
	vec2 i = floor(p);
	vec2 f = fract(p);
	f = f * f * (3.0 - 2.0 * f);
	
	float a = hash(i);
	float b = hash(i + vec2(1.0, 0.0));
	float c = hash(i + vec2(0.0, 1.0));
	float d = hash(i + vec2(1.0, 1.0));
	
	return mix(mix(a, b, f.x), mix(c, d, f.x), f.y);
}

float fbm(vec2 p) {
	float total = 0.0;
	float amp = 0.5;
	for (int i = 0; i < 3; i++) {
		total += noise(p) * amp;
		p *= 2.05;
		amp *= 0.5;
	}
	return total;
}

void fragment() {
	vec3 view_dir = normalize(VIEW);
	vec3 norm = normalize(NORMAL);
	
	// Inverted grazing Fresnel: aura glows brightest along silhouette edges
	float n_dot_v = clamp(abs(dot(norm, view_dir)), 0.0, 1.0);
	float fresnel = pow(1.0 - n_dot_v, fresnel_power);
	
	// Soft animated ectoplasm stream
	vec2 anim_uv = UV * 10.0 + vec2(0.0, -TIME * noise_speed * 0.3);
	float n = fbm(anim_uv);
	
	// Spectral intensity combination
	float intensity = smoothstep(0.1, 0.85, fresnel * (0.6 + 0.4 * n));
	vec3 glow = mix(aura_color.rgb, inner_core_color.rgb, pow(fresnel, 1.8));
	
	ALBEDO = glow * intensity * emission_energy;
	ALPHA = clamp(intensity * aura_color.a, 0.0, 1.0);
}
