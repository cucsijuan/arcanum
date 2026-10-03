// SPDX-License-Identifier: AGPL-3.0-or-later
// Default playmat: dark surface with a subtle grid.
shader_type canvas_item;

uniform vec2 rect_size = vec2(1920.0, 540.0);
uniform vec4 base_color : source_color = vec4(0.125, 0.129, 0.145, 1.0);
uniform vec4 line_color : source_color = vec4(0.165, 0.169, 0.188, 1.0);
uniform float cell = 24.0;

void fragment() {
	vec2 p = UV * rect_size;
	vec2 g = abs(fract(p / cell - 0.5) - 0.5) * cell;
	float line = 1.0 - smoothstep(0.0, 1.0, min(g.x, g.y));
	COLOR = mix(base_color, line_color, line);
}
