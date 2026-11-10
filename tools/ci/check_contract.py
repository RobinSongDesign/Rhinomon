"""CI guard for docs/CONTRACT.md.

The sprite-sheet layout is written down in four places that must agree:
docs/CONTRACT.md (the table), assets/atlas.json (generator output), the PNG
sheets themselves, and the constants hard-coded in src/Rhinomon/SpriteAtlas.cs.
This script fails the build when any of them drift apart, when the package
version in packaging/manifest.yml differs from the assembly version, or (with
Pillow installed) when a committed sheet is not what tools/spritegen/generate.py
produces - assets are generated, never hand-edited.

Standard library only, except the optional regeneration check.

Usage:  python tools/ci/check_contract.py
"""

import json
import os
import re
import struct
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
PETS = ["clawd", "crab", "nova"]

errors = []


def check(ok, message):
    if not ok:
        errors.append(message)


def read(*parts):
    with open(os.path.join(ROOT, *parts), encoding="utf-8") as f:
        return f.read()


def png_size(*parts):
    with open(os.path.join(ROOT, *parts), "rb") as f:
        head = f.read(24)
    if head[:8] != b"\x89PNG\r\n\x1a\n" or head[12:16] != b"IHDR":
        raise ValueError("not a PNG: " + os.path.join(*parts))
    return struct.unpack(">II", head[16:24])


def cs_int(source, name):
    m = re.search(r"\bconst\s+int\s+%s\s*=\s*(\d+)\s*;" % name, source)
    if not m:
        raise ValueError("SpriteAtlas.cs: constant %s not found" % name)
    return int(m.group(1))


def cs_int_array(source, name):
    m = re.search(r"\bint\[\]\s+%s\s*=\s*\{([^}]*)\}" % name, source)
    if not m:
        raise ValueError("SpriteAtlas.cs: array %s not found" % name)
    return [int(v) for v in m.group(1).split(",") if v.strip()]


def cs_string_array(source, name):
    m = re.search(r"\bstring\[\]\s+%s\s*=\s*\{([^}]*)\}" % name, source)
    if not m:
        raise ValueError("SpriteAtlas.cs: array %s not found" % name)
    return re.findall(r'"([^"]*)"', m.group(1))


def cs_enum(source, name):
    """Member names of an enum, ordered by their explicit values."""
    m = re.search(r"\benum\s+%s\s*\{([^}]*)\}" % name, source)
    if not m:
        raise ValueError("SpriteAtlas.cs: enum %s not found" % name)
    members = re.findall(r"(\w+)\s*=\s*(-?\d+)", m.group(1))
    return [n for n, v in sorted(members, key=lambda nv: int(nv[1])) if int(v) >= 0]


def contract_rows(markdown):
    """(row, name, frames, fps) from the animation table in CONTRACT.md."""
    rows = []
    for line in markdown.splitlines():
        m = re.match(r"\|\s*(\d+)\s*\|\s*(\w+)\s*\|\s*(\d+)\s*\|\s*(\d+)\s*\|", line)
        if m:
            rows.append((int(m.group(1)), m.group(2), int(m.group(3)), int(m.group(4))))
    return rows


def main():
    atlas = json.loads(read("assets", "atlas.json"))
    cs = read("src", "Rhinomon", "SpriteAtlas.cs")
    contract = read("docs", "CONTRACT.md")

    cell = atlas["cell"]["width"]
    sheet = atlas["sheet"]
    anims = atlas["animations"]
    emotes = atlas["emotes"]

    # ---- atlas.json is internally consistent ------------------------------
    check(atlas["cell"]["width"] == atlas["cell"]["height"], "atlas.json: cell is not square")
    check(sheet["width"] == sheet["columns"] * cell and sheet["height"] == sheet["rows"] * cell,
          "atlas.json: sheet size does not equal columns x rows x cell")
    check([a["row"] for a in anims] == list(range(len(anims))), "atlas.json: animation rows are not 0..n-1")
    check(len(anims) == sheet["rows"], "atlas.json: animation count differs from sheet rows")
    check(all(1 <= a["frames"] <= sheet["columns"] for a in anims),
          "atlas.json: an animation has more frames than the sheet has columns")

    # ---- the PNGs match atlas.json ----------------------------------------
    check([p["name"] for p in atlas["pets"]] == PETS, "atlas.json: pet list is not %s" % PETS)
    for pet in atlas["pets"]:
        size = png_size("assets", pet["file"])
        check(size == (sheet["width"], sheet["height"]),
              "assets/%s is %dx%d, atlas.json says %dx%d"
              % (pet["file"], size[0], size[1], sheet["width"], sheet["height"]))
    ecell = emotes["cell"]["width"]
    size = png_size("assets", emotes["file"])
    check(size == (ecell * len(emotes["order"]), ecell),
          "assets/%s is %dx%d, expected %dx%d"
          % (emotes["file"], size[0], size[1], ecell * len(emotes["order"]), ecell))

    # ---- SpriteAtlas.cs hard-codes the same numbers -----------------------
    check(cs_int(cs, "TileSize") == cell, "SpriteAtlas.TileSize != atlas.json cell")
    check(cs_int(cs, "SheetColumns") == sheet["columns"], "SpriteAtlas.SheetColumns != atlas.json columns")
    check(cs_int(cs, "AnimationCount") == sheet["rows"], "SpriteAtlas.AnimationCount != atlas.json rows")
    check(cs_int(cs, "EmoteTileSize") == ecell, "SpriteAtlas.EmoteTileSize != atlas.json emote cell")
    check(cs_int(cs, "EmoteCount") == len(emotes["order"]), "SpriteAtlas.EmoteCount != atlas.json emote count")
    check(cs_int_array(cs, "FrameCounts") == [a["frames"] for a in anims],
          "SpriteAtlas.FrameCounts != atlas.json frames")
    check(cs_int_array(cs, "FrameRates") == [a["fps"] for a in anims],
          "SpriteAtlas.FrameRates != atlas.json fps")
    check(cs_string_array(cs, "SheetNames") == [p["file"] for p in atlas["pets"]],
          "SpriteAtlas.SheetNames != atlas.json pet files")
    check([n.lower() for n in cs_enum(cs, "PetAnim")] == [a["name"] for a in anims],
          "enum PetAnim order != atlas.json animation order")
    check([n.lower() for n in cs_enum(cs, "EmoteKind")] == emotes["order"],
          "enum EmoteKind order != atlas.json emote order")

    # ---- the table in CONTRACT.md says the same ---------------------------
    check(contract_rows(contract) == [(a["row"], a["name"], a["frames"], a["fps"]) for a in anims],
          "docs/CONTRACT.md animation table != atlas.json")

    # ---- package version == assembly version ------------------------------
    csproj = re.search(r"<Version>([^<]+)</Version>", read("src", "Rhinomon", "Rhinomon.csproj"))
    manifest = re.search(r"(?m)^version:\s*(\S+)\s*$", read("packaging", "manifest.yml"))
    check(csproj is not None and manifest is not None, "version field missing in csproj or manifest.yml")
    if csproj and manifest:
        check(csproj.group(1).strip() == manifest.group(1).strip(),
              "version mismatch: Rhinomon.csproj %s vs packaging/manifest.yml %s"
              % (csproj.group(1).strip(), manifest.group(1).strip()))

    # ---- committed sheets are exactly what the generator produces ---------
    regenerated = check_regeneration()

    if errors:
        print("CONTRACT CHECK FAILED")
        for e in errors:
            print("  - " + e)
        return 1
    print("contract check OK (%d animations, %d pets, %d emotes%s)"
          % (len(anims), len(atlas["pets"]), len(emotes["order"]),
             "" if regenerated else "; regeneration check skipped - Pillow not installed"))
    return 0


def check_regeneration():
    try:
        from PIL import Image
    except ImportError:
        return False

    sys.path.insert(0, os.path.join(ROOT, "tools", "spritegen"))
    import generate  # seeded module-level RNG: build in the same order as generate.main()

    def same_pixels(built, name):
        with Image.open(os.path.join(ROOT, "assets", name)) as committed:
            a, b = built.convert("RGBA"), committed.convert("RGBA")
            check(a.size == b.size and a.tobytes() == b.tobytes(),
                  "assets/%s differs from tools/spritegen/generate.py output "
                  "(assets are generated - rerun the generator instead of editing them)" % name)

    for pet in PETS:
        sheet = generate.build_sheet(pet)
        generate.verify_sheet(pet, sheet)
        same_pixels(sheet, pet + ".png")
    same_pixels(generate.build_emotes(), "emotes.png")
    check(generate.build_atlas() == json.loads(read("assets", "atlas.json")),
          "assets/atlas.json differs from generate.build_atlas()")
    return True


if __name__ == "__main__":
    sys.exit(main())
