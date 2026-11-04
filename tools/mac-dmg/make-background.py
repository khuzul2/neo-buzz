#!/usr/bin/env python3
"""Draws the disk image's window background (tools/mac-dmg.sh): background.png at 640x480 and
background@2x.png for Retina, which dmgbuild combines into one HiDPI TIFF. The positions match
settings.py's icon_locations. Needs Pillow and the Inter font (OFL); the PNGs are committed, so CI
needs neither.

    python3 tools/mac-dmg/make-background.py [font folder]
"""
import os
import sys

from PIL import Image, ImageDraw, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
FONTS = sys.argv[1] if len(sys.argv) > 1 else "/usr/share/fonts/opentype/inter"
W, H = 640, 480
APP, APPLICATIONS = (170, 190), (470, 190)   # icon centres, as settings.py places them
README = (556, 372)

INK = (29, 29, 31)
SOFT = (99, 99, 102)
ARROW = (142, 142, 147)
PANEL = (236, 236, 240)


def font(weight, size, scale):
    return ImageFont.truetype(os.path.join(FONTS, f"Inter-{weight}.otf"), round(size * scale))


def draw(scale):
    s = lambda v: round(v * scale)
    img = Image.new("RGB", (s(W), s(H)), (248, 248, 250))
    d = ImageDraw.Draw(img)

    # The heading.
    d.text((s(W / 2), s(44)), "Install ReBuzz", font=font("SemiBold", 22, scale), fill=INK, anchor="mm")
    d.text((s(W / 2), s(72)), "Drag ReBuzz onto the Applications folder.", font=font("Regular", 14, scale),
           fill=SOFT, anchor="mm")

    # The arrow between the two icons.
    y = APP[1]
    x0, x1 = APP[0] + 78, APPLICATIONS[0] - 78
    d.rounded_rectangle((s(x0), s(y - 5), s(x1 - 18), s(y + 5)), radius=s(5), fill=ARROW)
    d.polygon([(s(x1 - 26), s(y - 17)), (s(x1), s(y)), (s(x1 - 26), s(y + 17))], fill=ARROW)

    # The first launch, which needs one confirmation (the app is not notarized).
    top, left = 300, 32
    d.rounded_rectangle((s(left), s(top), s(W - left), s(H - 28)), radius=s(12), fill=PANEL)
    d.text((s(left + 20), s(top + 22)), "The first time you open it", font=font("SemiBold", 13, scale), fill=INK)
    lines = [
        "macOS says it can't verify ReBuzz, because the app isn't",
        "notarized by Apple. Close that message (not Move to Trash),",
        "then go to System Settings → Privacy & Security and click",
        "Open Anyway. You only do this once. (Read me first.txt has",
        "the Terminal alternative.)",
    ]
    body = font("Regular", 12.5, scale)
    for i, line in enumerate(lines):
        d.text((s(left + 20), s(top + 48 + i * 19)), line, font=body, fill=SOFT)
    return img


if __name__ == "__main__":
    draw(1).save(os.path.join(HERE, "background.png"), optimize=True)
    draw(2).save(os.path.join(HERE, "background@2x.png"), optimize=True)
    print("background.png, background@2x.png written")
