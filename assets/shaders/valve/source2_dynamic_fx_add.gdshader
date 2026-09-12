shader_type spatial;
render_mode blend_add, depth_draw_never, cull_disabled, unshaded;

// Blend mode toggles
uniform bool is_additive = true;
uniform bool is_alpha_test = false;
uniform float alpha_test_threshold : hint_range(0.0, 1.0) = 0.5;
uniform bool cull_front_faces = false;
uniform bool is_arm_glow = false;
uniform bool is_sparkle = false;
uniform float normal_offset = 0.0;

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
uniform vec2 albedo_scroll_speed = vec2(0.0, -0.12);
uniform float albedo_scroll_quantize = 0.0;
uniform vec2 albedo_uv_scale = vec2(1.0, 1.0);
uniform vec2 albedo_uv_offset = vec2(0.0);

uniform vec2 self_illum_scroll_speed = vec2(0.0, -0.18);
uniform vec2 self_illum_uv_scale = vec2(1.0, 1.0);
uniform vec2 self_illum_uv_offset = vec2(0.0);

uniform vec2 translucent_scroll_speed = vec2(0.0);
uniform vec2 translucent_uv_scale = vec2(1.0, 1.0);
uniform vec2 translucent_uv_offset = vec2(0.0);

// Colors & Emission
uniform vec4 color_tint : source_color = vec4(1.0);
uniform vec4 self_illum_tint : source_color = vec4(0.835294, 0.431373, 0.0, 1.0);
uniform vec4 glow_color : source_color = vec4(0.835294, 0.431373, 0.0, 1.0);
uniform float self_illum_scale = 1.8;
uniform float self_illum_fresnel_exponent = 0.0;
uniform float self_illum_albedo_factor = 0.0;
uniform float opacity_scale = 1.0;
uniform float alpha_angle_power = 0.0;

// Material PBR properties (kept for uniform compatibility)
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
        // Harmonic A
        disp.x += sin(t * jitter_speed_a * 10.0 + v_src2.y * jitter_freq_a.x) * jitter_amp_x.x;
        disp.y += sin(t * jitter_speed_a * 10.0 + v_src2.z * jitter_freq_a.y) * (jitter_amp_y.y > 0.0001 ? jitter_amp_y.y : jitter_amp_y.x);
        disp.z += cos(t * jitter_speed_a * 10.0 + v_src2.x * jitter_freq_a.z) * (jitter_amp_z.z > 0.0001 ? jitter_amp_z.z : jitter_amp_z.x);

        // Harmonic B
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
    if (cull_front_faces && FRONT_FACING) {
        discard;
    }

    if (is_sparkle) {
        vec2 scroll_uv = UV + self_illum_scroll_speed * TIME;
        vec4 mask_sample = texture(texture_albedo, scroll_uv);
        
        float star_mask = max(mask_sample.r, mask_sample.a);

        if (star_mask < 0.1) {
            discard;
        }

        vec3 tint = (length(self_illum_tint.rgb) > 0.01 && self_illum_tint.rgb != vec3(1.0)) 
            ? self_illum_tint.rgb 
            : vec3(0.85098, 0.776471, 0.219608);

        ALBEDO = tint * (self_illum_scale > 0.01 ? self_illum_scale : 7.154);
        ALPHA = star_mask;
    } else {
        // 1. Animated UVs
        vec2 alb_uv = UV * albedo_uv_scale + albedo_uv_offset;
        vec2 alb_scroll = albedo_scroll_speed * TIME;
        if (albedo_scroll_quantize > 0.0) {
            alb_scroll = quantize_vec2(alb_scroll, albedo_scroll_quantize);
        }
        alb_uv += alb_scroll;

        vec2 si_uv = UV * self_illum_uv_scale + self_illum_uv_offset + self_illum_scroll_speed * TIME;

        // 2. Textures
        vec4 albedo_tex = texture(texture_albedo, alb_uv);
        vec4 si_mask = texture(texture_self_illum_mask, si_uv);

        // 3. Flame Mask extraction
        float tex_a = albedo_tex.a;
        float si_lum = max(si_mask.r, max(si_mask.g, si_mask.b));
        float alb_lum = max(albedo_tex.r, max(albedo_tex.g, albedo_tex.b));

        float flame_mask = 1.0;
        if (tex_a > 0.001 && tex_a < 0.99) {
            flame_mask = tex_a;
            if (si_lum > 0.001 && si_lum < 0.99) {
                flame_mask *= si_lum;
            }
        } else if (si_lum > 0.001 && si_lum < 0.99) {
            flame_mask = si_lum;
        } else if (alb_lum > 0.001 && alb_lum < 0.99) {
            flame_mask = alb_lum;
        } else {
            flame_mask = max(tex_a, max(si_lum, alb_lum));
        }

        if (use_translucent_mask) {
            vec2 trans_uv = UV * translucent_uv_scale + translucent_uv_offset + translucent_scroll_speed * TIME;
            vec4 trans_tex = texture(texture_translucent_mask, trans_uv);
            flame_mask *= trans_tex.r;
        }

        if (use_vertex_color) {
            flame_mask *= COLOR.a;
        }

        if (is_alpha_test) {
            if (flame_mask < alpha_test_threshold) {
                discard;
            }
        }

        // 4. View Angle (Fresnel)
        float ndotv = clamp(abs(dot(normalize(NORMAL), normalize(VIEW))), 0.0, 1.0);

        // 5. Authentic Chroma & Emission
        vec3 final_glow = vec3(0.835294, 0.431373, 0.0);
        if (length(glow_color.rgb) > 0.01 && glow_color.rgb != vec3(1.0)) {
            final_glow = glow_color.rgb;
        } else if (length(self_illum_tint.rgb) > 0.01 && self_illum_tint.rgb != vec3(1.0)) {
            final_glow = self_illum_tint.rgb;
        } else if (length(glow_color.rgb) > 0.01) {
            final_glow = glow_color.rgb;
        }

        if (length(color_tint.rgb) > 0.01 && color_tint.rgb != vec3(1.0)) {
            final_glow *= color_tint.rgb;
        }

        float emission_boost = clamp(self_illum_scale, 1.0, 6.0);
        if (self_illum_fresnel_exponent > 0.01) {
            float fe = pow(clamp(1.0 - ndotv, 0.0, 1.0), self_illum_fresnel_exponent);
            emission_boost *= mix(1.0, 1.8, fe);
        }

        // 6. Opacity & Silhouette Falloff
               // 6. Opacity & Silhouette Falloff
        if (is_arm_glow) {
            float edge = 1.0 - ndotv;
            // Clean rim: center is 0.0 (unobscured dark arm), silhouette edge is 1.0
            float rim_mask = smoothstep(0.45, 0.92, edge);

            float rim = mix(0.12, 1.0, rim_mask);

            float mask = flame_mask;
            if (mask < 0.001) mask = 1.0;

            ALBEDO = final_glow * (mask * rim * 3.2);
            ALPHA = clamp(rim * mask * opacity_scale, 0.0, 1.0);
        } else {
            // Volumetric plumes (e.g. hair)
            float plume_rim = pow(clamp(1.0 - ndotv, 0.0, 1.0), 0.8);
            float plume_density = mix(0.75, 1.0, plume_rim);

            float mask = flame_mask;
            if (mask < 0.001) mask = 1.0;

            ALBEDO = final_glow * (mask * emission_boost);
            ALPHA = clamp(mask * plume_density * opacity_scale, 0.0, 1.0);
        }
    }
}