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


# Round 2 (lucy, review round 1): on a frame only 2 px wide or tall the window
# cannot hold any radius, both evaluate to 0, and boxblur handed the box back
# untouched while the run reported success. Such a box is now filled with its
# own average colour. The fixtures are lossless and a checkerboard of black
# and white, the most contrast a box can hold.

def checkerboard(target: Path, size: str, *, chroma: bool = False) -> Path:
    """A lossless clip whose luma alternates black and white pixel by pixel,
    or (chroma=True) flat luma with red and blue columns two pixels wide."""
    if chroma:
        expr = "lum=128:cb='if(lt(mod(X,4),2),90,170)':cr='if(lt(mod(X,4),2),240,16)'"
    else:
        expr = "lum='if(eq(mod(X+Y,2),0),235,16)':cb=128:cr=128"
    subprocess.run(
        [FFMPEG, "-v", "error", "-y", "-f", "lavfi",
         "-i", f"color=c=black:size={size}:rate=10:duration=0.3,format=yuv444p,geq={expr}",
         "-c:v", "ffv1", "-level", "1", "-pix_fmt", "yuv420p", str(target)],
        check=True,
    )
    return target


def plane(path: Path, rect: tuple[int, int, int, int], fmt: str = "gray") -> list[int]:
    """The first frame's pixels inside rect in one plane: gray is luma; u and v
    take a yuv444p frame's chroma planes."""
    x, y, w, h = rect
    pick = {"gray": "format=gray", "u": "format=yuv444p,extractplanes=u", "v": "format=yuv444p,extractplanes=v"}[fmt]
    raw = subprocess.run(
        [FFMPEG, "-v", "error", "-i", str(path), "-frames:v", "1",
         "-vf", f"crop={w}:{h}:{x}:{y},{pick}", "-f", "rawvideo", "-"],
        capture_output=True, check=True,
    ).stdout
    assert len(raw) == w * h
    return list(raw)


@needs_ffmpeg
@pytest.mark.parametrize(
    "size, rect",
    [
        ("2x2", (0, 0, 2, 2)),     # the whole frame
        ("2x16", (0, 4, 2, 8)),    # two pixels wide
        ("16x2", (4, 0, 8, 2)),    # two pixels tall
    ],
)
def test_a_frame_too_small_to_blur_is_still_obscured(reg, tmp_path, size, rect):
    source = checkerboard(tmp_path / "check.mkv", size)
    assert max(plane(source, rect)) - min(plane(source, rect)) > 200
    result = reg.run("av.video_redact", [source], {"x": rect[0], "y": rect[1], "w": rect[2], "h": rect[3], "strength": 12})
    assert result.ok, result.message
    pixels = plane(result.outputs[0], rect)
    # Black and white have met in the middle: no pixel is near either.
    assert max(pixels) - min(pixels) < 40, pixels
    assert all(70 < v < 180 for v in pixels), pixels


@needs_ffmpeg
def test_colour_too_small_to_blur_is_still_obscured(reg, tmp_path):
    # A 4 px wide frame holds a luma radius of 1, but its 4:2:0 chroma plane is
    # 2 px wide and holds none: red and blue columns would come through.
    source = checkerboard(tmp_path / "stripes.mkv", "4x16", chroma=True)
    rect = (0, 0, 4, 16)
    result = reg.run("av.video_redact", [source], {"x": 0, "y": 0, "w": 4, "h": 16, "strength": 12})
    assert result.ok, result.message
    for fmt in ("u", "v"):
        assert max(plane(source, rect, fmt)) - min(plane(source, rect, fmt)) > 70
        pixels = plane(result.outputs[0], rect, fmt)
        assert max(pixels) - min(pixels) < 20, (fmt, pixels)


# Round 3 (lucy, review round 2): the average fill was pixelize with its block
# capped at 1024, so a box with a longer side came out as two or more tiles,
# each its own average. A 2x2048 frame half black and half white came back
# unchanged while the run reported success. A box that cannot be blurred and
# is longer than one block is now refused, and nothing is written.

def halves(target: Path, size: str, *, across: bool = False) -> Path:
    """A lossless clip, black in its first half and white in its second: top
    and bottom, or (across=True) left and right."""
    w, h = (int(n) for n in size.split("x"))
    expr = f"lt(X,{w // 2})" if across else f"lt(Y,{h // 2})"
    subprocess.run(
        [FFMPEG, "-v", "error", "-y", "-f", "lavfi",
         "-i", f"color=c=black:size={size}:rate=10:duration=0.3,format=yuv444p,"
               f"geq=lum='if({expr},16,235)':cb=128:cr=128",
         "-c:v", "ffv1", "-level", "1", "-pix_fmt", "yuv420p", str(target)],
        check=True,
    )
    return target


@needs_ffmpeg
@pytest.mark.parametrize(
    "size, across",
    [
        ("2x2048", False),  # the lucy repro
        ("2048x2", True),
        ("4x2048", False),  # luma holds a radius of 1, its chroma none
    ],
)
def test_a_box_too_long_to_fill_in_one_colour_is_refused(reg, tmp_path, size, across):
    source = halves(tmp_path / "halves.mkv", size, across=across)
    w, h = (int(n) for n in size.split("x"))
    assert max(plane(source, (0, 0, w, h))) - min(plane(source, (0, 0, w, h))) > 200
    result = reg.run("av.video_redact", [source], {"x": 0, "y": 0, "w": w, "h": h, "strength": 12})
    assert not result.ok
    assert result.code == "redact_box_too_long"
    assert "Traceback" not in result.message
    assert "1024" in result.message
    assert sorted(p.name for p in tmp_path.iterdir()) == ["halves.mkv"]


@needs_ffmpeg
def test_a_fill_up_to_one_block_long_is_one_colour(reg, tmp_path):
    # 1024 is the longest side one pixelize block covers: still filled, and
    # black and white meet in the middle.
    source = halves(tmp_path / "halves.mkv", "2x1024")
    rect = (0, 0, 2, 1024)
    result = reg.run("av.video_redact", [source], {"x": 0, "y": 0, "w": 2, "h": 1024, "strength": 12})
    assert result.ok, result.message
    pixels = plane(result.outputs[0], rect)
    assert max(pixels) - min(pixels) < 4, (min(pixels), max(pixels))
    assert all(100 < v < 150 for v in pixels), (min(pixels), max(pixels))


@needs_ffmpeg
def test_a_box_taller_than_one_block_on_a_wider_frame_is_blurred(reg, tmp_path):
    # A frame with room around the box blurs it, at any length.
    source = checkerboard(tmp_path / "check.mkv", "64x2048")
    rect = (30, 0, 2, 2048)
    assert max(plane(source, rect)) - min(plane(source, rect)) > 200
    result = reg.run("av.video_redact", [source], {"x": 30, "y": 0, "w": 2, "h": 2048, "strength": 12})
    assert result.ok, result.message
    pixels = plane(result.outputs[0], rect)
    assert max(pixels) - min(pixels) < 40, (min(pixels), max(pixels))
    assert all(70 < v < 180 for v in pixels), (min(pixels), max(pixels))
