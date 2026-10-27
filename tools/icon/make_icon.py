"""Draws SysStateTracer.ico: a small hard drive on black with green and blue "file name" lines
overlapping its right side. Each size is drawn separately so small sizes stay crisp.

Run from the repository root:  python tools/icon/make_icon.py
Needs Pillow (pip install pillow). Writes src/SysStateTracer/SysStateTracer.ico and a preview PNG.
"""
from pathlib import Path

from PIL import Image, ImageDraw

SIZES = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]
SUPERSAMPLE = 8

BACKGROUND = (8, 10, 12, 255)
BODY = (150, 158, 168, 255)
BODY_EDGE = (205, 212, 220, 255)
BODY_DARK = (92, 99, 108, 255)
PLATTER = (58, 64, 72, 255)
PLATTER_RING = (120, 128, 138, 255)
SPINDLE = (205, 212, 220, 255)
ARM = (225, 230, 236, 255)
LED = (60, 230, 110, 255)
GREEN = (46, 204, 113, 255)
BLUE = (52, 152, 219, 255)


def draw(size: int) -> Image.Image:
    s = size * SUPERSAMPLE
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    u = s / 32.0  # design grid: 32 x 32 units
    small = size <= 24

    # Black rounded square
    d.rounded_rectangle([0, 0, s - 1, s - 1], radius=5 * u, fill=BACKGROUND)

    # Drive body: a box seen from the front-top, left part of the icon
    body = [3 * u, 7 * u, 20 * u, 25 * u]
    d.rounded_rectangle(body, radius=2.2 * u, fill=BODY, outline=BODY_EDGE, width=max(1, int(0.8 * u)))
    if not small:
        # Platter with spindle and read arm
        cx, cy, r = 11.5 * u, 15 * u, 6.2 * u
        d.ellipse([cx - r, cy - r, cx + r, cy + r], fill=PLATTER, outline=PLATTER_RING, width=int(0.7 * u))
        r2 = 3.6 * u
        d.ellipse([cx - r2, cy - r2, cx + r2, cy + r2], outline=PLATTER_RING, width=int(0.5 * u))
        r3 = 1.2 * u
        d.ellipse([cx - r3, cy - r3, cx + r3, cy + r3], fill=SPINDLE)
        d.line([17.5 * u, 22 * u, 13.2 * u, 16.4 * u], fill=ARM, width=int(1.1 * u))
        d.ellipse([16.6 * u, 21.1 * u, 18.6 * u, 23.1 * u], fill=ARM)
    else:
        # At 16-24 px a darker slot reads better than a platter
        d.rounded_rectangle([5 * u, 10 * u, 18 * u, 19 * u], radius=1.5 * u, fill=PLATTER)
    # Activity LED
    d.rounded_rectangle([5 * u, 21.6 * u, 8.6 * u, 23.2 * u], radius=0.6 * u, fill=LED)

    # "File names": green and blue lines overlapping the drive's right side
    if small:
        lines = [(10.5, 22, GREEN), (14.5, 29, BLUE), (18.5, 25, GREEN)]
        thickness = 2.6
    else:
        lines = [(9.0, 26, GREEN), (12.5, 29.5, BLUE), (16.0, 23, GREEN), (19.5, 28, BLUE), (23.0, 25, GREEN)]
        thickness = 2.0
    for y, end, color in lines:
        top = y * u
        bottom = (y + thickness) * u
        # dark gap so each line stands apart from the drive behind it
        d.rounded_rectangle([14 * u, top - 0.6 * u, end * u + 0.6 * u, bottom + 0.6 * u], radius=1.4 * u, fill=BACKGROUND)
        d.rounded_rectangle([15 * u, top, end * u, bottom], radius=thickness / 2 * u, fill=color)

    return img.resize((size, size), Image.LANCZOS)


def main() -> None:
    root = Path(__file__).resolve().parents[2]
    images = [draw(size) for size in SIZES]
    target = root / "src" / "SysStateTracer" / "SysStateTracer.ico"
    images[-1].save(target, format="ICO", sizes=[(s, s) for s in SIZES], append_images=images[:-1])

    preview = Image.new("RGBA", (sum(SIZES) + 10 * len(SIZES), 256), (255, 255, 255, 255))
    x = 0
    for img in images:
        preview.paste(img, (x, 256 - img.height), img)
        x += img.width + 10
    preview.save(Path(__file__).with_name("preview.png"))
    print(f"wrote {target} ({target.stat().st_size} bytes)")


if __name__ == "__main__":
    main()
