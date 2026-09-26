shader_type spatial;
render_mode blend_add, depth_draw_never, cull_disabled, unshaded;

varying float v_offset;

uniform sampler2D sparkle_mask : filter_linear_mipmap;
uniform vec4 sparkle_tint : source_color = vec4(0.85098, 0.776471, 0.219608, 1.0);
uniform float emission_scale = 7.154;
uniform float sweep_speed = 0.5;

void vertex() {
    // Desynchronize billboard quads across 3D space (left shoulder, right shoulder, floating heights)
    v_offset = VERTEX.x * 1.5 + VERTEX.y * 2.0 + VERTEX.z * 1.2;
}

void fragment() {
    // 1. Sample the star sprite statically at its quad UVs (anchored, NO repeat_enable)
    vec4 mask_tex = texture(sparkle_mask, UV);
    float raw_star = max(max(mask_tex.r, mask_tex.g), max(mask_tex.b, mask_tex.a));

    if (raw_star < 0.1) {
        discard;
    }

    // 2. Horizontal sweep wipe:
    // Create a scrolling phase across UV.x so the star wipes in and out horizontally without soft alpha gradients
    float sweep_phase = fract(TIME * sweep_speed + UV.x * 0.5 + v_offset);
    
    // Crisp step: star is visible during active window of the sweep, then cuts off cleanly
    float wipe = step(sweep_phase, 0.65) * step(0.15, sweep_phase);

    if (wipe <= 0.01) {
        discard;
    }

    // 3. Crisp radiant gold output matching Source 2
    ALBEDO = sparkle_tint.rgb * (raw_star * emission_scale);
    ALPHA = raw_star;
}
