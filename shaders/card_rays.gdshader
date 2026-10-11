// SPDX-License-Identifier: AGPL-3.0-or-later
// Light coming out of a card: short rays of different lengths and widths shooting out of its outline and turning
// slowly, over a faint glow, all fading out with the distance to the card. Drawn on a rectangle larger than the card, centered on it.
shader_type canvas_item;

uniform vec4 color : source_color = vec4(1.0, 0.82, 0.42, 1.0);
// The card's size as a fraction of this rectangle.
uniform vec2 card = vec2(0.42, 0.42);
uniform float strength = 1.0;

float hash(float n) { return fract(sin(n * 127.1) * 43758.5453); }

// Rays around the card: `count` of them, each of its own length and brightness, turning at `speed`.
float rays(float a, float d, float count, float speed, float seed) {
	float t = (a / 6.28318 + 0.5) * count + TIME * speed;
	float i = floor(t);
	float f = fract(t);
	float width = 0.15 + 0.25 * hash(i + seed);
	float beam = smoothstep(width, 0.0, abs(f - 0.5));
	float reach = 0.35 + 0.9 * hash(i * 1.7 + seed);
	float bright = 0.4 + 0.6 * hash(i * 3.1 + seed);
	return beam * bright * exp(-d * 6.0 / reach);
}

void fragment() {
	vec2 p = UV - 0.5;
	// Distance from the card's outline (0 on it, inside counts as 0).
	vec2 q = abs(p) - card * 0.5;
	float d = length(max(q, 0.0)) / 0.5;
	float a = atan(p.y, p.x);
	float glow = exp(-d * 14.0) * 0.45;
	float beams = rays(a, d, 22.0, 0.05, 1.0) + rays(a, d, 13.0, -0.035, 7.0) * 0.7;
	beams *= smoothstep(0.0, 0.03, d);
	// Everything fades out well before the rectangle's edges (an ellipse), so no edge ever shows.
	float edge = 1.0 - smoothstep(0.55, 0.98, length(p * 2.0));
	float alpha = clamp(glow + beams * 0.85, 0.0, 1.0) * edge * strength;
	COLOR = vec4(color.rgb, alpha * color.a);
}
