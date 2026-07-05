#!/usr/bin/env python3
"""Compress an Image2-generated pet sheet into the Rhinomon atlas contract.

This keeps the model responsible for drawing the sprites and only does
format work:

* resize to 256x224 using nearest-neighbor
* clear unused cells from the fixed atlas contract
* remove model-drawn checker/chroma background by cell-edge flood fill
* force alpha to hard 0/255
* optionally emit a 4x preview
"""
from __future__ import annotations

import argparse
from collections import deque
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

CELL = 32
COLS = 8
ROWS = 7
SHEET_W = CELL * COLS
SHEET_H = CELL * ROWS
FRAMES = [4, 4, 4, 2, 4, 2, 4]
ANIMS = [
    ("idle", 4, 4, True),
    ("walk", 4, 6, True),
    ("climb", 4, 6, True),
    ("sleep", 2, 1, True),
    ("petted", 4, 6, False),
    ("surprised", 2, 6, False),
    ("happy", 4, 6, False),
]


def background_like(r: int, g: int, b: int, a: int) -> bool:
    if a < 32:
        return True
    if g > 220 and r < 40 and b < 40:
        return True
    return min(r, g, b) >= 214 and (max(r, g, b) - min(r, g, b)) <= 22


def clear_cell(px, x0: int, y0: int) -> None:
    for y in range(y0, y0 + CELL):
        for x in range(x0, x0 + CELL):
            px[x, y] = (0, 0, 0, 0)


def flood_clear_cell_background(px, x0: int, y0: int) -> None:
    barrier: set[tuple[int, int]] = set()
    for y in range(y0, y0 + CELL):
        for x in range(x0, x0 + CELL):
            if not background_like(*px[x, y]):
                barrier.add((x, y))

    # The model-drawn checker background can leak into white faces when the
    # downscaled outline has a one-pixel gap. Use a dilated barrier only for
    # the flood fill so enclosed white face/feet pixels stay opaque.
    dilated = set(barrier)
    for x, y in barrier:
        for ny in range(y - 1, y + 2):
            for nx in range(x - 1, x + 2):
                if x0 <= nx < x0 + CELL and y0 <= ny < y0 + CELL:
                    dilated.add((nx, ny))

    queue: deque[tuple[int, int]] = deque()
    seen: set[tuple[int, int]] = set()

    for i in range(CELL):
        queue.append((x0 + i, y0))
        queue.append((x0 + i, y0 + CELL - 1))
        queue.append((x0, y0 + i))
        queue.append((x0 + CELL - 1, y0 + i))

    while queue:
        x, y = queue.popleft()
        if (x, y) in seen:
            continue
        if not (x0 <= x < x0 + CELL and y0 <= y < y0 + CELL):
            continue

        seen.add((x, y))
        if (x, y) in dilated:
            continue

        px[x, y] = (0, 0, 0, 0)
        queue.extend(((x - 1, y), (x + 1, y), (x, y - 1), (x, y + 1)))

    for y in range(y0, y0 + CELL):
        for x in range(x0, x0 + CELL):
            if (x, y) in seen:
                continue
            r, g, b, a = px[x, y]
            if background_like(r, g, b, a):
                if (x, y) in dilated and (x, y) not in barrier:
                    px[x, y] = (0, 0, 0, 0)
                else:
                    px[x, y] = (246, 249, 255, 255)


def harden_alpha(img: Image.Image) -> Image.Image:
    data = []
    for r, g, b, a in img.getdata():
        data.append((r, g, b, 255) if a >= 128 else (0, 0, 0, 0))
    img.putdata(data)
    return img


def flip_active_cells_x(sheet: Image.Image) -> Image.Image:
    flipped = Image.new("RGBA", sheet.size, (0, 0, 0, 0))
    for row, frames in enumerate(FRAMES):
        for col in range(frames):
            box = (
                col * CELL,
                row * CELL,
                (col + 1) * CELL,
                (row + 1) * CELL,
            )
            cell = sheet.crop(box).transpose(Image.Transpose.FLIP_LEFT_RIGHT)
            flipped.paste(cell, box, cell)
    return flipped


def compress_sheet(src: Path, flip_cells_x: bool = False) -> Image.Image:
    sheet = Image.open(src).convert("RGBA")
    sheet = sheet.resize((SHEET_W, SHEET_H), Image.Resampling.NEAREST)
    px = sheet.load()

    for row, frames in enumerate(FRAMES):
        for col in range(COLS):
            x0, y0 = col * CELL, row * CELL
            if col >= frames:
                clear_cell(px, x0, y0)
            else:
                flood_clear_cell_background(px, x0, y0)

    sheet = harden_alpha(sheet)
    if flip_cells_x:
        sheet = flip_active_cells_x(sheet)
    return sheet


def checker(width: int, height: int, cell: int = 8) -> Image.Image:
    img = Image.new("RGB", (width, height), (104, 104, 104))
    draw = ImageDraw.Draw(img)
    for y in range(0, height, cell):
        for x in range(0, width, cell):
            if (x // cell + y // cell) % 2:
                draw.rectangle((x, y, x + cell - 1, y + cell - 1), fill=(92, 92, 92))
    return img


def load_font(size: int) -> ImageFont.ImageFont:
    try:
        return ImageFont.load_default(size=size)
    except TypeError:
        return ImageFont.load_default()


def save_preview(sheet: Image.Image, out: Path, label: str) -> None:
    scale = 4
    margin_left, margin_top = 176, 30
    big = sheet.resize((SHEET_W * scale, SHEET_H * scale), Image.Resampling.NEAREST)
    preview = Image.new(
        "RGB",
        (margin_left + big.width + 12, margin_top + big.height + 12),
        (34, 34, 34),
    )
    preview.paste(checker(big.width, big.height), (margin_left, margin_top))
    preview.paste(big, (margin_left, margin_top), big)

    draw = ImageDraw.Draw(preview)
    font = load_font(15)
    for col in range(COLS + 1):
        x = margin_left + col * CELL * scale
        draw.line((x, margin_top, x, margin_top + big.height), fill=(60, 60, 60))
    for row in range(ROWS + 1):
        y = margin_top + row * CELL * scale
        draw.line((margin_left, y, margin_left + big.width, y), fill=(60, 60, 60))
    for col in range(COLS):
        draw.text(
            (margin_left + col * CELL * scale + 4, 8),
            f"f{col}",
            fill=(200, 200, 200),
            font=font,
        )
    for row, (anim, frames, fps, loop) in enumerate(ANIMS):
        draw.text(
            (8, margin_top + row * CELL * scale + 4),
            f"{row} {anim} {frames}f@{fps} {'loop' if loop else 'once'}",
            fill=(230, 230, 230),
            font=font,
        )
    draw.text((8, preview.height - 22), label, fill=(150, 150, 150), font=font)
    preview.save(out)


def validate(sheet: Image.Image) -> list[str]:
    issues: list[str] = []
    if sheet.mode != "RGBA":
        issues.append(f"mode is {sheet.mode}, expected RGBA")
    if sheet.size != (SHEET_W, SHEET_H):
        issues.append(f"size is {sheet.size}, expected {(SHEET_W, SHEET_H)}")

    for row, frames in enumerate(FRAMES):
        for col in range(COLS):
            opaque = 0
            for y in range(CELL):
                for x in range(CELL):
                    if sheet.getpixel((col * CELL + x, row * CELL + y))[3]:
                        opaque += 1
            if col >= frames and opaque:
                issues.append(f"unused row {row} col {col}: {opaque} opaque px")
            if col < frames and not opaque:
                issues.append(f"active row {row} col {col}: empty")
    return issues


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("input", type=Path)
    parser.add_argument("--out", type=Path, required=True)
    parser.add_argument("--preview", type=Path)
    parser.add_argument(
        "--flip-cells-x",
        action="store_true",
        help="Flip each active 32x32 cell horizontally while preserving atlas layout.",
    )
    args = parser.parse_args()

    sheet = compress_sheet(args.input, flip_cells_x=args.flip_cells_x)
    args.out.parent.mkdir(parents=True, exist_ok=True)
    sheet.save(args.out)

    if args.preview:
        args.preview.parent.mkdir(parents=True, exist_ok=True)
        save_preview(sheet, args.preview, f"{args.out.name} 256x224  4x nearest")

    issues = validate(sheet)
    print(f"saved {args.out}")
    if args.preview:
        print(f"saved {args.preview}")
    print(f"alpha={sheet.getchannel('A').getextrema()} issues={len(issues)}")
    for issue in issues:
        print(f"- {issue}")
    return 1 if issues else 0


if __name__ == "__main__":
    raise SystemExit(main())
