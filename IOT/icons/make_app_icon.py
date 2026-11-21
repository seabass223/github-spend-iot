"""Builds the app icon: an LCD screen with four stacked "percent left" meters.

Deliberately simple so it reads at 16 px: no glyphs, just four chunky bars of falling levels
(the four services, draining at different rates) on a yellow-green LCD in a dark bezel.

Run:  python icons/make_app_icon.py     (writes API/lcd-usage.ico and icons/app_icon_preview.png)
"""
from pathlib import Path

from PIL import Image, ImageDraw

HERE = Path(__file__).resolve().parent
ICO_OUT = HERE.parent.parent / "API" / "lcd-usage.ico"
PREVIEW_OUT = HERE / "app_icon_preview.png"

BEZEL = (32, 38, 46, 255)
SCREEN = (190, 214, 100, 255)     # yellow-green backlight, like the A-1748
INK = (24, 38, 12, 255)           # lit part of a meter
TRACK = (146, 171, 70, 255)       # empty part of a meter

LEVELS = [0.90, 0.65, 0.40, 0.15]  # fraction left, top to bottom


def render_large(size: int = 256) -> Image.Image:
    """Smooth version for 256 and for downscaling to 128/64/48."""
    s = size / 256
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    d.rounded_rectangle([8 * s, 8 * s, 248 * s, 248 * s], radius=34 * s, fill=BEZEL)
    d.rounded_rectangle([30 * s, 30 * s, 226 * s, 226 * s], radius=14 * s, fill=SCREEN)
    bar_h, gap = 30 * s, 14 * s
    top = 128 * s - (4 * bar_h + 3 * gap) / 2
    left, right = 50 * s, 206 * s
    for i, level in enumerate(LEVELS):
        y = top + i * (bar_h + gap)
        d.rounded_rectangle([left, y, right, y + bar_h], radius=7 * s, fill=TRACK)
        d.rounded_rectangle([left, y, left + (right - left) * level, y + bar_h], radius=7 * s, fill=INK)
    return img


def render_pixels(size: int) -> Image.Image:
    """Hand-placed whole pixels for 32 and 16 px so edges stay crisp."""
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    if size == 32:
        d.rounded_rectangle([0, 0, 31, 31], radius=5, fill=BEZEL)
        d.rectangle([3, 3, 28, 28], fill=SCREEN)
        bar_h, gap, top, left, width = 4, 2, 5, 6, 20
    else:  # 16
        d.rounded_rectangle([0, 0, 15, 15], radius=2, fill=BEZEL)
        d.rectangle([2, 2, 13, 13], fill=SCREEN)
        bar_h, gap, top, left, width = 2, 1, 3, 4, 8
    for i, level in enumerate(LEVELS):
        y = top + i * (bar_h + gap)
        d.rectangle([left, y, left + width - 1, y + bar_h - 1], fill=TRACK)
        d.rectangle([left, y, left + max(1, round(width * level)) - 1, y + bar_h - 1], fill=INK)
    return img


def main() -> None:
    big = render_large(256)
    frames = {256: big}
    for s in (128, 64, 48):
        frames[s] = big.resize((s, s), Image.LANCZOS)
    frames[32] = render_pixels(32)
    frames[16] = render_pixels(16)

    sizes = [256, 128, 64, 48, 32, 16]
    ordered = [frames[s] for s in sizes]
    ordered[0].save(ICO_OUT, format="ICO", sizes=[(s, s) for s in sizes], append_images=ordered[1:])

    # Preview: 256 icon, then 48/32/16 at true size on light and dark, and 32/16 enlarged.
    sheet = Image.new("RGBA", (256 + 24 + 140, 256 + 70), (255, 255, 255, 255))
    sheet.paste(big, (0, 0), big)
    for i, s in enumerate((32, 16)):
        e = frames[s].resize((128, 128), Image.NEAREST)
        sheet.paste(e, (256 + 24, i * 130), e)
    d = ImageDraw.Draw(sheet)
    d.rectangle([0, 262, 255, 326], fill=(255, 255, 255, 255))
    d.rectangle([128, 262, 255, 326], fill=(32, 32, 32, 255))
    x = 8
    for s in (48, 32, 16):
        sheet.paste(frames[s], (x, 270), frames[s])
        sheet.paste(frames[s], (x + 128, 270), frames[s])
        x += s + 10
    sheet.save(PREVIEW_OUT)
    print("wrote", ICO_OUT)


if __name__ == "__main__":
    main()
