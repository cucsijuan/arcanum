// SPDX-License-Identifier: AGPL-3.0-or-later
// Masks a card image with rounded corners, and draws the shine of a foil card: a rainbow (color dodge) and a soft
// glare (overlay) centered on the pointer, the rainbow's hue turning as the pointer moves across the card.
shader_type canvas_item;

uniform vec2 rect_size = vec2(90.0, 126.0);
uniform float radius = 5.0;
// How much the foil shines (0: not at all; it fades in while the pointer is on the card).
uniform float foil = 0.0;
// The pointer on the card, 0..1 across and down.
uniform vec2 foil_at = vec2(0.5);

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
		tex.rgb = mix(tex.rgb, color_dodge(tex.rgb, rainbow), reach * 0.72 * foil);
		// Glare: white, faint, reaching the farthest corner.
		float g = length((UV - foil_at) / (max(foil_at, 1.0 - foil_at) * 1.41421));
		float glare = g < 0.4 ? mix(0.18, 0.05, g / 0.4) : mix(0.05, 0.0, clamp((g - 0.4) / 0.28, 0.0, 1.0));
		tex.rgb = mix(tex.rgb, overlay(tex.rgb, vec3(1.0)), glare * foil);
	}
	vec2 p = UV * rect_size;
	vec2 q = abs(p - rect_size * 0.5) - (rect_size * 0.5 - vec2(radius));
	float d = length(max(q, 0.0)) - radius;
	tex.a *= 1.0 - smoothstep(-1.0, 0.0, d);
	COLOR = tex;
}
