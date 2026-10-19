#!/usr/bin/env python3
"""Draws the starter-template backdrops in assets/templates/backdrops/ (original art, GPL-3.0-or-later).

Deterministic: same output on every run. Needs Pillow. Usage:
    python3 -I scripts/generate_template_backdrops.py [output_dir]
"""
import math
import random
import sys
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter

W, H = 640, 512


def lerp(a, b, t):
    return tuple(round(a[i] + (b[i] - a[i]) * t) for i in range(3))


def vertical_gradient(stops):
    img = Image.new("RGB", (W, H))
    px = img.load()
    for y in range(H):
        t = y / (H - 1)
        for i in range(len(stops) - 1):
            (t0, c0), (t1, c1) = stops[i], stops[i + 1]
            if t0 <= t <= t1:
                c = lerp(c0, c1, (t - t0) / (t1 - t0))
                break
        for x in range(W):
            px[x, y] = c
    return img


def tower_dusk():
    img = vertical_gradient([
        (0.0, (6, 9, 28)), (0.45, (20, 22, 60)), (0.66, (62, 34, 78)),
        (0.74, (150, 70, 52)), (0.78, (196, 104, 48)), (1.0, (196, 104, 48)),
    ])
    rnd = random.Random(73)
    stars = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    sd = ImageDraw.Draw(stars)
    for _ in range(140):
        x, y = rnd.randrange(W), rnd.randrange(int(H * 0.55))
        a = rnd.randrange(60, 170)
        r = 1 if rnd.random() < 0.85 else 2
        sd.ellipse((x - r / 2, y - r / 2, x + r / 2, y + r / 2), fill=(220, 225, 255, a))
    img.paste(stars, (0, 0), stars)

    d = ImageDraw.Draw(img, "RGBA")
    # Rolling hills, near-black, covering the bottom fifth.
    hill = [(0, H)]
    for x in range(0, W + 1, 8):
        y = H * 0.79 + 10 * math.sin(x / 90.0) + 6 * math.sin(x / 37.0 + 1.3)
        hill.append((x, y))
    hill.append((W, H))
    d.polygon(hill, fill=(6, 6, 12))

    # Lattice tower with a Yagi on top.
    sil = (8, 8, 16)
    base_y, top_y, cx = H * 0.80, H * 0.30, W * 0.64
    half = lambda y: 4 + 18 * (y - top_y) / (base_y - top_y)
    d.line([(cx - half(base_y), base_y), (cx - half(top_y), top_y)], fill=sil, width=3)
    d.line([(cx + half(base_y), base_y), (cx + half(top_y), top_y)], fill=sil, width=3)
    steps = 9
    for i in range(steps):
        y0 = top_y + (base_y - top_y) * i / steps
        y1 = top_y + (base_y - top_y) * (i + 1) / steps
        d.line([(cx - half(y0), y0), (cx + half(y1), y1)], fill=sil, width=2)
        d.line([(cx + half(y0), y0), (cx - half(y1), y1)], fill=sil, width=2)
        d.line([(cx - half(y1), y1), (cx + half(y1), y1)], fill=sil, width=2)
    mast_top = top_y - 26
    d.line([(cx, top_y), (cx, mast_top)], fill=sil, width=3)
    boom_y = mast_top + 6
    d.line([(cx - 78, boom_y), (cx + 78, boom_y)], fill=sil, width=3)
    for i, length in enumerate([30, 26, 24, 22, 20, 18]):
        x = cx - 70 + i * 28
        d.line([(x, boom_y - length / 2), (x, boom_y + length / 2)], fill=sil, width=2)

    # Radio waves leaving the antenna.
    waves = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    wd = ImageDraw.Draw(waves)
    for i, r in enumerate([40, 70, 100, 130]):
        a = 120 - i * 22
        box = (cx + 90 - r, boom_y - r, cx + 90 + r, boom_y + r)
        wd.arc(box, start=-40, end=40, fill=(120, 220, 255, a), width=3)
    waves = waves.filter(ImageFilter.GaussianBlur(0.8))
    img.paste(waves, (0, 0), waves)
    return img


def waterfall():
    img = Image.new("RGB", (W, H), (3, 12, 24))
    px = img.load()
    rnd = random.Random(1200)
    # Signal tracks as (centre x, half width, strength): sync at 1200 Hz, video 1500-2300 Hz.
    def freq_x(f):
        return W * (0.08 + 0.84 * (f - 400) / 2600)
    tracks = [(freq_x(1200), 3, 0.9)] + [(freq_x(f), 26, 0.45) for f in (1600, 1900, 2200)]
    for y in range(H):
        drift = 4 * math.sin(y / 61.0)
        fade = 0.35 + 0.65 * min(1.0, max(0.0, (y - H * 0.28) / (H * 0.2)))
        for x in range(W):
            n = rnd.random() ** 3
            v = 0.10 + 0.25 * n
            for tx, hw, s in tracks:
                dx = abs(x - tx - drift)
                if dx < hw * 3:
                    v += s * math.exp(-(dx / hw) ** 2) * (0.6 + 0.4 * rnd.random())
            v = min(1.0, v) * fade
            px[x, y] = lerp((3, 12, 24), (60, 200, 230), v) if v < 0.6 else lerp((60, 200, 230), (250, 240, 160), (v - 0.6) / 0.4)
    d = ImageDraw.Draw(img, "RGBA")
    # Faint frequency grid every 500 Hz.
    for f in range(500, 3000, 500):
        x = freq_x(f)
        d.line([(x, 0), (x, H)], fill=(120, 170, 200, 28), width=1)
    # Spectrum trace across the middle.
    pts = []
    for x in range(0, W, 3):
        level = 0.08 + 0.04 * math.sin(x / 9.0)
        for tx, hw, s in tracks:
            level += s * 0.6 * math.exp(-((x - tx) / (hw * 1.6)) ** 2)
        pts.append((x, H * 0.66 - level * H * 0.28))
    d.line(pts, fill=(140, 240, 255, 150), width=2)
    return img


def globe_grid():
    img = Image.new("RGB", (W, H))
    px = img.load()
    cx0, cy0 = W * 0.42, H * 0.52
    for y in range(H):
        for x in range(W):
            r = math.hypot(x - cx0, y - cy0) / (W * 0.7)
            px[x, y] = lerp((4, 30, 34), (1, 6, 9), min(1.0, r))

    d = ImageDraw.Draw(img, "RGBA")
    R = H * 0.36
    d.ellipse((cx0 - R, cy0 - R, cx0 + R, cy0 + R), fill=(6, 44, 46), outline=(40, 120, 115), width=2)

    tilt = math.radians(20)
    rot = math.radians(40)

    def project(lat, lon):
        la, lo = math.radians(lat), math.radians(lon) + rot
        x, y, z = math.cos(la) * math.sin(lo), math.sin(la), math.cos(la) * math.cos(lo)
        y, z = y * math.cos(tilt) - z * math.sin(tilt), y * math.sin(tilt) + z * math.cos(tilt)
        return (cx0 + R * x, cy0 - R * y), z

    def draw_path(points, fill, width):
        seg = []
        for p, z in points:
            if z > 0:
                seg.append(p)
            elif seg:
                if len(seg) > 1:
                    d.line(seg, fill=fill, width=width)
                seg = []
        if len(seg) > 1:
            d.line(seg, fill=fill, width=width)

    grid = (40, 120, 115, 150)
    for lat in range(-60, 90, 30):
        draw_path([project(lat, lon) for lon in range(-180, 181, 3)], grid, 1)
    for lon in range(-180, 180, 30):
        draw_path([project(lat, lon) for lat in range(-90, 91, 3)], grid, 1)

    # Great-circle QSO path between two stations, kept on the left half of the globe.
    a, b = (52.0, -40.0), (-20.0, -85.0)
    def to_vec(lat, lon):
        la, lo = math.radians(lat), math.radians(lon)
        return (math.cos(la) * math.cos(lo), math.cos(la) * math.sin(lo), math.sin(la))
    va, vb = to_vec(*a), to_vec(*b)
    omega = math.acos(sum(p * q for p, q in zip(va, vb)))
    path = []
    for i in range(61):
        t = i / 60
        s0, s1 = math.sin((1 - t) * omega) / math.sin(omega), math.sin(t * omega) / math.sin(omega)
        v = [s0 * p + s1 * q for p, q in zip(va, vb)]
        path.append(project(math.degrees(math.asin(v[2])), math.degrees(math.atan2(v[1], v[0]))))
    draw_path(path, (255, 176, 0, 210), 3)
    for end in (a, b):
        (x, y), z = project(*end)
        if z > 0:
            d.ellipse((x - 5, y - 5, x + 5, y + 5), fill=(255, 200, 60, 255))
            for r, al in ((10, 120), (16, 60)):
                d.ellipse((x - r, y - r, x + r, y + r), outline=(255, 200, 60, al), width=2)
    return img


BACKDROPS = {"tower-dusk.png": tower_dusk, "waterfall.png": waterfall, "globe-grid.png": globe_grid}


def main():
    out = Path(sys.argv[1]) if len(sys.argv) > 1 else Path(__file__).resolve().parent.parent / "assets" / "templates" / "backdrops"
    out.mkdir(parents=True, exist_ok=True)
    for name, draw in BACKDROPS.items():
        img = draw()
        if name == "waterfall.png":
            # Fine noise compresses badly as truecolour; 64 colours look the same at SSTV sizes.
            img = img.quantize(colors=64, method=Image.Quantize.MEDIANCUT, dither=Image.Dither.NONE)
        img.save(out / name, optimize=True)
        print(out / name)


if __name__ == "__main__":
    main()
