// SPDX-License-Identifier: AGPL-3.0-or-later
// Animated menu backdrop: a slowly drifting nebula, twinkling stars (the brighter ones with four-pointed glints) and a
// faint arcane circle of turning rune rings. Procedural; no external assets.
shader_type canvas_item;

uniform vec2 rect_size = vec2(1920.0, 1080.0);
uniform vec4 deep : source_color = vec4(0.03, 0.02, 0.06, 1.0);
uniform vec4 mid : source_color = vec4(0.17, 0.07, 0.27, 1.0);
uniform vec4 glow : source_color = vec4(0.10, 0.28, 0.45, 1.0);
uniform vec4 gold : source_color = vec4(0.95, 0.78, 0.45, 1.0);
uniform vec4 violet : source_color = vec4(0.62, 0.42, 1.0, 1.0);
// Center of the arcane circle (0..1 of the screen) and its outer radius (fraction of the screen height).
uniform vec2 circle_center = vec2(0.66, 0.5);
uniform float circle_radius = 0.46;
uniform float circle_strength = 0.55;
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

// A thin line at distance d (pixels) with a soft glow around it.
float line(float d, float width) { return smoothstep(width, 0.0, abs(d)) + 0.25 * smoothstep(width * 6.0, 0.0, abs(d)); }

// Distance from p to the segment a-b.
float segment(vec2 p, vec2 a, vec2 b) {
	vec2 pa = p - a, ba = b - a;
	return length(pa - ba * clamp(dot(pa, ba) / dot(ba, ba), 0.0, 1.0));
}

// Stars of one layer: cells of `cell` pixels, each with maybe one star that twinkles; bright ones get a glint.
float stars(vec2 p, float cell, float density, float t) {
	vec2 id = floor(p / cell);
	vec2 f = fract(p / cell) - 0.5;
	float h = hash(id);
	if (h < 1.0 - density) return 0.0;
	vec2 offset = vec2(hash(id + 7.1), hash(id + 3.3)) - 0.5;
	vec2 d = (f - offset * 0.7) * cell;
	float twinkle = 0.25 + 0.95 * pow(0.5 + 0.5 * sin(t * (0.8 + 2.4 * hash(id + 1.7)) + h * 40.0), 1.6);
	float core = smoothstep(1.6, 0.0, length(d));
	float bright = step(0.985, hash(id + 9.2));
	// Four-pointed glint: thin crosses fading with distance.
	float cross = (smoothstep(1.2, 0.0, abs(d.x)) * smoothstep(9.0, 0.0, abs(d.y)) + smoothstep(1.2, 0.0, abs(d.y)) * smoothstep(9.0, 0.0, abs(d.x)));
	return twinkle * (core + bright * cross * 0.9);
}

// The 24 runes of the Elder Futhark (fehu ... othala), 5 strokes each (x0, y0, x1, y1); y points outward.
const vec4 RUNES[120] = vec4[](
	vec4(0.25, 0, 0.25, 1), vec4(0.25, 0.55, 0.8, 0.85), vec4(0.25, 0.8, 0.75, 1.05), vec4(-1, -1, -1, -1), vec4(-1, -1, -1, -1) /* fehu */,
	vec4(0.25, 0, 0.25, 1), vec4(0.25, 1, 0.8, 0.7), vec4(0.8, 0.7, 0.8, 0), vec4(-1, -1, -1, -1), vec4(-1, -1, -1, -1) /* uruz */,
	vec4(0.3, 0, 0.3, 1), vec4(0.3, 0.72, 0.78, 0.5), vec4(0.78, 0.5, 0.3, 0.28), vec4(-1, -1, -1, -1), vec4(-1, -1, -1, -1) /* thurisaz */,
	vec4(0.3, 0, 0.3, 1), vec4(0.3, 1, 0.8, 0.75), vec4(0.3, 0.75, 0.8, 0.5), vec4(-1, -1, -1, -1), vec4(-1, -1, -1, -1) /* ansuz */,
	vec4(0.3, 0, 0.3, 1), vec4(0.3, 1, 0.78, 0.78), vec4(0.78, 0.78, 0.3, 0.55), vec4(0.3, 0.55, 0.78, 0), vec4(-1, -1, -1, -1) /* raidho */,
	vec4(0.72, 0.85, 0.3, 0.5), vec4(0.3, 0.5, 0.72, 0.15), vec4(-1, -1, -1, -1), vec4(-1, -1, -1, -1), vec4(-1, -1, -1, -1) /* kaunan */,
	vec4(0.2, 0, 0.8, 1), vec4(0.2, 1, 0.8, 0), vec4(-1, -1, -1, -1), vec4(-1, -1, -1, -1), vec4(-1, -1, -1, -1) /* gebo */,
	vec4(0.3, 0, 0.3, 1), vec4(0.3, 1, 0.75, 0.8), vec4(0.75, 0.8, 0.3, 0.6), vec4(-1, -1, -1, -1), vec4(-1, -1, -1, -1) /* wunjo */,
	vec4(0.22, 0, 0.22, 1), vec4(0.78, 0, 0.78, 1), vec4(0.22, 0.65, 0.78, 0.35), vec4(-1, -1, -1, -1), vec4(-1, -1, -1, -1) /* hagalaz */,
	vec4(0.5, 0, 0.5, 1), vec4(0.25, 0.68, 0.75, 0.38), vec4(-1, -1, -1, -1), vec4(-1, -1, -1, -1), vec4(-1, -1, -1, -1) /* naudiz */,
	vec4(0.5, 0, 0.5, 1), vec4(-1, -1, -1, -1), vec4(-1, -1, -1, -1), vec4(-1, -1, -1, -1), vec4(-1, -1, -1, -1) /* isa */,
	vec4(0.5, 0.95, 0.25, 0.72), vec4(0.25, 0.72, 0.5, 0.49), vec4(0.5, 0.51, 0.75, 0.28), vec4(0.75, 0.28, 0.5, 0.05), vec4(-1, -1, -1, -1) /* jera */,
	vec4(0.5, 0, 0.5, 1), vec4(0.5, 1, 0.78, 0.8), vec4(0.5, 0, 0.22, 0.2), vec4(-1, -1, -1, -1), vec4(-1, -1, -1, -1) /* eihwaz */,
	vec4(0.25, 0, 0.25, 1), vec4(0.25, 1, 0.6, 0.8), vec4(0.6, 0.8, 0.85, 1), vec4(0.25, 0, 0.6, 0.2), vec4(0.6, 0.2, 0.85, 0) /* perth */,
	vec4(0.5, 0, 0.5, 1), vec4(0.5, 0.55, 0.15, 0.9), vec4(0.5, 0.55, 0.85, 0.9), vec4(-1, -1, -1, -1), vec4(-1, -1, -1, -1) /* algiz */,
	vec4(0.68, 1, 0.3, 0.66), vec4(0.3, 0.66, 0.7, 0.34), vec4(0.7, 0.34, 0.32, 0), vec4(-1, -1, -1, -1), vec4(-1, -1, -1, -1) /* sowilo */,
	vec4(0.5, 0, 0.5, 1), vec4(0.5, 1, 0.15, 0.66), vec4(0.5, 1, 0.85, 0.66), vec4(-1, -1, -1, -1), vec4(-1, -1, -1, -1) /* tiwaz */,
	vec4(0.28, 0, 0.28, 1), vec4(0.28, 1, 0.75, 0.75), vec4(0.75, 0.75, 0.28, 0.5), vec4(0.28, 0.5, 0.75, 0.25), vec4(0.75, 0.25, 0.28, 0) /* berkanan */,
	vec4(0.2, 0, 0.2, 1), vec4(0.8, 0, 0.8, 1), vec4(0.2, 1, 0.5, 0.66), vec4(0.5, 0.66, 0.8, 1), vec4(-1, -1, -1, -1) /* ehwaz */,
	vec4(0.2, 0, 0.2, 1), vec4(0.8, 0, 0.8, 1), vec4(0.2, 1, 0.8, 0.55), vec4(0.8, 1, 0.2, 0.55), vec4(-1, -1, -1, -1) /* mannaz */,
	vec4(0.32, 0, 0.32, 1), vec4(0.32, 1, 0.75, 0.68), vec4(-1, -1, -1, -1), vec4(-1, -1, -1, -1), vec4(-1, -1, -1, -1) /* laguz */,
	vec4(0.5, 0.95, 0.8, 0.5), vec4(0.8, 0.5, 0.5, 0.05), vec4(0.5, 0.05, 0.2, 0.5), vec4(0.2, 0.5, 0.5, 0.95), vec4(-1, -1, -1, -1) /* ingwaz */,
	vec4(0.15, 0, 0.15, 1), vec4(0.85, 0, 0.85, 1), vec4(0.15, 1, 0.85, 0), vec4(0.15, 0, 0.85, 1), vec4(-1, -1, -1, -1) /* dagaz */,
	vec4(0.5, 1, 0.8, 0.68), vec4(0.5, 1, 0.2, 0.68), vec4(0.8, 0.68, 0.18, 0), vec4(0.2, 0.68, 0.82, 0), vec4(-1, -1, -1, -1) /* othala */
);

// A ring of Elder Futhark runes between radii r0 and r1, turning with `angle`; each cell shows one rune, upright
// (pointing outward).
float runes(float r, float a, float r0, float r1, float count, float angle) {
	if (r < r0 || r > r1) return 0.0;
	float u = fract((a + angle) / TAU) * count;
	float cell = floor(u);
	float height = r1 - r0;
	float arc = (r0 + r1) * 0.5 * TAU / count; // the cell's width in pixels
	vec2 px = vec2((fract(u) - 0.5) * arc, r - (r0 + r1) * 0.5); // pixels from the cell's center
	vec2 box = vec2(height * 0.5, height * 0.78); // the rune's size
	int rune = int(floor(hash(vec2(cell, 17.0 + seed)) * 24.0));
	float d = 1e9;
	for (int k = 0; k < 5; k++) {
		vec4 s = RUNES[rune * 5 + k];
		if (s.x < 0.0) break;
		d = min(d, segment(px, (s.xy - 0.5) * box, (s.zw - 0.5) * box));
	}
	return smoothstep(1.4, 0.0, d) + 0.3 * smoothstep(5.0, 0.0, d);
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
	col *= 0.8;

	// Stars: three layers, the far ones slowly moving.
	col += vec3(0.85, 0.82, 1.0) * stars(p + vec2(t * 2.0, 0.0), 22.0, 0.10, t) * 0.45;
	col += vec3(0.9, 0.85, 1.0) * stars(p + vec2(t * 4.0, t * 1.0), 48.0, 0.12, t * 1.3) * 0.7;
	col += vec3(1.0, 0.92, 0.8) * stars(p + vec2(t * 7.0, t * 2.0), 110.0, 0.08, t * 0.8);

	// A shooting star every 20-30 s, somewhere across the upper part of the screen.
	{
		float period = 25.0;
		float cycle = floor(t / period);
		float local = t - cycle * period - hash(vec2(cycle, 5.0)) * (period - 2.0);
		if (local > 0.0 && local < 1.1) {
			vec2 start = vec2(hash(vec2(cycle, 1.0)) * 0.7 + 0.15, hash(vec2(cycle, 2.0)) * 0.3 + 0.05) * rect_size;
			vec2 dir = normalize(vec2(hash(vec2(cycle, 3.0)) > 0.5 ? 1.0 : -1.0, 0.45 + 0.3 * hash(vec2(cycle, 4.0))));
			vec2 head = start + dir * local * 900.0;
			vec2 tail = head - dir * 220.0;
			vec2 ph = p - tail, ba = head - tail;
			float along = clamp(dot(ph, ba) / dot(ba, ba), 0.0, 1.0);
			float dist = length(ph - ba * along);
			float fade = smoothstep(0.0, 0.15, local) * smoothstep(1.1, 0.7, local);
			col += vec3(1.0, 0.95, 0.85) * smoothstep(2.0, 0.0, dist) * along * along * fade * 1.2;
			col += vec3(0.7, 0.6, 1.0) * smoothstep(9.0, 0.0, dist) * along * fade * 0.25;
		}
	}

	// Arcane circle: rings, two turning rune bands, a rotating star polygon, a pulse and a light running around it.
	vec2 c = circle_center * rect_size;
	vec2 v = p - c;
	float r = length(v);
	float a = atan(v.y, v.x);
	float R = circle_radius * rect_size.y;
	float pulse = 0.75 + 0.25 * sin(t * 0.7);
	float circle = 0.0;
	circle += line(r - R, 1.4);
	circle += line(r - R * 0.93, 1.0);
	circle += runes(r, a, R * 0.80, R * 0.90, 36.0, t * 0.03) * 0.9;
	circle += line(r - R * 0.78, 1.0);
	circle += line(r - R * 0.62, 1.2);
	circle += runes(r, a, R * 0.52, R * 0.60, 24.0, -t * 0.05) * 0.8;
	circle += line(r - R * 0.50, 0.9);
	// Two triangles turning opposite ways (a hexagram), inside the inner ring.
	for (int k = 0; k < 2; k++) {
		float rot = (k == 0 ? 1.0 : -1.0) * t * 0.04 + float(k) * TAU / 6.0;
		float d = 1e9;
		for (int i = 0; i < 3; i++) {
			float a0 = rot + float(i) * TAU / 3.0, a1 = rot + float(i + 1) * TAU / 3.0;
			d = min(d, segment(v, vec2(cos(a0), sin(a0)) * R * 0.48, vec2(cos(a1), sin(a1)) * R * 0.48));
		}
		circle += line(d, 1.0) * 0.8;
	}
	// Small ticks on the outer band.
	float ticks = step(0.92, fract((a - t * 0.02) / TAU * 120.0)) * step(R * 0.94, r) * step(r, R * 0.98);
	circle += ticks * 0.6;
	// A brighter light slowly running around the rings.
	float runner = pow(max(0.0, cos(a - t * 0.25)), 24.0);
	float fade = smoothstep(R * 1.08, R * 0.95, r) * smoothstep(R * 0.2, R * 0.45, r);
	vec3 circleColor = mix(violet.rgb, gold.rgb, 0.45 + 0.35 * runner);
	col += circleColor * circle * fade * circle_strength * (pulse * 0.6 + runner * 0.8) * 0.5;

	// Vignette keeps the edges dark.
	vec2 uv = UV - 0.5;
	col *= 1.0 - dot(uv, uv) * 0.9;
	COLOR = vec4(col, 1.0);
}
