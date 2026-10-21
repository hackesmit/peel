"""Small redaction boxes (hq-plct.27, from lucy round 2 on hq-plct.11).

av.video_redact cropped the box and ran ``boxblur=<strength>:2`` on the crop
alone. boxblur refuses a radius over (shorter side - 1) / 2 of each plane, and
a yuv420p chroma plane is half the box, so a 40x40 box at the default strength
of 12 failed outright, and a 2x2 box could never be blurred at all (its radius
would have to be 0). The box is now blurred inside a window of its own
neighbourhood, wide enough for the blur's full reach, with every plane's
radius clamped to what that window can hold, then cropped back to its exact
size.
"""
from __future__ import annotations

import shutil
import statistics
import subprocess
from pathlib import Path

import pytest

from peel_engine.registry import load_registry

FFMPEG = shutil.which("ffmpeg")
needs_ffmpeg = pytest.mark.skipif(FFMPEG is None, reason="ffmpeg is not installed")


@pytest.fixture(scope="module")
def reg():
    return load_registry(force=True, strict=True)


@pytest.fixture(scope="module")
def grainy(tmp_path_factory) -> Path:
    """Heavy grain on flat grey, so every pixel sits far from the grey until a
    blur averages its neighbourhood back onto it."""
    target = tmp_path_factory.mktemp("redact_small") / "grainy.mp4"
    subprocess.run(
        [FFMPEG, "-v", "error", "-y", "-f", "lavfi",
         "-i", "color=c=gray:size=320x240:rate=10:duration=0.5,noise=alls=80:allf=t",
         "-c:v", "libx264", "-qp", "0", "-pix_fmt", "yuv420p", str(target)],
        check=True,
    )
    return target


def luma(path: Path, rect: tuple[int, int, int, int]) -> list[list[int]]:
    """The first frame's luma inside rect, row by row."""
    x, y, w, h = rect
    raw = subprocess.run(
        [FFMPEG, "-v", "error", "-i", str(path), "-frames:v", "1",
         "-vf", f"format=gray,crop={w}:{h}:{x}:{y}", "-f", "rawvideo", "-"],
        capture_output=True, check=True,
    ).stdout
    assert len(raw) == w * h
    return [list(raw[row * w:(row + 1) * w]) for row in range(h)]


def grain(rows: list[list[int]]) -> float:
    """Mean difference between each pixel and its right and lower neighbours."""
    steps = [abs(r[i + 1] - r[i]) for r in rows for i in range(len(r) - 1)]
    steps += [abs(b[i] - a[i]) for a, b in zip(rows, rows[1:]) for i in range(len(a))]
    return statistics.mean(steps)


def blurred(source: Path, target: Path, rect, size=(320, 240)) -> tuple[float, float]:
    """How far the box's pixels sit from the frame's mean grey, before and
    after. Unlike neighbour differences this still means something for a 2x2
    box, which has only four pixels to compare."""
    mean = statistics.mean(v for row in luma(source, (0, 0, *size)) for v in row)
    spread = lambda path: statistics.mean(abs(v - mean) for row in luma(path, rect) for v in row)
    return spread(source), spread(target)


@needs_ffmpeg
@pytest.mark.parametrize(
    "rect",
    [
        (10, 10, 40, 40),   # the lucy repro: chroma plane 20x20, radius 12
        (100, 60, 10, 10),
        (150, 100, 2, 2),   # the minimum box
        (151, 101, 3, 3),   # odd, so on the yuv444p path
        (0, 0, 2, 2),       # in a corner: the window cannot reach past the frame
        (318, 238, 2, 2),
    ],
)
def test_a_small_box_is_redacted_and_blurred(reg, grainy, tmp_path, rect):
    source = tmp_path / grainy.name
    shutil.copy2(grainy, source)
    x, y, w, h = rect
    result = reg.run(
        "av.video_redact", [source], {"x": x, "y": y, "w": w, "h": h, "strength": 12}
    )
    assert result.ok, result.message
    before, after = blurred(source, result.outputs[0], rect)
    assert after < before / 2, (rect, before, after)
    if w > 2 and h > 2:
        assert grain(luma(result.outputs[0], rect)) < grain(luma(source, rect)) / 3


@needs_ffmpeg
def test_the_strongest_blur_on_a_tiny_frame_still_runs(reg, tmp_path):
    # strength 50 asks for a radius far past what a 16x16 frame can hold.
    source = tmp_path / "tiny.mp4"
    subprocess.run(
        [FFMPEG, "-v", "error", "-y", "-f", "lavfi",
         "-i", "color=c=gray:size=16x16:rate=10:duration=0.3,noise=alls=80:allf=t",
         "-c:v", "libx264", "-qp", "0", "-pix_fmt", "yuv420p", str(source)],
        check=True,
    )
    result = reg.run("av.video_redact", [source], {"x": 4, "y": 4, "w": 8, "h": 8, "strength": 50})
    assert result.ok, result.message
    before, after = blurred(source, result.outputs[0], (4, 4, 8, 8), size=(16, 16))
    assert after < before / 2, (before, after)


@needs_ffmpeg
def test_the_pixels_outside_the_box_are_not_blurred(reg, grainy, tmp_path):
    source = tmp_path / grainy.name
    shutil.copy2(grainy, source)
    result = reg.run(
        "av.video_redact", [source], {"x": 150, "y": 100, "w": 2, "h": 2, "strength": 12}
    )
    assert result.ok, result.message
    # The neighbourhood the blur read from stays as sharp as it was.
    ring = (140, 90, 8, 8)
    assert grain(luma(result.outputs[0], ring)) > grain(luma(source, ring)) * 0.6
