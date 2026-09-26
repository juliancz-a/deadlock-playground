shader_type spatial;
render_mode blend_mix, depth_draw_always, cull_disabled, unshaded;

// Vertex Jitter for organic flame lick and flicker
uniform bool enable_jitter = true;
uniform float jitter_speed_a = 1.746;
uniform float jitter_speed_b = 0.0;
uniform float jitter_quantize_time = 0.0;
uniform float jitter_quantize_disp = 0.0;
uniform vec3 jitter_amp_x = vec3(0.015);
uniform vec3 jitter_amp_y = vec3(0.015);
uniform vec3 jitter_amp_z = vec3(0.015);
uniform vec3 jitter_freq_a = vec3(10.0);
uniform vec3 jitter_freq_b = vec3(0.0);
uniform float normal_offset = 0.0;

// UV Transforms & Scrolling
uniform vec2 albedo_scroll_speed = vec2(-0.6, 0.2);
uniform float albedo_scroll_quantize = 0.0;
uniform vec2 albedo_uv_scale = vec2(1.0, 1.0);
uniform vec2 albedo_uv_offset = vec2(0.0);

uniform vec2 self_illum_scroll_speed = vec2(-0.6, 0.2);
uniform vec2 self_illum_uv_scale = vec2(1.0, 1.0);
uniform vec2 self_illum_uv_offset = vec2(0.0);

// Colors & Emission
uniform vec4 color_tint : source_color = vec4(1.0);
uniform vec4 self_illum_tint : source_color = vec4(1.0);
uniform float self_illum_scale = 5.16;

// Textures
uniform sampler2D texture_albedo : hint_default_white, filter_linear_mipmap;
uniform sampler2D texture_self_illum_mask : hint_default_white, filter_linear_mipmap;
uniform bool use_vertex_color = false;

// Alpha testing (for flame ribbon edge cutouts if present)
uniform bool is_alpha_test = false;
uniform float alpha_test_threshold : hint_range(0.0, 1.0) = 0.1;

float quantize(float val, float step_size) {
    return step_size > 0.0 ? floor(val / step_size) * step_size : val;
}

vec2 quantize_vec2(vec2 val, float step_size) {
    return step_size > 0.0 ? floor(val / step_size) * step_size : val;
}

void vertex() {
    if (normal_offset > 0.0) {
        VERTEX += NORMAL * normal_offset;
    }

    if (enable_jitter) {
        float t = TIME;
        if (jitter_quantize_time > 0.0) {
            t = quantize(t, jitter_quantize_time);
        }

        vec3 v_src2 = VERTEX * 50.0;

        vec3 disp = vec3(0.0);
        disp.x += sin(t * jitter_speed_a * 10.0 + v_src2.y * jitter_freq_a.x) * jitter_amp_x.x;
        disp.y += sin(t * jitter_speed_a * 10.0 + v_src2.z * jitter_freq_a.y) * (jitter_amp_y.y > 0.0001 ? jitter_amp_y.y : jitter_amp_y.x);
        disp.z += cos(t * jitter_speed_a * 10.0 + v_src2.x * jitter_freq_a.z) * (jitter_amp_z.z > 0.0001 ? jitter_amp_z.z : jitter_amp_z.x);

        if (jitter_speed_b > 0.0) {
            disp.x += sin(t * jitter_speed_b * 10.0 + v_src2.y * jitter_freq_b.x) * jitter_amp_x.z;
            disp.y += sin(t * jitter_speed_b * 10.0 + v_src2.z * jitter_freq_b.y) * (jitter_amp_y.z > 0.0001 ? jitter_amp_y.z : jitter_amp_y.y);
            disp.z += cos(t * jitter_speed_b * 10.0 + v_src2.x * jitter_freq_b.z) * (jitter_amp_z.z > 0.0001 ? jitter_amp_z.z : jitter_amp_z.x);
        }

        if (jitter_quantize_disp > 0.0) {
            disp = floor(disp / jitter_quantize_disp) * jitter_quantize_disp;
        }

        VERTEX += disp * 0.02;
    }
}

void fragment() {
    // 1. Compute animated UVs with optional quantization
    vec2 alb_uv = UV * albedo_uv_scale + albedo_uv_offset;
    vec2 alb_scroll = albedo_scroll_speed * TIME;
    if (albedo_scroll_quantize > 0.0) {
        alb_scroll = quantize_vec2(alb_scroll, albedo_scroll_quantize);
    }
    alb_uv += alb_scroll;

    // 2. Sample the scrolling flame texture (provides native incandescent yellow-to-orange core)
    vec4 albedo_tex = texture(texture_albedo, alb_uv);

    if (is_alpha_test) {
        if (albedo_tex.a < alpha_test_threshold) {
            discard;
        }
    }

    vec3 flame_tex = albedo_tex.rgb;
    if (use_vertex_color) {
        flame_tex *= COLOR.rgb;
    }

    vec3 tint = (length(self_illum_tint.rgb) > 0.01) ? self_illum_tint.rgb : vec3(1.0);
    if (length(color_tint.rgb) > 0.01 && color_tint.rgb != vec3(1.0)) {
        tint *= color_tint.rgb;
    }

    // Boost the texture's native fire colors by Valve's self-illum scale (5.16 * 0.8)
    ALBEDO = flame_tex * tint * (self_illum_scale * 0.8);
    ALPHA = 1.0; // Solid fire mass with depth write
}
