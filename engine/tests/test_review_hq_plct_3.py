"""Adversarial review checks for hq-plct.3 (av family), round 1.

Each test was a failing check found in review. Renamed to test_ in the fix
round so the default suite collects them and keeps them as regressions.
"""

from __future__ import annotations

import json
import shutil
import subprocess
from pathlib import Path

import pytest

from peel_engine.registry import load_registry

pytestmark = pytest.mark.skipif(
    not (shutil.which("ffmpeg") and shutil.which("ffprobe")), reason="ffmpeg missing"
)


def ff(*args: str) -> None:
    subprocess.run(["ffmpeg", "-v", "error", "-y", *args], check=True)


def probe(path: Path) -> dict:
    out = subprocess.run(
        ["ffprobe", "-v", "error", "-show_format", "-show_streams", "-of", "json", str(path)],
        capture_output=True, text=True,
    ).stdout
    return json.loads(out or "{}")


def rotation(path: Path):
    for s in probe(path).get("streams", []):
        for sd in s.get("side_data_list", []) or []:
            if "rotation" in sd:
                return sd["rotation"]
    return None


@pytest.fixture(scope="module")
def reg():
    return load_registry(force=True)


@pytest.fixture
def clips(tmp_path):
    base = tmp_path / "base.mp4"
    ff("-f", "lavfi", "-i", "testsrc=size=320x240:rate=25:duration=2",
       "-f", "lavfi", "-i", "sine=duration=2",
       "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", str(base))
    # A phone clip: same coded stream, display matrix says rotate 90 degrees.
    rot = tmp_path / "rot.mp4"
    ff("-display_rotation", "90", "-i", str(base), "-c", "copy", str(rot))
    # Audio outlasts the video (common with screen and phone recordings).
    shortvid = tmp_path / "shortvid.mp4"
    ff("-f", "lavfi", "-i", "sine=duration=2",
       "-f", "lavfi", "-i", "color=c=red:s=320x240:d=0.04",
       "-map", "0:a", "-map", "1:v", "-c:v", "libx264", "-pix_fmt", "yuv420p",
       "-c:a", "aac", str(shortvid))
    return {"base": base, "rot": rot, "shortvid": shortvid, "dir": tmp_path}


# --- B1: Snapshot reports success and leaves zero byte PNGs ------------------


def test_snapshot_near_the_end_is_a_real_png_or_a_clean_failure(reg, clips):
    """1.99s is inside a 2.00s clip at 25 fps (last frame starts at 1.96s)."""
    r = reg.run("av.video_snapshot", [clips["base"]], {"time": 1.99})
    if r.ok:
        for o in r.outputs:
            assert o.exists() and o.stat().st_size > 0, f"ok=True but {o.name} is empty"
    assert not [p for p in clips["dir"].glob("*.png") if p.stat().st_size == 0], \
        "zero byte PNG left beside the source"


def test_snapshot_every_n_never_reports_empty_frames(reg, clips):
    r = reg.run("av.video_snapshot", [clips["shortvid"]], {"every": 0.5})
    if r.ok:
        empty = [o.name for o in r.outputs if not o.exists() or o.stat().st_size == 0]
        assert not empty, f"ok=True with empty outputs: {empty}"
    assert not [p for p in clips["dir"].glob("*.png") if p.stat().st_size == 0]


def test_split_parts_all_have_video(reg, clips):
    r = reg.run("av.video_split", [clips["shortvid"]], {"every": 0.5})
    if r.ok:
        novideo = [o.name for o in r.outputs
                   if not [s for s in probe(o).get("streams", []) if s["codec_type"] == "video"]]
        assert not novideo, f"Split of a video wrote parts with no video track: {novideo}"


# --- B2: phone rotation ------------------------------------------------------


def test_crop_accepts_a_rect_inside_the_displayed_portrait_frame(reg, clips):
    """rot.mp4 displays as 240x320. A 200x300 rect fits; it is refused today."""
    r = reg.run("av.video_crop", [clips["rot"]], {"x": 0, "y": 0, "w": 200, "h": 300})
    assert r.ok, r.message


def test_redact_accepts_a_rect_inside_the_displayed_portrait_frame(reg, clips):
    r = reg.run("av.video_redact", [clips["rot"]], {"x": 0, "y": 200, "w": 200, "h": 100})
    assert r.ok, r.message


def test_join_does_not_stream_copy_one_rotation_over_a_landscape_clip(reg, clips):
    """Signatures ignore rotation, so rot+base is stream copied and the landscape
    half inherits rotate 90 (plays sideways)."""
    r = reg.run("av.video_join", [clips["rot"], clips["base"]], {})
    assert r.ok, r.message
    assert rotation(r.outputs[0]) is None, \
        f"joined file carries rotation {rotation(r.outputs[0])} over the unrotated clip"


# --- B3: Metadata silently not written ---------------------------------------


@pytest.mark.parametrize("ext,codec", [("ogg", "libvorbis"), ("opus", "libopus")])
def test_metadata_replaces_an_existing_title_in_ogg_and_opus(reg, tmp_path, ext, codec):
    src = tmp_path / f"old.{ext}"
    ff("-f", "lavfi", "-i", "sine=duration=1", "-metadata", "title=OLD", "-c:a", codec, str(src))
    r = reg.run("av.audio_metadata", [src], {"title": "NEW"})
    assert r.ok, r.message
    info = probe(r.outputs[0])
    titles = [s.get("tags", {}).get("title") for s in info.get("streams", [])]
    titles.append(info.get("format", {}).get("tags", {}).get("title"))
    assert "OLD" not in titles and "NEW" in titles, f"titles after edit: {titles}"


def test_metadata_on_aiff_writes_the_artist_it_reports(reg, tmp_path):
    src = tmp_path / "old.aiff"
    ff("-f", "lavfi", "-i", "sine=duration=1", str(src))
    r = reg.run("av.audio_metadata", [src], {"title": "T", "artist": "A"})
    assert r.ok, r.message
    assert "artist" in r.message
    tags = probe(r.outputs[0]).get("format", {}).get("tags", {})
    assert tags.get("artist") == "A" or tags.get("author") == "A", f"tags: {tags}"


# --- non-blocking: relative names ---------------------------------------------


def test_relative_dash_name_is_not_read_as_an_ffmpeg_option(reg, clips, monkeypatch):
    monkeypatch.chdir(clips["dir"])
    shutil.copy(clips["base"], clips["dir"] / "-x.mp4")
    r = reg.run("av.video_to_mkv", [Path("-x.mp4")], {})
    assert r.ok, r.message
