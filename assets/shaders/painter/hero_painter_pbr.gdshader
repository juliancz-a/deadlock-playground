shader_type spatial;
render_mode blend_mix, depth_draw_opaque, cull_back;

// Hero PBR Painting Shader
// Accurately decodes Source 2 vertex coloring (COLOR) and hemi-octahedral normal encodings
// Uses true alpha-over blending to combine base diffuse with dynamic painting viewport canvas.

uniform sampler2D g_tColor : source_color, hint_default_white, filter_linear_mipmap;
uniform sampler2D u_paint_composite : source_color, hint_default_transparent, filter_linear_mipmap;
uniform float u_paint_layer_opacity : hint_range(0.0, 1.0) = 1.0;

uniform sampler2D g_tNormalRoughness : hint_normal, filter_linear_mipmap;
uniform sampler2D g_tAmbientOcclusion : hint_default_white, filter_linear_mipmap;

uniform float roughness : hint_range(0.0, 1.0) = 1.0;
uniform float metallic : hint_range(0.0, 1.0) = 0.0;
uniform float specular : hint_range(0.0, 1.0) = 0.5;
uniform float normal_depth : hint_range(0.0, 2.0) = 1.0;

vec3 DecodeHemiOctahedron(vec2 e) {
    vec2 temp = e * 2.0 - 1.0;
    vec3 n = vec3(temp.x + temp.y, temp.x - temp.y, 1.0 - abs(temp.x) - abs(temp.y));
    return normalize(n);
}

void fragment() {
    vec4 baseColor = texture(g_tColor, UV);
    vec4 paintLayer = texture(u_paint_composite, UV);

    // True alpha-over blend: retain original diffuse texture completely where paint canvas is transparent
    vec3 compositeAlbedo = mix(baseColor.rgb, paintLayer.rgb, paintLayer.a * u_paint_layer_opacity);

    // Decode COLOR vertex stream if non-zero; fallback to vec3(1.0) if vertex color is pitch black/unassigned
    vec3 vert = (COLOR.rgb != vec3(0.0)) ? COLOR.rgb : vec3(1.0);
    ALBEDO = compositeAlbedo * vert;

    vec4 normSample = texture(g_tNormalRoughness, UV);
    vec3 normal_decoded = DecodeHemiOctahedron(normSample.rg);
    NORMAL_MAP = normal_decoded * 0.5 + 0.5;
    NORMAL_MAP_DEPTH = normal_depth;

    ROUGHNESS = normSample.b * roughness;
    METALLIC = metallic;
    SPECULAR = specular;
    AO = texture(g_tAmbientOcclusion, UV).r;
}
