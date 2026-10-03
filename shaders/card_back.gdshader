// SPDX-License-Identifier: AGPL-3.0-or-later
// Arcanum's own procedural card back (original design, no third-party artwork).
shader_type canvas_item;

uniform vec2 rect_size = vec2(90.0, 126.0);
uniform float radius = 5.0;
uniform vec4 color_a : source_color = vec4(0.13, 0.09, 0.22, 1.0);
uniform vec4 color_b : source_color = vec4(0.30, 0.18, 0.45, 1.0);
uniform vec4 accent : source_color = vec4(0.85, 0.70, 0.35, 1.0);

float rounded_box(vec2 p, vec2 half_size, float r) {
	vec2 q = abs(p) - (half_size - vec2(r));
	return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - r;
}

void fragment() {
	vec2 p = UV * rect_size - rect_size * 0.5;
	float outer = rounded_box(p, rect_size * 0.5, radius);
	float inner = rounded_box(p, rect_size * 0.5 - vec2(rect_size.x * 0.07), radius * 0.6);

	// Diamond lattice background with a radial glow.
	vec2 uv = p / rect_size.x * 9.0;
	vec2 d = abs(fract(vec2(uv.x + uv.y, uv.x - uv.y) * 0.5) - 0.5);
	float lattice = smoothstep(0.03, 0.0, min(d.x, d.y));
	float glow = 1.0 - clamp(length(p / rect_size) * 1.6, 0.0, 1.0);
	vec3 col = mix(color_a.rgb, color_b.rgb, glow);
	col = mix(col, accent.rgb * 0.6, lattice * 0.35);

	// Central emblem: a ring with a four-point star.
	vec2 e = p / (rect_size.x * 0.5);
	float ring = smoothstep(0.03, 0.0, abs(length(e) - 0.42));
	float star = smoothstep(0.02, 0.0, abs(e.x) * abs(e.y) * 6.0 + length(e) * 0.12 - 0.05);
	col = mix(col, accent.rgb, max(ring, star));

	// Gold frame border.
	float frame = smoothstep(1.0, 0.0, abs(inner));
	col = mix(col, accent.rgb * 0.8, frame);
	if (inner > 0.0) col = vec3(0.06, 0.05, 0.08);

	COLOR = vec4(col, 1.0 - smoothstep(-1.0, 0.0, outer));
}
