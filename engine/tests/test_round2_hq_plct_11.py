"""Engine fixes from review round 1 of hq-plct.11 (timeline editors).

1. av.video_split dropped a typed cut past the end of the video track without
   saying so: a file with a 4 s picture and 8 s of audio, cut at 2,6, wrote 2
   parts where 3 were asked for. A typed cut the video cannot honour is now
   refused by name.
2. av.video_redact floored an odd box to even (and on a yuv420p frame crop
   and overlay snap to the chroma grid), so an odd frame covered edge to edge
   kept its last column sharp. The box is now blurred at its exact size.
"""
from __future__ import annotations

import io
import shutil
import statistics
import subprocess
from pathlib import Path

import pytest
from PIL import Image

from peel_engine.result import InvalidParams
from peel_engine.families import av
from peel_engine.registry import load_registry

FFMPEG = shutil.which("ffmpeg")
needs_ffmpeg = pytest.mark.skipif(FFMPEG is None, reason="ffmpeg is not installed")


@pytest.fixture(scope="module")
def reg():
    return load_registry(force=True, strict=True)


def ff(*args: str) -> None:
    subprocess.run([FFMPEG, "-v", "error", "-y", *args], check=True)


def first_frame(path: Path) -> Image.Image:
    data = subprocess.run(
        [FFMPEG, "-v", "error", "-i", str(path), "-frames:v", "1",
         "-vf", "extractplanes=y", "-f", "image2pipe", "-vcodec", "png", "-"],
        capture_output=True, check=True,
    ).stdout
    return Image.open(io.BytesIO(data)).convert("L")


def column_grain(image: Image.Image, x: int, rows: range) -> float:
    """How much one column changes from pixel to pixel down its length: high
    on the noisy fixture, near zero once blurred. Read within the column only,
    so a sharp neighbour does not count."""
    return statistics.mean(abs(image.getpixel((x, y + 1)) - image.getpixel((x, y))) for y in rows)


def row_grain(image: Image.Image, y: int, columns: range) -> float:
    return statistics.mean(abs(image.getpixel((x + 1, y)) - image.getpixel((x, y))) for x in columns)


# --- split: a typed cut is never dropped ---------------------------------------


def test_a_typed_cut_past_the_video_is_refused_not_dropped():
    # The video track ends at 4 s; the container (audio) runs to 8 s.
    with pytest.raises(InvalidParams) as refused:
        av._cut_points({"at": ["2", "6"]}, 4.0, 3.96)
    assert "6s" in str(refused.value)


def test_a_typed_cut_before_a_late_video_is_refused_not_dropped():
    with pytest.raises(InvalidParams):
        av._cut_points({"at": ["0.5", "2"]}, 4.0, 3.96, first=1.0)


def test_an_interval_still_stops_at_the_last_frame():
    # every is the engine's own list, so it ends where the video ends.
    assert av._cut_points({"every": 1.5}, 4.0, 3.96) == [(0.0, 1.5), (1.5, 3.0), (3.0, 4.0)]


@needs_ffmpeg
def test_split_of_a_short_video_with_long_audio_refuses_a_cut_after_the_picture(reg, tmp_path):
    source = tmp_path / "long audio.mp4"
    ff("-f", "lavfi", "-i", "testsrc=size=160x120:rate=25:duration=4",
       "-f", "lavfi", "-i", "sine=duration=8",
       "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", str(source))
    refused = reg.run("av.video_split", [source], {"at": ["2", "6"]})
    assert not refused.ok, [p.name for p in refused.outputs]
    assert "6" in refused.message
    assert not list(tmp_path.glob("long audio (Split)*"))
    kept = reg.run("av.video_split", [source], {"at": ["2"]})
    assert kept.ok and len(kept.outputs) == 2, kept.message


# --- redact: an odd box is blurred to its last pixel ---------------------------


@needs_ffmpeg
@pytest.mark.parametrize("codec", [("ffv1", "yuv420p", "mkv"), ("libx264", "yuv444p", "mp4")])
def test_redacting_an_odd_frame_edge_to_edge_blurs_the_last_column(reg, tmp_path, codec):
    encoder, pix_fmt, ext = codec
    source = tmp_path / f"odd.{ext}"
    ff("-f", "lavfi", "-i", "testsrc=size=321x241:rate=10:duration=1,noise=alls=80:allf=t",
       "-c:v", encoder, "-pix_fmt", pix_fmt, str(source))
    before = first_frame(source)
    sharp = column_grain(before, before.width - 1, range(20, 220))
    result = reg.run("av.video_redact", [source],
                     {"x": 0, "y": 0, "w": 321, "h": 241, "strength": 12})
    assert result.ok, result.message
    after = first_frame(result.outputs[0])
    # The encoder still needs an even frame, so the output is 320x240; its
    # last columns come from the source's last columns and must be blurred.
    assert after.size == (320, 240)
    last = column_grain(after, after.width - 1, range(20, 220))
    middle = column_grain(after, 160, range(20, 220))
    assert last < sharp / 4, (sharp, last, middle)


@needs_ffmpeg
def test_an_odd_redaction_box_inside_the_frame_is_blurred_to_its_last_column(reg, tmp_path):
    source = tmp_path / "even.mp4"
    ff("-f", "lavfi", "-i", "testsrc=size=320x240:rate=10:duration=1,noise=alls=80:allf=t",
       "-c:v", "libx264", "-pix_fmt", "yuv420p", str(source))
    result = reg.run("av.video_redact", [source],
                     {"x": 11, "y": 11, "w": 101, "h": 101, "strength": 12})
    assert result.ok, result.message
    after = first_frame(result.outputs[0])
    sharp = column_grain(after, 250, range(30, 90))
    # Column 111 is the last covered column, row 111 the last covered row.
    assert column_grain(after, 111, range(30, 90)) < sharp / 4, sharp
    assert row_grain(after, 111, range(30, 90)) < sharp / 4, sharp
    # The picture beyond the box is untouched.
    assert column_grain(after, 112, range(30, 90)) > sharp / 2
