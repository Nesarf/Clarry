#!/usr/bin/env python
"""Turn a generated portrait into a real pixel-art sprite.

The image model draws a very good illustration and quietly ignores the words "pixel
art" in the prompt, so the pixels have to be made rather than asked for:

  1. area-average down to a small canvas (BOX resampling, so colours mix honestly)
  2. cut the palette down so the result reads as a sprite, not a shrunken painting
  3. scale back up with NEAREST so every pixel is a hard square

Dithering is off on purpose: ordered dithering makes a sprite look muddy at this size.
"""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

from PIL import Image, ImageChops, ImageEnhance

try:
    BOX = Image.Resampling.BOX
    NEAREST = Image.Resampling.NEAREST
    MEDIANCUT = Image.Quantize.MEDIANCUT
    NO_DITHER = Image.Dither.NONE
except AttributeError:  # Pillow < 9.1
    BOX = Image.BOX
    NEAREST = Image.NEAREST
    MEDIANCUT = Image.MEDIANCUT
    NO_DITHER = Image.NONE


def cut_background(im: Image.Image, brightness: int, neutrality: int) -> Image.Image:
    """Remove the flat dark backdrop without eating the character's own shadows.

    Brightness alone is the wrong test. These portraits are lit so that a cloak or a
    breastplate can be darker than the "solid black" behind them, and a flood fill run
    on brightness therefore leaks straight through the silhouette and hollows the figure
    out. What actually separates backdrop from costume is that the backdrop is *neutral*:
    its three channels sit within a few points of each other, while cloth and metal carry
    a clear hue. JPEG noise on the backdrop is grey, so it falls on the right side too.

    So a pixel is backdrop when it is both dark and colourless, and this runs before the
    shrink so the noise can never be averaged up into pale specks.
    """
    r, g, b, _ = im.split()
    brightest = ImageChops.lighter(ImageChops.lighter(r, g), b)
    darkest = ImageChops.darker(ImageChops.darker(r, g), b)
    spread = ImageChops.subtract(brightest, darkest)

    dark = brightest.point(lambda v: 255 if v < brightness else 0)
    flat = spread.point(lambda v: 255 if v <= neutrality else 0)
    backdrop = ImageChops.multiply(dark, flat)

    clear = Image.new("RGBA", im.size, (0, 0, 0, 0))
    return Image.composite(clear, im, backdrop)


def pixelize(path: Path, out_dir: Path | None, width: int, colors: int, scale: int,
             key_black: int, suffix: str, saturate: float, contrast: float,
             cut_bg: tuple[int, int] | None) -> Path:
    im = Image.open(path).convert("RGBA")
    if cut_bg is not None:
        im = cut_background(im, cut_bg[0], cut_bg[1])

    src_w, src_h = im.size
    height = max(1, round(src_h * width / src_w))

    small = im.resize((width, height), BOX)

    alpha = small.getchannel("A")
    rgb = small.convert("RGB")
    # Averaging merges a hundred metal shades into one muddy brown. Lifting the
    # saturation before the palette is cut keeps the gold readable at 32 colours.
    if saturate != 1.0:
        rgb = ImageEnhance.Color(rgb).enhance(saturate)
    if contrast != 1.0:
        rgb = ImageEnhance.Contrast(rgb).enhance(contrast)

    quantized = rgb.quantize(colors=colors, method=MEDIANCUT, dither=NO_DITHER).convert("RGBA")
    quantized.putalpha(alpha)

    if key_black > 0:
        px = quantized.load()
        for y in range(height):
            for x in range(width):
                r, g, b, a = px[x, y]
                if r < key_black and g < key_black and b < key_black:
                    px[x, y] = (r, g, b, 0)

    big = quantized.resize((width * scale, height * scale), NEAREST)

    target = (out_dir or path.parent) / f"{path.stem}{suffix}.png"
    target.parent.mkdir(parents=True, exist_ok=True)
    big.save(target)
    print(f"  {path.name} → {target.name}　{width}×{height} 格，"
          f"{colors} 色，放大 {scale} 倍 = {big.width}×{big.height}")
    return target


def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser(description="Make a portrait into a pixel sprite.")
    ap.add_argument("inputs", nargs="+", help="source images")
    ap.add_argument("--out-dir", default=None)
    ap.add_argument("--width", type=int, default=96, help="grid width in pixels (default 96)")
    ap.add_argument("--colors", type=int, default=24, help="palette size (default 24)")
    ap.add_argument("--scale", type=int, default=8, help="nearest-neighbour zoom (default 8)")
    ap.add_argument("--key-black", type=int, default=0,
                    help="make pixels darker than this transparent; 0 keeps everything")
    ap.add_argument("--cut-bg", default=None, metavar="BRIGHT:FLAT",
                    help="cut the backdrop: a pixel counts as background when its "
                         "brightest channel is under BRIGHT and its channel spread is at "
                         "most FLAT, e.g. 55:18 (omit to keep the backdrop)")
    ap.add_argument("--saturate", type=float, default=1.0,
                    help="colour boost applied before the palette is cut (default 1.0)")
    ap.add_argument("--contrast", type=float, default=1.0,
                    help="contrast boost applied before the palette is cut (default 1.0)")
    ap.add_argument("--suffix", default="_px")
    args = ap.parse_args(argv)

    cut_bg = None
    if args.cut_bg:
        try:
            bright, flat = (int(v) for v in args.cut_bg.split(":"))
            cut_bg = (bright, flat)
        except ValueError:
            ap.error("--cut-bg expects BRIGHT:FLAT, e.g. 55:18")

    out_dir = Path(args.out_dir) if args.out_dir else None
    failures = 0
    for raw in args.inputs:
        path = Path(raw)
        if not path.is_file():
            print(f"  跳过 {path}：不是文件", file=sys.stderr)
            failures += 1
            continue
        try:
            pixelize(path, out_dir, args.width, args.colors, args.scale,
                     args.key_black, args.suffix, args.saturate, args.contrast,
                     cut_bg)
        except Exception as exc:  # keep going: one bad file must not stop a batch
            print(f"  失败 {path.name}：{type(exc).__name__}：{exc}", file=sys.stderr)
            failures += 1
    return 1 if failures else 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
