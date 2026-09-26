#[compute]
#version 450

// Invocations in the (x, y, z) dimension
layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(rgba16f, set = 0, binding = 0) uniform restrict readonly image2D framebuffer;

layout(set = 1, binding = 0) uniform sampler2D brush_shape;

layout(push_constant, std430) uniform Params {
	vec4 brush_color;
    float delta;
    float max_distance;
    float start_distance_fade;
    float min_bleed;
    float max_bleed;
    float is_erase;
    float blend_mode;
    float use_selection_mask;
} params;

layout(rgba16f, set = 2, binding = 0) uniform restrict image2D overlay_texture_0;
layout(rgba16f, set = 2, binding = 1) uniform restrict image2D overlay_texture_1;
layout(rgba16f, set = 2, binding = 2) uniform restrict image2D overlay_texture_2;
layout(rgba16f, set = 2, binding = 3) uniform restrict image2D overlay_texture_3;
layout(rgba16f, set = 2, binding = 4) uniform restrict image2D overlay_texture_4;
layout(rgba16f, set = 2, binding = 5) uniform restrict image2D overlay_texture_5;
layout(rgba16f, set = 2, binding = 6) uniform restrict image2D overlay_texture_6;
layout(rgba16f, set = 2, binding = 7) uniform restrict image2D overlay_texture_7;
layout(rgba16f, set = 2, binding = 8) uniform restrict readonly image2D base_texture_0;
layout(rgba16f, set = 2, binding = 9) uniform restrict readonly image2D selection_mask_image;


// The code we want to execute in each invocation
void main() {
    // gl_GlobalInvocationID.x uniquely identifies this invocation across all work groups
    ivec2 framebuffer_coords = ivec2(gl_GlobalInvocationID.xy);

    if ((framebuffer_coords.x >= imageSize(framebuffer).x) || (framebuffer_coords.y >= imageSize(framebuffer).y)) {
		return;
	}

    vec4 framebuffer_color = imageLoad(framebuffer, framebuffer_coords);
    framebuffer_color.xy = framebuffer_color.xy + vec2(0.5f);
    
    uint b_bits = floatBitsToUint(framebuffer_color.b);
    if ((b_bits & (1u << 30u)) == 0u) {
        return;
    }
    // get distance in 13-23 -> 11 bits truncated
    float distance = unpackHalf2x16(b_bits >> uint(13 - 5) & 0xFFE0u).x;
    float distance_value = clamp(abs(distance) / params.max_distance, 0.0f, 1.0f);

    //distance fade
    float distance_fade = 1.0f;
    if (params.start_distance_fade < 1.0f) {
        if (distance_value >= params.start_distance_fade) {
            distance_fade = 1.0f - ((distance_value - params.start_distance_fade) / (1.0f - params.start_distance_fade));
        }
    }

    //distance bleed
    int bleed = int(params.max_bleed);
    if (params.max_bleed > 0.0f && params.min_bleed < params.max_bleed) {
        bleed = int(mix(params.min_bleed, params.max_bleed, distance_value));
    }

    vec2 brush_shape_uv = (vec2(framebuffer_coords) + vec2(0.5f)) / vec2(imageSize(framebuffer));
    vec4 brush_shape_color = texture(brush_shape, brush_shape_uv);
    float brush_shape_val = brush_shape_color.a * distance_fade;
    vec4 brush_color = vec4(params.brush_color.rgb, brush_shape_val * params.delta * params.brush_color.a);

#define PROCESS_TEXTURE(tex) { \
    ivec2 overlay_texture_coords = ivec2(framebuffer_color.xy * vec2(imageSize(tex))); \
    ivec2 tex_size = imageSize(tex); \
    for (int y = -bleed; y <= bleed; y++) { \
        for (int x = -bleed; x <= bleed; x++) { \
            ivec2 bleed_coords = overlay_texture_coords + ivec2(x, y); \
            if (bleed_coords.x < 0 || bleed_coords.y < 0 || bleed_coords.x >= tex_size.x || bleed_coords.y >= tex_size.y) continue; \
            float mask_val = 1.0f; \
            if (params.use_selection_mask > 0.5f) { \
                mask_val = imageLoad(selection_mask_image, bleed_coords).r; \
                if (mask_val < 0.5f) continue; \
                mask_val = clamp((mask_val - 0.5f) / 0.5f, 0.0f, 1.0f); \
            } \
            if (mask_val <= 0.001f) continue; \
            vec4 existing_color = imageLoad(tex, bleed_coords); \
            if (isnan(existing_color.a) || isnan(existing_color.r) || isnan(existing_color.g) || isnan(existing_color.b)) { \
                existing_color = vec4(0.0f); \
            } \
            existing_color = clamp(existing_color, vec4(0.0f), vec4(1.0f)); \
            float exist_a = existing_color.a; \
            if (params.is_erase > 0.5f || params.brush_color.a < 0.0f) { \
                float erase_amount = clamp(brush_shape_val * params.delta * abs(params.brush_color.a) * mask_val, 0.0f, 1.0f); \
                float new_alpha = clamp(exist_a - erase_amount, 0.0f, 1.0f); \
                imageStore(tex, bleed_coords, vec4(existing_color.rgb, new_alpha)); \
            } else { \
                vec4 base_val = imageLoad(base_texture_0, bleed_coords); \
                vec3 base_col = (base_val.a > 0.001f) ? base_val.rgb : vec3(1.0f); \
                vec3 under_col = base_col; \
                vec3 blended_brush_rgb; \
                int mode = int(params.blend_mode + 0.5f); \
                if (mode == 1) { \
                    blended_brush_rgb = under_col * brush_color.rgb; \
                } else if (mode == 2) { \
                    blended_brush_rgb = vec3(1.0f) - (vec3(1.0f) - under_col) * (vec3(1.0f) - brush_color.rgb); \
                } else if (mode == 3) { \
                    blended_brush_rgb = mix( \
                        2.0f * under_col * brush_color.rgb, \
                        vec3(1.0f) - 2.0f * (vec3(1.0f) - under_col) * (vec3(1.0f) - brush_color.rgb), \
                        step(vec3(0.5f), under_col) \
                    ); \
                } else if (mode == 4) { \
                    blended_brush_rgb = min(under_col, brush_color.rgb); \
                } else if (mode == 5) { \
                    blended_brush_rgb = max(under_col, brush_color.rgb); \
                } else if (mode == 6) { \
                    blended_brush_rgb = under_col / max(vec3(1.0f) - brush_color.rgb, vec3(0.001f)); \
                } else { \
                    blended_brush_rgb = brush_color.rgb; \
                } \
                float stroke_alpha = clamp(brush_color.a * mask_val, 0.0f, 1.0f); \
                float out_alpha = clamp(stroke_alpha + exist_a * (1.0f - stroke_alpha), 0.0f, 1.0f); \
                vec3 out_color = (out_alpha > 0.0001f) ? \
                    clamp((blended_brush_rgb * stroke_alpha + existing_color.rgb * exist_a * (1.0f - stroke_alpha)) / out_alpha, vec3(0.0f), vec3(1.0f)) : \
                    clamp(blended_brush_rgb, vec3(0.0f), vec3(1.0f)); \
                imageStore(tex, bleed_coords, vec4(out_color, out_alpha)); \
            } \
        } \
    } } 

    // get index in bit 24-25 and bit 31
    uint atlas_index = (b_bits >> uint(31 - 2) & 0x4u) | (b_bits >> uint(24) & 0x3u);
    switch (atlas_index) {
        case 0: PROCESS_TEXTURE(overlay_texture_0) break;
        case 1: PROCESS_TEXTURE(overlay_texture_1) break;
        case 2: PROCESS_TEXTURE(overlay_texture_2) break;
        case 3: PROCESS_TEXTURE(overlay_texture_3) break;
        case 4: PROCESS_TEXTURE(overlay_texture_4) break;
        case 5: PROCESS_TEXTURE(overlay_texture_5) break;
        case 6: PROCESS_TEXTURE(overlay_texture_6) break;
        case 7: PROCESS_TEXTURE(overlay_texture_7) break;
    }

}