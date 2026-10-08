// SPDX-License-Identifier: AGPL-3.0-or-later
// The tear line across a booster pack: a faint dashed line, and over the stretch already swiped (cut_from..cut_to,
// 0..1 along the line) a bright glowing cut with a white-hot core.
shader_type spatial;
render_mode unshaded, blend_add, cull_disabled, depth_draw_never, depth_test_disabled, shadows_disabled;

uniform vec4 glow_color : source_color = vec4(0.35, 0.8, 1.0, 1.0);
uniform float cut_from = 0.0;
uniform float cut_to = 0.0;
uniform float time_offset = 0.0;
uniform float strength = 1.0; // fades the whole line out

void fragment() {
	float across = abs(UV.y - 0.5) * 2.0; // 0 at the line's center, 1 at its edges
	// Dashes drifting slowly along the line.
	float dash = step(0.5, fract(UV.x * 24.0 - TIME * 0.6));
	float dashed = dash * (smoothstep(0.3, 0.0, across) + 0.35 * exp(-across * across * 8.0)) * (0.7 + 0.3 * sin(TIME * 3.0 + time_offset));
	// The cut: a core and a wide glow, fading in at its ends.
	float inside = smoothstep(cut_from - 0.02, cut_from, UV.x) * (1.0 - smoothstep(cut_to, cut_to + 0.02, UV.x));
	float core = smoothstep(0.18, 0.0, across);
	float glow = exp(-across * across * 6.0);
	float cut = inside * (glow * 1.1 + core * 1.5);
	vec3 col = glow_color.rgb * (dashed * (1.0 - inside) + cut) + vec3(1.0) * core * inside * 0.8;
	ALBEDO = col * strength;
	ALPHA = clamp(max(dashed * (1.0 - inside), cut), 0.0, 1.0) * strength;
}
