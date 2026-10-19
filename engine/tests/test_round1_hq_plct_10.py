"""Engine fixes from review round 1 of the image editor windows (hq-plct.10).

1. image.metadata action=set gains clear_location: the editor's way to say
   "the user emptied the location boxes". set keeps every tag it is not
   given, so without it an emptied location was silently kept.
2. image.redact's blur strength follows the region's larger side, so a wide
   one-line region (a name, an account number) is as unreadable as a square.

Fixtures are generated at run time (CLAUDE.md: no committed binaries).
"""

from __future__ import annotations

from pathlib import Path

import pytest

from peel_engine.families import image
from peel_engine.registry import load_registry

pytest.importorskip("PIL", reason="the image family needs pillow")

TAG_DESCRIPTION = 0x010E
TAG_ARTIST = 0x013B
TAG_GPS_IFD = 0x8825


@pytest.fixture(scope="module")
def real():
    return load_registry(force=True, strict=True)


def _photo(tmp_path: Path, name: str = "photo.jpg") -> Path:
    Image = image.pillow()
    img = Image.new("RGB", (40, 30), (120, 140, 160))
    exif = Image.Exif()
    exif[TAG_DESCRIPTION] = "a title"
    exif[TAG_ARTIST] = "an author"
    gps = exif.get_ifd(TAG_GPS_IFD)
    gps[1] = "N"
    gps[2] = (32.0, 27.0, 24.12)
    gps[3] = "W"
    gps[4] = (116.0, 36.0, 44.28)
    path = tmp_path / name
    img.save(path, exif=exif.tobytes())
    return path


def _exif(path: Path) -> tuple[dict, dict]:
    Image = image.pillow()
    with Image.open(path) as img:
        exif = img.getexif()
        return dict(exif), dict(exif.get_ifd(TAG_GPS_IFD))


# --- metadata clear_location -------------------------------------------------


def test_the_schema_declares_clear_location_as_a_bool(real):
    entry = real.get("image.metadata")
    spec = {param.name: param for param in entry.params}["clear_location"]
    assert spec.type == "bool"
    assert spec.default is False


def test_clear_location_removes_the_gps_and_keeps_every_other_tag(real, tmp_path):
    source = _photo(tmp_path)
    before, gps_before = _exif(source)
    assert gps_before, "the fixture must carry a location"

    result = real.run("image.metadata", [source], {"action": "set", "clear_location": "true"})

    assert result.ok, result.message
    after, gps_after = _exif(result.outputs[0])
    assert TAG_GPS_IFD not in after
    assert gps_after == {}
    assert after.get(TAG_DESCRIPTION) == "a title"
    assert after.get(TAG_ARTIST) == "an author"
    view = real.run("image.metadata", [result.outputs[0]], {"action": "view"})
    assert view.ok, view.message
    assert view.data["metadata"]["latitude"] is None
    assert view.data["metadata"]["longitude"] is None


def test_clear_location_with_a_new_title_does_both(real, tmp_path):
    source = _photo(tmp_path)
    result = real.run(
        "image.metadata", [source],
        {"action": "set", "title": "new", "clear_location": True},
    )
    assert result.ok, result.message
    after, gps_after = _exif(result.outputs[0])
    assert after.get(TAG_DESCRIPTION) == "new"
    assert after.get(TAG_ARTIST) == "an author"
    assert gps_after == {}


def test_without_clear_location_set_still_keeps_the_location(real, tmp_path):
    """The old contract for every other caller is unchanged."""
    source = _photo(tmp_path)
    result = real.run("image.metadata", [source], {"action": "set", "title": "b"})
    assert result.ok, result.message
    _, gps_after = _exif(result.outputs[0])
    assert gps_after.get(1) == "N"


def test_clear_location_and_a_new_location_together_are_refused(real, tmp_path):
    source = _photo(tmp_path)
    result = real.run(
        "image.metadata", [source],
        {"action": "set", "clear_location": True, "latitude": 1.0, "longitude": 2.0},
    )
    assert not result.ok
    assert result.code == "invalid_params", result.message


def test_clear_location_on_a_file_without_one_is_harmless(real, tmp_path):
    Image = image.pillow()
    source = tmp_path / "plain.jpg"
    Image.new("RGB", (10, 10), (1, 2, 3)).save(source)
    result = real.run("image.metadata", [source], {"action": "set", "clear_location": True})
    assert result.ok, result.message
    _, gps_after = _exif(result.outputs[0])
    assert gps_after == {}


# --- redact strength on a wide region ------------------------------------------


def _cell_spread(img, box, cell: int) -> list[float]:
    """Standard deviation of the luma inside each cell x cell square of box."""
    grey = img.convert("L")
    left, top, right, bottom = box
    spreads = []
    for y in range(top, bottom - cell + 1, cell):
        for x in range(left, right - cell + 1, cell):
            values = list(grey.crop((x, y, x + cell, y + cell)).tobytes())
            mean = sum(values) / len(values)
            spreads.append((sum((v - mean) ** 2 for v in values) / len(values)) ** 0.5)
    return spreads


def test_a_wide_one_line_region_loses_its_text_under_blur(real, tmp_path):
    """A 600 by 20 line of black text on white, redacted with the blur.

    The check is OCR free: text is high variance inside every small square
    (ink next to paper), and a redaction that hides it leaves each 10 pixel
    square nearly flat. Under the old strength (step = shorter side / 6, so
    3 pixel cells and a 3 pixel blur on a 20 pixel line) the glyph strokes
    survived as clear dark and light patches.
    """
    from PIL import ImageDraw, ImageFont

    Image = image.pillow()
    img = Image.new("RGB", (640, 60), (255, 255, 255))
    draw = ImageDraw.Draw(img)
    try:
        font = ImageFont.load_default(size=16)
    except TypeError:  # pillow older than 10.1 has one fixed size
        font = ImageFont.load_default()
    line = "ACCOUNT 4111 1111 1111 1111  DANIEL HACK  EXP 09/29  CVV 123"
    draw.text((22, 22), line, fill=(0, 0, 0), font=font)
    source = tmp_path / "card.png"
    img.save(source)
    box = (20, 20, 620, 40)

    before = _cell_spread(img, box, 10)
    inked = [spread for spread in before if spread > 40]
    assert len(inked) > 20, "the fixture must actually have text in the region"

    result = real.run(
        "image.redact", [source],
        {"regions": [{"x": 20, "y": 20, "w": 600, "h": 20}], "mode": "blur"},
    )
    assert result.ok, result.message
    with Image.open(result.outputs[0]) as out:
        out.load()
        after = _cell_spread(out, box, 10)

    assert max(after) < 12, f"a 10 pixel square still varies by {max(after):.1f} levels"


def test_a_small_region_is_flattened_not_left_sharp(real, tmp_path):
    """Below the 12 pixel minimum block the region becomes one averaged block."""
    Image = image.pillow()
    img = Image.new("RGB", (40, 40), (255, 255, 255))
    pixels = img.load()
    for y in range(10, 20):
        for x in range(10, 20):
            pixels[x, y] = (0, 0, 0) if (x + y) % 2 else (255, 255, 255)
    source = tmp_path / "checker.png"
    img.save(source)

    result = real.run(
        "image.redact", [source],
        {"regions": [{"x": 10, "y": 10, "w": 10, "h": 10}], "mode": "blur"},
    )
    assert result.ok, result.message
    with Image.open(result.outputs[0]) as out:
        out.load()
        spread = _cell_spread(out, (10, 10, 20, 20), 10)
    assert max(spread) < 12
