shader_type spatial;
render_mode cull_back, depth_draw_never;

// Blend mode toggles
uniform bool is_alpha_test = true;
uniform float alpha_test_threshold : hint_range(0.0, 1.0) = 0.079;

// Vertex Jitter
uniform bool enable_jitter = true;
uniform float jitter_speed_a = 2.0;
uniform float jitter_speed_b = 0.0;
uniform float jitter_quantize_time = 0.2;       // Billy's stop-motion 5 FPS jitter
uniform float jitter_quantize_disp = 1.0;
uniform vec3 jitter_amp_x = vec3(1.0, 0.0, 2.0);
uniform vec3 jitter_amp_y = vec3(0.0);
uniform vec3 jitter_amp_z = vec3(0.0);
uniform vec3 jitter_freq_a = vec3(0.02, 0.5, 0.08);
uniform vec3 jitter_freq_b = vec3(0.05, 0.09, 0.17);

// UV Transforms & Scrolling
uniform vec2 albedo_scroll_speed = vec2(0.2, 0.0);
uniform float albedo_scroll_quantize = 0.2;
uniform vec2 albedo_uv_scale = vec2(1.0, 1.0);
uniform vec2 albedo_uv_offset = vec2(0.0);

uniform vec2 translucent_scroll_speed = vec2(0.0, 0.1);
uniform vec2 translucent_uv_scale = vec2(1.0, 1.0);
uniform vec2 translucent_uv_offset = vec2(0.0);

uniform vec2 self_illum_scroll_speed = vec2(0.0);
uniform vec2 self_illum_uv_scale = vec2(1.0, 1.0);
uniform vec2 self_illum_uv_offset = vec2(0.0);

// Colors & Emission
uniform bool mask_color_tint = true;
uniform vec3 base_paper_color : source_color = vec3(0.462745, 0.392157, 0.247059);
uniform vec4 color_tint : source_color = vec4(0.905882, 0.360784, 0.784314, 1.0);
uniform vec4 self_illum_tint : source_color = vec4(1.0);
uniform float self_illum_scale = 2.374;
uniform float self_illum_albedo_factor = 1.0;
uniform float opacity_scale = 1.0;
uniform float alpha_angle_power = 2.059;

// Textures
uniform sampler2D texture_albedo : hint_default_white, filter_linear_mipmap;
uniform sampler2D texture_translucent_mask : hint_default_white, filter_linear_mipmap;
uniform sampler2D texture_self_illum_mask : hint_default_white, filter_linear_mipmap;
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

        // Convert Godot meter coordinates to Source 2 units (approx 50 units/m) for spatial frequencies
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

        VERTEX += disp * 0.02; // Scaled to meters
    }
}

void fragment() {
    // 1. Compute animated UVs with quantization
    vec2 alb_uv = UV * albedo_uv_scale + albedo_uv_offset;
    vec2 alb_scroll = albedo_scroll_speed * TIME;
    if (albedo_scroll_quantize > 0.0) {
        alb_scroll = quantize_vec2(alb_scroll, albedo_scroll_quantize);
    }
    alb_uv += alb_scroll;

    vec2 trans_uv = UV * translucent_uv_scale + translucent_uv_offset + translucent_scroll_speed * TIME;
    vec2 si_uv = UV * self_illum_uv_scale + self_illum_uv_offset + self_illum_scroll_speed * TIME;

    // 2. Sample textures
    vec4 albedo_tex = texture(texture_albedo, alb_uv);

    // 3. Alpha testing with angle power
    float ndotv = clamp(dot(NORMAL, VIEW), 0.0, 1.0);
    float angle_fade = 1.0;
    if (abs(alpha_angle_power) > 0.01) {
        if (alpha_angle_power > 0.0) {
            angle_fade = pow(clamp(1.0 - ndotv, 0.0, 1.0), alpha_angle_power);
        } else {
            angle_fade = pow(clamp(ndotv, 0.0, 1.0), abs(alpha_angle_power));
        }
    }

    float tex_a = albedo_tex.a;
    float alpha = tex_a * color_tint.a * opacity_scale * angle_fade;

    if (is_alpha_test) {
        if (alpha < alpha_test_threshold) {
            discard;
        }
    }

    // 4. Halftone paper & pink color mixing
    vec3 final_rgb;
    if (mask_color_tint && use_translucent_mask) {
        float tint_mask = texture(texture_translucent_mask, trans_uv).r;
        float paper_noise = (albedo_tex.r - 0.46) * 0.8;
        float tint_step = step(0.5, tint_mask + paper_noise);

        // Creamy tan paper color (warm comic cardboard/paper matching Source 2)
        vec3 paper_color = vec3(0.85, 0.76, 0.58) * (albedo_tex.rgb / 0.46);
        // Vibrant punchy comic magenta pink
        vec3 pink_color = color_tint.rgb * 1.1;

        final_rgb = mix(paper_color, pink_color, tint_step);
    } else {
        final_rgb = albedo_tex.rgb * base_paper_color * color_tint.rgb * 2.0;
    }

    // 5. Output shading: 100% full solid, opaque, matte comic paper (NO transparency!)
    ALBEDO = final_rgb;
    ALPHA = 1.0;
    SPECULAR = 0.0;
    ROUGHNESS = 1.0;
    METALLIC = 0.0;

    // 6. Subtle comic emission for punchy pink highlights (no blinding white flare)
    if (self_illum_scale > 0.0) {
        vec4 si_mask = texture(texture_self_illum_mask, si_uv);
        EMISSION = final_rgb * si_mask.r * min(self_illum_scale, 0.8);
    }
}
