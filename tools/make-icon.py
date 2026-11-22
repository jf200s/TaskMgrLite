#!/usr/bin/env python3
"""Generate the TaskMgrLite icon from a single vector-ish design.

Design: dark rounded square, three process-list bars, mint lightning bolt.
Pillow is only used at build time; the shipped app only carries the .ico.
"""

from __future__ import annotations

import os
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter

ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / "src" / "TaskMgrLite" / "Assets"
DOCS = ROOT / "docs"

S = 2048          # supersampled canvas
K = S / 1024.0    # design is 1024x1024

BG_TOP = (30, 30, 46, 255)       # #1E1E2E
BG_BOTTOM = (42, 32, 62, 255)    # dark purple
BLUE = (137, 180, 250, 255)      # #89B4FA
BLUE_DEEP = (104, 145, 215, 255)
MINT = (166, 227, 161, 255)      # #A6E3A1
MINT_DEEP = (126, 205, 170, 255)
BORDER = (66, 68, 92, 190)


def sc(v: float) -> int:
    return int(round(v * K))


def lerp(a: int, b: int, t: float) -> int:
    return int(round(a + (b - a) * t))


def vertical_gradient(size: int, top: tuple[int, int, int, int],
                      bottom: tuple[int, int, int, int]) -> Image.Image:
    img = Image.new("RGBA", (size, size))
    px = img.load()
    for y in range(size):
        t = y / max(1, size - 1)
        color = (lerp(top[0], bottom[0], t), lerp(top[1], bottom[1], t),
                 lerp(top[2], bottom[2], t), 255)
        for x in range(size):
            px[x, y] = color
    return img


def main() -> None:
    ASSETS.mkdir(parents=True, exist_ok=True)
    DOCS.mkdir(parents=True, exist_ok=True)

    canvas = Image.new("RGBA", (S, S), (0, 0, 0, 0))

    # Rounded-square background with a vertical dark-blue -> dark-purple gradient.
    bg = vertical_gradient(S, BG_TOP, BG_BOTTOM)
    mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(mask).rounded_rectangle(
        (sc(44), sc(44), sc(980), sc(980)), radius=sc(220), fill=255)
    canvas.alpha_composite(Image.composite(bg, Image.new("RGBA", (S, S)), mask))

    # Soft blue glow in the lower-right, keeps the icon from looking flat.
    glow = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    gd = ImageDraw.Draw(glow)
    gd.ellipse((sc(560), sc(560), sc(1100), sc(1080)),
               fill=(137, 180, 250, 55))
    glow = glow.filter(ImageFilter.GaussianBlur(sc(90)))
    canvas.alpha_composite(Image.composite(
        glow, Image.new("RGBA", (S, S)), mask))

    # Three list bars. Slight different widths read as a process tree/list.
    bars = [
        (230, 300, 700, 400),
        (230, 460, 590, 560),
        (230, 620, 650, 720),
    ]
    bar_layer = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    bd = ImageDraw.Draw(bar_layer)
    for i, (x0, y0, x1, y1) in enumerate(bars):
        color = BLUE if i != 1 else (150, 190, 250, 255)
        bd.rounded_rectangle(
            (sc(x0), sc(y0), sc(x1), sc(y1)), radius=sc(50), fill=color)
    canvas.alpha_composite(bar_layer)

    # Bullet nodes: tiny mint dots on the left, a nod to "process list".
    nodes = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    nd = ImageDraw.Draw(nodes)
    for cy in (350, 510, 670):
        nd.ellipse((sc(162), sc(cy - 23), sc(208), sc(cy + 23)),
                   fill=(166, 227, 161, 220))
    canvas.alpha_composite(nodes)

    # Lightning bolt: mint body, blue glow underneath.
    bolt = [
        (742, 218), (574, 530), (694, 530), (604, 818),
        (866, 448), (730, 448),
    ]
    bolt_scaled = [(sc(x), sc(y)) for x, y in bolt]

    bolt_glow = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    ImageDraw.Draw(bolt_glow).polygon(bolt_scaled, fill=(137, 180, 250, 135))
    bolt_glow = bolt_glow.filter(ImageFilter.GaussianBlur(sc(34)))
    canvas.alpha_composite(bolt_glow)

    bolt_layer = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    bl = ImageDraw.Draw(bolt_layer)
    bl.polygon(bolt_scaled, fill=MINT)
    # A small blue edge along the right face gives it a subtle 3D/Fluent feel.
    bl.line([bolt_scaled[0], bolt_scaled[1], bolt_scaled[2]],
            fill=(137, 180, 250, 210), width=sc(8), joint="curve")
    canvas.alpha_composite(bolt_layer)

    # Inner border / top highlight.
    border = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    ImageDraw.Draw(border).rounded_rectangle(
        (sc(47), sc(47), sc(977), sc(977)), radius=sc(217),
        outline=BORDER, width=sc(9))
    canvas.alpha_composite(border)

    # Master png + standard icon sizes.
    master = canvas.resize((1024, 1024), Image.Resampling.LANCZOS)
    master.save(DOCS / "icon.png")
    master.save(ASSETS / "app-256.png")
    master.resize((256, 256), Image.Resampling.LANCZOS).save(
        ASSETS / "app-256.png")

    sizes = [(16, 16), (20, 20), (24, 24), (32, 32), (40, 40),
             (48, 48), (64, 64), (128, 128), (256, 256)]
    master.save(ASSETS / "app.ico", format="ICO", sizes=sizes)

    # Preview: dark and light taskbar backgrounds + small size ladder.
    preview = Image.new("RGBA", (1200, 620), (20, 20, 23, 255))
    pd = ImageDraw.Draw(preview)
    pd.rectangle((600, 0, 1200, 620), fill=(246, 246, 249, 255))
    dark_icon = master.resize((256, 256), Image.Resampling.LANCZOS)
    preview.alpha_composite(dark_icon, (120, 92))
    preview.alpha_composite(dark_icon, (748, 92))
    small = [16, 24, 32, 48, 64]
    x = 120
    for size in small:
        icon = master.resize((size, size), Image.Resampling.LANCZOS)
        preview.alpha_composite(icon, (x, 430))
        x += size + 48
    x = 748
    for size in small:
        icon = master.resize((size, size), Image.Resampling.LANCZOS)
        preview.alpha_composite(icon, (x, 430))
        x += size + 48
    preview.convert("RGB").save(DOCS / "icon-preview.png")

    for path in (DOCS / "icon.png", ASSETS / "app.ico",
                 ASSETS / "app-256.png", DOCS / "icon-preview.png"):
        print(f"{path}  {os.path.getsize(path)} bytes")


if __name__ == "__main__":
    main()
