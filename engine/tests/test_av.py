"""Audio and video family: every SPEC.md conversion and tool, checked for real.

The inputs are generated at run time with ffmpeg's lavfi sources (``testsrc``
and ``sine``), so the repo carries no media fixtures, and the outputs are
verified with ffprobe or by decoding them back to raw samples: a test asserts
the duration, the streams, the frame size, the loudness or the pixels, never
just "ffmpeg exited 0".

The data tests (what is registered, what the wheel shows, the param parsing)
run anywhere. The tests that actually encode are skipped when ffmpeg is not
installed, which is the only thing a machine without it can honestly report.
"""

from __future__ import annotations

import array
import json
import math
import os
import shutil
import subprocess
import sys
from pathlib import Path

import pytest

from peel_engine import backends
from peel_engine.families import av
from peel_engine.registry import CONVERT, SAME, TOOL, load_registry
from peel_engine.result import InvalidParams

ENGINE_DIR = Path(__file__).resolve().parents[1]

HAVE_FFMPEG = backends.available("ffmpeg") and backends.available("ffprobe")
needs_ffmpeg = pytest.mark.skipif(not HAVE_FFMPEG, reason="ffmpeg and ffprobe are not installed")


# --- helpers ----------------------------------------------------------------


def _ffmpeg_bytes(args: list[str]) -> bytes:
    """Run ffmpeg and return its stdout. Used to decode an output for checking."""
    proc = subprocess.run(
        [str(backends.find("ffmpeg")), "-hide_banner", "-loglevel", "error", "-nostdin", "-y", *args],
        capture_output=True,
        timeout=180,
    )
    assert proc.returncode == 0, proc.stderr.decode("utf-8", "replace")[-500:]
    return proc.stdout


def _ffmpeg_log(args: list[str]) -> str:
    proc = subprocess.run(
        [str(backends.find("ffmpeg")), "-hide_banner", "-nostdin", "-y", *args],
        capture_output=True,
        text=True,
        timeout=180,
    )
    return proc.stderr


def probe(path: Path) -> dict:
    proc = subprocess.run(
        [
            str(backends.find("ffprobe")), "-v", "error", "-show_format",
            "-show_streams", "-of", "json", str(path),
        ],
        capture_output=True,
        text=True,
        timeout=120,
    )
    assert proc.returncode == 0, proc.stderr[-400:]
    return json.loads(proc.stdout)


def streams(path: Path, kind: str) -> list[dict]:
    return [s for s in probe(path).get("streams", []) if s.get("codec_type") == kind]


def codec(path: Path, kind: str) -> str:
    found = streams(path, kind)
    assert found, f"{path.name} has no {kind} stream"
    return found[0]["codec_name"]


def duration(path: Path) -> float:
    info = probe(path)
    values = [info.get("format", {}).get("duration")]
    values += [s.get("duration") for s in info.get("streams", [])]
    best = 0.0
    for value in values:
        try:
            best = max(best, float(value))
        except (TypeError, ValueError):
            continue
    return best


def frame_size(path: Path) -> tuple[int, int]:
    stream = streams(path, "video")[0]
    return int(stream["width"]), int(stream["height"])


def tags(path: Path) -> dict[str, str]:
    found = {k.lower(): v for k, v in (probe(path).get("format", {}).get("tags") or {}).items()}
    for stream in probe(path).get("streams", []):
        for key, value in (stream.get("tags") or {}).items():
            found.setdefault(key.lower(), value)
    return found


def loudness(path: Path) -> float:
    """Integrated loudness in LUFS, measured by ffmpeg itself."""
    log = _ffmpeg_log(["-i", str(path), "-af", "loudnorm=print_format=json", "-f", "null", os.devnull])
    start, end = log.rfind("{"), log.rfind("}")
    assert start != -1 and end > start, log[-400:]
    return float(json.loads(log[start : end + 1])["input_i"])


def detail(path: Path, at: float, rect: tuple[int, int, int, int]) -> float:
    """Average difference between neighbouring pixels in a region of one frame.

    A blurred region loses its local detail, so this is how a Redact test can
    say the blur landed where it was asked to and nowhere else.
    """
    x, y, w, h = rect
    raw = _ffmpeg_bytes(
        [
            "-ss", f"{at:.3f}", "-i", str(path), "-frames:v", "1",
            "-vf", f"crop={w}:{h}:{x}:{y},format=gray", "-f", "rawvideo", "-",
        ]
    )
    assert len(raw) >= w * h, f"expected {w * h} gray bytes, got {len(raw)}"
    total = 0
    count = 0
    for row in range(h):
        line = raw[row * w : (row + 1) * w]
        for index in range(1, len(line)):
            total += abs(line[index] - line[index - 1])
            count += 1
    return total / max(1, count)


def samples(path: Path, start: float, end: float) -> array.array:
    """A slice of the audio as mono 8 kHz signed 16 bit samples."""
    raw = _ffmpeg_bytes(
        [
            "-ss", f"{start:.3f}", "-i", str(path), "-t", f"{end - start:.3f}",
            "-vn", "-ac", "1", "-ar", "8000", "-f", "s16le", "-",
        ]
    )
    return array.array("h", raw[: len(raw) // 2 * 2])


def mean_abs_diff(left: array.array, right: array.array) -> float:
    size = min(len(left), len(right))
    assert size, "no samples to compare"
    return sum(abs(left[i] - right[i]) for i in range(size)) / size


def generate(path: Path, args: list[str]) -> Path:
    _ffmpeg_bytes(args + [str(path)])
    assert path.exists() and path.stat().st_size > 0
    return path


# --- fixtures ---------------------------------------------------------------


@pytest.fixture(scope="session")
def masters(tmp_path_factory) -> dict[str, Path]:
    """The generated source media, built once for the whole session."""
    if not HAVE_FFMPEG:
        pytest.skip("ffmpeg and ffprobe are not installed")
    base = tmp_path_factory.mktemp("av-masters")
    video_in = ["-f", "lavfi", "-i", "testsrc=size=320x240:rate=20:duration=1.2"]
    tone_in = ["-f", "lavfi", "-i", "sine=frequency=440:duration=1.2"]
    x264 = ["-c:v", "libx264", "-preset", "veryfast", "-crf", "16", "-pix_fmt", "yuv420p"]
    return {
        # crf 16 leaves room for Compress to actually make the file smaller.
        "video": generate(base / "video.mp4", video_in + tone_in + x264 + ["-c:a", "aac", "-shortest"]),
        "small": generate(
            base / "small.mp4",
            ["-f", "lavfi", "-i", "testsrc=size=160x120:rate=10:duration=1"]
            + ["-f", "lavfi", "-i", "sine=frequency=880:duration=1"]
            + x264 + ["-c:a", "aac", "-shortest"],
        ),
        # Noise gives the frame real high frequency detail, which is what makes a
        # blur (Redact) and a bitrate cap (Compress) measurable rather than a
        # guess: a synthetic test pattern alone compresses to almost nothing.
        "noisy": generate(
            base / "noisy.mp4",
            ["-f", "lavfi", "-i", "testsrc=size=320x240:rate=20:duration=1.2,noise=alls=50:allf=t"]
            + tone_in + x264 + ["-c:a", "aac", "-shortest"],
        ),
        "silent": generate(
            base / "silent.mp4",
            ["-f", "lavfi", "-i", "testsrc=size=320x240:rate=20:duration=1"] + x264,
        ),
        # An odd frame size: yuv420p refuses it, so every x264 path has to carry
        # the even size guard to convert this at all.
        "odd": generate(
            base / "odd.gif",
            ["-f", "lavfi", "-i", "testsrc=size=161x121:rate=10:duration=1"],
        ),
        "audio": generate(
            base / "tone.mp3",
            ["-f", "lavfi", "-i", "sine=frequency=440:duration=1.5", "-ac", "2", "-c:a", "libmp3lame"],
        ),
        "wav": generate(
            base / "tone.wav",
            ["-f", "lavfi", "-i", "sine=frequency=440:duration=1.5", "-ac", "2", "-c:a", "pcm_s16le"],
        ),
    }


@pytest.fixture
def media(masters, tmp_path):
    """Copy a master beside the test's own tmp_path, where outputs will land."""

    def _copy(kind: str, name: str | None = None) -> Path:
        source = masters[kind]
        target = tmp_path / (name or source.name)
        shutil.copy2(source, target)
        return target

    return _copy


@pytest.fixture(scope="module")
def real():
    """The real registry, built strictly so a family clash fails the suite."""
    return load_registry(force=True, strict=True)


def run(real, action: str, paths, **params):
    """Run an action and require it to succeed."""
    files = paths if isinstance(paths, list) else [paths]
    result = real.run(action, files, params)
    assert result.ok, f"{action}: {result.message}"
    assert result.outputs, f"{action} reported no outputs"
    for output in result.outputs:
        assert output.exists() and output.stat().st_size > 0, f"{output} is empty"
    return result


def fails(real, action: str, paths, **params):
    """Run an action and require a clean failure, never a traceback."""
    files = paths if isinstance(paths, list) else [paths]
    result = real.run(action, files, params)
    assert not result.ok, f"{action} unexpectedly succeeded: {result.message}"
    assert result.message and "Traceback" not in result.message
    return result


def entries(real, family: str, group: str) -> dict[str, object]:
    return {a.label: a for a in real.all() if a.family == family and a.group == group}


# --- what is registered -----------------------------------------------------


def test_the_audio_row_converts_to_every_other_audio_format(real):
    for ext in av.AUDIO:
        action = real.get(f"av.audio_to_{ext}")
        assert action.family == CONVERT and action.group == "Audio"
        assert action.output_ext == ext
        # Every other format of the row is an input, and the format itself is
        # not: the wheel never offers MP3 for an MP3.
        assert set(action.inputs) == set(av.AUDIO) - {ext}


def test_the_video_row_converts_to_every_other_video_format_plus_mp3(real):
    for ext in av.VIDEO:
        action = real.get(f"av.video_to_{ext}")
        assert action.family == CONVERT and action.group == "Video"
        assert action.output_ext == ext
        assert set(action.inputs) == set(av.VIDEO) - {ext}
    extract = real.get("av.video_to_mp3")
    assert extract.output_ext == "mp3" and extract.group == "Video"
    assert set(extract.inputs) == set(av.VIDEO) - {"gif"}


def test_the_spec_lists_these_video_tools_and_so_does_the_registry(real):
    found = entries(real, TOOL, "Video")
    assert set(found) == {
        "Compress", "Crop", "Trim", "Split", "Join", "Speed",
        "Remove Audio", "Redact", "Snapshot",
    }
    for label, action in found.items():
        assert action.output_ext in (SAME, "png"), label


def test_the_spec_lists_these_audio_tools_and_so_does_the_registry(real):
    found = entries(real, TOOL, "Audio")
    assert set(found) == {
        "Compress", "Trim", "Normalize", "Bleep", "Channels", "Visualize", "Metadata",
    }


def test_every_av_entry_needs_ffmpeg_and_resolves_its_runner(real):
    av_entries = [a for a in real.all() if a.id.startswith("av.")]
    assert len(av_entries) == 32
    for action in av_entries:
        assert action.backends == ("ffmpeg", "ffprobe"), action.id
        assert callable(action.resolve_runner()), action.id


def test_the_whole_family_disappears_when_ffmpeg_is_missing(real, make_file, fake_backends):
    fake_backends(ffmpeg=False, ffprobe=False)
    visible = real.options([make_file("clip.mp4")])
    listed = {
        e["id"]
        for wheel in visible["wheels"]
        for group in wheel["groups"]
        for e in group["entries"]
    }
    assert not {i for i in listed if i.startswith("av.")}

    asked = real.options([make_file("clip2.mp4")], include_unavailable=True)
    entry = next(
        e
        for wheel in asked["wheels"]
        for group in wheel["groups"]
        for e in group["entries"]
        if e["id"] == "av.video_compress"
    )
    assert entry["available"] is False
    assert set(entry["missing_backends"]) == {"ffmpeg", "ffprobe"}


def test_a_tool_that_opens_an_editor_takes_one_file_and_the_rest_batch(real):
    for action in real.all():
        if not action.id.startswith("av."):
            continue
        if action.needs_editor:
            assert action.max_files == 1, f"{action.id} opens an editor, so it is one file"
        elif action.mode == "each":
            assert action.max_files is None, f"{action.id} should accept a batch"


def test_a_gif_is_a_conversion_input_but_not_a_tool_input(real, make_file):
    gif = make_file("loop.gif")
    wheels = {w["family"]: w for w in real.options([gif])["wheels"]}
    conversions = {
        e["label"] for g in wheels[CONVERT]["groups"] if g["group"] == "Video" for e in g["entries"]
    }
    assert "MP4" in conversions
    video_tools = [g for g in wheels.get(TOOL, {"groups": []})["groups"] if g["group"] == "Video"]
    assert video_tools == []


def test_the_wheel_never_shows_one_label_twice_for_a_video(real, make_file):
    for wheel in real.options([make_file("clip.mp4")])["wheels"]:
        for group in wheel["groups"]:
            labels = [e["label"] for e in group["entries"]]
            assert len(labels) == len(set(labels)), (wheel["family"], group["group"], labels)


def test_the_real_registry_still_loads_with_no_warnings():
    assert load_registry(force=True).warnings == []


# --- param parsing (no encoding needed) -------------------------------------


@pytest.mark.parametrize("factor", [0.25, 0.5, 0.75, 1.0, 1.5, 2.0, 3.0, 4.0])
def test_the_atempo_chain_multiplies_out_to_the_asked_speed(factor):
    product = 1.0
    for stage in av._atempo(factor).split(","):
        value = float(stage.split("=")[1])
        assert 0.5 <= value <= 2.0, "atempo only spans 0.5x to 2x per stage"
        product *= value
    assert product == pytest.approx(factor, rel=1e-4)


def test_ranges_accept_both_shapes_and_merge_what_overlaps():
    assert av._ranges(["0:01-0:02"], 10) == [(1.0, 2.0)]
    assert av._ranges([[0.5, 1.0], [0.9, 1.4]], 10) == [(0.5, 1.4)]
    assert av._ranges(["3-4", "1-2"], 10) == [(1.0, 2.0), (3.0, 4.0)]
    # A range past the end is clamped, not an error.
    assert av._ranges(["1-30"], 10) == [(1.0, 10.0)]


@pytest.mark.parametrize("bad", [["nonsense"], ["2-1"], ["1-2-3"], ["20-30"]])
def test_a_bad_range_is_a_message_not_a_crash(bad):
    with pytest.raises(InvalidParams):
        av._ranges(bad, 10)


def test_cut_points_come_from_timestamps_or_an_interval():
    assert av._cut_points({"at": ["0:01"], "every": None}, 4) == [(0.0, 1.0), (1.0, 4.0)]
    assert av._cut_points({"at": [], "every": 2}, 6) == [(0.0, 2.0), (2.0, 4.0), (4.0, 6.0)]
    # Timestamps win when both are given, and a repeat is one cut.
    assert av._cut_points({"at": [1, 1], "every": 0.5}, 2) == [(0.0, 1.0), (1.0, 2.0)]


@pytest.mark.parametrize(
    "params", [{"at": [], "every": None}, {"at": [9], "every": None}, {"at": [0], "every": None}]
)
def test_a_split_that_would_cut_nothing_says_so(params):
    with pytest.raises(InvalidParams):
        av._cut_points(params, 4)


# --- conversions ------------------------------------------------------------


@needs_ffmpeg
@pytest.mark.parametrize(
    "ext,expect",
    [
        ("wav", "pcm_s16le"), ("flac", "flac"), ("ogg", "vorbis"), ("opus", "opus"),
        ("aiff", "pcm_s16be"), ("wma", "wmav2"), ("m4a", "aac"),
    ],
)
def test_every_audio_conversion_writes_a_playable_file(real, media, ext, expect):
    source = media("audio")
    result = run(real, f"av.audio_to_{ext}", source)
    target = result.outputs[0]
    assert target.name == f"tone.{ext}"
    assert codec(target, "audio") == expect
    assert duration(target) == pytest.approx(duration(source), abs=0.15)


@needs_ffmpeg
def test_an_mp3_is_reachable_from_every_other_audio_format(real, media):
    """The row converts in both directions, not only away from MP3."""
    flac = run(real, "av.audio_to_flac", media("audio")).outputs[0]
    back = run(real, "av.audio_to_mp3", flac).outputs[0]
    assert codec(back, "audio") == "mp3"
    assert duration(back) == pytest.approx(duration(flac), abs=0.15)


@needs_ffmpeg
@pytest.mark.parametrize(
    "ext,expect", [("mov", "h264"), ("mkv", "h264"), ("webm", "vp9"), ("avi", "mpeg4"), ("wmv", "wmv2")]
)
def test_every_video_conversion_keeps_both_streams(real, media, ext, expect):
    source = media("video")
    target = run(real, f"av.video_to_{ext}", source).outputs[0]
    assert target.name == f"video.{ext}"
    assert codec(target, "video") == expect
    assert streams(target, "audio"), f"{ext} lost the audio track"
    assert frame_size(target) == (320, 240)
    assert duration(target) == pytest.approx(duration(source), abs=0.25)


@needs_ffmpeg
def test_a_gif_conversion_honours_its_fps_and_width(real, media):
    source = media("video")
    target = run(real, "av.video_to_gif", source, fps=5, width=160).outputs[0]
    assert target.suffix == ".gif"
    assert codec(target, "video") == "gif"
    assert frame_size(target) == (160, 120)
    # 1.2s at 5 fps is 6 frames, give or take the last one.
    frames = int(streams(target, "video")[0].get("nb_frames") or 0)
    assert 4 <= frames <= 7, frames
    assert not streams(target, "audio")


@needs_ffmpeg
def test_a_gif_with_an_odd_frame_size_still_converts_to_mp4(real, media):
    """yuv420p rejects an odd width or height, so this only passes with the
    even size guard in the filter chain."""
    target = run(real, "av.video_to_mp4", media("odd")).outputs[0]
    assert frame_size(target) == (160, 120)
    assert codec(target, "video") == "h264"


@needs_ffmpeg
def test_video_to_mp3_extracts_the_audio(real, media):
    source = media("video")
    target = run(real, "av.video_to_mp3", source).outputs[0]
    assert target.name == "video.mp3"
    assert codec(target, "audio") == "mp3"
    assert not streams(target, "video")
    assert duration(target) == pytest.approx(duration(source), abs=0.2)


@needs_ffmpeg
def test_extracting_audio_from_a_silent_video_says_what_is_wrong(real, media):
    result = fails(real, "av.video_to_mp3", media("silent"))
    assert result.code == "no_audio"
    assert "no audio" in result.message


@needs_ffmpeg
def test_a_file_that_is_not_media_is_rejected_without_leaving_an_output(real, tmp_path):
    fake = tmp_path / "notreally.mp3"
    fake.write_text("this is a text file wearing an mp3 extension\n", encoding="utf-8")
    result = fails(real, "av.audio_to_wav", fake)
    assert result.code == "unreadable_media"
    assert not (tmp_path / "notreally.wav").exists(), "a failed run left a claim behind"


# --- video tools ------------------------------------------------------------


@needs_ffmpeg
def test_compress_makes_the_file_smaller_and_keeps_it_playable(real, media):
    source = media("video")
    before = source.stat().st_size
    target = run(real, "av.video_compress", source, quality="low").outputs[0]
    assert target.name == "video (Compress).mp4"
    assert target.stat().st_size < before
    assert codec(target, "video") == "h264" and streams(target, "audio")
    assert duration(target) == pytest.approx(duration(source), abs=0.25)


@needs_ffmpeg
@pytest.mark.parametrize("ext", ["mp4", "webm"])
def test_compress_to_a_target_size_lands_near_that_size(real, tmp_path, ext):
    """Five seconds of noise: long enough for rate control to settle, which a
    one second clip is not. 1 MB is 1,000,000 bytes (the param help says so)."""
    codecs = {
        "mp4": ["-c:v", "libx264", "-preset", "veryfast", "-crf", "16", "-c:a", "aac"],
        "webm": ["-c:v", "libvpx-vp9", "-deadline", "realtime", "-crf", "10", "-b:v", "0",
                 "-c:a", "libopus"],
    }[ext]
    source = generate(
        tmp_path / f"noise5.{ext}",
        ["-f", "lavfi", "-i", "testsrc=size=320x240:rate=20:duration=5,noise=alls=50:allf=t",
         "-f", "lavfi", "-i", "sine=frequency=440:duration=5", "-pix_fmt", "yuv420p",
         *codecs, "-shortest"],
    )
    target_mb = 0.15
    target = run(real, "av.video_compress", source, target_mb=target_mb).outputs[0]
    assert target.stat().st_size < source.stat().st_size
    wanted = target_mb * 1_000_000
    assert abs(target.stat().st_size - wanted) <= wanted * 0.10, target.stat().st_size


@needs_ffmpeg
@pytest.mark.parametrize("ext", ["avi", "wmv"])
def test_compress_to_a_target_size_never_overshoots_on_the_legacy_encoders(real, media, ext):
    """mpeg4 and wmv2 ran 1.5x to 2x over a one pass bitrate; two pass fixes it."""
    source = run(real, f"av.video_to_{ext}", media("noisy")).outputs[0]
    target_mb = 0.05
    target = run(real, "av.video_compress", source, target_mb=target_mb).outputs[0]
    assert target.stat().st_size <= target_mb * 1_000_000 * 1.10, target.stat().st_size


def test_the_target_size_param_states_its_unit():
    entry = load_registry(force=True).get("av.video_compress")
    param = next(p for p in entry.params if p.name == "target_mb")
    assert "1,000,000 bytes" in (param.help or "")


@needs_ffmpeg
def test_crop_cuts_the_frame_to_the_asked_rectangle(real, media):
    target = run(real, "av.video_crop", media("video"), x=10, y=20, w=100, h=80).outputs[0]
    assert target.name == "video (Crop).mp4"
    assert frame_size(target) == (100, 80)


@needs_ffmpeg
def test_an_odd_crop_is_rounded_down_rather_than_failing_the_encode(real, media):
    target = run(real, "av.video_crop", media("video"), x=0, y=0, w=101, h=81).outputs[0]
    assert frame_size(target) == (100, 80)


@needs_ffmpeg
@pytest.mark.parametrize(
    "params", [{"x": 300, "y": 0, "w": 100, "h": 80}, {"x": 0, "y": 0, "w": 1, "h": 1}]
)
def test_a_crop_outside_the_frame_is_refused_with_the_numbers(real, media, params):
    result = fails(real, "av.video_crop", media("video"), **params)
    assert result.code == "invalid_params"


@needs_ffmpeg
def test_trim_keeps_only_the_asked_span(real, media):
    target = run(real, "av.video_trim", media("video"), start=0.3, end=0.9).outputs[0]
    assert target.name == "video (Trim).mp4"
    assert duration(target) == pytest.approx(0.6, abs=0.15)


@needs_ffmpeg
def test_trim_with_no_end_runs_to_the_end_of_the_file(real, media):
    source = media("video")
    target = run(real, "av.video_trim", source, start=0.5).outputs[0]
    assert duration(target) == pytest.approx(duration(source) - 0.5, abs=0.2)


@needs_ffmpeg
@pytest.mark.parametrize("params", [{"start": 0.9, "end": 0.4}, {"start": 5.0}, {"start": 1, "end": 1}])
def test_a_backwards_or_out_of_range_trim_is_refused(real, media, params):
    result = fails(real, "av.video_trim", media("video"), **params)
    assert result.code == "invalid_params"


@needs_ffmpeg
def test_split_at_timestamps_writes_one_numbered_part_per_segment(real, media):
    source = media("video")
    result = run(real, "av.video_split", source, at=[0.4, 0.8])
    assert [p.name for p in result.outputs] == [
        "video (Split 1).mp4", "video (Split 2).mp4", "video (Split 3).mp4",
    ]
    assert duration(result.outputs[0]) == pytest.approx(0.4, abs=0.15)
    assert duration(result.outputs[1]) == pytest.approx(0.4, abs=0.15)
    total = sum(duration(p) for p in result.outputs)
    assert total == pytest.approx(duration(source), abs=0.3)


@needs_ffmpeg
def test_split_every_n_seconds_covers_the_whole_file(real, media):
    source = media("video")
    result = run(real, "av.video_split", source, every=0.5)
    assert len(result.outputs) == 3
    assert sum(duration(p) for p in result.outputs) == pytest.approx(duration(source), abs=0.3)


@needs_ffmpeg
def test_join_copies_the_streams_when_the_clips_match(real, media):
    first = media("video", "a.mp4")
    second = media("video", "b.mp4")
    target = run(real, "av.video_join", [first, second]).outputs[0]
    assert target.name == "a (Join).mp4"
    assert duration(target) == pytest.approx(duration(first) * 2, abs=0.25)
    assert frame_size(target) == (320, 240) and streams(target, "audio")


@needs_ffmpeg
def test_join_works_on_a_container_that_may_refuse_a_stream_copy(real, media):
    """WMV is the case where the copy path can be refused, and the runner has to
    fall back to a re-encode instead of reporting a failure."""
    first = run(real, "av.video_to_wmv", media("video", "a.mp4")).outputs[0]
    second = run(real, "av.video_to_wmv", media("video", "b.mp4")).outputs[0]
    target = run(real, "av.video_join", [first, second]).outputs[0]
    assert target.name == "a (Join).wmv"
    assert duration(target) == pytest.approx(duration(first) + duration(second), abs=0.4)
    assert streams(target, "video")


@needs_ffmpeg
def test_join_re_encodes_to_the_first_clip_when_the_clips_differ(real, media):
    first = media("video", "a.mp4")
    second = media("small", "b.mp4")
    target = run(real, "av.video_join", [first, second]).outputs[0]
    assert frame_size(target) == (320, 240), "the smaller clip should be padded, not win"
    assert duration(target) == pytest.approx(duration(first) + duration(second), abs=0.35)
    assert streams(target, "audio")


@needs_ffmpeg
def test_join_with_a_silent_clip_keeps_the_audio_of_the_others(real, media):
    """The silent clip gets a silent track of its own length, so the tone of
    the first clip survives and the second half is quiet."""
    first = media("video", "a.mp4")
    second = media("silent", "b.mp4")
    result = run(real, "av.video_join", [first, second])
    target = result.outputs[0]
    assert streams(target, "audio"), "one silent clip must not strip the whole join"
    assert "silence" in result.message
    assert duration(target) == pytest.approx(duration(first) + duration(second), abs=0.35)
    loud = samples(target, 0.2, 0.9)
    quiet = samples(target, duration(first) + 0.2, duration(first) + 0.8)
    assert max(abs(v) for v in loud) > 1000
    assert max(abs(v) for v in quiet) < 50


@needs_ffmpeg
def test_join_of_clips_that_all_lack_audio_is_silent(real, media):
    result = run(real, "av.video_join", [media("silent", "a.mp4"), media("small", "b.mp4")])
    assert streams(result.outputs[0], "audio"), "the second clip has audio"
    result = run(real, "av.video_join", [media("silent", "c.mp4"), media("silent", "d.mp4")])
    assert not streams(result.outputs[0], "audio")


@needs_ffmpeg
@pytest.mark.parametrize("factor,ratio", [(2.0, 0.5), (0.5, 2.0)])
def test_speed_changes_the_duration_and_keeps_the_audio(real, media, factor, ratio):
    source = media("video")
    target = run(real, "av.video_speed", source, factor=factor).outputs[0]
    expected = duration(source) * ratio
    assert duration(target) == pytest.approx(expected, rel=0.2)
    assert streams(target, "audio"), "speed must bring the audio along"


@needs_ffmpeg
def test_remove_audio_drops_the_track_and_copies_the_video(real, media):
    source = media("video")
    target = run(real, "av.video_mute", source).outputs[0]
    assert target.name == "video (Remove Audio).mp4"
    assert not streams(target, "audio")
    assert frame_size(target) == (320, 240)
    assert duration(target) == pytest.approx(duration(source), abs=0.2)


@needs_ffmpeg
def test_removing_audio_from_a_silent_video_says_there_is_none(real, media):
    assert fails(real, "av.video_mute", media("silent")).code == "no_audio"


@needs_ffmpeg
def test_redact_blurs_the_region_and_leaves_the_rest_sharp(real, media):
    source = media("noisy")
    rect = (0, 0, 120, 120)
    target = run(real, "av.video_redact", source, x=0, y=0, w=120, h=120, strength=15).outputs[0]
    assert target.name == "noisy (Redact).mp4"
    assert frame_size(target) == (320, 240)
    inside_before = detail(source, 0.5, rect)
    inside_after = detail(target, 0.5, rect)
    assert inside_after < inside_before / 2, (inside_before, inside_after)
    outside = (200, 120, 100, 100)
    assert detail(target, 0.5, outside) > detail(target, 0.5, rect)


@needs_ffmpeg
def test_a_redact_with_a_time_range_only_blurs_inside_it(real, media):
    source = media("noisy")
    rect = (0, 0, 120, 120)
    target = run(
        real, "av.video_redact", source, x=0, y=0, w=120, h=120, strength=15, start=0.6, end=1.2
    ).outputs[0]
    assert detail(target, 0.8, rect) < detail(target, 0.1, rect) / 2


def _rotated(media, name: str = "phone.mp4") -> Path:
    """The 320x240 master with a display matrix that says rotate 90: a phone
    clip, shown as 240x320."""
    source = media("video", "landscape.mp4")
    target = source.parent / name
    _ffmpeg_bytes(["-display_rotation", "90", "-i", str(source), "-c", "copy", str(target)])
    return target


def _rotation_of(path: Path):
    for stream in streams(path, "video"):
        for side in stream.get("side_data_list") or []:
            if "rotation" in side:
                return side["rotation"]
    return None


@needs_ffmpeg
def test_crop_on_a_phone_clip_uses_the_displayed_frame(real, media):
    source = _rotated(media)
    target = run(real, "av.video_crop", source, x=20, y=200, w=200, h=100).outputs[0]
    assert frame_size(target) == (200, 100)
    assert not _rotation_of(target), "the crop is already upright"
    # A rect that fits the coded 320x240 frame but not the displayed 240x320.
    assert fails(real, "av.video_crop", source, x=0, y=0, w=300, h=200).code == "invalid_params"


@needs_ffmpeg
def test_join_of_a_phone_clip_and_a_landscape_clip_is_upright(real, media):
    phone = _rotated(media)
    flat = media("video", "flat.mp4")
    target = run(real, "av.video_join", [phone, flat]).outputs[0]
    assert frame_size(target) == (240, 320), "re-encoded to the first clip's displayed shape"
    assert not _rotation_of(target)
    assert duration(target) == pytest.approx(duration(phone) + duration(flat), abs=0.35)


@needs_ffmpeg
def test_join_of_two_phone_clips_keeps_their_shared_rotation(real, media):
    first, second = _rotated(media, "p1.mp4"), _rotated(media, "p2.mp4")
    target = run(real, "av.video_join", [first, second]).outputs[0]
    assert abs(_rotation_of(target) or 0) == 90


@needs_ffmpeg
def test_snapshot_reports_only_whole_png_frames(real, media, tmp_path):
    """Audio runs 1.5s past a 0.25s video: frames come from the video only."""
    source = generate(
        tmp_path / "talk.mp4",
        ["-f", "lavfi", "-i", "sine=duration=1.75", "-f", "lavfi",
         "-i", "testsrc=size=160x120:rate=20:duration=0.25",
         "-map", "0:a", "-map", "1:v", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac"],
    )
    result = run(real, "av.video_snapshot", source, every=0.1)
    assert len(result.outputs) == 3
    for output in result.outputs:
        assert av._is_png(output) and frame_size(output) == (160, 120)
    assert not [p for p in tmp_path.glob("*.png") if p.stat().st_size == 0]
    single = run(real, "av.video_snapshot", source, time=0.24).outputs[0]
    assert av._is_png(single)


@needs_ffmpeg
def test_split_cuts_on_the_video_track_and_keeps_the_audio_tail(real, media, tmp_path):
    source = generate(
        tmp_path / "talk.mp4",
        ["-f", "lavfi", "-i", "sine=duration=2", "-f", "lavfi",
         "-i", "testsrc=size=160x120:rate=20:duration=1",
         "-map", "0:a", "-map", "1:v", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac"],
    )
    result = run(real, "av.video_split", source, every=0.4)
    assert len(result.outputs) == 3, [p.name for p in result.outputs]
    for output in result.outputs:
        assert streams(output, "video"), f"{output.name} has no video"
    assert duration(result.outputs[-1]) == pytest.approx(2.0 - 0.8, abs=0.2)


@needs_ffmpeg
def test_snapshot_saves_one_frame_at_the_asked_time(real, media):
    target = run(real, "av.video_snapshot", media("video"), time=0.5).outputs[0]
    assert target.name == "video (Snapshot).png"
    assert codec(target, "video") == "png"
    assert frame_size(target) == (320, 240)


@needs_ffmpeg
def test_snapshot_every_n_seconds_numbers_its_frames(real, media):
    result = run(real, "av.video_snapshot", media("video"), every=0.5)
    assert [p.name for p in result.outputs] == [
        "video (Snapshot 1).png", "video (Snapshot 2).png", "video (Snapshot 3).png",
    ]
    for output in result.outputs:
        assert codec(output, "video") == "png"


@needs_ffmpeg
def test_a_snapshot_past_the_end_of_the_file_is_refused(real, media):
    assert fails(real, "av.video_snapshot", media("video"), time=30).code == "invalid_params"


# --- audio tools ------------------------------------------------------------


@needs_ffmpeg
def test_audio_compress_drops_the_bitrate_and_the_size(real, media):
    source = media("audio")
    target = run(real, "av.audio_compress", source, bitrate=48).outputs[0]
    assert target.name == "tone (Compress).mp3"
    assert target.stat().st_size < source.stat().st_size
    assert codec(target, "audio") == "mp3"
    assert duration(target) == pytest.approx(duration(source), abs=0.15)


@needs_ffmpeg
def test_compressing_a_lossless_file_keeps_it_lossless(real, media):
    """WAV and FLAC have no bitrate to turn down, so Compress stays in the
    format instead of silently handing back a lossy file."""
    wav = run(real, "av.audio_compress", media("wav"), bitrate=48).outputs[0]
    assert codec(wav, "audio") == "pcm_s16le"
    assert int(streams(wav, "audio")[0]["sample_rate"]) <= 44100
    flac = run(real, "av.audio_to_flac", media("audio")).outputs[0]
    smaller = run(real, "av.audio_compress", flac, bitrate=48).outputs[0]
    assert codec(smaller, "audio") == "flac"


@needs_ffmpeg
def test_audio_trim_keeps_only_the_asked_span(real, media):
    target = run(real, "av.audio_trim", media("audio"), start=0.2, end=0.9).outputs[0]
    assert target.name == "tone (Trim).mp3"
    assert duration(target) == pytest.approx(0.7, abs=0.12)


@needs_ffmpeg
def test_normalize_hits_the_target_loudness_and_keeps_the_sample_rate(real, media):
    source = media("audio")
    before = loudness(source)
    target = run(real, "av.audio_normalize", source, target=-16.0).outputs[0]
    assert target.name == "tone (Normalize).mp3"
    assert loudness(target) == pytest.approx(-16.0, abs=1.5), f"was {before} LUFS"
    # loudnorm works at 192 kHz internally; without the resample back, this is
    # where libmp3lame would have refused the stream.
    assert streams(target, "audio")[0]["sample_rate"] == streams(source, "audio")[0]["sample_rate"]


@needs_ffmpeg
def test_normalizing_silence_says_there_is_nothing_to_normalize(real, tmp_path):
    silent = generate(
        tmp_path / "quiet.wav",
        ["-f", "lavfi", "-i", "anullsrc=r=44100:cl=stereo", "-t", "1", "-c:a", "pcm_s16le"],
    )
    result = fails(real, "av.audio_normalize", silent)
    assert result.code == "silent"


@needs_ffmpeg
def test_bleep_replaces_the_audio_inside_the_range_and_nowhere_else(real, media):
    source = media("wav")
    target = run(real, "av.audio_bleep", source, ranges=["0.5-0.9"], frequency=1000).outputs[0]
    assert target.name == "tone (Bleep).wav"
    assert duration(target) == pytest.approx(duration(source), abs=0.15)
    inside = mean_abs_diff(samples(source, 0.55, 0.85), samples(target, 0.55, 0.85))
    outside = mean_abs_diff(samples(source, 0.0, 0.4), samples(target, 0.0, 0.4))
    assert inside > 10 * max(outside, 1.0), (inside, outside)


@needs_ffmpeg
def test_bleep_takes_several_ranges_at_once(real, media):
    source = media("wav")
    result = run(real, "av.audio_bleep", source, ranges=["0.2-0.4", "0.9-1.1"])
    assert "2 bleeped ranges" in result.message
    for start, end in ((0.25, 0.35), (0.95, 1.05)):
        assert mean_abs_diff(samples(source, start, end), samples(result.outputs[0], start, end)) > 50


@needs_ffmpeg
@pytest.mark.parametrize("wanted,count", [("mono", 1), ("stereo", 2)])
def test_channels_sets_the_channel_count(real, media, wanted, count):
    target = run(real, "av.audio_channels", media("audio"), channels=wanted).outputs[0]
    assert int(streams(target, "audio")[0]["channels"]) == count


@needs_ffmpeg
def test_visualize_writes_a_waveform_video_that_still_has_the_audio(real, media):
    source = media("audio")
    target = run(real, "av.audio_visualize", source, width=320, height=120).outputs[0]
    assert target.name == "tone (Visualize).mp4"
    assert frame_size(target) == (320, 120)
    assert codec(target, "video") == "h264" and streams(target, "audio")
    assert duration(target) == pytest.approx(duration(source), abs=0.3)


@needs_ffmpeg
def test_metadata_writes_the_fields_it_was_given(real, media):
    target = run(
        real, "av.audio_metadata", media("audio"), title="Peel Test", artist="HQ"
    ).outputs[0]
    assert target.name == "tone (Metadata).mp3"
    written = tags(target)
    assert written.get("title") == "Peel Test" and written.get("artist") == "HQ"
    assert duration(target) == pytest.approx(duration(media("audio")), abs=0.15)


@needs_ffmpeg
def test_metadata_with_nothing_to_write_says_so(real, media):
    assert fails(real, "av.audio_metadata", media("audio")).code == "invalid_params"


# --- naming, batching and progress ------------------------------------------


@needs_ffmpeg
def test_outputs_land_next_to_the_source_and_never_overwrite(real, media, tmp_path):
    source = media("audio")
    first = run(real, "av.audio_to_wav", source).outputs[0]
    second = run(real, "av.audio_to_wav", source).outputs[0]
    assert first.parent == tmp_path and first.name == "tone.wav"
    assert second.name == "tone 2.wav"
    assert first.read_bytes() and first.stat().st_size > 0


@needs_ffmpeg
def test_a_batch_converts_every_file_it_was_handed(real, media):
    files = [media("audio", "one.mp3"), media("audio", "two.mp3")]
    result = run(real, "av.audio_to_wav", files)
    assert sorted(p.name for p in result.outputs) == ["one.wav", "two.wav"]


@needs_ffmpeg
def test_a_batch_keeps_the_good_files_when_one_is_not_media(real, media, tmp_path):
    bad = tmp_path / "broken.mp3"
    bad.write_text("not audio", encoding="utf-8")
    result = real.run("av.audio_to_wav", [media("audio", "good.mp3"), bad], {})
    assert not result.ok and result.code == "partial"
    assert [p.name for p in result.outputs] == ["good.wav"]
    assert not (tmp_path / "broken.wav").exists()


@needs_ffmpeg
def test_progress_is_reported_on_stderr_as_it_runs(real, media, capsys):
    run(real, "av.audio_to_wav", media("audio"))
    lines = [
        line for line in capsys.readouterr().err.splitlines()
        if line.startswith(av.PROGRESS_PREFIX)
    ]
    assert lines, "no progress was reported"
    reports = [json.loads(line[len(av.PROGRESS_PREFIX):]) for line in lines]
    for report in reports:
        assert report["action"] == "av.audio_to_wav"
        assert report["file"] == "tone.mp3"
        assert 0 <= report["percent"] <= 100
        assert report["steps"] == 1
    percents = [r["percent"] for r in reports]
    assert percents == sorted(percents), percents
    assert percents[-1] == 100.0


@needs_ffmpeg
def test_a_two_pass_job_reports_one_bar_over_both_passes(real, media, capsys):
    run(real, "av.video_to_gif", media("video"), fps=5, width=160)
    reports = [
        json.loads(line[len(av.PROGRESS_PREFIX):])
        for line in capsys.readouterr().err.splitlines()
        if line.startswith(av.PROGRESS_PREFIX)
    ]
    assert {r["steps"] for r in reports} == {2}
    assert {r["step"] for r in reports} == {1, 2}
    percents = [r["percent"] for r in reports]
    assert percents == sorted(percents) and percents[-1] == 100.0
    assert max(p for p, r in zip(percents, reports) if r["step"] == 1) <= 50.0


@needs_ffmpeg
def test_normalize_reports_both_of_its_passes(real, media, capsys):
    """The measure pass is half the work, so it belongs on the same bar."""
    run(real, "av.audio_normalize", media("audio"))
    reports = [
        json.loads(line[len(av.PROGRESS_PREFIX):])
        for line in capsys.readouterr().err.splitlines()
        if line.startswith(av.PROGRESS_PREFIX)
    ]
    assert {r["steps"] for r in reports} == {2}
    assert {r["step"] for r in reports} == {1, 2}
    assert reports[-1]["percent"] == 100.0


@needs_ffmpeg
def test_the_cli_keeps_the_json_on_stdout_and_the_progress_on_stderr(media):
    """The contract the shell depends on: parse stdout, watch stderr for a bar."""
    source = media("audio")
    proc = subprocess.run(
        [sys.executable, "-m", "peel_engine.cli", "run", "av.audio_to_wav", str(source)],
        cwd=ENGINE_DIR,
        capture_output=True,
        text=True,
        timeout=180,
        env={**os.environ, "PYTHONPATH": str(ENGINE_DIR)},
    )
    assert proc.returncode == 0, proc.stderr[-400:]
    payload = json.loads(proc.stdout)
    assert payload["ok"] is True and payload["outputs"]
    assert Path(payload["outputs"][0]).exists()
    progress = [l for l in proc.stderr.splitlines() if l.startswith(av.PROGRESS_PREFIX)]
    assert progress and json.loads(progress[-1][len(av.PROGRESS_PREFIX):])["percent"] == 100.0


@needs_ffmpeg
def test_a_failed_run_leaves_no_half_written_output(real, media, tmp_path):
    """ffmpeg dying mid encode must not leave a stub beside the source."""
    source = media("video")
    truncated = tmp_path / "cut.mp4"
    truncated.write_bytes(source.read_bytes()[: 1024])
    result = real.run("av.video_to_webm", [truncated], {})
    assert not result.ok
    assert not (tmp_path / "cut.webm").exists()
    assert not any(p.name.startswith("cut 2") for p in tmp_path.iterdir())


def test_progress_percent_is_null_when_the_duration_is_unknown(capsys):
    """A stream with no duration still reports, so the shell can show a spinner."""

    class _Action:
        id = "av.demo"

    class _Job:
        action = _Action()
        source = Path("mystery.mkv")

    progress = av._Progress(_Job(), 0.0)
    progress.feed("out_time_us=500000\n")
    progress.feed("progress=end\n")
    reports = [
        json.loads(line[len(av.PROGRESS_PREFIX):])
        for line in capsys.readouterr().err.splitlines()
        if line.startswith(av.PROGRESS_PREFIX)
    ]
    assert reports and all(r["percent"] is None for r in reports)
    assert reports[0]["seconds"] == 0.5
