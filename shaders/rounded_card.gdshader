// SPDX-License-Identifier: AGPL-3.0-or-later
// Masks a card image with rounded corners.
shader_type canvas_item;

uniform vec2 rect_size = vec2(90.0, 126.0);
uniform float radius = 5.0;

void fragment() {
	vec4 tex = texture(TEXTURE, UV);
	vec2 p = UV * rect_size;
	vec2 q = abs(p - rect_size * 0.5) - (rect_size * 0.5 - vec2(radius));
	float d = length(max(q, 0.0)) - radius;
	tex.a *= 1.0 - smoothstep(-1.0, 0.0, d);
	COLOR = tex;
}
