"""Render src/DevinCleaner/app.ico: three black hexagons on a white rounded tile.

The mark matches DevinLogo in DesktopTheme.cs (pointy-top hexagons at the same
centres). Run: python scripts/make_icon.py
"""
import math
import os
from PIL import Image, ImageDraw

SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]
OUT = os.path.join(os.path.dirname(__file__), "..", "src", "DevinCleaner", "app.ico")

# Unit square, shared with DevinLogo.Hexagons.
CENTRES = [(0.36, 0.30), (0.36, 0.70), (0.70, 0.50)]
RADIUS = 0.205
NOTCH = 0.075


def hexagon(cx, cy, r):
    return [(cx + r * math.cos(math.radians(a)), cy + r * math.sin(math.radians(a))) for a in range(-90, 270, 60)]


def render(size):
    scale = 4
    s = size * scale
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    pad = round(s * 0.04)
    d.rounded_rectangle([pad, pad, s - pad - 1, s - pad - 1], radius=round(s * 0.22), fill=(255, 255, 255, 255), outline=(222, 222, 220, 255), width=max(1, scale))
    inset, span = s * 0.14, s * 0.72
    for cx, cy in CENTRES:
        d.polygon([(inset + x * span, inset + y * span) for x, y in hexagon(cx, cy, RADIUS)], fill=(17, 17, 17, 255))
    # The white notch where the three hexagons meet, as in the Devin mark.
    nx, ny = inset + 0.50 * span, inset + 0.50 * span
    nr = NOTCH * span
    d.ellipse([nx - nr, ny - nr, nx + nr, ny + nr], fill=(255, 255, 255, 255))
    return img.resize((size, size), Image.LANCZOS)


images = [render(n) for n in SIZES]
images[-1].save(OUT, format="ICO", sizes=[(n, n) for n in SIZES], append_images=images[:-1])
images[-1].save(os.path.join(os.environ.get("TEMP", "."), "devincleaner-icon-preview.png"))
print("wrote", os.path.abspath(OUT))
