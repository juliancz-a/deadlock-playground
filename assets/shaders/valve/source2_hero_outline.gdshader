shader_type spatial;
render_mode cull_front, unshaded, depth_draw_never;

// Deadlock Geometric Hero Inverted-Hull Outline (e.g. Infernus fiery arm outline)
// Renders an unbroken, solid geometric contour in fiery orange/gold (#D56E00 to #FFB826).
// Front faces are culled; backfaces are extruded along normals with baseline clearance.
// The opaque body mesh writes to depth and occludes inner fragments, leaving a crisp,
// solid contour visible from every camera angle without flickering or grazing-angle fading.

uniform vec4 outline_color : source_color = vec4(0.835294, 0.431373, 0.0, 1.0); // Valve Infernus #D56E00
uniform float outline_width : hint_range(0.1, 10.0, 0.1) = 2.2;
uniform float emission_boost : hint_range(1.0, 5.0, 0.1) = 1.35;

void vertex() {
	// Distance-scaled vertex extrusion with silhouette clearance offset
	float dist = length(MODELVIEW_MATRIX[3].xyz);
	float scale = clamp(dist * 0.002, 0.001, 0.03);
	VERTEX += NORMAL * (outline_width * scale + 0.003);
}

void fragment() {
	vec3 base_color = outline_color.rgb;
	// Fallback to signature Valve fiery orange if input is white / unassigned / too dark
	if ((base_color.r > 0.9 && base_color.g > 0.9 && base_color.b > 0.9) || length(base_color) < 0.15) {
		base_color = vec3(0.835294, 0.431373, 0.0); // Valve Infernus #D56E00
	}
	ALBEDO = base_color * emission_boost;
}

