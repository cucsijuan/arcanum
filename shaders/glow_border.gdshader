// SPDX-License-Identifier: AGPL-3.0-or-later
// Rounded border with a soft outer glow. The inside of the card stays fully transparent.
// The rect is larger than the card by `pad` on every side to leave room for the glow.
shader_type canvas_item;

uniform vec2 rect_size = vec2(100.0, 136.0);
uniform float pad = 10.0;
uniform float radius = 6.0;
uniform float border = 2.5;
uniform vec4 glow_color : source_color = vec4(1.0, 0.8, 0.3, 1.0);
uniform float intensity = 1.0;

void fragment() {
	vec2 p = UV * rect_size - rect_size * 0.5;
	vec2 half_card = rect_size * 0.5 - vec2(pad);
	vec2 q = abs(p) - (half_card - vec2(radius));
	float d = length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - radius; // <0 inside the card

	float ring = 1.0 - smoothstep(border * 0.5, border * 0.5 + 1.0, abs(d - border * 0.5 + 0.5));
	float glow = d > 0.0 ? pow(1.0 - clamp(d / pad, 0.0, 1.0), 2.2) * 0.85 : 0.0;
	float a = max(ring, glow) * intensity;
	COLOR = vec4(glow_color.rgb, glow_color.a * a);
}
