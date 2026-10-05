#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-or-later
"""Renders the application icon from the card back's design (shaders/card_back.gdshader): the diamond lattice
on a violet glow, and the gold ring with the four-point star. Writes icon.png (1024 px) and icon.ico."""
import numpy as np
from PIL import Image

N = 1024
SS = 3  # supersampling
color_a = np.array([0.13, 0.09, 0.22])
color_b = np.array([0.30, 0.18, 0.45])
accent = np.array([0.85, 0.70, 0.35])

def smoothstep(e0, e1, x):
    t = np.clip((x - e0) / (e1 - e0), 0.0, 1.0)
    return t * t * (3 - 2 * t)

def rounded_box(px, py, half, r):
    qx, qy = np.abs(px) - (half - r), np.abs(py) - (half - r)
    return np.hypot(np.maximum(qx, 0), np.maximum(qy, 0)) + np.minimum(np.maximum(qx, qy), 0) - r

M = N * SS
c = (np.arange(M) + 0.5) / M * N - N / 2
px, py = np.meshgrid(c, c)
size = float(N)

outer = rounded_box(px, py, size / 2, size * 0.18)
inner = rounded_box(px, py, size / 2 - size * 0.018, size * 0.165)

# Lattice and glow, as on the card back (a little coarser so it reads at small sizes).
u, v = px / size * 6.0, py / size * 6.0
dx = np.abs(np.mod((u + v) * 0.5, 1.0) - 0.5)
dy = np.abs(np.mod((u - v) * 0.5, 1.0) - 0.5)
lattice = smoothstep(0.03, 0.0, np.minimum(dx, dy))
glow = 1.0 - np.clip(np.hypot(px, py) / size * 1.6, 0.0, 1.0)
col = color_a + (color_b - color_a) * glow[..., None]
col = col + (accent * 0.6 - col) * (lattice * 0.35)[..., None]

# Emblem: the ring and the four-point star, larger than on the card.
ex, ey = px / (size * 0.5) / 1.55, py / (size * 0.5) / 1.55
r = np.hypot(ex, ey)
ring = smoothstep(0.024, 0.018, np.abs(r - 0.42))
star = smoothstep(0.003, 0.0, np.abs(ex) * np.abs(ey) * 6.0 + r * 0.12 - 0.05)
col = col + (accent - col) * np.maximum(ring, star)[..., None]

# Gold frame.
frame = smoothstep(size * 0.008, size * 0.004, np.abs(inner + size * 0.012))
col = col + (accent * 0.8 - col) * frame[..., None]
col = np.where((inner > 0)[..., None], np.array([0.06, 0.05, 0.08]), col)
alpha = 1.0 - smoothstep(-1.0, 0.0, outer)

rgba = np.dstack([np.clip(col, 0, 1), alpha])
img = Image.fromarray((rgba * 255 + 0.5).astype(np.uint8), "RGBA").resize((N, N), Image.LANCZOS)
img.save("icon.png")
img.save("icon.ico", sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
