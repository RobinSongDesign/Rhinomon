#!/usr/bin/env python3
"""Rhinomon sprite sheet generator.

Procedurally draws the three pet sprite sheets (clawd / crab / nova), the
emote strip and atlas.json exactly as specified by docs/CONTRACT.md:

  * per pet:  256 x 224 PNG-32, 8 cols x 7 rows of 32x32 cells
              row = animation, col = frame (left aligned, rest transparent)
              rows: idle4 walk4 climb4 sleep2 petted4 surprised2 happy4
              all frames face RIGHT, transparent bg, hard pixels (no AA)
  * emotes:   80 x 16, five 16x16 cells [heart, exclaim, question, zzz, sparkle]
  * atlas.json: machine readable description of the above
  * assets/preview: 4x nearest-neighbour inspection sheets + idle comparison

Deterministic: fixed seed, pure per-pixel math, no external assets.
Run:  python tools/spritegen/generate.py
"""
from __future__ import annotations

import json
import math
import os
import random

from PIL import Image, ImageDraw, ImageFont

# --------------------------------------------------------------------------
# contract constants (docs/CONTRACT.md - do not change without syncing C#)
# --------------------------------------------------------------------------
ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
ASSETS = os.path.join(ROOT, "assets")
PREVIEW = os.path.join(ASSETS, "preview")

CELL = 32
COLS = 8
ROWS = 7
SHEET_W, SHEET_H = CELL * COLS, CELL * ROWS          # 256 x 224
EMOTE_CELL = 16
EMOTE_ORDER = ["heart", "exclaim", "question", "zzz", "sparkle"]

#             name       frames fps  loop
ANIMS = [
    ("idle",      4, 4, True),
    ("walk",      4, 6, True),
    ("climb",     4, 6, True),
    ("sleep",     2, 1, True),
    ("petted",    4, 6, False),
    ("surprised", 2, 6, False),
    ("happy",     4, 6, False),
]

SEED = 20260705
RNG = random.Random(SEED)

GROUND_V = 30.0          # screen y of the body's lowest opaque edge (outline: +1)

# --------------------------------------------------------------------------
# palettes (<= 8 opaque colours per pet, incl. outline)
# --------------------------------------------------------------------------
def hx(s):
    return (int(s[0:2], 16), int(s[2:4], 16), int(s[4:6], 16), 255)

PETS = {
    "clawd": {
        "pal": {
            "outline": hx("4A2418"), "base": hx("D97757"), "light": hx("EDA184"),
            "dark": hx("B55B3D"), "eye": hx("2B1710"), "white": hx("FFF6EE"),
            "blush": hx("C74334"),
        },
        "r0": 8.4, "amp": 0.30, "lobes": 4, "phase": 0.0,
        "bsx": 1.0, "bsy": 1.0,
        "claws": False, "legs": False, "sparkles": False,
    },
    "crab": {
        "pal": {
            "outline": hx("4D190E"), "base": hx("D9543F"), "light": hx("EE8468"),
            "dark": hx("B0392A"), "eye": hx("290F08"), "white": hx("FFF3EC"),
            "blush": hx("F7B29C"),
        },
        "r0": 7.8, "amp": 0.30, "lobes": 4, "phase": 0.0,
        "bsx": 1.10, "bsy": 0.94,
        "claws": True, "legs": True, "sparkles": False,
    },
    "nova": {
        "pal": {
            "outline": hx("0C1626"), "base": hx("2C4E77"), "light": hx("4A79A6"),
            "dark": hx("1E3A5F"), "eye": hx("0A1220"), "white": hx("F2F7FF"),
            "star": hx("9FC6EC"),
        },
        "r0": 8.4, "amp": 0.30, "lobes": 5, "phase": -math.pi / 2,  # one point up
        "bsx": 1.0, "bsy": 1.0,
        "claws": False, "legs": False, "sparkles": True, "dark_body": True,
    },
}

# body-local face layout (u right, v down; face biased right = facing right)
EYE_L, EYE_R, EYE_V = 1.0, 5.0, -3.0
MOUTH_U, MOUTH_V = 3.0, -0.2
BLUSH_L, BLUSH_R, BLUSH_V = -2.8, 7.0, -0.6
# crab claw rig (local)
SHOULDER = 4.0, -3.0             # |u|, v of arm root
CLAW_POS = 8.5, -6.0             # |u|, v of claw centre (before per-frame offset)
CLAW_R = 3.0
LEG_POS = 3.6, 8.8               # |u|, v of leg nubs
# nova sparkle spots (local, interior, away from the face zone and rim)
SPARKLES = [(-3.5, -1.0), (3.0, 3.5), (-0.5, 4.5)]


# --------------------------------------------------------------------------
# pose table: every animation frame is a small dict of body deformations
# --------------------------------------------------------------------------
def P(sx=1.0, sy=1.0, dy=0.0, tilt=0.0, eyes="open", mouth=None, blush=False,
      boosts=(), amp_add=0.0, eye_dv=0.0, claw_l=(0, 0), claw_r=(0, 0),
      leg_dx=(0, 0), hot=None):
    return dict(sx=sx, sy=sy, dy=dy, tilt=tilt, eyes=eyes, mouth=mouth,
                blush=blush, boosts=list(boosts), amp_add=amp_add,
                eye_dv=eye_dv, claw_l=claw_l, claw_r=claw_r, leg_dx=leg_dx,
                hot=hot)


def poses(pet_name, anim):
    """Return the list of poses for one animation row of one pet."""
    crab = pet_name == "crab"
    nova = pet_name == "nova"

    if anim == "idle":
        f = [P(),
             P(sy=1.06, sx=0.97),
             P(),
             P(sy=0.96, sx=1.04, eyes="closed")]
        if crab:
            f[1]["claw_l"] = f[1]["claw_r"] = (0, -1)
        if nova:  # twinkling star speckles
            order = list(range(len(SPARKLES)))
            RNG.shuffle(order)
            for i in range(4):
                f[i]["hot"] = order[i % len(order)]
        return f

    if anim == "walk":
        if crab:  # sideways scuttle: no lean, claws + legs alternate
            return [
                P(sx=1.06, sy=0.95, claw_l=(0, -2), claw_r=(0, 0), leg_dx=(-1, 1)),
                P(sx=0.97, sy=1.04, dy=-1, claw_l=(0, -1), claw_r=(0, -1)),
                P(sx=1.06, sy=0.95, claw_l=(0, 0), claw_r=(0, -2), leg_dx=(1, -1)),
                P(sx=0.97, sy=1.04, dy=-1, claw_l=(0, -1), claw_r=(0, -1)),
            ]
        return [  # bouncy waddle
            P(sy=0.92, sx=1.08, tilt=-0.10),
            P(sy=1.07, sx=0.95, dy=-2, tilt=-0.03),
            P(sy=0.92, sx=1.08, tilt=0.10),
            P(sy=1.07, sx=0.95, dy=-2, tilt=0.03),
        ]

    if anim == "climb":
        if crab:  # grip with alternating claws
            return [
                P(sy=1.05, claw_r=(0, -3), claw_l=(0, 1)),
                P(sy=0.95, sx=1.04, dy=-2, claw_l=(0, -1), claw_r=(0, -1)),
                P(sy=1.05, claw_l=(0, -3), claw_r=(0, 1)),
                P(sy=0.95, sx=1.04, dy=-1, claw_l=(0, -1), claw_r=(0, -1)),
            ]
        return [  # alternating stretched "arm" lobes reaching up
            P(sy=1.07, tilt=-0.05, boosts=[(-60, 0.35, 5.5)]),
            P(sy=0.94, sx=1.05, dy=-2),
            P(sy=1.07, tilt=0.05, boosts=[(-120, 0.35, 5.5)]),
            P(sy=0.94, sx=1.05, dy=-1),
        ]

    if anim == "sleep":
        f = [P(sy=0.70, sx=1.22, eyes="closed"),
             P(sy=0.63, sx=1.26, eyes="closed")]
        if crab:
            f = [P(sy=0.70, sx=1.10, eyes="closed"),
                 P(sy=0.63, sx=1.13, eyes="closed")]
            for p in f:
                p["claw_l"], p["claw_r"] = (2, 4), (-2, 4)
        return f

    if anim == "petted":
        f = [P(sy=0.96, sx=1.05, eyes="happy", mouth="smile", blush=True),
             P(tilt=-0.12, eyes="happy", mouth="smile", blush=True),
             P(sy=0.93, sx=1.07, eyes="happy", mouth="smile", blush=True),
             P(tilt=0.12, eyes="happy", mouth="smile", blush=True)]
        if crab:  # happy claw wave
            offs = [((0, -3), (0, -3)), ((0, -4), (0, -2)),
                    ((0, -3), (0, -3)), ((0, -2), (0, -4))]
            for p, (l, r) in zip(f, offs):
                p["claw_l"], p["claw_r"] = l, r
        return f

    if anim == "surprised":
        f = [P(sy=1.10, sx=0.92, dy=-3, eyes="wide", mouth="o", amp_add=0.08),
             P(sy=0.86, sx=1.12, eyes="wide", mouth="o")]
        if crab:
            f[0]["claw_l"], f[0]["claw_r"] = (-1, -4), (1, -4)
            f[1]["claw_l"], f[1]["claw_r"] = (-1, 0), (1, 0)
        return f

    if anim == "happy":
        f = [P(sy=0.86, sx=1.10, eyes="happy", mouth="smile"),
             P(sy=1.08, sx=0.95, dy=-3, eyes="happy", mouth="smile"),
             P(dy=-5, eyes="happy", mouth="smile", blush=True),
             P(sy=0.90, sx=1.08, dy=-1, eyes="happy", mouth="smile")]
        if crab:  # banzai claws
            offs = [(0, -1), (0, -3), (0, -4), (0, -2)]
            for p, o in zip(f, offs):
                p["claw_l"] = p["claw_r"] = o
        return f

    raise ValueError(anim)


# --------------------------------------------------------------------------
# geometry helpers
# --------------------------------------------------------------------------
def ang_diff(a, b):
    d = (a - b) % (2 * math.pi)
    return d - 2 * math.pi if d > math.pi else d


def r_max(theta, spec, pose):
    """star radius profile in body-local space"""
    amp = spec["amp"] + pose["amp_add"]
    r = spec["r0"] * (1.0 + amp * math.cos(spec["lobes"] * (theta - spec["phase"])))
    for deg, width, extra in pose["boosts"]:
        r += extra * math.exp(-0.5 * (ang_diff(theta, math.radians(deg)) / width) ** 2)
    return r


def frame_transform(spec, pose):
    """Compute cx/cy (ground anchored) and return local->screen fn."""
    sx = pose["sx"] * spec["bsx"]
    sy = pose["sy"] * spec["bsy"]
    phi = pose["tilt"]
    cphi, sphi = math.cos(phi), math.sin(phi)
    # numerically find the body's lowest screen v (relative to centre)
    max_v = 0.0
    for k in range(720):
        th = k * math.pi / 360.0
        r = r_max(th, spec, pose)
        u, v = r * math.cos(th) * sx, r * math.sin(th) * sy
        max_v = max(max_v, u * sphi + v * cphi)
    cx = 16.0
    cy = GROUND_V - max_v + pose["dy"]

    def T(u, v):
        return (cx + (u * sx) * cphi - (v * sy) * sphi,
                cy + (u * sx) * sphi + (v * sy) * cphi)

    def inside(px, py):
        dx, dy_ = px + 0.5 - cx, py + 0.5 - cy
        ru = dx * cphi + dy_ * sphi
        rv = -dx * sphi + dy_ * cphi
        u, v = ru / sx, rv / sy
        r = math.hypot(u, v)
        return r <= r_max(math.atan2(v, u), spec, pose)

    return T, inside


def disc(cx, cy, r):
    s = set()
    for y in range(CELL):
        for x in range(CELL):
            if math.hypot(x + 0.5 - cx, y + 0.5 - cy) <= r:
                s.add((x, y))
    return s


def capsule(x0, y0, x1, y1, rad):
    s = set()
    steps = max(2, int(math.hypot(x1 - x0, y1 - y0) * 2))
    for i in range(steps + 1):
        t = i / steps
        s |= disc(x0 + (x1 - x0) * t, y0 + (y1 - y0) * t, rad)
    return s


def claw_mask(ccx, ccy):
    """pincer: disc with an upward wedge notch"""
    m = disc(ccx, ccy, CLAW_R)
    notch_dir = -math.pi / 2  # opening points up
    for (x, y) in list(m):
        dx, dy = x + 0.5 - ccx, y + 0.5 - ccy
        if math.hypot(dx, dy) > 1.0 and abs(ang_diff(math.atan2(dy, dx), notch_dir)) < 0.55:
            m.discard((x, y))
    return m


# --------------------------------------------------------------------------
# frame renderer
# --------------------------------------------------------------------------
def render_frame(pet_name, anim, pose):
    spec = PETS[pet_name]
    pal = spec["pal"]
    T, inside = frame_transform(spec, pose)

    body = {(x, y) for y in range(CELL) for x in range(CELL) if inside(x, y)}
    silhouette = set(body)

    # --- crab extras -------------------------------------------------------
    if spec["claws"]:
        for side, off in ((-1, pose["claw_l"]), (1, pose["claw_r"])):
            sx0, sy0 = T(side * SHOULDER[0], SHOULDER[1])
            cxy = T(side * CLAW_POS[0], CLAW_POS[1])
            ccx, ccy = cxy[0] + off[0], cxy[1] + off[1]
            # arm
            silhouette |= capsule(sx0, sy0, ccx, ccy, 1.1)
            silhouette |= claw_mask(ccx, ccy)
    if spec["legs"]:
        for side, ldx in ((-1, pose["leg_dx"][0]), (1, pose["leg_dx"][1])):
            lx, ly = T(side * LEG_POS[0], LEG_POS[1])
            lx, ly = int(round(lx + ldx)), int(round(ly))
            for yy in range(ly - 1, ly + 1):
                for xx in range(lx - 1, lx + 1):
                    if 0 <= xx < CELL and 0 <= yy < CELL:
                        silhouette.add((xx, yy))

    # --- colour layers ------------------------------------------------------
    img = {}
    for p in silhouette:
        img[p] = pal["base"]
    # rim shading: light from top-left, shade bottom-right
    _, cy0 = T(0, 0)
    for (x, y) in silhouette:
        dark_edge = (x + 1, y) not in silhouette or (x, y + 1) not in silhouette
        light_edge = (x - 1, y) not in silhouette or (x, y - 1) not in silhouette
        if dark_edge and light_edge:
            img[(x, y)] = pal["light"] if y + 0.5 < cy0 else pal["dark"]
        elif dark_edge:
            img[(x, y)] = pal["dark"]
        elif light_edge:
            img[(x, y)] = pal["light"]

    # --- nova sparkles -------------------------------------------------------
    if spec["sparkles"]:
        for i, (su, sv) in enumerate(SPARKLES):
            px, py = T(su, sv)
            px, py = int(round(px)), int(round(py))
            if (px, py) in body and img.get((px, py)) == pal["base"]:
                if pose["hot"] == i:  # twinkle: small plus in white
                    img[(px, py)] = pal["white"]
                    for qx, qy in ((px - 1, py), (px + 1, py), (px, py - 1), (px, py + 1)):
                        if (qx, qy) in body and img.get((qx, qy)) == pal["base"]:
                            img[(qx, qy)] = pal["star"]
                else:
                    img[(px, py)] = pal["star"]

    # --- face ----------------------------------------------------------------
    def put(x, y, col):
        if (x, y) in body:
            img[(x, y)] = col

    # on a dark body (nova) dark strokes vanish: use bright ink / white eyeballs
    dark_body = spec.get("dark_body", False)
    ink = pal["white"] if dark_body else pal["eye"]
    style = pose["eyes"]
    spread = 0.5 if style == "wide" else 0.0  # wide 3x3 eyes: push apart
    ex_l = T(EYE_L - spread, EYE_V + pose["eye_dv"])
    ex_r = T(EYE_R + spread, EYE_V + pose["eye_dv"])
    for (fx, fy) in (ex_l, ex_r):
        ex, ey = int(round(fx)), int(round(fy))
        if style == "open":
            if dark_body:  # white eyeball, dark pupil bottom-right
                for (qx, qy) in ((ex - 1, ey - 1), (ex, ey - 1), (ex - 1, ey), (ex, ey)):
                    put(qx, qy, pal["white"])
                put(ex, ey, pal["eye"])
            else:  # dark bead, white catchlight top-left
                for (qx, qy) in ((ex - 1, ey - 1), (ex, ey - 1), (ex - 1, ey), (ex, ey)):
                    put(qx, qy, pal["eye"])
                put(ex - 1, ey - 1, pal["white"])
        elif style == "closed":
            put(ex - 1, ey, ink)
            put(ex, ey, ink)
        elif style == "happy":
            put(ex - 1, ey - 1, ink)
            put(ex - 2, ey, ink)
            put(ex, ey, ink)
        elif style == "wide":
            if dark_body:  # big white eye, dark pupil
                for qy in range(ey - 2, ey + 1):
                    for qx in range(ex - 2, ex + 1):
                        put(qx, qy, pal["white"])
                put(ex - 1, ey - 1, pal["eye"])
            else:
                for qy in range(ey - 2, ey + 1):
                    for qx in range(ex - 2, ex + 1):
                        put(qx, qy, pal["eye"])
                put(ex - 1, ey - 1, pal["white"])

    mx, my = T(MOUTH_U, MOUTH_V)
    mx, my = int(round(mx)), int(round(my))
    if pose["mouth"] == "smile":
        put(mx - 2, my - 1, ink)
        put(mx - 1, my, ink)
        put(mx, my, ink)
        put(mx + 1, my - 1, ink)
    elif pose["mouth"] == "o":
        for (qx, qy) in ((mx - 1, my + 1), (mx, my + 1), (mx - 1, my + 2), (mx, my + 2)):
            put(qx, qy, ink)

    if pose["blush"]:
        bc = pal.get("blush", pal.get("star"))
        for bu in (BLUSH_L, BLUSH_R):
            bx, by = T(bu, BLUSH_V)
            bx, by = int(round(bx)), int(round(by))
            put(bx, by, bc)
            put(bx + 1, by, bc)

    # --- outline (outside the silhouette) ------------------------------------
    for (x, y) in list(silhouette):
        for (qx, qy) in ((x - 1, y), (x + 1, y), (x, y - 1), (x, y + 1)):
            if (qx, qy) not in silhouette and 0 <= qx < CELL and 0 <= qy < CELL:
                if (qx, qy) not in img:
                    img[(qx, qy)] = pal["outline"]

    # --- to image -------------------------------------------------------------
    frame = Image.new("RGBA", (CELL, CELL), (0, 0, 0, 0))
    px = frame.load()
    for (x, y), col in img.items():
        px[x, y] = col
    return frame


# --------------------------------------------------------------------------
# emotes (16x16 string art, auto-outlined)
# --------------------------------------------------------------------------
def rows_to_pixels(rows, colmap):
    assert len(rows) == EMOTE_CELL
    pix = {}
    for y, row in enumerate(rows):
        assert len(row) == EMOTE_CELL, f"row {y} len {len(row)}: {row!r}"
        for x, ch in enumerate(row):
            if ch != ".":
                pix[(x, y)] = colmap[ch]
    return pix


def emote_heart():
    fill, hi, out = hx("E8506A"), hx("FFB3C0"), hx("5A1428")
    pix = {}
    # implicit heart curve, sampled at pixel centres
    for y in range(EMOTE_CELL):
        for x in range(EMOTE_CELL):
            nx = (x + 0.5 - 8.0) / 5.6
            ny = -(y + 0.5 - 7.4) / 5.6
            if (nx * nx + ny * ny - 1) ** 3 - nx * nx * ny * ny * ny <= 0:
                pix[(x, y)] = fill
    for (x, y) in ((5, 4), (6, 4), (5, 5)):  # highlight on left bump
        if (x, y) in pix:
            pix[(x, y)] = hi
    return outline_pixels(pix, out)


def emote_exclaim():
    f, h = "y", "h"
    rows = [
        "................",
        "......yyyy......",
        "......hyyy......",
        "......hyyy......",
        "......yyyy......",
        "......yyyy......",
        "......yyyy......",
        ".......yy.......",
        ".......yy.......",
        "................",
        "................",
        "......yyyy......",
        "......yyyy......",
        "......yyyy......",
        "................",
        "................",
    ]
    pix = rows_to_pixels(rows, {"y": hx("FFB300"), "h": hx("FFE08A")})
    return outline_pixels(pix, hx("5A3200"))


def emote_question():
    rows = [
        "................",
        "....qqqqq.......",
        "...qqqqqqq......",
        "...qqh..qqq.....",
        "...qq....qq.....",
        "..........qq....",
        ".........qq.....",
        ".......qqq......",
        "......qq........",
        "......qq........",
        "................",
        "................",
        "......qq........",
        "......qq........",
        "................",
        "................",
    ]
    pix = rows_to_pixels(rows, {"q": hx("6FB7FF"), "h": hx("D8ECFF")})
    return outline_pixels(pix, hx("143A66"))


def emote_zzz():
    fill, out = hx("BFD9FF"), hx("22406E")
    pix = {}

    def z(x0, y0, w, h):
        for x in range(x0, x0 + w):
            pix[(x, y0)] = fill
            pix[(x, y0 + h - 1)] = fill
        for i in range(1, h - 1):
            x = x0 + (w - 1) - round(i * (w - 1) / (h - 1))
            pix[(x, y0 + i)] = fill

    z(10, 1, 5, 5)    # big, top-right
    z(4, 7, 4, 4)     # medium, centre
    z(1, 12, 3, 3)    # small, bottom-left
    return outline_pixels(pix, out)


def emote_sparkle():
    rows = [
        "................",
        ".......y........",
        "......yhy.......",
        "......yhy.......",
        ".....yyhyy......",
        "...yyyhhhyyy....",
        "..yhhhhhhhhhy...",
        "...yyyhhhyyy....",
        ".....yyhyy......",
        "......yhy.......",
        "......yhy.......",
        ".......y........",
        "................",
        "..............y.",
        "................",
        "................",
    ]
    pix = rows_to_pixels(rows, {"y": hx("FFD75E"), "h": hx("FFF6C8")})
    return outline_pixels(pix, hx("6E5312"))


def outline_pixels(pix, out_col):
    for (x, y) in list(pix.keys()):
        for (qx, qy) in ((x - 1, y), (x + 1, y), (x, y - 1), (x, y + 1)):
            if (qx, qy) not in pix and 0 <= qx < EMOTE_CELL and 0 <= qy < EMOTE_CELL:
                pix.setdefault((qx, qy), None)
    for k, v in list(pix.items()):
        if v is None:
            pix[k] = out_col
    return pix


def build_emotes():
    fns = {"heart": emote_heart, "exclaim": emote_exclaim,
           "question": emote_question, "zzz": emote_zzz, "sparkle": emote_sparkle}
    sheet = Image.new("RGBA", (EMOTE_CELL * len(EMOTE_ORDER), EMOTE_CELL), (0, 0, 0, 0))
    for i, name in enumerate(EMOTE_ORDER):
        cell = Image.new("RGBA", (EMOTE_CELL, EMOTE_CELL), (0, 0, 0, 0))
        px = cell.load()
        for (x, y), col in fns[name]().items():
            px[x, y] = col
        sheet.paste(cell, (i * EMOTE_CELL, 0))
    return sheet


# --------------------------------------------------------------------------
# sheet assembly + verification
# --------------------------------------------------------------------------
def build_sheet(pet_name):
    sheet = Image.new("RGBA", (SHEET_W, SHEET_H), (0, 0, 0, 0))
    for row, (anim, nframes, _fps, _loop) in enumerate(ANIMS):
        frames = poses(pet_name, anim)
        assert len(frames) == nframes, (pet_name, anim)
        for col, pose in enumerate(frames):
            frame = render_frame(pet_name, anim, pose)
            sheet.paste(frame, (col * CELL, row * CELL))
    return sheet


def verify_sheet(pet_name, sheet):
    assert sheet.size == (SHEET_W, SHEET_H), sheet.size
    px = sheet.load()
    colors = set()
    for row, (anim, nframes, _f, _l) in enumerate(ANIMS):
        for col in range(COLS):
            x0, y0 = col * CELL, row * CELL
            opaque = [(x, y) for y in range(CELL) for x in range(CELL)
                      if px[x0 + x, y0 + y][3] != 0]
            if col >= nframes:
                assert not opaque, f"{pet_name} {anim} col{col} must be empty"
                continue
            assert opaque, f"{pet_name} {anim} f{col} is empty"
            for (x, y) in opaque:
                a = px[x0 + x, y0 + y][3]
                assert a == 255, f"{pet_name} {anim} f{col}: soft alpha {a}"
                assert 1 <= x <= 30 and 0 <= y <= 30, \
                    f"{pet_name} {anim} f{col}: pixel at cell edge ({x},{y})"
                colors.add(px[x0 + x, y0 + y])
    assert len(colors) <= 8, f"{pet_name}: {len(colors)} colours (max 8)"
    return len(colors)


# --------------------------------------------------------------------------
# previews (not part of the contract; for human inspection only)
# --------------------------------------------------------------------------
def load_font(size):
    try:
        return ImageFont.load_default(size=size)
    except TypeError:
        return ImageFont.load_default()


def checker(w, h, c1=(104, 104, 104), c2=(92, 92, 92), cell=8):
    img = Image.new("RGB", (w, h), c1)
    d = ImageDraw.Draw(img)
    for y in range(0, h, cell):
        for x in range(0, w, cell):
            if (x // cell + y // cell) % 2:
                d.rectangle([x, y, x + cell - 1, y + cell - 1], fill=c2)
    return img


def preview_sheet(pet_name, sheet, scale=4):
    ml, mt = 176, 30
    big = sheet.resize((SHEET_W * scale, SHEET_H * scale), Image.NEAREST)
    img = Image.new("RGB", (ml + big.width + 12, mt + big.height + 12), (34, 34, 34))
    bg = checker(big.width, big.height)
    img.paste(bg, (ml, mt))
    img.paste(big, (ml, mt), big)
    d = ImageDraw.Draw(img)
    font = load_font(15)
    for c in range(COLS + 1):  # grid
        x = ml + c * CELL * scale
        d.line([x, mt, x, mt + big.height], fill=(60, 60, 60))
    for r in range(ROWS + 1):
        y = mt + r * CELL * scale
        d.line([ml, y, ml + big.width, y], fill=(60, 60, 60))
    for c in range(COLS):
        d.text((ml + c * CELL * scale + 4, 8), f"f{c}", fill=(200, 200, 200), font=font)
    for r, (anim, n, fps, loop) in enumerate(ANIMS):
        label = f"{r} {anim} {n}f@{fps} {'loop' if loop else 'once'}"
        d.text((8, mt + r * CELL * scale + 4), label, fill=(230, 230, 230), font=font)
    d.text((8, img.height - 22), f"{pet_name}.png 256x224  4x nearest", fill=(150, 150, 150), font=font)
    return img


def preview_compare(sheets, scale=4):
    names = list(sheets.keys())
    pw = CELL * scale
    pad = 24
    band_h = pw + 46
    w = pad + len(names) * (pw + pad)
    img = Image.new("RGB", (w, band_h * 2), (0, 0, 0))
    d = ImageDraw.Draw(img)
    font = load_font(15)
    for bi, (bg_col, tx_col) in enumerate([((214, 214, 214), (20, 20, 20)),
                                           ((43, 43, 43), (225, 225, 225))]):
        y0 = bi * band_h
        d.rectangle([0, y0, w, y0 + band_h - 1], fill=bg_col)
        for i, name in enumerate(names):
            x0 = pad + i * (pw + pad)
            cell = sheets[name].crop((0, 0, CELL, CELL))  # idle f0
            big = cell.resize((pw, pw), Image.NEAREST)
            img.paste(big, (x0, y0 + 8), big)
            d.text((x0 + 2, y0 + pw + 14), name, fill=tx_col, font=font)
    return img


def preview_emotes(emotes, scale=8):
    pw = EMOTE_CELL * scale
    pad = 16
    band_h = pw + 44
    w = pad + len(EMOTE_ORDER) * (pw + pad)
    img = Image.new("RGB", (w, band_h * 2), (0, 0, 0))
    d = ImageDraw.Draw(img)
    font = load_font(15)
    for bi, (bg_col, tx_col) in enumerate([((214, 214, 214), (20, 20, 20)),
                                           ((43, 43, 43), (225, 225, 225))]):
        y0 = bi * band_h
        d.rectangle([0, y0, w, y0 + band_h - 1], fill=bg_col)
        for i, name in enumerate(EMOTE_ORDER):
            x0 = pad + i * (pw + pad)
            cell = emotes.crop((i * EMOTE_CELL, 0, (i + 1) * EMOTE_CELL, EMOTE_CELL))
            big = cell.resize((pw, pw), Image.NEAREST)
            img.paste(big, (x0, y0 + 6), big)
            d.text((x0 + 2, y0 + pw + 12), name, fill=tx_col, font=font)
    return img


# --------------------------------------------------------------------------
# atlas.json
# --------------------------------------------------------------------------
def build_atlas():
    return {
        "version": 1,
        "generator": {"tool": "tools/spritegen/generate.py", "seed": SEED},
        "cell": {"width": CELL, "height": CELL},
        "sheet": {"width": SHEET_W, "height": SHEET_H, "columns": COLS, "rows": ROWS},
        "facing": "right",
        "layout": "row = animation, column = frame (left aligned; unused cells transparent)",
        "pets": [{"name": n, "file": f"{n}.png"} for n in ("clawd", "crab", "nova")],
        "animations": [
            {"row": r, "name": name, "frames": n, "fps": fps, "loop": loop}
            for r, (name, n, fps, loop) in enumerate(ANIMS)
        ],
        "emotes": {
            "file": "emotes.png",
            "cell": {"width": EMOTE_CELL, "height": EMOTE_CELL},
            "order": EMOTE_ORDER,
        },
    }


# --------------------------------------------------------------------------
def main():
    os.makedirs(ASSETS, exist_ok=True)
    os.makedirs(PREVIEW, exist_ok=True)

    sheets = {}
    for pet in ("clawd", "crab", "nova"):
        sheet = build_sheet(pet)
        ncol = verify_sheet(pet, sheet)
        sheet.save(os.path.join(ASSETS, f"{pet}.png"))
        sheets[pet] = sheet
        print(f"{pet}.png  {sheet.size[0]}x{sheet.size[1]}  {ncol} colours  OK")

    emotes = build_emotes()
    assert emotes.size == (EMOTE_CELL * 5, EMOTE_CELL)
    emotes.save(os.path.join(ASSETS, "emotes.png"))
    print(f"emotes.png  {emotes.size[0]}x{emotes.size[1]}  OK")

    with open(os.path.join(ASSETS, "atlas.json"), "w", encoding="utf-8") as f:
        json.dump(build_atlas(), f, indent=2)
        f.write("\n")
    print("atlas.json  OK")

    for pet, sheet in sheets.items():
        preview_sheet(pet, sheet).save(os.path.join(PREVIEW, f"{pet}_preview.png"))
    preview_compare(sheets).save(os.path.join(PREVIEW, "idle_compare.png"))
    preview_emotes(emotes).save(os.path.join(PREVIEW, "emotes_preview.png"))
    print("previews  OK")


if __name__ == "__main__":
    main()
