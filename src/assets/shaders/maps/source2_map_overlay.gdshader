shader_type spatial;
render_mode cull_disabled, depth_draw_never, blend_mix;

// --- Textures ---
uniform sampler2D texture_albedo : source_color, filter_linear_mipmap, repeat_enable;
uniform sampler2D texture_normal_roughness : hint_default_black, filter_linear_mipmap, repeat_enable;

// --- Parameters ---
uniform vec4 albedo_tint : source_color = vec4(1.0);
instance uniform vec4 instance_tint : source_color = vec4(1.0);
uniform float roughness_mult = 1.0;
uniform bool has_albedo_texture = true;
uniform bool has_normal_texture = false;
uniform bool use_vertex_color = false;

varying float v_has_tangent;

void vertex() {
	v_has_tangent = length(TANGENT) > 0.01 ? 1.0 : 0.0;
	// Offset vertices outward along normal so decals and graffiti sit cleanly above wall/floor geometry
	VERTEX += NORMAL * 0.002;
}

void fragment() {
	if (!has_albedo_texture) {
		discard;
	}

	vec4 tex = texture(texture_albedo, UV);
	vec4 col = albedo_tint * instance_tint;

	float alpha = col.a;
	if (use_vertex_color) {
		// In Source 2 decals, vertex COLOR.a is the authored overlay opacity
		alpha *= COLOR.a;
	}

	if (tex.a < 0.99) {
		alpha *= tex.a;
	} else {
		// Decal texture has no alpha channel (or uniform 1.0 alpha on black background)
		// Derive opacity from RGB luminance/max channel so black background is transparent
		float max_channel = max(tex.r, max(tex.g, tex.b));
		alpha *= max_channel;
	}

	if (alpha < 0.05) {
		discard;
	}

	ALBEDO = col.rgb * tex.rgb;
	ALPHA = alpha;

	if (has_normal_texture) {
		vec4 nr = texture(texture_normal_roughness, UV);
		if (v_has_tangent > 0.5) {
			vec2 temp = vec2(nr.r + nr.g - 1.003922, nr.r - nr.g);
			float nz = 1.0 - abs(temp.x) - abs(temp.y);
			vec3 normal_ts = normalize(vec3(temp.x, -temp.y, nz));
			NORMAL_MAP = normal_ts * 0.5 + 0.5;
		}
		ROUGHNESS = clamp(nr.b * roughness_mult, 0.25, 0.95);
	} else {
		ROUGHNESS = 0.85;
	}

	METALLIC = 0.0;
	SPECULAR = 0.2;
}
