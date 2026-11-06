#!/usr/bin/env python3
"""Draws KSAMusic's 512x512 listing icon: Kitten Main Menu's kitten-head planet in its orbit ring, with a music note
riding the ring instead of the rocket, on a starfield.
    python3 tools/make_icon.py content/icon.png
"""
import math
import random
import sys

from PIL import Image, ImageDraw

SS = 4
N = 512


def main(out):
    img = Image.new("RGBA", (N * SS, N * SS), (6, 8, 18, 255))
    d = ImageDraw.Draw(img, "RGBA")
    rnd = random.Random(11)
    for _ in range(260):
        x, y, r = rnd.uniform(0, N * SS), rnd.uniform(0, N * SS), rnd.choice([2, 2, 3, 4, 6])
        d.ellipse([x - r, y - r, x + r, y + r], fill=(255, 255, 255, rnd.randint(60, 200)))

    def C(r, g, b, a=1.0):
        return (int(r * 255), int(g * 255), int(b * 255), int(a * 255))

    def P(p):
        return (p[0] * SS, p[1] * SS)

    def poly(pts, col):
        d.polygon([P(p) for p in pts], fill=col)

    def circ(c, r, col):
        d.ellipse([(c[0] - r) * SS, (c[1] - r) * SS, (c[0] + r) * SS, (c[1] + r) * SS], fill=col)

    def line(a, b, col, w):
        d.line([P(a), P(b)], fill=col, width=max(1, int(w * SS)))

    c = (N / 2, N / 2 + 18)
    r = 118.0
    ocean, deep, land = C(.20, .47, .88), C(.13, .32, .66), C(.36, .72, .42)
    ring, ring_back = C(.98, .62, .22), C(.98, .62, .22, .45)
    tilt, a, b = -0.32, r * 1.75, r * 0.46

    def ring_point(t):
        return (c[0] + a * math.cos(t) * math.cos(tilt) - b * math.sin(t) * math.sin(tilt),
                c[1] + a * math.cos(t) * math.sin(tilt) + b * math.sin(t) * math.cos(tilt))

    n = 96
    for i in range(n // 2):
        line(ring_point(math.pi + i * math.pi / (n / 2)), ring_point(math.pi + (i + 1) * math.pi / (n / 2)), ring_back, 9)
    for s in (-1, 1):
        poly([(c[0] + s * r * .92, c[1] - r * .38), (c[0] + s * r * .80, c[1] - r * 1.42), (c[0] + s * r * .18, c[1] - r * .92)], deep)
        poly([(c[0] + s * r * .74, c[1] - r * .62), (c[0] + s * r * .76, c[1] - r * 1.18), (c[0] + s * r * .38, c[1] - r * .88)], C(.98, .66, .72))
    circ(c, r, deep)
    circ((c[0] - r * .06, c[1] - r * .06), r * .92, ocean)
    circ((c[0] - r * .42, c[1] + r * .38), r * .30, land)
    circ((c[0] + r * .48, c[1] - r * .52), r * .20, land)
    circ((c[0] - r * .38, c[1] - r * .42), r * .16, C(1, 1, 1, .35))
    for s in (-1, 1):
        eye = (c[0] + s * r * .36, c[1] - r * .02)
        circ(eye, r * .15, C(.06, .08, .14))
        circ((eye[0] - r * .05, eye[1] - r * .05), r * .05, C(1, 1, 1))
        for k in range(3):
            line((c[0] + s * r * .42, c[1] + r * (.30 + .10 * k)), (c[0] + s * r * 1.32, c[1] + r * (.16 + .22 * k)), C(1, 1, 1, .85), 4)
    poly([(c[0] - r * .09, c[1] + r * .20), (c[0] + r * .09, c[1] + r * .20), (c[0], c[1] + r * .31)], C(.98, .55, .65))
    for i in range(n // 2):
        line(ring_point(i * math.pi / (n / 2)), ring_point((i + 1) * math.pi / (n / 2)), ring, 10)

    # a music note riding the near side of the ring (two beamed eighth notes)
    ct = 0.75
    cx, cy = ring_point(ct)
    note = C(1, .97, .9)
    shade = C(.98, .55, .18)
    for dx in (-22, 22):
        d.ellipse([(cx + dx - 16) * SS, (cy + 20 - 11) * SS, (cx + dx + 16) * SS, (cy + 20 + 11) * SS], fill=note, outline=shade, width=3 * SS)
        line((cx + dx + 14, cy + 20), (cx + dx + 14, cy - 50), note, 6)
    poly([(cx - 8 + 14 - 22, cy - 50), (cx + 14 + 22 + 3, cy - 50), (cx + 14 + 22 + 3, cy - 36), (cx - 8 + 14 - 22, cy - 36)], note)

    img.resize((N, N), Image.LANCZOS).convert("RGB").save(out)


if __name__ == "__main__":
    main(sys.argv[1])
