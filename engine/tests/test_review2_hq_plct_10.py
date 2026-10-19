"""Review round 2 checks of the image editor windows (hq-plct.10).

Written by the round 2 reviewer. Fixtures are generated at run time.
"""

from __future__ import annotations

from pathlib import Path

import pytest

from peel_engine.families import image
from peel_engine.registry import load_registry

pytest.importorskip("PIL", reason="the image family needs pillow")

TAG_GPS_IFD = 0x8825
TAG_XMP = 700

XMP_GPS = (
    b'<x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF '
    b'xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">'
    b'<rdf:Description xmlns:exif="http://ns.adobe.com/exif/1.0/" '
    b'exif:GPSLatitude="32,27.4N" exif:GPSLongitude="116,36.7W"/>'
    b"</rdf:RDF></x:xmpmeta>"
)


@pytest.fixture(scope="module")
def real():
    return load_registry(force=True, strict=True)


def _gps_photo(tmp_path: Path, fmt: str, ext: str) -> Path:
    Image = image.pillow()
    img = Image.new("RGB", (40, 30), (120, 140, 160))
    exif = Image.Exif()
    gps = exif.get_ifd(TAG_GPS_IFD)
    gps[1] = "N"
    gps[2] = (32.0, 27.0, 24.12)
    gps[3] = "W"
    gps[4] = (116.0, 36.0, 44.28)
    path = tmp_path / f"photo.{ext}"
    if fmt == "TIFF":
        img.save(path, fmt, exif=exif, tiffinfo={TAG_XMP: XMP_GPS})
    else:
        img.save(path, fmt, exif=exif, xmp=XMP_GPS)
    assert b"GPSLatitude" in path.read_bytes(), "fixture must carry an XMP location"
    return path


@pytest.mark.parametrize("fmt,ext", [("JPEG", "jpg"), ("TIFF", "tif")])
def test_clear_location_leaves_no_location_anywhere_in_the_file(real, tmp_path, fmt, ext):
    """The user emptied the location boxes; no copy of the place may survive.

    A TIFF keeps its XMP packet in IFD0 tag 700, which img.getexif() returns
    and set writes back, so the XMP exif:GPSLatitude survives clear_location.
    """
    source = _gps_photo(tmp_path, fmt, ext)
    result = real.run(
        "image.metadata", [source],
        {"action": "set", "title": "t", "clear_location": True},
    )
    assert result.ok, result.message
    data = Path(result.outputs[0]).read_bytes()
    Image = image.pillow()
    with Image.open(result.outputs[0]) as out:
        assert not dict(out.getexif().get_ifd(TAG_GPS_IFD))
    assert b"GPSLatitude" not in data and b"GPSLongitude" not in data, (
        "the XMP copy of the location is still in the output"
    )


def _cell_spread(img, box, cell: int) -> list[float]:
    left, top, right, bottom = box
    spreads = []
    for y in range(top, bottom - cell + 1, cell):
        for x in range(left, right - cell + 1, cell):
            values = [
                sum(img.getpixel((x + dx, y + dy))[:3]) / 3
                for dy in range(cell)
                for dx in range(cell)
            ]
            mean = sum(values) / len(values)
            spreads.append((sum((v - mean) ** 2 for v in values) / len(values)) ** 0.5)
    return spreads


def test_a_tall_one_column_region_loses_its_text_under_blur(real, tmp_path):
    """The 600x20 check turned on its side: a 20x600 column of text."""
    from PIL import ImageDraw, ImageFont

    Image = image.pillow()
    wide = Image.new("RGB", (640, 60), (255, 255, 255))
    draw = ImageDraw.Draw(wide)
    try:
        font = ImageFont.load_default(size=16)
    except TypeError:
        font = ImageFont.load_default()
    draw.text((22, 22), "ACCOUNT 4111 1111 1111 1111  DANIEL HACK  EXP 09/29  CVV 123",
              fill=(0, 0, 0), font=font)
    img = wide.transpose(Image.Transpose.ROTATE_90)  # 60 x 640
    source = tmp_path / "column.png"
    img.save(source)
    box = (20, 20, 40, 620)
    inked = [s for s in _cell_spread(img, box, 10) if s > 40]
    assert len(inked) > 20

    result = real.run(
        "image.redact", [source],
        {"regions": [{"x": 20, "y": 20, "w": 20, "h": 600}], "mode": "blur"},
    )
    assert result.ok, result.message
    with Image.open(result.outputs[0]) as out:
        out.load()
        after = _cell_spread(out, box, 10)
    assert max(after) < 12, f"a 10 pixel square still varies by {max(after):.1f} levels"
