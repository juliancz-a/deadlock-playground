shader_type spatial;
render_mode blend_mix, depth_draw_always, cull_back;

group_uniforms Glass.Properties;
uniform vec4 glass_tint : source_color = vec4(0.06, 0.07, 0.09, 0.55);
uniform float roughness : hint_range(0.0, 1.0, 0.005) = 0.0667;
uniform float metallic : hint_range(0.0, 1.0, 0.005) = 0.116;
uniform float specular : hint_range(0.0, 1.0, 0.01) = 0.90;
uniform float clearcoat : hint_range(0.0, 1.0, 0.01) = 1.0;
uniform float clearcoat_roughness : hint_range(0.0, 1.0, 0.01) = 0.05;

group_uniforms Glass.Fresnel;
uniform float fresnel_power : hint_range(0.5, 8.0, 0.1) = 4.0;
uniform float fresnel_rim_strength : hint_range(0.0, 2.0, 0.05) = 0.65;
uniform vec4 fresnel_rim_color : source_color = vec4(0.85, 0.90, 0.95, 1.0);

group_uniforms Glass.Refraction;
// g_flCloakRefractAmount: 0.075 for head dome (subtle warp), 0.0 for gun canister (crisp interior)
uniform float refract_amount : hint_range(0.0, 0.2, 0.005) = 0.0;

group_uniforms Glass.SelfIllum;
uniform bool enable_self_illum = false;
uniform vec4 self_illum_tint : source_color = vec4(0.0, 1.0, 0.765, 1.0);
uniform float self_illum_scale = 1.0;
uniform float self_illum_albedo_factor = 0.0;
uniform vec2 self_illum_scroll_speed = vec2(0.0, 0.0);
uniform vec2 albedo_scroll_speed = vec2(0.0, 0.0);
uniform sampler2D self_illum_mask : hint_default_white, filter_linear_mipmap;
uniform bool use_self_illum_mask = false;

group_uniforms Glass.Textures;
uniform sampler2D albedo_texture : source_color, filter_linear_mipmap;
uniform sampler2D normal_texture : hint_normal, filter_linear_mipmap;
uniform sampler2D screen_texture : hint_screen_texture, filter_linear_mipmap;

void fragment() {
    // 1. Normal mapping (animated UVs for scrolling glass variants like Viscous)
    vec2 alb_uv = UV + albedo_scroll_speed * TIME;
    vec3 normal = NORMAL;
    vec4 normal_sample = texture(normal_texture, alb_uv);
    if (length(normal_sample.rgb) > 0.1) {
        vec3 n_map = normal_sample.rgb * 2.0 - 1.0;
        normal = normalize(TANGENT * n_map.x + BINORMAL * n_map.y + NORMAL * n_map.z);
    }
    NORMAL = normal;

    // 2. Optical Transmission (Beer-Lambert Absorption)
    // Refraction ONLY displaces background when refract_amount > 0.001.
    // Gun canister stays crisp and unwarped at 0.0; head dome gets subtle optical bend at 0.075.
    vec2 screen_offset = (refract_amount > 0.001) ? normal.xy * refract_amount : vec2(0.0);
    vec3 background = texture(screen_texture, SCREEN_UV + screen_offset).rgb;

    // 3. Specular Fresnel Rim — uses abs(dot) so grazing angles from either side catch the rim
    float ndotv = clamp(abs(dot(normalize(NORMAL), normalize(VIEW))), 0.0, 1.0);
    float fresnel = pow(1.0 - ndotv, fresnel_power);
    vec3 rim = fresnel_rim_color.rgb * (fresnel * fresnel_rim_strength);

    // 4. Tint & Texture Absorption
    // Absorption darkens and colors transmitted light without adding opaque milky diffuse wash
    vec4 alb_sample = texture(albedo_texture, alb_uv);
    vec3 base_tint = glass_tint.rgb;
    if (length(alb_sample.rgb) > 0.01) {
        base_tint = mix(base_tint, alb_sample.rgb, 0.35);
    }
    vec3 absorption = pow(base_tint, vec3(1.2));
    vec3 transmitted = background * absorption;

    // 5. Self-Illumination and Animated Liquid/Bubbles in Glass (e.g. Viscous glowing liquid head)
    vec3 emissive = vec3(0.0);
    if (enable_self_illum && self_illum_scale > 0.0) {
        vec2 si_uv = UV + self_illum_scroll_speed * TIME;
        float mask_val = 1.0;
        if (use_self_illum_mask) {
            mask_val = texture(self_illum_mask, si_uv).r;
        }
        emissive = self_illum_tint.rgb * mask_val * self_illum_scale;
        if (self_illum_albedo_factor > 0.0) {
            emissive = mix(emissive, emissive * base_tint, self_illum_albedo_factor);
        }
    }

    ALBEDO = transmitted + rim + emissive;
    ALPHA = 1.0;

    ROUGHNESS = roughness;
    METALLIC = metallic;
    SPECULAR = specular;
    CLEARCOAT = clearcoat;
    CLEARCOAT_ROUGHNESS = clearcoat_roughness;
}
