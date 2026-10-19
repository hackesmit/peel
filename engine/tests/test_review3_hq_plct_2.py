"""Adversarial review of hq-plct.2 (image family), round 3 (final).

Runnable failing check for a regression that round 3 (ed91e85) introduced.
Not collected by default (no test_ prefix); run it by path:

    cd engine && python3 -m pytest -q tests/review3_hq_plct_2.py
"""

from __future__ import annotations

from pathlib import Path

import pytest

from peel_engine.registry import load_registry

pytest.importorskip("PIL")


@pytest.fixture(scope="module")
def real():
    return load_registry(force=True, strict=True)


def _changed(path: Path):
    from PIL import Image, ImageChops

    with Image.open(path) as img:
        rgb = img.convert("RGB")
        return ImageChops.difference(rgb, Image.new("RGB", rgb.size, "white")).getbbox()


@pytest.mark.parametrize("line", [1, 3])
def test_a_rect_wholly_above_the_image_draws_nothing(real, tmp_path: Path, line):
    """_fit_shape clamps every box edge to [-margin, size + margin].

    A rect wholly above the image (y=-50, h=10) has top and bottom both
    clamped to -margin, a zero-height box at y=-line. Pillow then paints the
    outline into row 0, so a red line appears on the picture where nothing
    was drawn before ed91e85. Before round 3 this run left the image blank.
    """
    source = tmp_path / "plain.png"
    from PIL import Image

    Image.new("RGB", (40, 30), "white").save(source)
    shape = {"type": "rect", "x": 0, "y": -50, "w": 20, "h": 10, "width": line}

    result = real.run("image.annotate", [source], {"shapes": [shape]})

    assert result.ok, result.message
    assert _changed(result.outputs[0]) is None


def test_a_rect_wholly_left_of_the_image_draws_nothing(real, tmp_path: Path):
    source = tmp_path / "plain.png"
    from PIL import Image

    Image.new("RGB", (40, 30), "white").save(source)
    shape = {"type": "rect", "x": -80, "y": 5, "w": 20, "h": 10, "width": 1,
             "fill": "#0000ff"}

    result = real.run("image.annotate", [source], {"shapes": [shape]})

    assert result.ok, result.message
    assert _changed(result.outputs[0]) is None
