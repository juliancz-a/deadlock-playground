shader_type spatial;
render_mode blend_mix, cull_disabled, depth_draw_always;

// Lady Geist fur shawl shader matching Blender node pipeline:
// - UV tile mapping scaled by (4.0, 0.5)
// - Base detail texture multiplied by vertex color gradient (COLOR.rgb)
// - Normal map from RGB channels and roughness from Alpha channel (geist_shawl_test_vmat_g_tnormalroughness)
// - Two-sided rendering with backfaces fully visible (cull_disabled)
// - Supports both opaque base cloth (ghost_shawl) and alpha-scissor fur cards (ghost_shawl_fur01-05)

uniform vec2 uv_scale = vec2(4.0, 0.5);
uniform sampler2D texture_albedo : source_color, filter_linear_mipmap, repeat_enable;
uniform sampler2D texture_normal_roughness : hint_normal, filter_linear_mipmap, repeat_enable;

uniform float normal_map_depth : hint_range(0.0, 2.0) = 1.0;
uniform float roughness_mult : hint_range(0.0, 2.0) = 1.0;
uniform float metallic : hint_range(0.0, 1.0) = 0.0;
uniform float specular : hint_range(0.0, 1.0) = 0.5;

uniform bool is_fur_shell = false;
uniform float alpha_scissor_threshold : hint_range(0.0, 1.0) = 0.35;

void fragment() {
	vec2 tiled_uv = UV * uv_scale;
	vec4 tex_color = texture(texture_albedo, tiled_uv);

	// Protect against zero/unpainted vertex colors on submeshes
	vec3 vert_color = COLOR.rgb;
	if (length(vert_color) < 0.01) {
		vert_color = vec3(1.0);
	}

	// Multiply base detail texture by vertex color gradient
	ALBEDO = tex_color.rgb * vert_color;

	if (is_fur_shell) {
		ALPHA = tex_color.a;
		ALPHA_SCISSOR_THRESHOLD = alpha_scissor_threshold;
	} else {
		// Base shawl cloth (ghost_shawl) is solid, opaque geometry
		ALPHA = 1.0;
	}

	// Normal & Roughness Unpacking
	vec4 norm_rough = texture(texture_normal_roughness, tiled_uv);
	NORMAL_MAP = norm_rough.rgb;
	NORMAL_MAP_DEPTH = normal_map_depth;
	ROUGHNESS = norm_rough.a * roughness_mult;
	METALLIC = metallic;
	SPECULAR = specular;
}

