"""Adversarial review of hq-plct.2 (image family), round 1.

Each test was a runnable failing check for one blocking finding; the round 1
fixes made them pass, and the file now runs with the normal suite as a
regression guard:

    cd engine && python3 -m pytest -q tests/test_review_hq_plct_2.py
"""

from __future__ import annotations

from pathlib import Path

import pytest

from peel_engine.families import image
from peel_engine.registry import load_registry

pytest.importorskip("PIL")


@pytest.fixture(scope="module")
def real():
    return load_registry(force=True, strict=True)


def test_redact_drops_the_jpeg_comment_segment(real, tmp_path: Path):
    """Redact promises an output with no metadata. A JPEG COM segment survives.

    redact() does ``_rgb_or_rgba(img).copy()``, which keeps ``img.info``, and
    pillow's JPEG writer falls back to ``im.info["comment"]`` when no comment
    is passed, so the original COM text is written into the redacted file.
    """
    Image = image.pillow()
    source = tmp_path / "id.jpg"
    Image.new("RGB", (40, 40), (200, 100, 50)).save(
        source, comment=b"Passport of Jane Roe, 1 Main St"
    )

    result = real.run(
        "image.redact", [source], {"regions": [{"x": 0, "y": 0, "w": 10, "h": 10}], "mode": "box"}
    )

    assert result.ok, result.message
    data = result.outputs[0].read_bytes()
    assert b"Jane Roe" not in data, "the JPEG COM comment survived Redact"


def test_metadata_strip_keeps_a_phone_photo_upright(real, tmp_path: Path):
    """Strip removes the Orientation tag but keeps the pixels as stored.

    A phone photo stored landscape with Orientation 6 displays portrait. After
    strip it has no tag, so every viewer shows it rotated 90 degrees.
    """
    from PIL import ImageOps

    Image = image.pillow()
    source = tmp_path / "phone.jpg"
    exif = Image.Exif()
    exif[0x0112] = 6
    Image.new("RGB", (40, 20), (255, 0, 0)).save(source, exif=exif)
    with Image.open(source) as src:
        shown_before = ImageOps.exif_transpose(src).size

    result = real.run("image.metadata", [source], {"action": "strip"})

    assert result.ok, result.message
    with Image.open(result.outputs[0]) as out:
        shown_after = ImageOps.exif_transpose(out).size
    assert shown_after == shown_before, (
        f"displayed {shown_before} before strip, {shown_after} after"
    )


@pytest.mark.parametrize("action_id", ["image.to_jpg", "image.to_webp", "image.to_bmp"])
def test_a_16_bit_greyscale_image_is_not_clipped_to_white(real, tmp_path: Path, action_id):
    """A 16 bit greyscale PNG (scanners, scientific images) converts to white.

    _prepare and the _write fallback call ``convert("RGB")`` straight from
    I;16, which clips every value above 255 instead of scaling, so a mid grey
    of 32768 becomes 255.
    """
    Image = image.pillow()
    source = tmp_path / "scan.png"
    ramp = Image.new("I;16", (256, 1))
    ramp.putdata([value * 256 for value in range(256)])
    ramp.save(source)

    result = real.run(action_id, [source], {})

    assert result.ok, result.message
    with Image.open(result.outputs[0]) as out:
        grey = out.convert("L")
        middle = grey.getpixel((128, 0))
    assert 100 <= middle <= 160, f"mid grey came out as {middle}, expected about 128"
