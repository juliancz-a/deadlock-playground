shader_type spatial;
render_mode blend_mix, depth_draw_opaque, cull_back;

// Vertex-Color Head/Hair PBR Shader for Deadlock (Citadel)
// Applies vertex coloring into head/hair/turban meshes where pigmentation is baked
// into the vertex color stream (COLOR.rgb) rather than standard diffuse textures.

// Base skin/hair transmissive tone (g_tNprTransmissiveColor or g_vSkinColor)
uniform vec4 g_vSkinColor : source_color = vec4(0.556, 0.361, 0.274, 1.0);
uniform sampler2D g_tNprTransmissiveColor : source_color, filter_linear_mipmap;
uniform bool has_transmissive_texture = false;
uniform mat4 g_mAlbedoColorCorrect = mat4(1.0);

// Direct Material Parameter Overrides (synced to patched .vmat_c on VPK export)
uniform vec4 color_tint : source_color = vec4(1.0, 1.0, 1.0, 1.0);
uniform float vertex_color_strength : hint_range(0.0, 1.0) = 1.0;
uniform vec3 albedo_csb = vec3(1.0, 1.0, 1.0); // x=Contrast, y=Saturation, z=Brightness

uniform sampler2D g_tNormalRoughness : hint_normal, filter_linear_mipmap;
uniform sampler2D g_tAmbientOcclusion : hint_default_white, filter_linear_mipmap;

uniform float roughness         : hint_range(0.0, 1.0) = 1.0;
uniform float metallic          : hint_range(0.0, 1.0) = 0.0;
uniform float normal_map_depth  : hint_range(0.0, 2.0) = 1.0;

// Self-illumination / Emissive parameters (e.g. Vindicta spectral limbs)
uniform bool enable_self_illum = false;
uniform vec4 self_illum_tint : source_color = vec4(1.0);
uniform float self_illum_scale = 1.0;
uniform sampler2D texture_self_illum_mask : hint_default_white, filter_linear_mipmap;
uniform vec2 self_illum_scroll_speed = vec2(0.0);
uniform float self_illum_fresnel_exponent = 0.0;
uniform float self_illum_albedo_factor = 0.0;

// Stylized NPR Stepped Toon & Shadow Tint controls
uniform float toon_intensity : hint_range(0.0, 1.0) = 0.0;
uniform float step_smoothness : hint_range(0.0, 1.0) = 0.3;
uniform vec4 shadow_tint : source_color = vec4(0.18, 0.16, 0.26, 1.0);
uniform float shadow_tint_amount : hint_range(0.0, 1.0) = 0.40;

// Multi-channel interactive toggles & Solo channel preview
uniform bool enable_color_map = true;
uniform bool enable_ao = true;
uniform bool enable_normal = true;
uniform bool enable_roughness = true;
uniform bool enable_shadow_map = true;
uniform bool has_shadow_map = false;
uniform int debug_view_channel = 0; // 0=Combined, 1=Color, 2=AO, 3=Normal, 4=Roughness, 5=Shadow, 6=Emissive, 7=Metalness
uniform bool has_painter_base_override = false;
uniform sampler2D painter_base_texture : hint_default_transparent, filter_linear, repeat_enable;
uniform float alpha_scissor_threshold = 0.0;


vec3 DecodeHemiOctahedron(vec2 e) {
    vec2 temp = e * 2.0 - 1.0;
    vec3 n = vec3(temp.x + temp.y, temp.x - temp.y, 1.0 - abs(temp.x) - abs(temp.y));
    return normalize(n);
}

void fragment() {
    if (has_painter_base_override && alpha_scissor_threshold > 0.0) {
        if (texture(painter_base_texture, fract(UV)).a < alpha_scissor_threshold) {
            discard;
        }
    }
    vec3 vert = mix(vec3(1.0), COLOR.rgb, vertex_color_strength);

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
    vec3 finalAlbedo = mix(vert, multiplied, ramp) * color_tint.rgb;
    float csbLum = dot(finalAlbedo, vec3(0.2126, 0.7152, 0.0722));
    finalAlbedo = mix(vec3(csbLum), finalAlbedo, albedo_csb.y);
    finalAlbedo = ((finalAlbedo - vec3(0.5)) * albedo_csb.x + vec3(0.5)) * albedo_csb.z;
    finalAlbedo = clamp(finalAlbedo, vec3(0.0), vec3(1.0));

    // 5. Normal & Roughness from g_tNormalRoughness (Separate Color.001: Blue -> Roughness)
    vec4 normSample = texture(g_tNormalRoughness, UV);
    vec3 decodedNorm = DecodeHemiOctahedron(normSample.rg) * 0.5 + 0.5;
    NORMAL_MAP = enable_normal ? decodedNorm : vec3(0.5, 0.5, 1.0);
    NORMAL_MAP_DEPTH = enable_normal ? normal_map_depth : 0.0;

    float ao_val = enable_ao ? texture(g_tAmbientOcclusion, UV).r : 1.0;
    float rough_val = enable_roughness ? normSample.b * roughness : roughness;
    vec3 color_out = enable_color_map ? clamp(finalAlbedo, vec3(0.0), vec3(1.0)) : vec3(0.85);

    ALBEDO = color_out;
    ROUGHNESS = rough_val;
    METALLIC = metallic;
    SPECULAR = 0.5;
    AO = ao_val;
    AO_LIGHT_AFFECT = enable_ao ? 0.65 : 0.0;

    if (debug_view_channel > 0) {
        vec3 dbg = color_out;
        if (debug_view_channel == 2) dbg = vec3(ao_val);
        else if (debug_view_channel == 3) dbg = decodedNorm;
        else if (debug_view_channel == 4) dbg = vec3(normSample.b);
        else if (debug_view_channel == 5) dbg = has_transmissive_texture ? texture(g_tNprTransmissiveColor, UV).rgb : shadow_tint.rgb;
        ALBEDO = vec3(0.0);
        EMISSION = dbg;
        AO = 1.0;
    }

    // 6. Self-illumination & Emissive spectral pulse
    if (enable_self_illum) {
        vec2 si_uv = UV + self_illum_scroll_speed * TIME;
        vec4 si_mask = texture(texture_self_illum_mask, si_uv);
        float si_val = max(si_mask.r, max(si_mask.g, si_mask.b));

        vec3 illum_color = mix(self_illum_tint.rgb, finalAlbedo * self_illum_tint.rgb, self_illum_albedo_factor);

        if (self_illum_fresnel_exponent > 0.01) {
            float ndotv = clamp(abs(dot(normalize(NORMAL), normalize(VIEW))), 0.0, 1.0);
            float fresnel = pow(clamp(1.0 - ndotv, 0.0, 1.0), self_illum_fresnel_exponent);
            illum_color *= mix(1.0, 2.0, fresnel);
        }

        EMISSION = illum_color * (si_val * self_illum_scale);
    }
}

void light() {
	if (debug_view_channel > 0) {
		DIFFUSE_LIGHT = vec3(0.0);
		SPECULAR_LIGHT = vec3(0.0);
	} else if (toon_intensity > 0.01) {
		vec3 safe_norm = normalize(NORMAL);
		vec3 safe_light = normalize(LIGHT);
		float NdotL = dot(safe_norm, safe_light);
		float half_lambert = clamp(NdotL * 0.5 + 0.5, 0.0, 1.0);

		float shadow_threshold = 0.45;
		float shadow_feather = mix(0.04, 0.12, step_smoothness);
		float light_step = smoothstep(shadow_threshold - shadow_feather, shadow_threshold + shadow_feather, half_lambert);
		float light_intensity = mix(half_lambert, light_step, toon_intensity);

		vec3 cool_ambient = vec3(0.75, 0.75, 0.85);
		vec3 base_shadow = ALBEDO * 0.35 * cool_ambient;
		vec3 tinted_shadow = ALBEDO * shadow_tint.rgb * 1.5;
		vec3 shadow_ambient = mix(base_shadow, tinted_shadow, shadow_tint_amount);
		vec3 lit_color = ALBEDO * LIGHT_COLOR;

		DIFFUSE_LIGHT += mix(shadow_ambient, lit_color, light_intensity) * clamp(ATTENUATION, 0.0, 1.0);
	} else {
		float NdotL = clamp(dot(NORMAL, LIGHT), 0.0, 1.0);
		DIFFUSE_LIGHT += ALBEDO * LIGHT_COLOR * NdotL * ATTENUATION;
	}
}

