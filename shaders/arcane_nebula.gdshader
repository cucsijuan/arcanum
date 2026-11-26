// SPDX-License-Identifier: AGPL-3.0-or-later
// The slowly drifting nebula of the menu backdrop (arcane_backdrop.gdshader). It has no fine detail and is the costly
// part, so it is drawn small and the backdrop stretches it. Written at twice its brightness, to keep its darkest steps.
shader_type canvas_item;

uniform vec2 rect_size = vec2(1920.0, 1080.0); // the backdrop's size (not this picture's)
uniform vec4 deep : source_color = vec4(0.03, 0.02, 0.06, 1.0);
uniform vec4 mid : source_color = vec4(0.17, 0.07, 0.27, 1.0);
uniform vec4 glow : source_color = vec4(0.10, 0.28, 0.45, 1.0);
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
	float t = TIME;
	vec2 p = UV * rect_size;

	// Nebula: domain-warped noise drifting slowly.
	vec2 n = p / 300.0;
	vec2 drift = vec2(t * 0.012, -t * 0.008);
	float warp = fbm(n * 0.7 + drift + vec2(fbm(n + t * 0.01), fbm(n - t * 0.013)));
	vec3 col = mix(deep.rgb, mid.rgb, smoothstep(0.35, 0.78, warp));
	col = mix(col, glow.rgb, smoothstep(0.62, 0.92, fbm(n * 1.6 + 4.0 - drift * 1.4)) * 0.55);
	COLOR = vec4(col * 0.8 * 2.0, 1.0);
}
