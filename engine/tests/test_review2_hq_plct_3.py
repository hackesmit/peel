"""Adversarial review checks for hq-plct.3 (av family), round 2.

Collected with the suite since round 3 (renamed from review2_hq_plct_3.py).
Run alone: cd engine && python3 -m pytest -q tests/test_review2_hq_plct_3.py

BLOCKING (R2-B1): av.probe() runs ffprobe with text=True and no encoding, so
Python decodes ffprobe's UTF-8 JSON with the locale code page. On George
(Windows 11, Python 3.14, locale cp1252, utf8_mode 0) that means:
  * the round-1 B3 readback compares mojibake with the asked value, so
    Metadata with ANY non-ASCII value ("Cancion" with an accent) fails with
    metadata_not_written and its output is deleted, on every audio format;
  * a tag or path holding a byte cp1252 cannot map (0x81 0x8D 0x8F 0x90 0x9D,
    for example the UTF-8 of an accented capital A or I) raises
    UnicodeDecodeError in the reader thread, stdout comes back None, and every
    av tool reports "has no audio track" / "has no video track".
The *_real tests fail on George as is. The *_cp1252 tests reproduce the
Windows decode on any OS by giving av's subprocess.run the encoding Windows
would pick.

Round 3 (Daniel approved) fixed R2-B1 and three of the non-blocking notes
(join flips, late video start, ignored interval); their checks now pass as
plain tests. Round 3 additions are at the end of the file.
"""

from __future__ import annotations

import json
import shutil
import subprocess
import sys
from pathlib import Path

import pytest

from peel_engine.families import av
from peel_engine.registry import load_registry

pytestmark = pytest.mark.skipif(
    not (shutil.which("ffmpeg") and shutil.which("ffprobe")), reason="ffmpeg missing"
)

ACCENT = "Canción"            # o with acute: UTF-8 C3 B3, decodable as mojibake
UNMAPPED = "Álbum"            # A with acute: UTF-8 C3 81, 0x81 is undefined in cp1252

FORMATS = {
    "mp3": ["-c:a", "libmp3lame"],
    "m4a": ["-c:a", "aac"],
    "flac": [],
    "ogg": ["-c:a", "libvorbis"],
    "opus": ["-c:a", "libopus"],
    "wav": [],
    "aiff": [],
    "wma": ["-c:a", "wmav2"],
}


def ff(*args: str) -> None:
    subprocess.run([shutil.which("ffmpeg"), "-v", "error", "-y", *args], check=True)


def probe_utf8(path: Path) -> dict:
    out = subprocess.run(
        [shutil.which("ffprobe"), "-v", "error", "-show_format", "-show_streams",
         "-of", "json", str(path)],
        capture_output=True,
    ).stdout
    return json.loads(out.decode("utf-8") or "{}")


def display_matrix(path: Path):
    for s in probe_utf8(path).get("streams", []):
        for sd in s.get("side_data_list", []) or []:
            if "displaymatrix" in sd:
                return sd["displaymatrix"].split()
    return None


@pytest.fixture(scope="module")
def reg():
    return load_registry(force=True)


@pytest.fixture
def cp1252(monkeypatch):
    """Make av's ffprobe call decode the way Windows (cp1252 locale) does."""
    real = subprocess.run

    def run(*args, **kwargs):
        if kwargs.get("text") and "encoding" not in kwargs:
            kwargs["encoding"] = "cp1252"
        return real(*args, **kwargs)

    monkeypatch.setattr(av.subprocess, "run", run)


def _source(tmp_path: Path, ext: str, *tags: str) -> Path:
    src = tmp_path / f"old.{ext}"
    meta: list[str] = []
    for tag in tags:
        meta += ["-metadata", tag]
    ff("-f", "lavfi", "-i", "sine=duration=1", *meta, *FORMATS[ext], str(src))
    return src


def _assert_written(reg, src: Path) -> None:
    r = reg.run("av.audio_metadata", [src], {"title": ACCENT})
    assert r.ok, f"{src.suffix}: {r.message}"
    tags = probe_utf8(r.outputs[0]).get("format", {}).get("tags", {})
    stream = [s.get("tags", {}) for s in probe_utf8(r.outputs[0])["streams"]]
    assert ACCENT in json.dumps([tags, stream], ensure_ascii=False)


# --- BLOCKING R2-B1: Windows decode of ffprobe output -----------------------


@pytest.mark.skipif(sys.platform != "win32", reason="real cp1252 locale only on Windows")
@pytest.mark.parametrize("ext", sorted(FORMATS))
def test_metadata_with_an_accent_is_written_real(reg, tmp_path, ext):
    _assert_written(reg, _source(tmp_path, ext, "title=OLD"))


@pytest.mark.parametrize("ext", sorted(FORMATS))
def test_metadata_with_an_accent_is_written_cp1252(reg, tmp_path, cp1252, ext):
    _assert_written(reg, _source(tmp_path, ext, "title=OLD"))


@pytest.mark.skipif(sys.platform != "win32", reason="real cp1252 locale only on Windows")
def test_a_tag_with_an_accented_capital_does_not_hide_the_audio_real(reg, tmp_path):
    src = _source(tmp_path, "mp3", f"album={UNMAPPED}")
    r = reg.run("av.audio_to_wav", [src], {})
    assert r.ok, r.message


def test_a_tag_with_an_accented_capital_does_not_hide_the_audio_cp1252(reg, tmp_path, cp1252):
    src = _source(tmp_path, "mp3", f"album={UNMAPPED}")
    r = reg.run("av.audio_to_wav", [src], {})
    assert r.ok, r.message


# --- NON-BLOCKING ------------------------------------------------------------


def test_join_does_not_copy_a_flipped_display_matrix_over_a_plain_rotated_clip(reg, tmp_path):
    base = tmp_path / "base.mp4"
    ff("-f", "lavfi", "-i", "testsrc=size=320x240:rate=25:duration=1",
       "-c:v", "libx264", "-pix_fmt", "yuv420p", str(base))
    rot = tmp_path / "rot.mp4"
    ff("-display_rotation", "90", "-i", str(base), "-c", "copy", str(rot))
    flip = tmp_path / "fliprot.mp4"
    ff("-display_rotation", "90", "-display_hflip", "-i", str(base), "-c", "copy", str(flip))
    assert display_matrix(rot) != display_matrix(flip)
    r = reg.run("av.video_join", [flip, rot], {})
    assert r.ok, r.message
    assert display_matrix(r.outputs[0]) is None, "one display matrix copied over both clips"


@pytest.fixture
def late(tmp_path):
    """Audio 0 to 3 s, video 1 to 3 s (video stream start_time 1.0)."""
    path = tmp_path / "late.mp4"
    ff("-f", "lavfi", "-i", "sine=duration=3", "-itsoffset", "1",
       "-f", "lavfi", "-i", "testsrc=size=320x240:rate=25:duration=2",
       "-map", "0:a", "-map", "1:v", "-c:v", "libx264", "-pix_fmt", "yuv420p",
       "-c:a", "aac", str(path))
    return path


def test_snapshot_inside_a_late_starting_video_is_accepted(reg, late):
    r = reg.run("av.video_snapshot", [late], {"time": 2.5})
    assert r.ok, r.message


def test_split_every_second_of_a_late_starting_video(reg, late):
    r = reg.run("av.video_split", [late], {"every": 1})
    assert r.ok, r.message


# --- round 3 additions -------------------------------------------------------


def _streams_of(path: Path, kind: str) -> list[dict]:
    return [s for s in probe_utf8(path).get("streams", []) if s.get("codec_type") == kind]


def test_snapshot_every_second_of_a_late_video_saves_each_second_once(reg, late):
    r = reg.run("av.video_snapshot", [late], {"every": 1})
    assert r.ok, r.message
    # Video runs 1 to 3 s: frames at 1 s and 2 s, no duplicate of the first.
    assert len(r.outputs) == 2, r.message
    assert r.outputs[0].read_bytes() != r.outputs[1].read_bytes()


def test_snapshot_before_a_late_video_shows_its_first_frame(reg, late):
    r = reg.run("av.video_snapshot", [late], {"time": 0.2})
    assert r.ok, r.message


def test_snapshot_past_a_late_video_is_refused(reg, late):
    r = reg.run("av.video_snapshot", [late], {"time": 3.2})
    assert not r.ok


def test_split_part_one_of_a_late_video_keeps_its_audio(reg, late):
    r = reg.run("av.video_split", [late], {"at": ["1.5"]})
    assert r.ok, r.message
    audio = _streams_of(r.outputs[0], "audio")
    assert audio, "part 1 has no audio"
    assert float(audio[0].get("start_time", 0)) < 0.05, audio[0].get("start_time")
    assert float(audio[0]["duration"]) > 1.4, audio[0]["duration"]
    assert _streams_of(r.outputs[0], "video")


def test_split_at_a_cut_before_a_late_video_is_refused(reg, late):
    r = reg.run("av.video_split", [late], {"at": ["0.5"]})
    assert not r.ok


def test_join_still_copies_two_identically_flipped_clips(reg, tmp_path):
    base = tmp_path / "base.mp4"
    ff("-f", "lavfi", "-i", "testsrc=size=320x240:rate=25:duration=1",
       "-c:v", "libx264", "-pix_fmt", "yuv420p", str(base))
    flips = []
    for name in ("a", "b"):
        out = tmp_path / f"{name}.mp4"
        ff("-display_rotation", "90", "-display_hflip", "-i", str(base), "-c", "copy", str(out))
        flips.append(out)
    r = reg.run("av.video_join", flips, {})
    assert r.ok, r.message
    assert display_matrix(r.outputs[0]) == display_matrix(flips[0])


def test_snapshot_every_with_no_duration_says_the_interval_was_ignored(reg, tmp_path):
    src = tmp_path / "piped.mkv"
    # Matroska written to a pipe carries no duration anywhere.
    data = subprocess.run(
        [shutil.which("ffmpeg"), "-v", "error", "-f", "lavfi",
         "-i", "testsrc=size=160x120:rate=25:duration=2", "-c:v", "libx264",
         "-pix_fmt", "yuv420p", "-f", "matroska", "-live", "1", "pipe:1"],
        capture_output=True, check=True,
    ).stdout
    src.write_bytes(data)
    info = probe_utf8(src)
    durations = [info.get("format", {}).get("duration")]
    durations += [s.get("duration") for s in info.get("streams", [])]
    durations += [(s.get("tags") or {}).get("DURATION") for s in info.get("streams", [])]
    if any(durations):
        pytest.skip(f"this ffmpeg still writes a duration: {durations}")
    r = reg.run("av.video_snapshot", [src], {"every": 0.5})
    assert r.ok, r.message
    assert "ignored" in r.message, r.message
