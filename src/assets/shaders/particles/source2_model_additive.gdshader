shader_type spatial;
render_mode blend_add, depth_draw_never, cull_disabled, unshaded;

uniform sampler2D noise_texture : source_color, filter_linear_mipmap_anisotropic, repeat_enable, hint_default_transparent;
uniform vec4 illum_color : source_color = vec4(0.255, 0.663, 1.0, 1.0); // Source 2 blue-white energy shell (65, 169, 255)
uniform float base_alpha_test : hint_range(0.0, 1.0) = 0.0;
uniform float fresnel_power : hint_range(0.5, 8.0) = 2.5;
uniform float overbright_factor : hint_range(1.0, 16.0) = 2.5;

varying vec4 v_custom;

void vertex() {
    v_custom = INSTANCE_CUSTOM;
}

void fragment() {
    // 1. Per-instance Worley noise scrolling from INSTANCE_CUSTOM (Field 10 = ScrollX, Field 27 = ScrollY)
    vec2 scroll_offset = vec2(v_custom.x, v_custom.y);
    vec2 scrolled_uv = UV + scroll_offset;
    
    vec4 noise_sample = texture(noise_texture, scrolled_uv);
    
    // 2. Spherical Fresnel rim glow
    float fresnel = pow(1.0 - clamp(dot(NORMAL, VIEW), 0.0, 1.0), fresnel_power);
    
    // 3. Alpha test from Field 26
    float threshold = (v_custom.z > 0.0) ? v_custom.z : base_alpha_test;
    float alpha = COLOR.a * (noise_sample.r * 0.7 + fresnel * 0.8);
    
    float final_alpha = alpha * illum_color.a;
    if (final_alpha <= 0.005 || alpha < threshold) {
        discard;
    }
    
    // 4. Energy shell self-illumination
    vec3 shell_color = illum_color.rgb * COLOR.rgb * (noise_sample.rgb + vec3(fresnel * 1.5)) * overbright_factor;
    
    ALBEDO = shell_color;
    ALPHA = final_alpha;
}
