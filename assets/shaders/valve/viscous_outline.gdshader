shader_type spatial;
render_mode cull_front, unshaded, depth_draw_always;

// Deadlock Viscous Inverted-Hull Outline Shader
// Renders an inverted-hull rim stroke: front faces are culled, backfaces are
// extruded along normals, and camera-facing fragments are discarded per Rule 1
// of the materials guide to prevent solid silhouette occlusion.

uniform vec4 g_vSolidOutlineTint : source_color = vec4(0.407843, 0.94902, 0.415686, 1.0);
uniform vec4 outline_color : source_color = vec4(0.407843, 0.94902, 0.415686, 1.0);
uniform float outline_thickness : hint_range(0.0, 0.05, 0.001) = 0.004;

void vertex() {
	VERTEX += NORMAL * outline_thickness;
}

void fragment() {
	float ndotv = abs(dot(normalize(NORMAL), normalize(VIEW)));
	if (ndotv > 0.45) {
		discard;
	}
	vec3 col = g_vSolidOutlineTint.rgb;
	// Fallback to outline_color if g_vSolidOutlineTint is default dark gray or unconfigured
	if ((abs(col.r - 0.102) < 0.01 && abs(col.g - 0.102) < 0.01 && abs(col.b - 0.102) < 0.01) ||
	    (abs(col.r - 0.08) < 0.01 && abs(col.g - 0.08) < 0.01 && abs(col.b - 0.08) < 0.01)) {
		col = outline_color.rgb;
	}
	ALBEDO = col;
}
