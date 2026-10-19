"""Final review (round 3) checks for hq-plct.3, av family.

Run alone: python -m pytest -q tests/review3_hq_plct_3.py
Not collected by default (no test_ prefix), same as the round 2 file was.
All checks pass on 7110434; none is blocking. They pin R2-B1 for non-Latin
text and the round 3 regression surface.
"""

from __future__ import annotations

import json
import shutil
import subprocess
from pathlib import Path

import pytest

from peel_engine import backends
from peel_engine.families import av
from peel_engine.registry import load_registry

pytestmark = pytest.mark.skipif(
    not (shutil.which("ffmpeg") and shutil.which("ffprobe")), reason="ffmpeg missing"
)

NONLATIN = "Привет 日本 Ñandú Ωμέγα"
PATHNAME = "Ünïcödé 日本 Ábc Ж"


def ff(*args: str) -> None:
    subprocess.run([shutil.which("ffmpeg"), "-v", "error", "-y", *args], check=True)


def tags_of(path: Path) -> str:
    out = subprocess.run(
        [shutil.which("ffprobe"), "-v", "error", "-show_format", "-show_streams",
         "-of", "json", str(path)], capture_output=True,
    ).stdout
    info = json.loads(out.decode("utf-8"))
    return json.dumps(
        [info.get("format", {}).get("tags", {})] + [s.get("tags", {}) for s in info["streams"]],
        ensure_ascii=False,
    )


@pytest.fixture(scope="module")
def reg():
    return load_registry(force=True)


@pytest.mark.parametrize("ext,codec", [("mp3", ["-c:a", "libmp3lame"]), ("flac", []),
                                       ("m4a", ["-c:a", "aac"]), ("ogg", ["-c:a", "libvorbis"])])
def test_non_latin_metadata_reads_back(reg, tmp_path, ext, codec):
    src = tmp_path / f"{PATHNAME}.{ext}"
    ff("-f", "lavfi", "-i", "sine=duration=1", "-metadata", f"artist={NONLATIN}",
       *codec, str(src))
    r = reg.run("av.audio_metadata", [src], {"title": NONLATIN})
    assert r.ok, r.message
    assert NONLATIN in tags_of(r.outputs[0])


def test_non_latin_path_through_probing_video_tools(reg, tmp_path):
    src = tmp_path / f"{PATHNAME}.mp4"
    ff("-f", "lavfi", "-i", "testsrc=size=160x120:rate=25:duration=2",
       "-f", "lavfi", "-i", "sine=duration=2", "-metadata", f"title={NONLATIN}",
       "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", str(src))
    info = av.probe(src)
    assert info["format"]["tags"]["title"] == NONLATIN
    assert av._streams(info, "audio") and av._streams(info, "video")
    for tool, params, inputs in [
        ("av.video_snapshot", {"time": 1}, [src]),
        ("av.video_split", {"every": 1}, [src]),
        ("av.video_join", {}, [src, src]),
    ]:
        r = reg.run(tool, inputs, params)
        assert r.ok, f"{tool}: {r.message}"


def test_backend_version_decodes_ffmpeg():
    text = backends.version("ffmpeg")
    assert "ffmpeg" in text.lower() and "\ufffd" not in text


def test_unrotated_and_identity_matrix_clips_share_a_signature(tmp_path):
    base = tmp_path / "base.mp4"
    ff("-f", "lavfi", "-i", "testsrc=size=160x120:rate=25:duration=1",
       "-c:v", "libx264", "-pix_fmt", "yuv420p", str(base))
    ident = tmp_path / "ident.mp4"
    ff("-display_rotation", "0", "-i", str(base), "-c", "copy", str(ident))
    a, b = av.probe(base), av.probe(ident)
    assert av._join_signature(a) == av._join_signature(b)
    assert av._flip(av._streams(a, "video")[0]) is None


def test_negative_container_start_offsets_from_container():
    info = {"format": {"start_time": "-0.323"},
            "streams": [{"codec_type": "video", "start_time": "0.000000", "duration": "4.0",
                         "avg_frame_rate": "25/1"}]}
    first, end, _ = av._video_window(info)
    assert abs(first - 0.323) < 1e-6 and abs(end - 4.323) < 1e-6


def test_negative_start_split_and_snapshot(reg, tmp_path):
    mkv = tmp_path / "neg.mkv"
    ff("-f", "lavfi", "-i", "testsrc=size=160x120:rate=25:duration=4", "-itsoffset", "-0.3",
       "-f", "lavfi", "-i", "sine=duration=4.3", "-map", "0:v", "-map", "1:a",
       "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac",
       "-avoid_negative_ts", "disabled", "-copyts", str(mkv))
    r = reg.run("av.video_split", [mkv], {"every": 1})
    assert r.ok and len(r.outputs) == 4, r.message
    r = reg.run("av.video_snapshot", [mkv], {"every": 1})
    assert r.ok and len(r.outputs) == 4, r.message


def test_part_one_of_a_long_zero_start_file(reg, tmp_path):
    src = tmp_path / "long.mp4"
    ff("-f", "lavfi", "-i", "testsrc=size=160x120:rate=25:duration=40",
       "-f", "lavfi", "-i", "sine=duration=40",
       "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", str(src))
    r = reg.run("av.video_split", [src], {"at": ["20"]})
    assert r.ok, r.message
    out = subprocess.run(
        [shutil.which("ffprobe"), "-v", "error", "-show_entries", "stream=codec_type,duration",
         "-of", "json", str(r.outputs[0])], capture_output=True).stdout
    durs = {s["codec_type"]: float(s["duration"]) for s in json.loads(out)["streams"]}
    assert abs(durs["video"] - 20) < 0.1 and abs(durs["audio"] - 20) < 0.1, durs
