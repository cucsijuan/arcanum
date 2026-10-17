// SPDX-License-Identifier: AGPL-3.0-or-later
// Masks a card image with rounded corners, and draws the shine of a foil card: a rainbow (color dodge) and a soft
// glare (overlay) centered on the pointer, the rainbow's hue turning as the pointer moves across the card. A surge foil
// has the pattern of its foil paper: long, smooth diagonal streaks rising to the right in three families at slightly
// different angles that cross and run into each other, fading softly into the silver, breaking off and merging, and a
// rainbow running down the card; the pattern stays put on the card while its colors and shine turn with the pointer (as
// tilting the card would), and drift slowly on their own.
shader_type canvas_item;

uniform vec2 rect_size = vec2(90.0, 126.0);
uniform float radius = 5.0;
// How much the foil shines (0: not at all; it fades in while the pointer is on the card).
uniform float foil = 0.0;
// The pointer on the card, 0..1 across and down.
uniform vec2 foil_at = vec2(0.5);
// A surge foil (1) or not (0).
uniform float surge = 0.0;
// Drawn in greys, darker (1), as a card the player doesn't have; or in color (0).
uniform float grey = 0.0;

vec3 hsl(float hue, float s, float l) {
	vec3 k = mod(vec3(0.0, 8.0, 4.0) + hue / 30.0, 12.0);
	float a = s * min(l, 1.0 - l);
	return l - a * clamp(min(k - 3.0, 9.0 - k), -1.0, 1.0);
}

vec3 color_dodge(vec3 base, vec3 blend) {
	return min(base / max(1.0 - blend, vec3(0.001)), vec3(1.0));
}

vec3 overlay(vec3 base, vec3 blend) {
	return mix(2.0 * base * blend, 1.0 - 2.0 * (1.0 - base) * (1.0 - blend), step(0.5, base));
}

float hash2(vec2 p) {
	return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453);
}

float vnoise(vec2 p) {
	vec2 i = floor(p);
	vec2 f = p - i;
	f = f * f * (3.0 - 2.0 * f);
	float a = hash2(i), b = hash2(i + vec2(1.0, 0.0)), c = hash2(i + vec2(0.0, 1.0)), d = hash2(i + vec2(1.0, 1.0));
	return mix(mix(a, b, f.x), mix(c, d, f.x), f.y);
}

float fbm(vec2 p, int octaves) {
	float sum = 0.0, amp = 0.5;
	for (int i = 0; i < octaves; i++) {
		sum += amp * vnoise(p);
		p = p * 2.03 + vec2(17.1, 5.3);
		amp *= 0.5;
	}
	return sum;
}

// One family of streaks across the foil: smooth bands of a warped wave fading softly into the silver, their width and presence
// varying along them.
float streaks(vec2 along_across, float warp, float freq, float turn, float seed) {
	float c = cos(turn), s = sin(turn);
	vec2 q = vec2(along_across.x * c - along_across.y * s, along_across.x * s + along_across.y * c);
	float f = sin(q.y * freq + warp) * 0.5 + 0.5;
	float th = 0.25 + 0.65 * fbm(q * vec2(5.0, 4.0) + vec2(4.0 + seed, 8.0), 2);
	float on = smoothstep(0.3, 0.65, fbm(q * vec2(4.5, 7.0) + vec2(9.0 + seed, 2.0), 3));
	return smoothstep(th - 0.3, th + 0.55, f) * on;
}

void fragment() {
	vec4 tex = texture(TEXTURE, UV);
	if (foil > 0.0) {
		// Rainbow: an ellipse 140% by 110% of the card around the pointer, three hues 80° apart fading out at 82%.
		float hue = foil_at.x * 300.0;
		float t = length((UV - foil_at) / vec2(1.4, 1.1));
		vec3 rainbow = t < 0.35
			? mix(hsl(hue, 0.68, 0.62), hsl(hue + 80.0, 0.62, 0.56), t / 0.35)
			: mix(hsl(hue + 80.0, 0.62, 0.56), hsl(hue + 160.0, 0.55, 0.48), clamp((t - 0.35) / 0.27, 0.0, 1.0));
		float reach = clamp((0.82 - t) / 0.2, 0.0, 1.0);
		// On a surge foil the paper's streaks carry the color: the even wash is faint.
		tex.rgb = mix(tex.rgb, color_dodge(tex.rgb, rainbow), reach * 0.72 * foil * (1.0 - 0.75 * surge));
		// Glare: white, faint, reaching the farthest corner.
		float g = length((UV - foil_at) / (max(foil_at, 1.0 - foil_at) * 1.41421));
		float glare = g < 0.4 ? mix(0.18, 0.05, g / 0.4) : mix(0.05, 0.0, clamp((g - 0.4) / 0.28, 0.0, 1.0));
		tex.rgb = mix(tex.rgb, overlay(tex.rgb, vec3(1.0)), glare * foil);
	}
	if (surge > 0.0) {
		// The pattern in card heights, so it keeps its proportions; streaks rise at 58° to the right.
		vec2 pos = UV * vec2(rect_size.x / rect_size.y, 1.0);
		const float ANGLE = 1.0123;
		vec2 aa = vec2(pos.x * cos(ANGLE) - pos.y * sin(ANGLE), pos.x * sin(ANGLE) + pos.y * cos(ANGLE));
		float w = fbm(aa * 1.3, 4);
		float w2 = fbm(aa * vec2(3.5, 4.0) + vec2(3.0, 1.0), 3);
		float warp = w * 22.0 + w2 * 8.0;
		// Three families at different angles, so streaks cross and run into each other.
		float s1 = streaks(aa, warp, 80.0, 0.0, 0.0);
		float s2 = streaks(aa, warp, 60.0, 0.28, 13.0) * 0.85;
		float s3 = streaks(aa, warp, 55.0, -0.22, 29.0) * 0.85;
		float lum = clamp(0.45 + fbm(aa * 2.5 + vec2(1.0, 4.0), 3) * 1.1, 0.0, 1.3);
		float band = max(max(s1, s2), s3) * lum;
		// The rainbow runs down the card, bent by the streaks' flow; tilting (the pointer) and time turn it.
		float t = TIME * 0.03;
		float hue = fract(-pos.y * 0.9 + pos.x * 0.3 + w * 0.55 + w2 * 0.45 + 0.55 + (foil_at.x - foil_at.y) * 0.6 + t) * 360.0;
		vec3 rainbow = hsl(hue, 0.9, 0.55);
		float light = 0.35 + 0.65 * foil * clamp(1.2 - length(UV - foil_at) * 1.1, 0.25, 1.0);
		float m = clamp(band * 0.75, 0.0, 1.0) * light * surge;
		// The foil's color shows through the ink: light areas take the streak's color, dark ones get a colored sheen.
		vec3 tinted = clamp(tex.rgb * (0.35 + 1.0 * rainbow) + rainbow * 0.18, 0.0, 1.0);
		tex.rgb = mix(tex.rgb, tinted, m);
	}
	if (grey > 0.0) {
		float l = dot(tex.rgb, vec3(0.299, 0.587, 0.114));
		tex.rgb = mix(tex.rgb, vec3(l * 0.75), grey);
	}
	vec2 p = UV * rect_size;
	vec2 q = abs(p - rect_size * 0.5) - (rect_size * 0.5 - vec2(radius));
	float d = length(max(q, 0.0)) - radius;
	tex.a *= 1.0 - smoothstep(-1.0, 0.0, d);
	COLOR = tex;
}
