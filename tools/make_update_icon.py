#!/usr/bin/env python3
"""Writes src/Volco.App/Assets/volco-update.ico: volco.ico with an accent dot in the corner, the
tray icon shown while a controller firmware update is available (spec §8.3). Needs Pillow.

    python3 tools/make_update_icon.py
"""
import os

from PIL import Image, ImageDraw

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
SRC = os.path.join(ROOT, "src", "Volco.App", "Assets", "volco.ico")
DST = os.path.join(ROOT, "src", "Volco.App", "Assets", "volco-update.ico")
ACCENT = (255, 150, 0, 255)   # amber: stands out on the blue icon
SIZES = [16, 20, 24, 32, 48, 256]


def with_dot(size):
    base = Image.open(SRC)
    base.size = (size, size)   # pick that frame of the .ico
    img = base.convert("RGBA").resize((size, size), Image.LANCZOS)
    d = max(5, round(size * 0.42))
    x0, y0 = size - d, 0
    draw = ImageDraw.Draw(img)
    ring = max(1, size // 16)
    draw.ellipse([x0 - ring, y0 - ring, size - 1 + ring, d - 1 + ring], fill=(255, 255, 255, 255))
    draw.ellipse([x0, y0, size - 1, d - 1], fill=ACCENT)
    return img


frames = [with_dot(s) for s in SIZES]
frames[-1].save(DST, format="ICO", sizes=[(s, s) for s in SIZES], append_images=frames[:-1])
print(f"wrote {DST}")
