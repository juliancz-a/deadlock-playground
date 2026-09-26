shader_type spatial;
render_mode cull_disabled, depth_draw_never, blend_add, unshaded;

// Blend mode toggles (set at material assignment or branch)
uniform bool is_additive = true;
uniform bool is_translucent = false;
uniform bool is_alpha_test = false;
uniform float alpha_test_threshold : hint_range(0.0, 1.0) = 0.5;

// Vertex Jitter
uniform bool enable_jitter = false;
uniform float jitter_speed_a = 1.0;
uniform float jitter_speed_b = 0.0;
uniform float jitter_quantize_time = 0.0;       // For Billy's stop-motion jitter
uniform float jitter_quantize_disp = 0.0;
uniform vec3 jitter_amp_x = vec3(0.0);
uniform vec3 jitter_amp_y = vec3(0.0);
uniform vec3 jitter_amp_z = vec3(0.0);
uniform vec3 jitter_freq_a = vec3(0.0);
uniform vec3 jitter_freq_b = vec3(0.0);

// UV Transforms & Scrolling
uniform vec2 albedo_scroll_speed = vec2(0.0);
uniform float albedo_scroll_quantize = 0.0;
uniform vec2 albedo_uv_scale = vec2(1.0, 1.0);
uniform vec2 albedo_uv_offset = vec2(0.0);

uniform vec2 self_illum_scroll_speed = vec2(0.0);
uniform vec2 self_illum_uv_scale = vec2(1.0, 1.0);
uniform vec2 self_illum_uv_offset = vec2(0.0);

uniform vec2 translucent_scroll_speed = vec2(0.0);
uniform vec2 translucent_uv_scale = vec2(1.0, 1.0);
uniform vec2 translucent_uv_offset = vec2(0.0);

// Colors & Emission
uniform bool mask_color_tint = false;
uniform vec3 base_paper_color : source_color = vec3(1.0);
uniform vec4 color_tint : source_color = vec4(1.0);
uniform vec4 self_illum_tint : source_color = vec4(0.0);
uniform vec4 glow_color : source_color = vec4(0.0);
uniform float self_illum_scale = 1.0;
uniform float self_illum_fresnel_exponent = 0.0;
uniform float self_illum_albedo_factor = 0.0;
uniform float opacity_scale = 1.0;
uniform float alpha_angle_power = 0.0;

// Material PBR properties (for solid surfaces like Lady Geist crystal arm)
uniform float roughness : hint_range(0.0, 1.0) = 0.5;
uniform float metallic : hint_range(0.0, 1.0) = 0.0;

// Textures
uniform sampler2D texture_albedo : hint_default_white, filter_linear_mipmap;
uniform sampler2D texture_normal_roughness : hint_normal, filter_linear_mipmap;
uniform sampler2D texture_self_illum_mask : hint_default_white, filter_linear_mipmap;
uniform sampler2D texture_translucent_mask : hint_default_white, filter_linear_mipmap;
uniform bool use_vertex_color = false;
uniform bool use_normal_map = false;
uniform bool use_translucent_mask = false;

float quantize(float val, float step_size) {
    return step_size > 0.0 ? floor(val / step_size) * step_size : val;
}

vec2 quantize_vec2(vec2 val, float step_size) {
    return step_size > 0.0 ? floor(val / step_size) * step_size : val;
}

void vertex() {
    if (enable_jitter) {
        float t = TIME;
        if (jitter_quantize_time > 0.0) {
            t = quantize(t, jitter_quantize_time);
        }

        // Convert Godot meter coordinates to Source 2 units (~50 units/m)
        vec3 v_src2 = VERTEX * 50.0;

        vec3 disp = vec3(0.0);
        // Harmonic A
        disp.x += sin(t * jitter_speed_a * 10.0 + v_src2.y * jitter_freq_a.x) * jitter_amp_x.x;
        disp.y += sin(t * jitter_speed_a * 10.0 + v_src2.z * jitter_freq_a.y) * jitter_amp_y.x;
        disp.z += cos(t * jitter_speed_a * 10.0 + v_src2.x * jitter_freq_a.z) * jitter_amp_z.x;

        // Harmonic B
        if (jitter_speed_b > 0.0) {
            disp.x += sin(t * jitter_speed_b * 10.0 + v_src2.y * jitter_freq_b.x) * jitter_amp_x.z;
            disp.y += sin(t * jitter_speed_b * 10.0 + v_src2.z * jitter_freq_b.y) * jitter_amp_y.z;
            disp.z += cos(t * jitter_speed_b * 10.0 + v_src2.x * jitter_freq_b.z) * jitter_amp_z.z;
        }

        if (jitter_quantize_disp > 0.0) {
            disp = floor(disp / jitter_quantize_disp) * jitter_quantize_disp;
        }

        VERTEX += disp * 0.02;
    }
}

void fragment() {
    // 1. Animated UVs
    vec2 alb_uv = UV * albedo_uv_scale + albedo_uv_offset;
    vec2 alb_scroll = albedo_scroll_speed * TIME;
    if (albedo_scroll_quantize > 0.0) {
        alb_scroll = quantize_vec2(alb_scroll, albedo_scroll_quantize);
    }
    alb_uv += alb_scroll;

    vec2 si_uv = UV * self_illum_uv_scale + self_illum_uv_offset + self_illum_scroll_speed * TIME;
    vec2 trans_uv = UV * translucent_uv_scale + translucent_uv_offset + translucent_scroll_speed * TIME;

    // 2. Sample Textures
    vec4 albedo_tex = texture(texture_albedo, alb_uv);
    vec4 si_mask = texture(texture_self_illum_mask, si_uv);

    // Color Tinting logic
    vec4 final_color;
    if (mask_color_tint && use_translucent_mask) {
        float tint_mask = texture(texture_translucent_mask, trans_uv).r;
        float paper_noise = (albedo_tex.r - 0.46) * 0.8;
        float tint_step = step(0.5, tint_mask + paper_noise);
        vec3 amber_color = albedo_tex.rgb * base_paper_color * 1.5;
        vec3 pink_color = color_tint.rgb * (albedo_tex.rgb * 1.8);
        vec3 tinted = mix(amber_color, pink_color, tint_step);
        final_color = vec4(tinted, albedo_tex.a * color_tint.a);
    } else {
        vec3 blended_albedo = albedo_tex.rgb * base_paper_color;
        final_color = vec4(blended_albedo * color_tint.rgb, albedo_tex.a * color_tint.a);
    }

    if (use_vertex_color) {
        final_color *= COLOR;
    }

    // 3. Fresnel & Rim Calculation
    float ndotv = clamp(abs(dot(normalize(NORMAL), normalize(VIEW))), 0.0, 1.0);
    float edge = 1.0 - ndotv;
    
    float angle_fade = 1.0;
    if (abs(alpha_angle_power) > 0.01) {
        if (alpha_angle_power > 0.0) {
            angle_fade = pow(clamp(edge, 0.0, 1.0), alpha_angle_power);
        } else {
            angle_fade = pow(clamp(ndotv, 0.0, 1.0), abs(alpha_angle_power));
        }
    }

    float fresnel = angle_fade;
    if (self_illum_fresnel_exponent > 0.01) {
        fresnel *= pow(clamp(edge, 0.0001, 1.0), self_illum_fresnel_exponent);
    }

    // 4. Alpha Testing
    float tex_a = albedo_tex.a;
    float alpha = tex_a * color_tint.a * opacity_scale * angle_fade;

    if (is_alpha_test) {
        if (alpha < alpha_test_threshold) {
            discard;
        }
    }

    // 5. Output Shading
    // Determine authoritative emission tint
    vec3 effective_tint = (length(glow_color.rgb) > 0.01) ? glow_color.rgb : self_illum_tint.rgb;
    if (length(effective_tint) <= 0.01) {
        effective_tint = vec3(0.835294, 0.431373, 0.0); // Safe Infernus fallback
    }

    if (is_additive) {
        // Crisp silhouette cutoff so the center of the arm remains clean
        float rim_mask = smoothstep(0.60, 0.95, edge);
        float mask_val = si_mask.r;
        if (mask_val < 0.001) mask_val = 1.0;

        // Drive color directly into ALBEDO in blend_add mode
        float scale = max(self_illum_scale, 1.0);
        ALBEDO = effective_tint * (mask_val * rim_mask * scale * 2.5);
        ALPHA = clamp(rim_mask * mask_val * opacity_scale, 0.0, 1.0);
    } else {
        ALBEDO = final_color.rgb;
        if (use_normal_map) {
            NORMAL_MAP = texture(texture_normal_roughness, alb_uv).rgb;
        }
        ROUGHNESS = roughness;
        METALLIC = metallic;

        if (self_illum_scale > 0.0 && (length(effective_tint) > 0.01 || self_illum_albedo_factor > 0.0)) {
            vec3 illum_color = mix(effective_tint, effective_tint * final_color.rgb, self_illum_albedo_factor);
            EMISSION = illum_color * si_mask.rgb * min(self_illum_scale, 3.0) * fresnel;
        }

        if (is_alpha_test) {
            ALPHA = 1.0;
            SPECULAR = 0.0;
            ROUGHNESS = 1.0;
            METALLIC = 0.0;
        } else if (is_translucent) {
            ALPHA = clamp(alpha, 0.0, 1.0);
        } else {
            ALPHA = 1.0;
        }
    }
}