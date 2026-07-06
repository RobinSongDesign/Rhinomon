"""Convert an existing multi-pose character sheet into a CONTRACT sprite sheet.

Input: a transparent PNG laid out as horizontal rows of poses (any cell size,
irregular spacing tolerated). Rows top-to-bottom must carry the CONTRACT frame
counts (default 4,4,4,2,4,2,4 = idle/walk/climb/sleep/petted/surprised/happy);
decorative bands that are much shorter than a real pose row (floating "Zzz"
glyphs and the like) are detected and dropped automatically.

Output: a 256x224 sheet (32px tiles, 8 cols x 7 rows, frames left-aligned,
facing right, alpha strictly {0,255}) ready for %AppData%/Rhinomon/pets, plus
an optional upscaled preview with a checkerboard background for eyeballing.

Usage:
  python make_custom_sheet.py SRC.png OUT.png [--preview PREVIEW.png]
"""

import argparse
from PIL import Image, ImageChops, ImageDraw, ImageFont

TILE = 32
COLS = 8
ROW_NAMES = ["idle", "walk", "climb", "sleep", "petted", "surprised", "happy"]
DEFAULT_COUNTS = [4, 4, 4, 2, 4, 2, 4]

ALPHA_MIN = 16      # source pixels below this alpha count as empty
ALPHA_CUT = 128     # output alpha threshold -> {0,255}
CLUSTER_GAP = 8     # merge column clusters separated by less than this
MIN_BAND_RATIO = 0.4  # bands shorter than this x median height are decoration
BODY_H = 31         # tallest frame maps to this many pixels of the 32px tile


def bands(occupied):
    out, start = [], None
    for i, v in enumerate(occupied):
        if v and start is None:
            start = i
        elif not v and start is not None:
            out.append((start, i - 1))
            start = None
    if start is not None:
        out.append((start, len(occupied) - 1))
    return out


def detect_frames(im, expected_counts):
    """Returns per row a list of tight frame bboxes plus the row's baseline y."""
    w, h = im.size
    px = im.getchannel("A").load()

    row_occ = [any(px[x, y] >= ALPHA_MIN for x in range(w)) for y in range(h)]
    row_bands = bands(row_occ)
    heights = sorted(y1 - y0 + 1 for y0, y1 in row_bands)
    median_h = heights[len(heights) // 2]
    pose_bands = [b for b in row_bands
                  if (b[1] - b[0] + 1) >= MIN_BAND_RATIO * median_h]

    if len(pose_bands) != len(expected_counts):
        raise SystemExit(
            f"expected {len(expected_counts)} pose rows, found {len(pose_bands)} "
            f"(of {len(row_bands)} bands) - check the source layout")

    rows = []
    for (y0, y1), want in zip(pose_bands, expected_counts):
        col_occ = [any(px[x, y] >= ALPHA_MIN for y in range(y0, y1 + 1))
                   for x in range(w)]
        clusters = []
        for c in bands(col_occ):
            if clusters and c[0] - clusters[-1][1] < CLUSTER_GAP:
                clusters[-1] = (clusters[-1][0], c[1])
            else:
                clusters.append(c)
        if len(clusters) != want:
            raise SystemExit(
                f"row y{y0}-{y1}: expected {want} frames, found {len(clusters)}")
        frames = []
        for x0, x1 in clusters:
            ty0, ty1 = y1, y0
            for y in range(y0, y1 + 1):
                if any(px[x, y] >= ALPHA_MIN for x in range(x0, x1 + 1)):
                    ty0 = min(ty0, y)
                    ty1 = max(ty1, y)
            frames.append((x0, ty0, x1, ty1))
        rows.append((frames, y1))
    return rows


def scale_frame(im, bbox, s):
    """Premultiplied BOX downscale of one frame, alpha thresholded to {0,255}."""
    crop = im.crop((bbox[0], bbox[1], bbox[2] + 1, bbox[3] + 1))
    sw = max(1, round(crop.width * s))
    sh = max(1, round(crop.height * s))
    r, g, b, a = crop.split()
    pre = Image.merge("RGB", tuple(ImageChops.multiply(c, a) for c in (r, g, b)))
    pre = pre.resize((sw, sh), Image.BOX)
    a = a.resize((sw, sh), Image.BOX)

    out = Image.new("RGBA", (sw, sh), (0, 0, 0, 0))
    po, pp, pa = out.load(), pre.load(), a.load()
    for y in range(sh):
        for x in range(sw):
            av = pa[x, y]
            if av >= ALPHA_CUT:
                cr, cg, cb = pp[x, y]
                po[x, y] = (min(255, round(cr * 255 / av)),
                            min(255, round(cg * 255 / av)),
                            min(255, round(cb * 255 / av)), 255)
    return out


def build_sheet(im, rows):
    sheet = Image.new("RGBA", (COLS * TILE, len(rows) * TILE), (0, 0, 0, 0))

    # One global scale keeps the character the same size across animations;
    # rows whose frames would overflow a tile sideways (lying poses) get their
    # own best-fit scale instead.
    max_h = max(b[3] - b[1] + 1 for frames, _ in rows for b in frames)
    global_s = BODY_H / max_h

    for ri, (frames, baseline) in enumerate(rows):
        row_w = max(b[2] - b[0] + 1 for b in frames)
        row_h = max(b[3] - b[1] + 1 for b in frames)
        s = global_s
        if row_w * s > BODY_H:
            s = min(BODY_H / row_w, BODY_H / row_h)
        for ci, bbox in enumerate(frames):
            frame = scale_frame(im, bbox, s)
            # Feet on the tile's bottom row, preserving each frame's own
            # bounce offset from the row baseline.
            lift = round((baseline - bbox[3]) * s)
            fx = ci * TILE + (TILE - frame.width) // 2
            fy = ri * TILE + (TILE - 1 - lift) - (frame.height - 1)
            sheet.paste(frame, (fx, max(ri * TILE, fy)))
    return sheet


def save_preview(sheet, path, zoom=4):
    w, h = sheet.width * zoom, sheet.height * zoom
    prev = Image.new("RGB", (w, h))
    d = ImageDraw.Draw(prev)
    for y in range(0, h, 8):
        for x in range(0, w, 8):
            v = 200 if (x // 8 + y // 8) % 2 else 150
            d.rectangle((x, y, x + 7, y + 7), fill=(v, v, v))
    prev.paste(sheet.resize((w, h), Image.NEAREST),
               (0, 0), sheet.resize((w, h), Image.NEAREST))
    d = ImageDraw.Draw(prev)
    for i in range(0, COLS + 1):
        d.line((i * TILE * zoom, 0, i * TILE * zoom, h), fill=(255, 0, 0))
    try:
        font = ImageFont.load_default(size=6 * zoom)
    except TypeError:
        font = ImageFont.load_default()
    for i, name in enumerate(ROW_NAMES[:sheet.height // TILE]):
        d.line((0, i * TILE * zoom, w, i * TILE * zoom), fill=(255, 0, 0))
        d.text((5 * TILE * zoom, (i * TILE + 10) * zoom), name,
               fill=(180, 0, 0), font=font)
    prev.save(path)


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("src")
    ap.add_argument("out")
    ap.add_argument("--preview", help="write an upscaled annotated preview PNG")
    ap.add_argument("--counts", default=",".join(map(str, DEFAULT_COUNTS)),
                    help="comma-separated frames per row (default CONTRACT)")
    args = ap.parse_args()

    counts = [int(c) for c in args.counts.split(",")]
    im = Image.open(args.src).convert("RGBA")
    rows = detect_frames(im, counts)
    sheet = build_sheet(im, rows)
    sheet.save(args.out)
    print(f"wrote {args.out} ({sheet.width}x{sheet.height})")
    if args.preview:
        save_preview(sheet, args.preview)
        print(f"wrote {args.preview}")


if __name__ == "__main__":
    main()
