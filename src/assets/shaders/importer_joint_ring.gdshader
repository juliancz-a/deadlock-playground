shader_type spatial;
render_mode unshaded, cull_disabled, depth_test_disabled;

uniform vec4 ring_color : source_color = vec4(1.0, 1.0, 1.0, 1.0);
uniform float is_selected = 0.0;
uniform vec4 select_ring_color : source_color = vec4(1.0, 0.94, 0.55, 1.0);

void vertex() {
    // Camera-facing billboard preserving instance scale
    float sx = length(MODEL_MATRIX[0].xyz);
    float sy = length(MODEL_MATRIX[1].xyz);
    float sz = length(MODEL_MATRIX[2].xyz);
    MODELVIEW_MATRIX = VIEW_MATRIX * mat4(
        INV_VIEW_MATRIX[0] * sx,
        INV_VIEW_MATRIX[1] * sy,
        INV_VIEW_MATRIX[2] * sz,
        MODEL_MATRIX[3]
    );
}

void fragment() {
    vec2 p = UV * 2.0 - 1.0;
    float d = length(p);
    float aa = max(fwidth(d), 0.035);

    vec4 base_col = ring_color * COLOR;

    // Dark contrast halo around the main hollow ring so it reads cleanly over light clothes
    float outer_shadow = smoothstep(0.40 - aa, 0.40 + aa, d) * (1.0 - smoothstep(0.70 - aa, 0.70 + aa, d));
    // Crisp hollow main ring (transparent center so underlying mesh vertex stays visible!)
    float main_ring = smoothstep(0.45 - aa, 0.45 + aa, d) * (1.0 - smoothstep(0.65 - aa, 0.65 + aa, d));
    // Outer selection highlight ring when hovered or selected
    float sel_ring = is_selected > 0.5
        ? smoothstep(0.78 - aa, 0.78 + aa, d) * (1.0 - smoothstep(0.95 - aa, 0.95 + aa, d))
        : 0.0;

    vec3 col = vec3(0.05, 0.06, 0.07);
    float alpha = outer_shadow * 0.55 * base_col.a;

    if (main_ring > 0.001) {
        col = mix(col, base_col.rgb, main_ring);
        alpha = max(alpha, main_ring * base_col.a);
    }
    if (sel_ring > 0.001) {
        col = mix(col, select_ring_color.rgb, sel_ring);
        alpha = max(alpha, sel_ring * select_ring_color.a);
    }

    if (alpha < 0.02) {
        discard;
    }
    ALBEDO = col;
    ALPHA = alpha;
}

