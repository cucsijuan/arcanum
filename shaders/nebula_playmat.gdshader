// SPDX-License-Identifier: AGPL-3.0-or-later
// Procedural nebula playmat: layered value noise with soft color bands. Original artwork, no external assets.
shader_type canvas_item;

uniform vec2 rect_size = vec2(1920.0, 540.0);
uniform vec4 deep : source_color = vec4(0.04, 0.03, 0.08, 1.0);
uniform vec4 mid : source_color = vec4(0.18, 0.08, 0.28, 1.0);
uniform vec4 glow : source_color = vec4(0.10, 0.30, 0.45, 1.0);
uniform float seed = 3.0;

float hash(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7)) + seed) * 43758.5453); }

float noise(vec2 p) {
	vec2 i = floor(p);
	vec2 f = fract(p);
	vec2 u = f * f * (3.0 - 2.0 * f);
	return mix(mix(hash(i), hash(i + vec2(1.0, 0.0)), u.x), mix(hash(i + vec2(0.0, 1.0)), hash(i + vec2(1.0, 1.0)), u.x), u.y);
}

float fbm(vec2 p) {
	float v = 0.0;
	float a = 0.5;
	for (int i = 0; i < 5; i++) { v += a * noise(p); p *= 2.03; a *= 0.5; }
	return v;
}

void fragment() {
	vec2 p = UV * rect_size / 260.0;
	float n = fbm(p + fbm(p * 0.7));
	vec3 col = mix(deep.rgb, mid.rgb, smoothstep(0.35, 0.75, n));
	col = mix(col, glow.rgb, smoothstep(0.62, 0.9, fbm(p * 1.7 + 4.0)) * 0.6);
	float stars = step(0.997, hash(floor(UV * rect_size / 2.0))) * 0.6;
	COLOR = vec4(col * 0.75 + stars, 1.0);
}
