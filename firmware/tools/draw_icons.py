#!/usr/bin/env python3
"""Draws Volco's 28x28 1-bit screen icons from vector descriptions (on a
24-unit grid). Run once, inspect, commit the PNGs: after that the PNGs are the source of truth and
can be hand-edited. Usage: python3 firmware/tools/draw_icons.py [--only stem,stem] [--preview out.png]"""
import math, sys
from pathlib import Path
from PIL import Image, ImageDraw

OUT = Path(__file__).resolve().parent.parent / "assets" / "icons"
SIZE, SS = 28, 8                      # final size, supersampling factor
K = SIZE / 24 * SS                    # 24-unit grid -> supersampled pixels
W = 2.3 * K                           # stroke width

def P(x, y): return (x * K, y * K)

class Pen:
    def __init__(self):
        self.img = Image.new("L", (SIZE * SS, SIZE * SS), 0)
        self.d = ImageDraw.Draw(self.img)
    def dot(self, x, y, r):            # filled circle, r in grid units
        cx, cy = P(x, y); rr = r * K
        self.d.ellipse([cx - rr, cy - rr, cx + rr, cy + rr], fill=255)
    def line(self, *pts):              # polyline with round caps and joins
        xy = [P(*p) for p in pts]
        self.d.line(xy, fill=255, width=round(W), joint="curve")
        for (x, y) in xy:
            self.d.ellipse([x - W / 2, y - W / 2, x + W / 2, y + W / 2], fill=255)
    def poly(self, *pts):
        self.d.polygon([P(*p) for p in pts], fill=255)
    def arc(self, cx, cy, r, a0, a1):  # degrees, clockwise from 3 o'clock (y down), stroke centred on r
        x, y = P(cx, cy); R = r * K + W / 2
        self.d.arc([x - R, y - R, x + R, y + R], a0, a1, fill=255, width=round(W))
    def ring(self, cx, cy, r):
        x, y = P(cx, cy); R = r * K + W / 2
        self.d.ellipse([x - R, y - R, x + R, y + R], outline=255, width=round(W))
    def rbox(self, x, y, w, h, r, fill):
        x0, y0 = P(x, y); x1, y1 = P(x + w, y + h)
        if fill:
            self.d.rounded_rectangle([x0, y0, x1, y1], r * K, fill=255)
        else:
            g = W / 2
            self.d.rounded_rectangle([x0 - g, y0 - g, x1 + g, y1 + g], r * K + g, outline=255, width=round(W))
    def curve(self, p0, c, p1, steps=20):  # quadratic Bezier as a polyline
        pts = [((1-t)**2*p0[0] + 2*(1-t)*t*c[0] + t*t*p1[0], (1-t)**2*p0[1] + 2*(1-t)*t*c[1] + t*t*p1[1])
               for t in (i / steps for i in range(steps + 1))]
        self.line(*pts)
    def done(self):
        small = self.img.resize((SIZE, SIZE), Image.LANCZOS)
        return small.point(lambda v: 255 if v >= 110 else 0).convert("1")

def generic(p):
    for x, a, b in ((4, 10, 14), (8, 6, 18), (12, 3, 21), (16, 7, 17), (20, 10, 14)): p.line((x, a), (x, b))
def speaker(p):
    p.poly((3, 9), (7, 9), (12, 4), (12, 20), (7, 15), (3, 15))
    p.arc(13, 12, 4, -52, 52); p.arc(13, 12, 8, -52, 52)
def speakers(p):
    for x in (1.5, 13):
        p.rbox(x, 2.5, 9.5, 19, 2, fill=False); p.ring(x + 4.75, 15, 2.6); p.dot(x + 4.75, 7.5, 1.3)
def headphones(p):
    p.arc(12, 13, 8.5, 180, 360); p.rbox(2.5, 13, 5, 8.5, 1.5, True); p.rbox(16.5, 13, 5, 8.5, 1.5, True)
def headset(p):
    p.arc(12, 12, 8.5, 180, 360); p.rbox(2.5, 11, 5, 8, 1.5, True); p.rbox(16.5, 11, 5, 8, 1.5, True)
    p.curve((5, 19), (6, 22.5), (11, 22)); p.dot(12.5, 22, 1.6)
def earbuds(p):
    p.dot(7, 8, 4); p.line((8.5, 11), (8.5, 21)); p.dot(17, 8, 4); p.line((15.5, 11), (15.5, 21))
def display(p):
    p.rbox(2, 3, 20, 13, 1.5, False); p.line((12, 16.5), (12, 20)); p.line((7, 21), (17, 21))
def mic(p):
    p.rbox(8, 2, 8, 13, 4, True); p.arc(12, 11, 6.5, 0, 180); p.line((12, 17.5), (12, 21.5)); p.line((8, 21.5), (16, 21.5))
def studiomic(p):
    p.rbox(7.5, 1.5, 9, 13, 4.5, False); p.line((7.5, 6), (16.5, 6)); p.line((7.5, 10), (16.5, 10))
    p.arc(12, 11, 7.5, 9, 171); p.line((12, 18.5), (12, 21.5)); p.line((6, 21.5), (18, 21.5))
def webcam(p):
    p.ring(12, 10, 7); p.dot(12, 10, 2.6); p.line((12, 17), (12, 20.5)); p.line((6, 21.5), (18, 21.5))
def presets(p):
    p.poly((12, 3), (21, 8), (12, 13), (3, 8)); p.line((3, 12.5), (12, 17.5), (21, 12.5)); p.line((3, 17), (12, 22), (21, 17))

# Equaliser bars (the playing indicator, docs/design/playing-indicator.md): square bars on exact
# pixels, standing on the bottom row 25. Unlike `generic` (round-capped lines centred on the middle).
EQ_X, EQ_W, EQ_GAP, EQ_BOTTOM = 3, 3, 2, 25      # pixels
def bars(*heights):                               # heights in pixels
    def draw(p):
        for i, h in enumerate(heights):
            x = EQ_X + i * (EQ_W + EQ_GAP)
            p.d.rectangle([x * SS, (EQ_BOTTOM + 1 - h) * SS, (x + EQ_W) * SS - 1, (EQ_BOTTOM + 1) * SS - 1], fill=255)
    return draw
EQUALIZER_FRAMES = [(9, 16, 12, 20, 7), (15, 10, 20, 13, 11), (20, 14, 8, 16, 17), (11, 21, 15, 9, 14)]

ICONS = [("generic", generic), ("speaker", speaker), ("speakers", speakers), ("headphones", headphones),
         ("headset", headset), ("earbuds", earbuds), ("display", display), ("mic", mic),
         ("studiomic", studiomic), ("webcam", webcam), ("presets", presets)]
ICONS += [(f"equalizer_{i}", bars(*h)) for i, h in enumerate(EQUALIZER_FRAMES)]
ICONS += [("equalizerstill", bars(5, 8, 3, 7, 4))]

def main():
    OUT.mkdir(parents=True, exist_ok=True)
    images = []
    only = sys.argv[sys.argv.index("--only") + 1].split(",") if "--only" in sys.argv else None
    for stem, draw in ICONS:
        if only and not any(stem == o or stem.startswith(o + "_") for o in only): continue
        pen = Pen(); draw(pen); img = pen.done()
        img.save(OUT / f"{stem}.png"); images.append(img)
    if "--preview" in sys.argv:
        out = Path(sys.argv[sys.argv.index("--preview") + 1])
        sheet = Image.new("1", (len(images) * (SIZE + 4), SIZE), 0)
        for i, img in enumerate(images): sheet.paste(img, (i * (SIZE + 4), 0))
        sheet.resize((sheet.width * 8, sheet.height * 8), Image.NEAREST).save(out)
    print(f"wrote {len(images)} icons to {OUT}")

if __name__ == "__main__":
    main()
