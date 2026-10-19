"""Fixtures and assertions for the image editor end to end run.

The driver is PowerShell, but what it has to assert is about pixels and EXIF,
and pillow is already installed in the engine's venv on George. Reading the
output with the same library the engine wrote it with is also the point: a
check that agrees with the engine about what a pixel is cannot be fooled by a
colour space the driver guessed at.

Run it with the engine venv's python, by path, one subcommand at a time:

    python e2e-editors-check.py make C:\\peel-dev\\e2e-editors\\testdir
    python e2e-editors-check.py info  <file>
    python e2e-editors-check.py pixel <file> <x> <y>
    python e2e-editors-check.py tags  <file>

Every subcommand prints one JSON object on stdout, so the driver can read it
with ConvertFrom-Json.
"""

import json
import sys
from pathlib import Path

from PIL import Image

#: The quadrant colours of the test picture. A crop, a redaction and a box
#: drawn on it are all checked by asking which quadrant a pixel came from.
RED = (220, 40, 40)
GREEN = (40, 200, 90)
BLUE = (40, 90, 220)
GREY = (200, 200, 200)

#: The flat colour of the Edit source. Doubling it is unambiguous in every
#: channel (no clipping), so a brightness of 2.0 is provable from one pixel.
FLAT = (20, 60, 100)


def quadrants(width: int, height: int):
    """A picture whose four quarters are plainly different colours."""
    image = Image.new("RGB", (width, height), RED)
    half_w, half_h = width // 2, height // 2
    image.paste(Image.new("RGB", (width - half_w, half_h), GREEN), (half_w, 0))
    image.paste(Image.new("RGB", (half_w, height - half_h), BLUE), (0, half_h))
    image.paste(Image.new("RGB", (width - half_w, height - half_h), GREY), (half_w, half_h))
    return image


def make(folder: Path) -> dict:
    """One source file per editor, so no two outputs can collide."""
    folder.mkdir(parents=True, exist_ok=True)
    written = {}

    for name in ("crop.png", "draw.png", "hide.png"):
        quadrants(400, 300).save(folder / name)
        written[name] = "400x300 quadrants"

    Image.new("RGB", (400, 300), FLAT).save(folder / "light.png")
    written["light.png"] = "400x300 flat %d,%d,%d" % FLAT

    tagged = quadrants(400, 300)
    exif = tagged.getexif()
    exif[270] = "the old title"
    exif[315] = "the old author"
    tagged.save(folder / "tags.jpg", quality=95, exif=exif.tobytes())
    written["tags.jpg"] = "400x300 quadrants, titled"
    return {"folder": str(folder), "files": written, "flat": list(FLAT),
            "red": list(RED), "green": list(GREEN), "blue": list(BLUE), "grey": list(GREY)}


def info(path: Path) -> dict:
    with Image.open(path) as image:
        return {"file": path.name, "width": image.width, "height": image.height,
                "mode": image.mode, "format": image.format}


def pixel(path: Path, x: int, y: int) -> dict:
    with Image.open(path) as image:
        rgb = image.convert("RGB").getpixel((x, y))
    return {"file": path.name, "x": x, "y": y, "rgb": list(rgb)}


def tags(path: Path) -> dict:
    with Image.open(path) as image:
        exif = image.getexif()
        gps = exif.get_ifd(34853)
        return {
            "file": path.name,
            "title": exif.get(270),
            "author": exif.get(315),
            "latitude": _decimal(gps.get(2), gps.get(1)),
            "longitude": _decimal(gps.get(4), gps.get(3)),
            "count": len(exif),
        }


def _decimal(dms, ref):
    if not dms or len(dms) != 3:
        return None
    value = float(dms[0]) + float(dms[1]) / 60 + float(dms[2]) / 3600
    return -value if str(ref).upper() in ("S", "W") else value


def main(argv: list[str]) -> int:
    if not argv:
        print(json.dumps({"error": "say what to do"}))
        return 2
    what, rest = argv[0], argv[1:]
    try:
        if what == "make":
            answer = make(Path(rest[0]))
        elif what == "info":
            answer = info(Path(rest[0]))
        elif what == "pixel":
            answer = pixel(Path(rest[0]), int(rest[1]), int(rest[2]))
        elif what == "tags":
            answer = tags(Path(rest[0]))
        else:
            answer = {"error": f"unknown subcommand {what}"}
    except Exception as exc:
        print(json.dumps({"error": f"{type(exc).__name__}: {exc}"}))
        return 1
    print(json.dumps(answer))
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
