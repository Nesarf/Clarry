"""Compose the sprites over the stage's own backdrop and lay the cast out side by side.

A transparent pixel and a very pale pixel look identical on a white viewer background,
so judging the cut-out sprite there is misleading. The stage is dark navy; that is where
these will actually be seen, so that is where they should be checked.
"""

from pathlib import Path

from PIL import Image

PORTRAITS = Path(r"D:\Clarry\assets\portraits")
OUT = Path(r"D:\Clarry\data")
DARK = (0x1A, 0x1E, 0x3A, 255)
NAMES = ("alice_001_sprite", "bob_001_sprite", "carol_001_sprite")


def on_dark(name: str) -> Image.Image:
    sprite = Image.open(PORTRAITS / f"{name}.png").convert("RGBA")
    backdrop = Image.new("RGBA", sprite.size, DARK)
    composed = Image.alpha_composite(backdrop, sprite)
    composed.convert("RGB").save(OUT / f"{name}_on_dark.png")
    print(f"  {name}_on_dark.png")
    return composed


def main() -> None:
    sprites = [on_dark(name) for name in NAMES]

    sheet = Image.new("RGBA", (sum(s.width for s in sprites), max(s.height for s in sprites)), DARK)
    x = 0
    for sprite in sprites:
        sheet.alpha_composite(sprite, (x, 0))
        x += sprite.width
    sheet.convert("RGB").save(OUT / "cast_sheet.png")
    print(f"  cast_sheet.png　{sheet.width}×{sheet.height}")


if __name__ == "__main__":
    main()
