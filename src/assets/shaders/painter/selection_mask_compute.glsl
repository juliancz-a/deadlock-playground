#[compute]
#version 450

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(rgba16f, set = 0, binding = 0) uniform restrict readonly image2D base_texture;
layout(rgba16f, set = 0, binding = 1) uniform restrict writeonly image2D selection_mask_image;

layout(push_constant, std430) uniform Params {
    vec4 target_color;
    vec4 submesh_rect;
    float tolerance;
    float isolate_submesh;
    float pad0;
    float pad1;
} params;

void main() {
    ivec2 uv_coords = ivec2(gl_GlobalInvocationID.xy);
    ivec2 size = imageSize(base_texture);
    if (uv_coords.x >= size.x || uv_coords.y >= size.y) {
        return;
    }

    vec4 base_col = imageLoad(base_texture, uv_coords);

    if (base_col.a < 0.001) {
        imageStore(selection_mask_image, uv_coords, vec4(0.0, 0.0, 0.0, 1.0));
        return;
    }

    if (params.isolate_submesh > 0.5) {
        vec2 norm_uv = (vec2(uv_coords) + vec2(0.5)) / vec2(size);
        if (norm_uv.x < params.submesh_rect.x ||
            norm_uv.y < params.submesh_rect.y ||
            norm_uv.x > (params.submesh_rect.x + params.submesh_rect.z) ||
            norm_uv.y > (params.submesh_rect.y + params.submesh_rect.w)) {
            imageStore(selection_mask_image, uv_coords, vec4(0.0, 0.0, 0.0, 1.0));
            return;
        }
    }

    float dist = distance(base_col.rgb, params.target_color.rgb);
    float mask = 1.0 - smoothstep(params.tolerance * 0.85, params.tolerance, dist);
    imageStore(selection_mask_image, uv_coords, vec4(vec3(mask), 1.0));
}
