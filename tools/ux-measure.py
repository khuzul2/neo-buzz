#!/usr/bin/env python3
"""UX measurements from gallery screenshots (docs/specs/ux-overhaul.md §8).

Contrast is WCAG 2.2's, from the pixels: in a region, the background is the most common colour and the
foreground the colour farthest from it in luminance that covers at least --min-pixels pixels (antialiased
text has a solid core). Sizes are in points: the Mac gallery is drawn at scale 2 (--scale).

  ux-measure.py contrast <png> <x> <y> <w> <h> [--scale 2]      region in points
  ux-measure.py pair <png> <x1> <y1> <x2> <y2> [--scale 2]      two points' colours
  ux-measure.py colour <png> <x> <y> [--scale 2]                one point's colour
  ux-measure.py deltae <hex> <hex>                              colour distance (CIE76)
  ux-measure.py ratio <hex> <hex>                               contrast of two colours

Needs Pillow (pip install pillow).
"""
import argparse
import math
import sys
from collections import Counter


def _lin(c):
    c = c / 255
    return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4


def luminance(rgb):
    r, g, b = rgb[:3]
    return 0.2126 * _lin(r) + 0.7152 * _lin(g) + 0.0722 * _lin(b)


def ratio(a, b):
    la, lb = luminance(a), luminance(b)
    return (max(la, lb) + 0.05) / (min(la, lb) + 0.05)


def lab(rgb):
    r, g, b = (_lin(v) for v in rgb[:3])
    x = (r * 0.4124 + g * 0.3576 + b * 0.1805) / 0.95047
    y = r * 0.2126 + g * 0.7152 + b * 0.0722
    z = (r * 0.0193 + g * 0.1192 + b * 0.9505) / 1.08883
    f = lambda t: t ** (1 / 3) if t > 0.008856 else 7.787 * t + 16 / 116
    return 116 * f(y) - 16, 500 * (f(x) - f(y)), 200 * (f(y) - f(z))


def delta_e(a, b):
    return math.dist(lab(a), lab(b))


def hexrgb(h):
    h = h.lstrip('#')
    if len(h) == 8:
        h = h[2:]
    return tuple(int(h[i:i + 2], 16) for i in (0, 2, 4))


def hexof(rgb):
    return '#%02X%02X%02X' % tuple(rgb[:3])


def region_contrast(img, x, y, w, h, scale, min_pixels):
    box = img.crop((round(x * scale), round(y * scale), round((x + w) * scale), round((y + h) * scale))).convert('RGB')
    counts = Counter(box.get_flattened_data() if hasattr(box, "get_flattened_data") else box.getdata())
    background, _ = counts.most_common(1)[0]
    lb = luminance(background)
    candidates = [c for c, n in counts.items() if n >= min_pixels]
    foreground = max(candidates, key=lambda c: abs(luminance(c) - lb))
    return background, foreground, ratio(background, foreground)


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument('what')
    p.add_argument('args', nargs='*')
    p.add_argument('--scale', type=float, default=2.0)
    p.add_argument('--min-pixels', type=int, default=6)
    a = p.parse_args()
    if a.what == 'ratio':
        x, y = hexrgb(a.args[0]), hexrgb(a.args[1])
        print(f'{ratio(x, y):.2f}:1')
        return
    if a.what == 'deltae':
        print(f'{delta_e(hexrgb(a.args[0]), hexrgb(a.args[1])):.1f}')
        return
    from PIL import Image
    img = Image.open(a.args[0]).convert('RGB')
    n = [float(v) for v in a.args[1:]]
    if a.what == 'contrast':
        bg, fg, r = region_contrast(img, *n[:4], a.scale, a.min_pixels)
        print(f'{r:.2f}:1  background {hexof(bg)}  foreground {hexof(fg)}')
    elif a.what == 'pair':
        c1 = img.getpixel((round(n[0] * a.scale), round(n[1] * a.scale)))
        c2 = img.getpixel((round(n[2] * a.scale), round(n[3] * a.scale)))
        print(f'{ratio(c1, c2):.2f}:1  {hexof(c1)} / {hexof(c2)}')
    elif a.what == 'colour':
        print(hexof(img.getpixel((round(n[0] * a.scale), round(n[1] * a.scale)))))
    else:
        sys.exit('unknown: ' + a.what)


if __name__ == '__main__':
    main()
