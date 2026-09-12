shader_type spatial;
render_mode blend_mix, depth_draw_opaque, cull_back;

// Base skin transmissive tone (g_tNprTransmissiveColor)
uniform vec4 g_vSkinColor : source_color = vec4(0.556, 0.361, 0.274, 1.0);
uniform sampler2D g_tNprTransmissiveColor : source_color, filter_linear_mipmap;
uniform bool has_transmissive_texture = false;
uniform mat4 g_mAlbedoColorCorrect = mat4(1.0);

uniform sampler2D g_tNormalRoughness : hint_normal, filter_linear_mipmap;
uniform sampler2D g_tAmbientOcclusion : hint_default_white, filter_linear_mipmap;

uniform float roughness         : hint_range(0.0, 1.0) = 1.0;
uniform float metallic          : hint_range(0.0, 1.0) = 0.0;
uniform float normal_map_depth  : hint_range(0.0, 2.0) = 1.0;

vec3 DecodeHemiOctahedron(vec2 e) {
    vec2 temp = e * 2.0 - 1.0;
    vec3 n = vec3(temp.x + temp.y, temp.x - temp.y, 1.0 - abs(temp.x) - abs(temp.y));
    return normalize(n);
}

void fragment() {
    vec3 vert = COLOR.rgb;

    // 1. Transmissive color (g_tNprTransmissiveColor texture or g_vSkinColor vector) with CSB matrix
    vec3 rawTransColor = has_transmissive_texture 
        ? texture(g_tNprTransmissiveColor, UV).rgb 
        : g_vSkinColor.rgb;
    vec3 transColor = (vec4(rawTransColor, 1.0) * g_mAlbedoColorCorrect).rgb;

    // 2. Mix node in Blender graph (blend_type: MULTIPLY)
    // Input A = COLOR (vert), Input B = g_tNprTransmissiveColor (transColor)
    vec3 multiplied = vert * transColor;

    // 3. Color Ramp in Blender graph:
    // Input = Blue channel of COLOR (vert.b)
    // Elements: Pos 0.0 -> White [1,1,1,1], Pos 1.0 -> Black [0,0,0,1], Interpolation: LINEAR
    float ramp = clamp(1.0 - vert.b, 0.0, 1.0);

    // 4. Mix.001 node in Blender graph (blend_type: MIX):
    // Factor = Color Ramp (ramp)
    // Input A = COLOR (vert)
    // Input B = Mix (multiplied)
    // Output = mix(vert, multiplied, ramp)
    vec3 finalAlbedo = mix(vert, multiplied, ramp);

    // 5. Normal & Roughness from g_tNormalRoughness (Separate Color.001: Blue -> Roughness)
    vec4 normSample = texture(g_tNormalRoughness, UV);
    NORMAL_MAP = DecodeHemiOctahedron(normSample.rg) * 0.5 + 0.5;
    NORMAL_MAP_DEPTH = normal_map_depth;

    ALBEDO = clamp(finalAlbedo, vec3(0.0), vec3(1.0));
    ROUGHNESS = normSample.b * roughness;
    METALLIC = metallic;
    SPECULAR = 0.5;
    AO = texture(g_tAmbientOcclusion, UV).r;
}