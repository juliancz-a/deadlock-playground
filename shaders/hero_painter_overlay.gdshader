shader_type spatial;
render_mode unshaded, blend_mix, depth_draw_opaque, cull_back;

uniform sampler2D overlay_texture : hint_default_transparent, filter_linear, repeat_disable;
uniform sampler2D g_tColor : source_color, filter_linear_mipmap, hint_default_white; // Base diffuse/AO
uniform vec2 position_in_atlas;
uniform vec2 size_in_atlas;
uniform uint atlas_index;
uniform float layer_opacity : hint_range(0.0, 1.0) = 1.0;
uniform bool is_paint_target = true;

varying vec2 v_atlas_uv;
varying vec2 v_mesh_uv;

void vertex() {
	// Source 2 models in Deadlock often lack a dedicated UV2 stream.
	// Fall back to primary UV coordinates so each submesh properly samples its allocated rect in the atlas.
	v_mesh_uv = (length(UV2) > 0.0001) ? UV2 : UV;
	v_atlas_uv = v_mesh_uv * size_in_atlas + position_in_atlas;
}

void fragment() {
	// Layer 21 (bit index 20) is used by camera brush to capture UVs and depths
	bool is_camera_brush = (CAMERA_VISIBLE_LAYERS & (1u << uint(20))) != 0u;
	if (is_camera_brush) {
		// Isolate painting strictly to the active target submesh/mesh
		if (!is_paint_target) {
			discard;
		}
		// Reject backfaces or grazing angles (> 89 degrees relative to brush camera)
		if (NORMAL.z <= 0.02) {
			discard;
		}
		uint exponential_bit = 1u << 30u;
		float distance = -VERTEX.z;
		uint distance_bits = (packHalf2x16(vec2(distance, 0)) & 0xFFE0u) << uint(13 - 5);
		uint index_sign_bit = (atlas_index & 0x4u) << uint(31 - 2);
		uint index_exp_bits = (atlas_index & 0x3u) << 24u;
		float b = uintBitsToFloat(exponential_bit | distance_bits | index_sign_bit | index_exp_bits);
		ALBEDO = vec3(v_atlas_uv - vec2(0.5), b);
		SPECULAR = 0.0;
		EMISSION = vec3(v_atlas_uv - vec2(0.5), b);
	} else {
		// Clamp atlas UV within submesh rect with half-texel inset to prevent sampling neighbors
		vec2 half_texel = vec2(0.5) / vec2(textureSize(overlay_texture, 0));
		vec2 min_atlas = position_in_atlas + half_texel;
		vec2 max_atlas = position_in_atlas + size_in_atlas - half_texel;
		vec2 clamped_uv = clamp(v_atlas_uv, min_atlas, max_atlas);

		vec4 paint = texture(overlay_texture, clamped_uv);

		ALBEDO = paint.rgb;
		ALPHA = paint.a;
	}
}
