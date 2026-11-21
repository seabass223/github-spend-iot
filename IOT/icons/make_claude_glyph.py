"""Builds 5x8 LCD-style glyphs and saves each as PNG + ICO.

The 8 bytes printed for each glyph are what you load into the LCD's custom-character
memory (CGRAM) with lcdCreateChar().
Run:  python icons/make_claude_glyph.py
"""
from pathlib import Path

from PIL import Image, ImageDraw

# 1 = dark dot. Each glyph is 8 rows of 5 dots.
GLYPHS = {
    # Claude Code mascot: body with two eye holes, legs at the bottom.
    "claude_code_5x8": [
        "00000",
        "11111",
        "10101",
        "11111",
        "11111",
        "11111",
        "10101",
        "00000",
    ],
    # GitHub-style cat: pointed ears, eye holes, narrow body, tail on the right, legs.
    "github_cat_5x8": [
        "10001",
        "11111",
        "10101",
        "11111",
        "01110",
        "01111",
        "01010",
        "00000",
    ],
    # ElevenLabs: two vertical bars in a box. Five dots is too narrow for side walls plus
    # gaps around both bars, so only the top and bottom edges of the box are drawn.
    "elevenlabs_5x8": [
        "11111",
        "00000",
        "01010",
        "01010",
        "01010",
        "00000",
        "11111",
        "00000",
    ],
    # Thumbs up: raised thumb over a closed fist. Animated by the Photon after a good refresh.
    "thumbs_up_5x8": [
        "00100",
        "00100",
        "01100",
        "11111",
        "11111",
        "11111",
        "11110",
        "00000",
    ],
    # Backslash for the refresh spinner: the LCD's font has a yen sign where "\" would be.
    # Mirror image of the font's own "/".
    "backslash_5x8": [
        "00000",
        "10000",
        "01000",
        "00100",
        "00010",
        "00001",
        "00000",
        "00000",
    ],
    # OpenAI logo: round interlaced knot reduced to a ring with diagonal gaps and a centre dot.
    "openai_logo_5x8": [
        "00000",
        "01110",
        "11011",
        "10101",
        "11011",
        "01110",
        "00000",
        "00000",
    ],
}

OUT = Path(__file__).resolve().parent


def render(glyph: list[str], size: int) -> Image.Image:
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    draw = ImageDraw.Draw(img)
    pitch = size / 9                      # 8 rows plus a margin
    dot = pitch * 0.86                    # small gap between dots, like the real LCD
    x0 = (size - pitch * 5) / 2
    y0 = (size - pitch * 8) / 2
    for r, row in enumerate(glyph):
        for c, bit in enumerate(row):
            if bit == "1":
                x = x0 + c * pitch + (pitch - dot) / 2
                y = y0 + r * pitch + (pitch - dot) / 2
                draw.rectangle([x, y, x + dot, y + dot], fill=(0, 0, 0, 255))
    return img


def main() -> None:
    for name, glyph in GLYPHS.items():
        big = render(glyph, 256)
        big.save(OUT / f"{name}.png")
        big.save(OUT / f"{name}.ico",
                 sizes=[(16, 16), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
        cgram = ", ".join(f"0x{int(row, 2):02X}" for row in glyph)
        print(f"{name}: {cgram}")


if __name__ == "__main__":
    main()
