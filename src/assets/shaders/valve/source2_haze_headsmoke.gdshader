shader_type spatial;
render_mode blend_mix, depth_draw_always, cull_disabled, unshaded;

// Haze Head Smoke Shader (Deadlock Source 2 pbr.vfx replication)
// Authentic volumetric black smoke plume:
// - Deep black / dark charcoal smoke core
// - Normal-mapped 3D billow curl definition
// - Subtle edge / grazing angle contour highlight
// - Mesh vertex color tinting (g_bMaskVertexColorTint1 = 1)
// - Dynamic UV scrolling flow (g_vAlbedoScrollSpeed1)
// - Alpha testing from opacity texture (hazev2_headsmoke_opacity)
// - Scalp-anchored smooth wave jitter (texture_jitter_mask)

// --- Vertex Jitter (Billowing Undulating Smoke) ---
uniform bool enable_jitter = true;
uniform float jitter_speed_a = 0.2;
uniform float jitter_speed_b = 0.5;
uniform vec3 jitter_amp_y = vec3(0.0, 1.0, 0.0);
uniform vec3 jitter_amp_z = vec3(1.0, 1.0, 2.0);
uniform vec3 jitter_freq_a = vec3(0.04, 0.04, 0.16);
uniform vec3 jitter_freq_b = vec3(0.05, 0.09, 0.17);
uniform sampler2D texture_jitter_mask : hint_default_white, filter_linear_mipmap;

// --- UV Transforms & Scrolling ---
uniform vec2 albedo_scroll_speed = vec2(0.1, 0.3);
uniform vec2 albedo_uv_scale = vec2(1.0, 1.0);
uniform vec2 albedo_uv_offset = vec2(0.0, 0.0);
uniform float albedo_scroll_quantize = 0.0;

// --- Textures & Normal Map ---
uniform sampler2D texture_albedo : hint_default_white, filter_linear_mipmap;
uniform sampler2D texture_normal_roughness : hint_normal, filter_linear_mipmap;

// --- Colors (from VMAT) ---
uniform vec4 color_tint : source_color = vec4(0.0, 0.0, 0.0, 1.0);
uniform bool mask_color_tint = true;
uniform bool use_vertex_color = true;
uniform float opacity_scale = 1.0;

// --- Alpha Testing / Cutout (from VMAT) ---
uniform float alpha_test_threshold : hint_range(0.0, 1.0) = 0.10;
uniform float alpha_angle_power = -0.176;

float quantize(float val, float step_size) {
	return step_size > 0.0 ? floor(val / step_size) * step_size : val;
}

vec2 quantize_vec2(vec2 val, float step_size) {
	return step_size > 0.0 ? floor(val / step_size) * step_size : val;
}

void vertex() {
	if (enable_jitter) {
		float t = TIME;
		vec3 v_src2 = VERTEX * 50.0; // convert Godot meters to Source 2 spatial units

		vec3 disp = vec3(0.0);
		disp.y += sin(t * jitter_speed_a * 5.0 + v_src2.z * jitter_freq_a.y) * jitter_amp_y.x;
		disp.z += cos(t * jitter_speed_a * 5.0 + v_src2.x * jitter_freq_a.z) * jitter_amp_z.x;
		disp.y += sin(t * jitter_speed_b * 5.0 + v_src2.z * jitter_freq_b.y) * jitter_amp_y.y;
		disp.z += cos(t * jitter_speed_b * 5.0 + v_src2.x * jitter_freq_b.z) * jitter_amp_z.z;

		// Mask jitter with vertical gradient so scalp connection does not pull apart
		float mask = texture(texture_jitter_mask, UV).r;
		VERTEX += disp * 0.015 * mask;
	}
}

void fragment() {
	// 1. Scroll UVs for animated smoke flow
	vec2 alb_scroll = albedo_scroll_speed * TIME;
	if (albedo_scroll_quantize > 0.0) {
		alb_scroll = quantize_vec2(alb_scroll, albedo_scroll_quantize);
	}
	vec2 alb_uv = UV * albedo_uv_scale + albedo_uv_offset + alb_scroll;
	vec4 tex_sample = texture(texture_albedo, alb_uv);

	// 2. Alpha Testing with view angle fade
	// Use abs(dot(NORMAL, VIEW)) so two-sided backfaces are NEVER blacked out or discarded!
	float ndotv = clamp(abs(dot(normalize(NORMAL), normalize(VIEW))), 0.0, 1.0);
	float edge = 1.0 - ndotv;

	float angle_fade = 1.0;
	if (abs(alpha_angle_power) > 0.01) {
		if (alpha_angle_power < 0.0) {
			// Negative angle power softens silhouette edges (standard Source 2 edge falloff)
			angle_fade = pow(clamp(edge, 0.05, 1.0), -alpha_angle_power);
		} else {
			// Positive angle power fades towards center
			angle_fade = pow(clamp(ndotv, 0.05, 1.0), alpha_angle_power);
		}
	}

	// Smoke density from opacity texture (supports alpha or red channel)
	float tex_density = max(tex_sample.a, tex_sample.r);
	float alpha = tex_density * opacity_scale * angle_fade;

	if (alpha < alpha_test_threshold) {
		discard;
	}

	// 3. Normal map output
	vec4 norm_map = texture(texture_normal_roughness, alb_uv);
	NORMAL_MAP = norm_map.rgb;
	NORMAL_MAP_DEPTH = 1.0;

	// 4. Color from VMAT data
	vec3 smoke_color = color_tint.rgb;
	if (use_vertex_color) {
		vec3 vert_col = COLOR.rgb;
		if (length(vert_col) > 0.01) {
			smoke_color *= vert_col;
		}
	}

	ALBEDO = smoke_color;
	ALPHA = alpha;
	ALPHA_SCISSOR_THRESHOLD = alpha_test_threshold;
}
