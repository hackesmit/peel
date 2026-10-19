"""Adversarial review of hq-plct.2 (image family), round 2.

Runnable failing checks for the round 2 blocking findings. Not collected by
default (no test_ prefix); run explicitly:

    cd engine && python3 -m pytest -q tests/review2_hq_plct_2.py
"""

from __future__ import annotations

import math
from pathlib import Path

import pytest

from peel_engine.families import image
from peel_engine.registry import load_registry

pytest.importorskip("PIL")


@pytest.fixture(scope="module")
def real():
    return load_registry(force=True, strict=True)


def _durations(path: Path) -> list[int]:
    Image = image.pillow()
    out = []
    with Image.open(path) as img:
        for index in range(getattr(img, "n_frames", 1)):
            img.seek(index)
            img.load()
            out.append(int(img.info.get("duration") or 0))
    return out


def _animation(path: Path) -> None:
    Image = image.pillow()
    frames = [Image.new("RGBA", (40, 40), c) for c in
              [(255, 0, 0, 255), (0, 255, 0, 255), (0, 0, 255, 255)]]
    frames[0].save(path, save_all=True, append_images=frames[1:],
                   duration=[50, 200, 1000], loop=3)


def test_compress_keeps_the_frame_timing_of_an_animated_webp(real, tmp_path: Path):
    """load_frames reads frame.info["duration"] before the frame is loaded.

    Pillow's WebP (and AVIF) decoder only fills info["duration"] on load, so
    every frame falls back to 100 ms: a 50/200/1000 ms animation comes out of
    Compress as 100/100/100 ms while the message says all frames were kept.
    """
    source = tmp_path / "sticker.webp"
    _animation(source)
    assert _durations(source) == [50, 200, 1000]

    result = real.run("image.compress", [source], {"quality": 70, "max_dimension": 0})

    assert result.ok, result.message
    assert _durations(result.outputs[0]) == [50, 200, 1000]


def test_animated_avif_to_webp_keeps_the_frame_timing(real, tmp_path: Path):
    source = tmp_path / "clip.avif"
    try:
        _animation(source)
    except Exception as exc:  # pillow built without AVIF
        pytest.skip(f"no AVIF writer: {exc}")

    result = real.run("image.to_webp", [source], {})

    assert result.ok, result.message
    assert _durations(result.outputs[0]) == [50, 200, 1000]


@pytest.mark.parametrize("bad", [float("nan"), float("inf")])
def test_one_nan_or_inf_pixel_does_not_blacken_a_float_image(real, tmp_path: Path, bad):
    """to_8bit takes getextrema() of an F image; one NaN or Inf pixel makes
    the range NaN or infinite, so every pixel scales to 0 and the whole image
    comes out black (float TIFFs use NaN as nodata routinely)."""
    Image = image.pillow()
    source = tmp_path / "float.tif"
    ramp = Image.new("F", (256, 1))
    ramp.putdata([bad if x == 0 else x / 255 for x in range(256)])
    ramp.save(source)

    result = real.run("image.to_jpg", [source], {})

    assert result.ok, result.message
    with Image.open(result.outputs[0]) as out:
        middle = out.convert("L").getpixel((128, 0))
    assert 100 <= middle <= 160, f"mid grey came out as {middle}, expected about 128"
    assert not math.isnan(middle)
