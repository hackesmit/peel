"""Audio and video: every SPEC.md Audio/Video conversion and tool, via ffmpeg.

One module, one backend pair (ffmpeg plus ffprobe), 32 registry entries. The
shell never learns a codec: it renders what ``peel options`` returns and sends
the params the entry declares.

Three rules shape the code:

* **Containers decide codecs.** ``_VIDEO_CODECS`` and ``_AUDIO_CODECS`` map an
  extension to the encoder that container actually accepts, so a conversion, a
  compress and a trim that all land on ``.wmv`` encode the same way. Nothing
  else in the module branches on a format.
* **Claims are filled in place.** ``job.output_path()`` creates the output as
  an empty placeholder (see the registry output contract), so every ffmpeg call
  gets ``-y``. Two pass work (GIF palettes, loudnorm, concat lists) happens in
  ``job.scratch_dir()`` and never beside the claim.
* **Progress is a side channel.** stdout belongs to the one JSON object the CLI
  prints, so ffmpeg's ``-progress`` stream is parsed and re-emitted on stderr,
  one line per update:

      PEEL-PROGRESS {"action": "av.video_compress", "file": "clip.mp4",
                     "percent": 42.0, "seconds": 1.26, "duration": 3.0,
                     "step": 1, "steps": 2}

  ``percent`` is null when the duration cannot be read, and a multi pass job
  reports its own step so one bar covers the whole run. The shell reads lines
  with the ``PEEL-PROGRESS`` prefix and ignores every other stderr line.

Gotchas that cost a debugging round, kept as code:

* yuv420p needs even dimensions, so ``_EVEN`` is appended to every video filter
  chain. A GIF input (odd height) converts to MP4 only because of it.
* ``loudnorm`` runs at 192 kHz internally and would hand the encoder a 192 kHz
  stream, which libmp3lame refuses. The normalize pass resamples back to the
  source rate.
* ``amix`` halves the level of every input unless ``normalize=0``, which would
  make Bleep quietly duck the whole track.
"""

from __future__ import annotations

import json
import math
import os
import subprocess
import sys
import tempfile
from pathlib import Path
from typing import Any, Iterable, Sequence

from .. import backends
from ..registry import CONVERT, SAME, TOOL, Job, Param, Registry
from ..result import InvalidParams, Result, RunnerFailed, UnsupportedInput, ok

MOD = "peel_engine.families.av"
FF = ("ffmpeg", "ffprobe")

#: The spec's Audio row, in wheel order.
AUDIO = ("mp3", "m4a", "wav", "flac", "ogg", "opus", "aiff", "wma")
#: The spec's Video row. GIF is a video output and a video input, but it is not
#: a tool input: a trimmed GIF would have to be re-quantised, and the spec only
#: lists GIF as a conversion.
VIDEO = ("mp4", "mov", "mkv", "webm", "avi", "wmv", "gif")
VIDEO_TOOL_INPUTS = tuple(e for e in VIDEO if e != "gif")

#: How each extension is spelled on the wheel.
LABELS = {
    "mp3": "MP3", "m4a": "M4A", "wav": "WAV", "flac": "FLAC", "ogg": "OGG",
    "opus": "Opus", "aiff": "AIFF", "wma": "WMA", "mp4": "MP4", "mov": "MOV",
    "mkv": "MKV", "webm": "WebM", "avi": "AVI", "wmv": "WMV", "gif": "GIF",
}

#: container -> (video encoder, extra encoder args, audio encoder, audio args)
_VIDEO_CODECS: dict[str, tuple[str, tuple[str, ...], str, tuple[str, ...]]] = {
    "mp4": ("libx264", ("-preset", "veryfast", "-movflags", "+faststart"), "aac", ("-b:a", "192k")),
    "mov": ("libx264", ("-preset", "veryfast"), "aac", ("-b:a", "192k")),
    "mkv": ("libx264", ("-preset", "veryfast"), "aac", ("-b:a", "192k")),
    # vp9 defaults to a one core multi second encode; row-mt plus cpu-used keeps
    # a drag and drop conversion interactive.
    "webm": ("libvpx-vp9", ("-row-mt", "1", "-deadline", "good", "-cpu-used", "4"),
             "libopus", ("-b:a", "128k")),
    "avi": ("mpeg4", ("-vtag", "XVID"), "libmp3lame", ("-b:a", "192k")),
    "wmv": ("wmv2", (), "wmav2", ("-b:a", "192k")),
}

#: container -> (audio encoder, encoder args) for an audio only file.
_AUDIO_CODECS: dict[str, tuple[str, tuple[str, ...]]] = {
    "mp3": ("libmp3lame", ("-b:a", "192k")),
    "m4a": ("aac", ("-b:a", "192k")),
    "wav": ("pcm_s16le", ()),
    "flac": ("flac", ()),
    "ogg": ("libvorbis", ("-q:a", "5")),
    "opus": ("libopus", ("-b:a", "128k")),
    "aiff": ("pcm_s16be", ()),
    "wma": ("wmav2", ("-b:a", "192k")),
}

#: Audio containers whose size a bitrate actually controls.
LOSSY = ("mp3", "m4a", "ogg", "opus", "wma")

#: Quality knob, per encoder kind. x264 and vp9 take a CRF; the two legacy
#: Windows encoders only understand a quantiser scale.
_CRF = {"high": 20, "medium": 26, "low": 32}
_QSCALE = {"high": 3, "medium": 6, "low": 10}

#: yuv420p rejects an odd width or height. Always last in a filter chain.
_EVEN = "scale=trunc(iw/2)*2:trunc(ih/2)*2"

PROGRESS_PREFIX = "PEEL-PROGRESS "

_QUALITY = Param(
    "quality", "enum", "Quality", default="medium", choices=("high", "medium", "low"),
    help="Ignored when a target size is given.",
)
_RECT = tuple(
    Param(name, "int", label, required=True, minimum=0)
    for name, label in (("x", "X"), ("y", "Y"), ("w", "Width"), ("h", "Height"))
)


def register(registry: Registry) -> None:
    for ext in AUDIO:
        registry.add(
            id=f"av.audio_to_{ext}",
            family=CONVERT,
            label=LABELS[ext],
            group="Audio",
            inputs=tuple(e for e in AUDIO if e != ext),
            output_ext=ext,
            backends=FF,
            runner=f"{MOD}:convert_audio",
        )
    for ext in VIDEO:
        registry.add(
            id=f"av.video_to_{ext}",
            family=CONVERT,
            label=LABELS[ext],
            group="Video",
            inputs=tuple(e for e in VIDEO if e != ext),
            output_ext=ext,
            params=(
                (
                    Param("fps", "int", "Frames per second", default=12, minimum=1, maximum=50),
                    Param("width", "int", "Width", default=480, minimum=16, maximum=1920,
                          help="Only shrinks; a narrower video keeps its width."),
                )
                if ext == "gif"
                else ()
            ),
            backends=FF,
            runner=f"{MOD}:convert_video",
        )
    # The spec's one cross row entry: video out also offers MP3. It lives in the
    # Video group, so it does not collide with the Audio group's MP3 segment.
    registry.add(
        id="av.video_to_mp3",
        family=CONVERT,
        label="MP3",
        group="Video",
        inputs=VIDEO_TOOL_INPUTS,
        output_ext="mp3",
        backends=FF,
        runner=f"{MOD}:extract_audio",
    )

    # --- video tools --------------------------------------------------------
    registry.add(
        id="av.video_compress", family=TOOL, label="Compress", group="Video",
        inputs=VIDEO_TOOL_INPUTS, output_ext=SAME, backends=FF,
        params=(
            _QUALITY,
            Param("target_mb", "float", "Target size (MB)", minimum=0.05,
                  help="Optional. 1 MB is 1,000,000 bytes. Overrides quality: the "
                       "bitrate is fitted to this size."),
        ),
        runner=f"{MOD}:video_compress",
    )
    registry.add(
        id="av.video_crop", family=TOOL, label="Crop", group="Video",
        inputs=VIDEO_TOOL_INPUTS, output_ext=SAME, backends=FF,
        params=_RECT + (_QUALITY,), needs_editor=True, max_files=1,
        runner=f"{MOD}:video_crop",
    )
    registry.add(
        id="av.video_trim", family=TOOL, label="Trim", group="Video",
        inputs=VIDEO_TOOL_INPUTS, output_ext=SAME, backends=FF,
        params=(
            Param("start", "timecode", "Start", default=0),
            Param("end", "timecode", "End", help="Empty means the end of the file."),
            _QUALITY,
        ),
        needs_editor=True, max_files=1,
        runner=f"{MOD}:video_trim",
    )
    registry.add(
        id="av.video_split", family=TOOL, label="Split", group="Video",
        inputs=VIDEO_TOOL_INPUTS, output_ext=SAME, backends=FF,
        params=(
            Param("at", "list", "Cut at", help="Timestamps, for example 0:10,0:25."),
            Param("every", "float", "Every (seconds)", minimum=0.1),
            _QUALITY,
        ),
        needs_editor=True, max_files=1,
        runner=f"{MOD}:video_split",
    )
    registry.add(
        id="av.video_join", family=TOOL, label="Join", group="Video",
        inputs=VIDEO_TOOL_INPUTS, output_ext=SAME, backends=FF,
        mode="combine", min_files=2, params=(_QUALITY,),
        runner=f"{MOD}:video_join",
    )
    registry.add(
        id="av.video_speed", family=TOOL, label="Speed", group="Video",
        inputs=VIDEO_TOOL_INPUTS, output_ext=SAME, backends=FF,
        params=(
            Param("factor", "float", "Speed", default=2.0, minimum=0.25, maximum=4.0),
            _QUALITY,
        ),
        runner=f"{MOD}:video_speed",
    )
    registry.add(
        id="av.video_mute", family=TOOL, label="Remove Audio", group="Video",
        inputs=VIDEO_TOOL_INPUTS, output_ext=SAME, backends=FF,
        runner=f"{MOD}:video_mute",
    )
    registry.add(
        id="av.video_redact", family=TOOL, label="Redact", group="Video",
        inputs=VIDEO_TOOL_INPUTS, output_ext=SAME, backends=FF,
        params=_RECT + (
            Param("strength", "int", "Blur", default=12, minimum=1, maximum=50),
            Param("start", "timecode", "From", help="Empty blurs the whole clip."),
            Param("end", "timecode", "To"),
            _QUALITY,
        ),
        needs_editor=True, max_files=1,
        runner=f"{MOD}:video_redact",
    )
    registry.add(
        id="av.video_snapshot", family=TOOL, label="Snapshot", group="Video",
        inputs=VIDEO_TOOL_INPUTS, output_ext="png", backends=FF,
        params=(
            Param("time", "timecode", "At", default=0),
            Param("every", "float", "Every (seconds)", minimum=0.1,
                  help="Optional. Saves a frame every N seconds instead of one frame."),
        ),
        runner=f"{MOD}:video_snapshot",
    )

    # --- audio tools --------------------------------------------------------
    registry.add(
        id="av.audio_compress", family=TOOL, label="Compress", group="Audio",
        inputs=AUDIO, output_ext=SAME, backends=FF,
        params=(Param("bitrate", "int", "Bitrate (kbps)", default=128, minimum=32, maximum=320),),
        runner=f"{MOD}:audio_compress",
    )
    registry.add(
        id="av.audio_trim", family=TOOL, label="Trim", group="Audio",
        inputs=AUDIO, output_ext=SAME, backends=FF,
        params=(
            Param("start", "timecode", "Start", default=0),
            Param("end", "timecode", "End", help="Empty means the end of the file."),
        ),
        needs_editor=True, max_files=1,
        runner=f"{MOD}:audio_trim",
    )
    registry.add(
        id="av.audio_normalize", family=TOOL, label="Normalize", group="Audio",
        inputs=AUDIO, output_ext=SAME, backends=FF,
        params=(
            Param("target", "float", "Loudness (LUFS)", default=-16.0, minimum=-40.0, maximum=-5.0),
        ),
        runner=f"{MOD}:audio_normalize",
    )
    registry.add(
        id="av.audio_bleep", family=TOOL, label="Bleep", group="Audio",
        inputs=AUDIO, output_ext=SAME, backends=FF,
        params=(
            Param("ranges", "list", "Ranges", required=True,
                  help="Each range is start-end, for example 0:03-0:05,1:10-1:12."),
            Param("frequency", "int", "Tone (Hz)", default=1000, minimum=100, maximum=8000),
        ),
        needs_editor=True, max_files=1,
        runner=f"{MOD}:audio_bleep",
    )
    registry.add(
        id="av.audio_channels", family=TOOL, label="Channels", group="Audio",
        inputs=AUDIO, output_ext=SAME, backends=FF,
        params=(Param("channels", "enum", "Channels", default="mono", choices=("mono", "stereo")),),
        runner=f"{MOD}:audio_channels",
    )
    registry.add(
        id="av.audio_visualize", family=TOOL, label="Visualize", group="Audio",
        inputs=AUDIO, output_ext="mp4", backends=FF,
        params=(
            Param("width", "int", "Width", default=1280, minimum=160, maximum=3840),
            Param("height", "int", "Height", default=240, minimum=80, maximum=2160),
        ),
        runner=f"{MOD}:audio_visualize",
    )
    registry.add(
        id="av.audio_metadata", family=TOOL, label="Metadata", group="Audio",
        inputs=AUDIO, output_ext=SAME, backends=FF,
        params=(
            Param("title", "string", "Title"),
            Param("artist", "string", "Artist"),
            Param("album", "string", "Album"),
        ),
        needs_editor=True, max_files=1,
        runner=f"{MOD}:audio_metadata",
    )


# --- ffmpeg plumbing --------------------------------------------------------


class _Progress:
    """Turns ffmpeg's ``-progress`` key/value stream into PEEL-PROGRESS lines.

    A job with several ffmpeg passes (a GIF palette, a loudnorm measure pass, a
    split) declares ``steps`` and calls ``step(n)`` before each pass, so the
    percentage walks once from 0 to 100 over the whole job.
    """

    def __init__(self, job: Job, duration: float, steps: int = 1) -> None:
        self.action = job.action.id
        self.file = job.source.name
        self.duration = float(duration) if duration and duration > 0 else 0.0
        self.steps = max(1, int(steps))
        self.current = 1
        self._last_percent = -1.0
        self._last_seconds = -1.0

    def step(self, index: int, duration: float | None = None) -> "_Progress":
        self.current = max(1, min(index, self.steps))
        if duration is not None:
            self.duration = float(duration) if duration > 0 else 0.0
        self._last_percent = -1.0
        self._last_seconds = -1.0
        return self

    def feed(self, line: str) -> None:
        key, sep, value = line.strip().partition("=")
        if not sep:
            return
        if key in ("out_time_us", "out_time_ms"):
            try:
                self._emit(int(value) / 1_000_000)
            except ValueError:
                return
        elif key == "progress" and value == "end":
            self._emit(self.duration, final=True)

    def done(self) -> None:
        """Report the job as finished, whatever the last pass reported."""
        self.current = self.steps
        self._last_percent = -1.0
        self._emit(self.duration, final=True)

    def _emit(self, seconds: float, final: bool = False) -> None:
        seconds = max(0.0, seconds)
        percent: float | None = None
        if self.duration:
            fraction = min(seconds / self.duration, 1.0)
            percent = round(((self.current - 1) + fraction) / self.steps * 100, 1)
            if not final and percent - self._last_percent < 1.0:
                return
            self._last_percent = percent
        elif not final and seconds - self._last_seconds < 0.25:
            return
        self._last_seconds = seconds
        payload = {
            "action": self.action,
            "file": self.file,
            "percent": percent,
            "seconds": round(seconds, 3),
            "duration": round(self.duration, 3) if self.duration else None,
            "step": self.current,
            "steps": self.steps,
        }
        print(PROGRESS_PREFIX + json.dumps(payload), file=sys.stderr, flush=True)


def _exe(name: str) -> str:
    return str(backends.require(name))


def _arg(value: Any) -> str:
    """One argv entry for ffmpeg or ffprobe.

    A path is always made absolute, so a relative name that starts with a dash
    (``-x.mp4`` dropped from its own folder) is never read as an option.
    """
    if isinstance(value, Path):
        return os.path.abspath(value)
    return str(value)


def probe(path: Path) -> dict[str, Any]:
    """ffprobe's format and streams for one file, as a dict."""
    args = [
        _exe("ffprobe"), "-v", "error", "-show_format", "-show_streams",
        "-of", "json", _arg(path),
    ]
    try:
        # ffprobe writes UTF-8 JSON. Without an explicit encoding Python uses
        # the locale code page (cp1252 on Windows), which turns accented tags
        # into mojibake or fails outright on bytes cp1252 cannot map.
        proc = subprocess.run(
            args, capture_output=True, text=True, encoding="utf-8", errors="replace",
            timeout=120,
        )
    except (OSError, subprocess.SubprocessError) as exc:
        raise RunnerFailed(f"could not read {Path(path).name}: {exc}") from None
    if proc.returncode != 0:
        detail = _last_line(proc.stderr)
        raise UnsupportedInput(
            f"{Path(path).name} is not a media file ffmpeg can read"
            + (f": {detail}" if detail else ""),
            code="unreadable_media",
        )
    try:
        return json.loads(proc.stdout or "{}")
    except json.JSONDecodeError:
        raise RunnerFailed(f"ffprobe gave no usable answer for {Path(path).name}") from None


def _streams(info: dict[str, Any], kind: str) -> list[dict[str, Any]]:
    return [s for s in info.get("streams", []) if s.get("codec_type") == kind]


def _duration(info: dict[str, Any]) -> float:
    """Seconds, from the container or the longest stream. 0.0 when unknown."""
    values = [info.get("format", {}).get("duration")]
    values += [s.get("duration") for s in info.get("streams", [])]
    best = 0.0
    for value in values:
        try:
            best = max(best, float(value))
        except (TypeError, ValueError):
            continue
    return best


def _seconds(value: Any) -> float:
    """A duration from ffprobe: plain seconds, or Matroska's HH:MM:SS.fff tag."""
    if value is None:
        return 0.0
    text = str(value).strip()
    try:
        if ":" in text:
            total = 0.0
            for part in text.split(":"):
                total = total * 60 + float(part)
            return total
        return float(text)
    except ValueError:
        return 0.0


def _rate(text: Any) -> float:
    """Frames per second from ffprobe's ``25/1`` form. 0.0 when unknown."""
    try:
        num, _, den = str(text).partition("/")
        value = float(num) / float(den or 1)
    except (TypeError, ValueError, ZeroDivisionError):
        return 0.0
    return value if value > 0 else 0.0


def _video_timing(info: dict[str, Any]) -> tuple[float, float]:
    """(video duration, length of one frame), both in seconds.

    The video track's own duration, not the container's: phone and screen
    recordings often carry audio that runs on after the last frame, and a seek
    into that tail decodes no picture at all. Falls back to the Matroska
    DURATION tag, then the container. The frame length falls back to 1/25 s.
    """
    streams = _streams(info, "video")
    stream = streams[0] if streams else {}
    duration = _seconds(stream.get("duration"))
    if not duration:
        tags = stream.get("tags") or {}
        duration = _seconds(next((v for k, v in tags.items() if k.upper() == "DURATION"), None))
    if not duration:
        duration = _seconds(info.get("format", {}).get("duration"))
    fps = _rate(stream.get("avg_frame_rate")) or _rate(stream.get("r_frame_rate"))
    frame = 1.0 / fps if fps else 0.04
    return duration, frame


def _video_start(info: dict[str, Any]) -> float:
    """Where the video track starts on the file's timeline, in seconds.

    A clip can carry audio before its first frame (video stream start_time
    later than the container's). ffmpeg's input ``-ss`` counts from the
    container start, so this is the offset every seek and bound must add.
    0.0 when either start time is missing.
    """
    streams = _streams(info, "video")
    stream = streams[0] if streams else {}
    try:
        video = float(stream.get("start_time"))
    except (TypeError, ValueError):
        return 0.0
    try:
        container = float(info.get("format", {}).get("start_time"))
    except (TypeError, ValueError):
        container = 0.0
    return max(0.0, video - container)


def _video_window(info: dict[str, Any]) -> tuple[float, float, float]:
    """(first frame, end of the video, length of one frame) on the file's
    timeline, in seconds. The end is 0.0 when no duration can be read.

    A duration read from the video track (or its Matroska DURATION tag) is
    added to the track's start time. The container duration fallback already
    counts from the container start, so it is used as the end as it is.
    """
    start = _video_start(info)
    streams = _streams(info, "video")
    stream = streams[0] if streams else {}
    length = _seconds(stream.get("duration"))
    if not length:
        tags = stream.get("tags") or {}
        length = _seconds(next((v for k, v in tags.items() if k.upper() == "DURATION"), None))
    _, frame = _video_timing(info)
    if length:
        return start, start + length, frame
    end = _seconds(info.get("format", {}).get("duration"))
    return start, (end if end > start else 0.0), frame


def _last_frame_start(info: dict[str, Any]) -> float:
    """The latest time a seek can ask for and still decode a frame: the start
    of the last frame, on the file's timeline. 0.0 when the duration is
    unknown. Seeks are handed to ffmpeg through ``_seek``, which rounds down,
    so they never pass it."""
    start, end, frame = _video_window(info)
    if not end:
        return 0.0
    return max(start, end - frame)


def _seek(seconds: float) -> str:
    """A seek time for ffmpeg, rounded DOWN to the millisecond. Rounding up
    could step past the start of the last frame, where a seek decodes nothing.
    The 1e-6 absorbs float noise such as 0.1 + 0.1 + 0.1."""
    return f"{math.floor(max(0.0, seconds) * 1000 + 1e-6) / 1000:.3f}"


def _rotation(stream: dict[str, Any]) -> int:
    """Display rotation in degrees (0, 90, 180 or 270) from a phone clip.

    Modern ffprobe reports a display matrix in ``side_data_list``; older builds
    put a ``rotate`` tag on the stream. Either way ffmpeg applies it when it
    decodes (autorotate), so every filter sees the frame as it is displayed.
    """
    value: Any = None
    for side in stream.get("side_data_list") or []:
        if "rotation" in side:
            value = side["rotation"]
            break
    if value is None:
        value = (stream.get("tags") or {}).get("rotate")
    try:
        degrees = int(round(float(value)))
    except (TypeError, ValueError):
        return 0
    return degrees % 360


def _display_matrix(stream: dict[str, Any]) -> list[int] | None:
    """The nine numbers of ffprobe's display matrix, or None when absent."""
    for side in stream.get("side_data_list") or []:
        text = side.get("displaymatrix")
        if not text:
            continue
        numbers: list[int] = []
        for line in str(text).splitlines():
            _, _, values = line.partition(":")
            for value in values.split():
                try:
                    numbers.append(int(value))
                except ValueError:
                    return None
        return numbers if len(numbers) == 9 else None
    return None


def _flip(stream: dict[str, Any]) -> tuple[int, ...] | None:
    """None for a clip that is not mirrored. For a mirrored clip (display
    matrix with a negative determinant) the signs of the matrix's 2x2 part,
    which tell the mirror axis and the rotation apart where ffprobe's rotation
    figure alone cannot."""
    m = _display_matrix(stream)
    if m is None or m[0] * m[4] - m[1] * m[3] >= 0:
        return None
    return tuple((v > 0) - (v < 0) for v in (m[0], m[1], m[3], m[4]))


def _frame_size(info: dict[str, Any], path: Path) -> tuple[int, int]:
    """The DISPLAYED frame size: width and height swap for a 90 or 270 degree
    phone clip. This is the frame the editor shows and the frame every ffmpeg
    filter receives, so crop and redact rectangles are checked against it."""
    stream = _require_video(info, path)
    try:
        width, height = int(stream["width"]), int(stream["height"])
    except (KeyError, TypeError, ValueError):
        raise UnsupportedInput(f"{path.name} does not report a frame size") from None
    if _rotation(stream) in (90, 270):
        return height, width
    return width, height


def _sample_rate(info: dict[str, Any]) -> int:
    for stream in _streams(info, "audio"):
        try:
            return int(stream["sample_rate"])
        except (KeyError, TypeError, ValueError):
            continue
    return 48000


def _channels(info: dict[str, Any]) -> int:
    for stream in _streams(info, "audio"):
        try:
            return max(1, int(stream["channels"]))
        except (KeyError, TypeError, ValueError):
            continue
    return 2


def _require_audio(info: dict[str, Any], path: Path) -> dict[str, Any]:
    streams = _streams(info, "audio")
    if not streams:
        raise UnsupportedInput(f"{path.name} has no audio track", code="no_audio")
    return streams[0]


def _require_video(info: dict[str, Any], path: Path) -> dict[str, Any]:
    streams = _streams(info, "video")
    if not streams:
        raise UnsupportedInput(f"{path.name} has no video track", code="no_video")
    return streams[0]


def _is_png(path: Path) -> bool:
    """True when ``path`` is a whole PNG with a real frame size.

    Checks the signature, a non-zero IHDR width and height, and the IEND chunk
    at the end, which is what a decoder needs. Cheaper than an ffprobe per
    frame on a 2000 frame Snapshot.
    """
    try:
        with open(path, "rb") as handle:
            head = handle.read(24)
            if len(head) < 24 or head[:8] != b"\x89PNG\r\n\x1a\n" or head[12:16] != b"IHDR":
                return False
            width = int.from_bytes(head[16:20], "big")
            height = int.from_bytes(head[20:24], "big")
            handle.seek(-12, os.SEEK_END)
            tail = handle.read(12)
    except OSError:
        return False
    return width > 0 and height > 0 and tail[4:8] == b"IEND"


def _has_video(path: Path) -> bool:
    """True when ffprobe finds a video stream with at least one frame."""
    try:
        info = probe(path)
    except (RunnerFailed, UnsupportedInput):
        return False
    for stream in _streams(info, "video"):
        frames = stream.get("nb_frames")
        try:
            if frames is not None and int(frames) == 0:
                continue
        except (TypeError, ValueError):
            pass
        return True
    return False


def _last_line(text: str) -> str:
    lines = [line.strip() for line in (text or "").splitlines() if line.strip()]
    return lines[-1] if lines else ""


def _ffmpeg(
    args: Sequence[str], *, progress: _Progress | None = None, want_log: bool = False
) -> str:
    """Run ffmpeg once. Returns its log; raises RunnerFailed on a bad exit.

    The log goes to a temporary file rather than a pipe, so a chatty encoder can
    never fill a pipe buffer and deadlock the progress reader.
    """
    command = [
        _exe("ffmpeg"), "-hide_banner", "-nostdin", "-y",
        "-loglevel", "info" if want_log else "error", "-nostats",
    ]
    if progress is not None:
        command += ["-progress", "pipe:1"]
    command += [_arg(a) for a in args]

    with tempfile.TemporaryFile(mode="w+", encoding="utf-8", errors="replace") as log:
        try:
            proc = subprocess.Popen(
                command,
                stdin=subprocess.DEVNULL,
                stdout=subprocess.PIPE if progress is not None else subprocess.DEVNULL,
                stderr=log,
                text=True,
                encoding="utf-8",
                errors="replace",
            )
        except OSError as exc:
            raise RunnerFailed(f"could not start ffmpeg: {exc}") from None
        try:
            if progress is not None and proc.stdout is not None:
                for line in proc.stdout:
                    progress.feed(line)
                proc.stdout.close()
            code = proc.wait()
        except BaseException:
            proc.kill()
            proc.wait()
            raise
        log.seek(0)
        text = log.read()
    if code != 0:
        detail = _last_line(text) or f"exit code {code}"
        raise RunnerFailed(f"ffmpeg could not finish: {detail}", code="ffmpeg_failed")
    return text


def _vcodec(ext: str, *, quality: str = "medium", video_kbps: int | None = None) -> list[str]:
    """Video encoder args for one container, honouring the quality knob."""
    codec, extra, _, _ = _VIDEO_CODECS[ext]
    args = ["-c:v", codec, *extra]
    if video_kbps is not None:
        args += [
            "-b:v", f"{video_kbps}k",
            "-maxrate", f"{int(video_kbps * 1.5)}k",
            "-bufsize", f"{video_kbps * 2}k",
        ]
    elif codec == "libx264":
        args += ["-crf", str(_CRF[quality])]
    elif codec == "libvpx-vp9":
        args += ["-b:v", "0", "-crf", str(_CRF[quality])]
    else:
        args += ["-qscale:v", str(_QSCALE[quality])]
    return args


def _acodec_for_video(ext: str, *, audio: bool, audio_kbps: int | None = None) -> list[str]:
    if not audio:
        return ["-an"]
    _, _, codec, extra = _VIDEO_CODECS[ext]
    if audio_kbps is not None:
        return ["-c:a", codec, "-b:a", f"{audio_kbps}k"]
    return ["-c:a", codec, *extra]


def _acodec(ext: str, *, bitrate: int | None = None, info: dict[str, Any] | None = None) -> list[str]:
    """Audio encoder args for an audio container.

    A bitrate only means something to a lossy codec. FLAC answers it with its
    strongest lossless setting, and WAV/AIFF by dropping to CD depth and rate,
    because the alternative (refusing Compress on half the Audio row) is worse.
    """
    codec, extra = _AUDIO_CODECS[ext]
    if bitrate is None:
        return ["-c:a", codec, *extra]
    if ext in LOSSY:
        return ["-c:a", codec, "-b:a", f"{bitrate}k"]
    if ext == "flac":
        return ["-c:a", codec, "-compression_level", "12"]
    rate = min(44100, _sample_rate(info or {}))
    return ["-c:a", codec, "-ar", str(rate)]


def _filters(*chain: str) -> list[str]:
    """A ``-vf`` pair from a chain, always ending with the even size guard."""
    parts = [c for c in chain if c] + [_EVEN]
    return ["-vf", ",".join(parts)]


def _even(value: int) -> int:
    return value - (value % 2)


def _atempo(factor: float) -> str:
    """atempo only spans 0.5x to 2x, so a bigger change is a chain of stages."""
    stages: list[float] = []
    remaining = factor
    while remaining > 2.0:
        stages.append(2.0)
        remaining /= 2.0
    while remaining < 0.5:
        stages.append(0.5)
        remaining /= 0.5
    stages.append(remaining)
    return ",".join(f"atempo={s:.6f}" for s in stages)


def _span(params: dict[str, Any], duration: float, label: str = "Trim") -> tuple[float, float]:
    """Validate a start/end pair against the real duration."""
    start = float(params.get("start") or 0.0)
    end = params.get("end")
    if end is None and not duration:
        raise RunnerFailed(
            f"{label}: the duration of this file cannot be read, so give an end time",
            code="no_duration",
        )
    end = duration if end is None else float(end)
    if start < 0:
        raise InvalidParams(f"{label}: the start cannot be negative")
    if duration and start >= duration:
        raise InvalidParams(
            f"{label}: the start {start:g}s is past the end of the file ({duration:.2f}s)"
        )
    if duration:
        end = min(end, duration)
    if end <= start:
        raise InvalidParams(f"{label}: the end ({end:g}s) must come after the start ({start:g}s)")
    return start, end


def _ranges(values: Iterable[Any], duration: float) -> list[tuple[float, float]]:
    """Parse and merge ``start-end`` ranges (or ``[start, end]`` pairs)."""
    from ..registry import _parse_timecode

    parsed: list[tuple[float, float]] = []
    for item in values:
        if isinstance(item, (list, tuple)):
            pair: Sequence[Any] = item
        else:
            text = str(item).strip()
            pair = [p for p in text.split("-") if p.strip()]
        if len(pair) != 2:
            raise InvalidParams(
                f"bad range {item!r}: each range is start-end, for example 0:03-0:05"
            )
        try:
            start, end = (_parse_timecode(pair[0]), _parse_timecode(pair[1]))
        except (TypeError, ValueError) as exc:
            raise InvalidParams(f"bad range {item!r}: {exc}") from None
        if end <= start:
            raise InvalidParams(f"bad range {item!r}: the end must come after the start")
        if duration:
            start, end = min(start, duration), min(end, duration)
            if end <= start:
                continue
        parsed.append((start, end))
    if not parsed:
        raise InvalidParams("give at least one range inside the file")
    parsed.sort()
    merged = [parsed[0]]
    for start, end in parsed[1:]:
        last_start, last_end = merged[-1]
        if start <= last_end:
            merged[-1] = (last_start, max(last_end, end))
        else:
            merged.append((start, end))
    return merged


def _between(ranges: Sequence[tuple[float, float]]) -> str:
    return "+".join(f"between(t,{s:.3f},{e:.3f})" for s, e in ranges)


def _crop_filter(
    params: dict[str, Any], width: int, height: int, *, even: bool = True
) -> tuple[str, int, int, int, int]:
    """Validate a rectangle against the frame and return the crop filter.

    A crop is an output frame, so its size is floored to even for the encoder.
    A redaction passes ``even=False``: the box is only blurred and laid back
    over the frame, so it keeps the exact size the user covered (flooring it
    would leave the last covered column or row in the clear)."""
    x, y = int(params["x"]), int(params["y"])
    w, h = int(params["w"]), int(params["h"])
    if even:
        w, h = _even(w), _even(h)
    if w < 2 or h < 2:
        raise InvalidParams("the region must be at least 2 pixels wide and tall")
    if x + w > width or y + h > height:
        raise InvalidParams(
            f"the region {w}x{h} at {x},{y} falls outside the {width}x{height} frame"
        )
    return f"crop={w}:{h}:{x}:{y}", x, y, w, h


# --- conversions ------------------------------------------------------------


def convert_audio(job: Job) -> Result:
    """Any audio format to any other. The target extension picks the encoder."""
    ext = job.action.ext_for(job.source) or ""
    info = probe(job.source)
    _require_audio(info, job.source)
    target = job.output_path()
    progress = _Progress(job, _duration(info))
    _ffmpeg(["-i", job.source, "-vn", *_acodec(ext), target], progress=progress)
    progress.done()
    return ok([target], f"Wrote {target.name}")


def convert_video(job: Job) -> Result:
    """Any video format to any other, GIF included (its own two pass runner)."""
    ext = job.action.ext_for(job.source) or ""
    if ext == "gif":
        return _to_gif(job)
    info = probe(job.source)
    _require_video(info, job.source)
    has_audio = bool(_streams(info, "audio"))
    target = job.output_path()
    progress = _Progress(job, _duration(info))
    _ffmpeg(
        [
            "-i", job.source,
            *_vcodec(ext, quality="high"), *_filters(),
            *_acodec_for_video(ext, audio=has_audio),
            target,
        ],
        progress=progress,
    )
    progress.done()
    note = "" if has_audio else " (no audio track to carry over)"
    return ok([target], f"Wrote {target.name}{note}")


def _to_gif(job: Job) -> Result:
    """Video to GIF: palettegen then paletteuse, which is the only way to get a
    256 colour GIF that does not look like a fax."""
    info = probe(job.source)
    _require_video(info, job.source)
    fps = int(job.params.get("fps") or 12)
    width = int(job.params.get("width") or 480)
    target = job.output_path()
    scratch = job.scratch_dir(target)
    palette = scratch / "palette.png"
    chain = f"fps={fps},scale='min(iw,{width})':-1:flags=lanczos"
    progress = _Progress(job, _duration(info), steps=2)
    _ffmpeg(
        ["-i", job.source, "-vf", f"{chain},palettegen", palette],
        progress=progress.step(1),
    )
    _ffmpeg(
        [
            "-i", job.source, "-i", palette,
            "-lavfi", f"{chain}[x];[x][1:v]paletteuse",
            "-loop", "0", "-an", target,
        ],
        progress=progress.step(2),
    )
    progress.done()
    return ok([target], f"Wrote {target.name}")


def extract_audio(job: Job) -> Result:
    """The spec's video to MP3 row."""
    info = probe(job.source)
    _require_audio(info, job.source)
    target = job.output_path()
    progress = _Progress(job, _duration(info))
    _ffmpeg(["-i", job.source, "-vn", *_acodec("mp3"), target], progress=progress)
    progress.done()
    return ok([target], f"Wrote {target.name}")


# --- video tools ------------------------------------------------------------


def video_compress(job: Job) -> Result:
    info = probe(job.source)
    _require_video(info, job.source)
    ext = job.action.ext_for(job.source) or ""
    duration = _duration(info)
    has_audio = bool(_streams(info, "audio"))
    target_mb = job.params.get("target_mb")

    video_kbps: int | None = None
    audio_kbps: int | None = None
    if target_mb is not None:
        if duration <= 0:
            raise RunnerFailed(
                f"the duration of {job.source.name} cannot be read, so a target size "
                "cannot be fitted. Use the quality setting instead.",
                code="no_duration",
            )
        # 1 MB = 1,000,000 bytes = 8,000 kbit. 3% is left for the container
        # (headers, index, interleaving), which a bitrate does not cover.
        total_kbps = float(target_mb) * 8000 * 0.97 / duration
        audio_kbps = 96 if has_audio else 0
        video_kbps = max(64, int(total_kbps - audio_kbps))
        if not has_audio:
            audio_kbps = None

    # Compress is about the video track. Re-encoding the audio as well can make
    # the file BIGGER than the source (a 64k phone recording turned into 192k
    # AAC), so the track is copied untouched unless a target size needs a
    # bitrate for it.
    if video_kbps is None:
        audio_args = ["-c:a", "copy"] if has_audio else ["-an"]
    else:
        audio_args = _acodec_for_video(ext, audio=has_audio, audio_kbps=audio_kbps)

    target = job.output_path()
    video_args = [
        *_vcodec(ext, quality=job.params["quality"], video_kbps=video_kbps), *_filters(),
    ]
    # x264's one pass ABR lands on the bitrate. vp9, mpeg4 and wmv2 overshoot a
    # one pass bitrate by half again or more, so a target size runs them as a
    # two pass encode (the first pass only measures, into the scratch folder).
    two_pass = video_kbps is not None and _VIDEO_CODECS[ext][0] != "libx264"
    progress = _Progress(job, duration, steps=2 if two_pass else 1)
    if two_pass:
        # Both passes must see the same frames, or pass 2 stops with "input is
        # longer than 2-pass log file": the null muxer and asf/avi would pick a
        # different frame rate mode, so it is fixed for both.
        video_args += ["-fps_mode", "cfr"]
        log = job.scratch_dir(target) / "pass"
        _ffmpeg(
            ["-i", job.source, *video_args, "-pass", "1", "-passlogfile", log,
             "-an", "-f", "null", os.devnull],
            progress=progress.step(1),
        )
        video_args += ["-pass", "2", "-passlogfile", log]
        progress.step(2)
    _ffmpeg(["-i", job.source, *video_args, *audio_args, target], progress=progress)
    progress.done()
    before = job.source.stat().st_size
    after = target.stat().st_size
    saved = f"{100 * (1 - after / before):.0f}% smaller" if before and after < before else "no smaller"
    return ok([target], f"Wrote {target.name} ({saved})")


def video_crop(job: Job) -> Result:
    info = probe(job.source)
    width, height = _frame_size(info, job.source)
    crop, _, _, w, h = _crop_filter(job.params, width, height)
    ext = job.action.ext_for(job.source) or ""
    target = job.output_path()
    progress = _Progress(job, _duration(info))
    _ffmpeg(
        [
            "-i", job.source,
            *_vcodec(ext, quality=job.params["quality"]), *_filters(crop),
            *_acodec_for_video(ext, audio=bool(_streams(info, "audio"))),
            target,
        ],
        progress=progress,
    )
    progress.done()
    return ok([target], f"Wrote {target.name} ({w}x{h})")


def video_trim(job: Job) -> Result:
    info = probe(job.source)
    _require_video(info, job.source)
    start, end = _span(job.params, _duration(info))
    ext = job.action.ext_for(job.source) or ""
    target = job.output_path()
    progress = _Progress(job, end - start)
    _ffmpeg(
        [
            "-ss", f"{start:.3f}", "-i", job.source, "-t", f"{end - start:.3f}",
            *_vcodec(ext, quality=job.params["quality"]), *_filters(),
            *_acodec_for_video(ext, audio=bool(_streams(info, "audio"))),
            target,
        ],
        progress=progress,
    )
    progress.done()
    return ok([target], f"Wrote {target.name} ({end - start:.2f}s)")


def _cut_points(
    params: dict[str, Any],
    duration: float,
    last_cut: float | None = None,
    first: float = 0.0,
) -> list[tuple[float, float]]:
    """Segment boundaries from ``at`` timestamps or an ``every`` interval.

    ``duration`` is where the video track ends on the file's timeline.
    ``last_cut`` is the latest cut that still leaves the next part a frame of
    its own (see ``_last_frame_start``); a cut after it would write a part with
    no picture. ``first`` is where the video starts (see ``_video_start``): a
    cut at or before it would leave part 1 with no picture, and an interval
    counts from it.
    """
    from ..registry import _parse_timecode

    if duration <= 0:
        raise RunnerFailed("the duration cannot be read, so the file cannot be split",
                           code="no_duration")
    limit = duration if last_cut is None else min(duration, last_cut + 1e-6)
    at = params.get("at") or []
    every = params.get("every")
    cuts: list[float] = []
    if at:
        for item in at:
            try:
                cuts.append(_parse_timecode(item))
            except (TypeError, ValueError) as exc:
                raise InvalidParams(f"bad timestamp {item!r}: {exc}") from None
    elif every:
        step = float(every)
        position = first + step
        while position < duration - 0.05:
            cuts.append(position)
            position += step
    else:
        raise InvalidParams("give timestamps to cut at, or an interval in seconds")
    if at:
        # Typed cuts are the user's; one the video cannot honour (before its
        # first frame, or at or after its last) is refused by name rather than
        # dropped, so the parts written are always the parts asked for.
        outside = [c for c in cuts if not (first + 1e-6 < c < duration and c <= limit)]
        if outside and len(outside) < len(cuts):
            where = f"from {first:.2f}s to {duration:.2f}s" if first else (
                f"up to {duration:.2f}s")
            listed = ", ".join(f"{c:g}s" for c in outside)
            raise InvalidParams(
                f"a cut at {listed} falls outside the video, which runs {where}"
            )
    inside = sorted(
        {round(c, 3) for c in cuts if first + 1e-6 < c < duration and c <= limit}
    )
    if not inside:
        where = f"runs from {first:.2f}s to {duration:.2f}s" if first else (
            f"is {duration:.2f}s long")
        raise InvalidParams(f"no cut falls inside the video, which {where}")
    bounds = [0.0] + inside + [duration]
    return [(bounds[i], bounds[i + 1]) for i in range(len(bounds) - 1)]


def video_split(job: Job) -> Result:
    """Cut a video into parts. Cuts are placed on the VIDEO track's timeline,
    so every part has a picture; the last part runs to the end of the file and
    keeps any audio that outlasts the video. Each part is probed after it is
    written, and a part with no video fails the job rather than shipping."""
    info = probe(job.source)
    _require_video(info, job.source)
    first, video_length, _ = _video_window(info)
    segments = _cut_points(job.params, video_length, _last_frame_start(info), first)
    total = max(_duration(info), video_length)
    ext = job.action.ext_for(job.source) or ""
    has_audio = bool(_streams(info, "audio"))
    quality = job.params["quality"]
    progress = _Progress(job, total, steps=len(segments))
    outputs: list[Path] = []
    for index, (start, end) in enumerate(segments, start=1):
        last = index == len(segments)
        target = job.output_path(suffix=str(index))
        length = (total - start) if last else (end - start)
        _ffmpeg(
            [
                # Part 1 is read from the top with no seek: a seek to 0 lands
                # on the first video keyframe and drops any audio before a
                # late starting picture.
                *(["-ss", _seek(start)] if start > 0 else []), "-i", job.source,
                *([] if last else ["-t", f"{end - start:.3f}"]),
                *_vcodec(ext, quality=quality), *_filters(),
                *_acodec_for_video(ext, audio=has_audio),
                target,
            ],
            progress=progress.step(index, duration=length),
        )
        if not _has_video(target):
            raise RunnerFailed(
                f"part {index} (from {start:.2f}s) came out with no video, so the split "
                "was stopped",
                code="no_frame",
            )
        outputs.append(target)
    progress.done()
    return ok(outputs, f"Wrote {len(outputs)} parts")


def _join_signature(info: dict[str, Any]) -> tuple:
    video = _streams(info, "video")[:1]
    audio = _streams(info, "audio")[:1]
    return (
        # Rotation and mirroring are part of the shape: a stream copy keeps
        # ONE display matrix for the whole file, so a portrait phone clip
        # copied next to a landscape clip would play one of them sideways, and
        # a mirrored clip copied next to a plain one would mirror both.
        tuple(
            (s.get("codec_name"), s.get("width"), s.get("height"), s.get("pix_fmt"),
             _rotation(s), _flip(s))
            for s in video
        ),
        tuple((s.get("codec_name"), s.get("sample_rate"), s.get("channels")) for s in audio),
    )


def video_join(job: Job) -> Result:
    """Concatenate clips. Identical streams are copied; anything else is
    re-encoded to the first clip's shape, which is what the spec's "join
    (multi-file)" has to mean when a drag mixes a phone clip and a screen grab.

    The re-encode works on displayed frames (ffmpeg autorotates every input),
    so mixed rotations come out upright with no display matrix. A clip with no
    audio gets a silent track of its own length when any other clip has audio,
    so one silent clip does not strip the sound from the whole join.
    """
    infos = [probe(path) for path in job.inputs]
    for path, info in zip(job.inputs, infos):
        _require_video(info, path)
    total = sum(_duration(i) for i in infos)
    ext = job.action.ext_for(job.source) or ""
    target = job.output_path()
    scratch = job.scratch_dir(target)
    progress = _Progress(job, total)
    identical = len({_join_signature(i) for i in infos}) == 1
    any_audio = any(_streams(i, "audio") for i in infos)
    filled = [not _streams(i, "audio") for i in infos] if any_audio else []

    if identical:
        listing = scratch / "inputs.txt"
        listing.write_text(
            "".join(
                # Forward slashes: the concat demuxer reads a backslash as an
                # escape, and ffmpeg takes forward slashes on Windows too.
                "file '{}'\n".format(
                    str(p.resolve()).replace("\\", "/").replace("'", "'\\''")
                )
                for p in job.inputs
            ),
            encoding="utf-8",
        )
        try:
            _ffmpeg(
                ["-f", "concat", "-safe", "0", "-i", listing, "-c", "copy", target],
                progress=progress,
            )
        except RunnerFailed:
            # Matching streams that the container still refuses to take as a
            # copy (an old AVI index, a WMV with a stale header). Fall through
            # to the re-encode below rather than handing back a failure.
            pass
        else:
            progress.done()
            return ok([target], f"Joined {len(job.inputs)} clips into {target.name}")

    width, height = _frame_size(infos[0], job.inputs[0])
    width, height = max(2, _even(width)), max(2, _even(height))
    rate = 30
    legs: list[str] = []
    labels: list[str] = []
    for index in range(len(job.inputs)):
        legs.append(
            f"[{index}:v]scale={width}:{height}:force_original_aspect_ratio=decrease,"
            f"pad={width}:{height}:-1:-1,setsar=1,fps={rate},format=yuv420p[v{index}]"
        )
        labels.append(f"[v{index}]")
        if any_audio:
            if filled[index]:
                length = _duration(infos[index]) or _video_timing(infos[index])[0]
                legs.append(
                    f"anullsrc=r=48000:cl=stereo,atrim=duration={max(length, 0.04):.3f},"
                    f"aformat=sample_fmts=fltp:channel_layouts=stereo[a{index}]"
                )
            else:
                legs.append(
                    f"[{index}:a]aresample=48000,"
                    f"aformat=sample_fmts=fltp:channel_layouts=stereo[a{index}]"
                )
            labels.append(f"[a{index}]")
    tail = f"concat=n={len(job.inputs)}:v=1:a={1 if any_audio else 0}"
    tail += "[v][a]" if any_audio else "[v]"
    graph = ";".join(legs + ["".join(labels) + tail])

    args: list[str] = []
    for path in job.inputs:
        args += ["-i", path]
    args += ["-filter_complex", graph, "-map", "[v]"]
    if any_audio:
        args += ["-map", "[a]"]
    args += [
        *_vcodec(ext, quality=job.params["quality"]),
        *_acodec_for_video(ext, audio=any_audio),
        target,
    ]
    _ffmpeg(args, progress=progress)
    progress.done()
    count = sum(filled)
    if not any_audio:
        note = " (no clip had audio)"
    elif count:
        note = f" ({count} clip{'s' if count > 1 else ''} without audio filled with silence)"
    else:
        note = ""
    return ok([target], f"Joined {len(job.inputs)} clips into {target.name}{note}")


def video_speed(job: Job) -> Result:
    info = probe(job.source)
    _require_video(info, job.source)
    factor = float(job.params["factor"])
    ext = job.action.ext_for(job.source) or ""
    has_audio = bool(_streams(info, "audio"))
    duration = _duration(info)
    target = job.output_path()
    graph = f"[0:v]setpts=PTS/{factor:.6f},{_EVEN}[v]"
    if has_audio:
        graph += f";[0:a]{_atempo(factor)}[a]"
    args = ["-i", job.source, "-filter_complex", graph, "-map", "[v]"]
    if has_audio:
        args += ["-map", "[a]"]
    args += [
        *_vcodec(ext, quality=job.params["quality"]),
        *_acodec_for_video(ext, audio=has_audio),
        target,
    ]
    progress = _Progress(job, duration / factor if duration else 0.0)
    _ffmpeg(args, progress=progress)
    progress.done()
    return ok([target], f"Wrote {target.name} at {factor:g}x")


def video_mute(job: Job) -> Result:
    """Drop the audio track. The video is copied, so this is instant and lossless."""
    info = probe(job.source)
    _require_video(info, job.source)
    if not _streams(info, "audio"):
        raise UnsupportedInput(f"{job.source.name} already has no audio", code="no_audio")
    target = job.output_path()
    progress = _Progress(job, _duration(info))
    _ffmpeg(["-i", job.source, "-c:v", "copy", "-an", target], progress=progress)
    progress.done()
    return ok([target], f"Wrote {target.name} without audio")


# Below this radius (or the asked strength, if smaller) a blur no longer hides
# what is under it: on a frame two pixels wide or tall every radius is 0 and
# boxblur passes the box through untouched.
_MIN_BLUR_RADIUS = 2

# pixelize's largest block. The average fill is one block over the whole box,
# so a box with a longer side would come out as two or more tiles, each its
# own average: a box half black and half white would come back unchanged.
_MAX_FILL_SIDE = 1024


def _box_window(x: int, y: int, w: int, h: int, width: int, height: int, strength: int) -> tuple[int, int, int, int]:
    """The left, top, right and bottom of the neighbourhood the box is blurred
    in: wide enough for the blur's full reach (two passes of the radius),
    clamped to the frame, starting on an even pixel."""
    reach = 2 * strength
    left, top = max(0, x - reach) & ~1, max(0, y - reach) & ~1
    return left, top, min(width, x + w + reach), min(height, y + h + reach)


def _blur_reaches(x: int, y: int, w: int, h: int, width: int, height: int, strength: int) -> bool:
    """Whether every plane of the box's window holds a useful blur radius.
    Chroma is taken as half the window (4:2:0, rounded up), the smallest any
    source can have, so this never says yes when a plane would come up short."""
    left, top, right, bottom = _box_window(x, y, w, h, width, height, strength)
    luma = min(right - left, bottom - top)
    chroma = min(-(-(right - left) // 2), -(-(bottom - top) // 2))
    floor = min(strength, _MIN_BLUR_RADIUS)
    return min((luma - 1) // 2, (chroma - 1) // 2) >= floor


def _box_blur(x: int, y: int, w: int, h: int, width: int, height: int, strength: int) -> str:
    """The filters that turn a frame into the blurred box at x, y, w by h.

    boxblur refuses a radius over (shorter side - 1) / 2 of each plane, and a
    chroma plane can be half the box, so blurring the cropped box alone fails
    on a small box and cannot touch a 2x2 one at all. Blur a window of the
    box's neighbourhood instead (_box_window), with each plane's radius
    clamped to what the window holds, then crop the box back out at its exact
    size. The window starts on an even pixel, so on a subsampled frame the
    inner crop stays on the chroma grid whenever the box does.

    When the window is too small for a useful radius (_blur_reaches), the box
    is filled with its own average colour instead, so it is always hidden.
    The caller runs that fill on yuv444p, where every pixel has its own
    chroma, and refuses a box with a side over _MAX_FILL_SIDE first, so the
    fill is always one block and one colour."""
    if not _blur_reaches(x, y, w, h, width, height, strength):
        return f"crop={w}:{h}:{x}:{y},pixelize=w={w}:h={h}:m=avg"
    left, top, right, bottom = _box_window(x, y, w, h, width, height, strength)
    radius = (
        f"lr='min({strength},floor((min(w,h)-1)/2))':lp=2:"
        f"cr='min({strength},floor((min(cw,ch)-1)/2))':cp=2"
    )
    return (
        f"crop={right - left}:{bottom - top}:{left}:{top},boxblur={radius},"
        f"crop={w}:{h}:{x - left}:{y - top}"
    )


def video_redact(job: Job) -> Result:
    """Blur a rectangle, over the whole clip or only inside a time range."""
    info = probe(job.source)
    width, height = _frame_size(info, job.source)
    _, x, y, w, h = _crop_filter(job.params, width, height, even=False)
    duration = _duration(info)
    ext = job.action.ext_for(job.source) or ""
    has_audio = bool(_streams(info, "audio"))
    strength = int(job.params["strength"])

    window = ""
    if job.params.get("start") is not None or job.params.get("end") is not None:
        start, end = _span(job.params, duration, label="Redact")
        window = f":enable='between(t,{start:.3f},{end:.3f})'"

    reaches = _blur_reaches(x, y, w, h, width, height, strength)
    if not reaches and max(w, h) > _MAX_FILL_SIDE:
        # Fail closed: a box that cannot be blurred and is too long to fill in
        # one colour would not be hidden.
        raise UnsupportedInput(
            f"{job.source.name} ({width}x{height}) leaves too little room around the "
            f"{w}x{h} box to blur it at strength {strength}, and a box filled in one "
            f"colour instead can be at most {_MAX_FILL_SIDE} px long. Select a shorter "
            "box, or redact it in parts.",
            code="redact_box_too_long",
        )

    target = job.output_path()
    if w % 2 or h % 2 or x % 2 or y % 2 or not reaches:
        # On a chroma subsampled frame (yuv420p) crop and overlay snap to the
        # 2 pixel chroma grid (overlay also works in yuv420 unless told
        # otherwise), so an odd box (an odd frame covered edge to edge, say)
        # would blur one column or row short. Blur and overlay on a frame with
        # no subsampling, then hand the encoder the source's own format back.
        # The average fill for a frame too small to blur goes this way too,
        # so the fill is one colour per pixel, not per chroma block.
        streams = _streams(info, "video")
        back = streams[0].get("pix_fmt") if streams else None
        graph = (
            f"[0:v]format=yuv444p,split[base][box];[box]{_box_blur(x, y, w, h, width, height, strength)}[blur];"
            f"[base][blur]overlay={x}:{y}:format=yuv444{window},{_EVEN}"
            f"{f',format={back}' if back else ''}[v]"
        )
    else:
        graph = (
            f"[0:v]{_box_blur(x, y, w, h, width, height, strength)}[blur];"
            f"[0:v][blur]overlay={x}:{y}{window},{_EVEN}[v]"
        )
    args = ["-i", job.source, "-filter_complex", graph, "-map", "[v]"]
    if has_audio:
        args += ["-map", "0:a", "-c:a", "copy"]
    else:
        args += ["-an"]
    args += [*_vcodec(ext, quality=job.params["quality"]), target]
    progress = _Progress(job, duration)
    _ffmpeg(args, progress=progress)
    progress.done()
    return ok([target], f"Wrote {target.name} with a {w}x{h} blur")


def video_snapshot(job: Job) -> Result:
    """One frame, or a frame every N seconds, as PNG.

    Each frame is its own ffmpeg seek rather than an ``fps`` filter writing a
    numbered pattern, because every output path has to be a claim (ffmpeg
    choosing its own names would walk straight over a sibling file).
    """
    info = probe(job.source)
    _require_video(info, job.source)
    # Bounds come from the video track, not the container: audio that runs on
    # after the last frame has no picture to save.
    # The window is on the file's timeline: a video that starts after its
    # audio is offset by its start time.
    first, duration, _ = _video_window(info)
    last = _last_frame_start(info)
    every = job.params.get("every")
    ignored = ""
    if every:
        step = float(every)
        times = []
        position = first
        while (duration and position <= last + 1e-6) or (not times):
            times.append(position)
            position += step
            if len(times) > 2000:
                raise InvalidParams(
                    f"an interval of {step:g}s over {duration:.0f}s would be more than "
                    "2000 frames. Use a longer interval."
                )
        if not duration:
            ignored = (
                f"; the length of {job.source.name} cannot be read, so the interval of "
                f"{step:g}s was ignored"
            )
    else:
        at = float(job.params.get("time") or 0.0)
        if duration and at >= duration:
            raise InvalidParams(
                f"{at:g}s is past the end of the video ({duration:.2f}s)"
            )
        times = [at]

    progress = _Progress(job, duration, steps=len(times))
    outputs: list[Path] = []
    for index, at in enumerate(times, start=1):
        suffix = str(index) if len(times) > 1 else ""
        target = job.output_path(suffix=suffix)
        # A time inside the last frame shows that frame, so the seek is held at
        # its start: a seek past it decodes nothing and ffmpeg still exits 0.
        # A time before the first frame shows the first frame.
        seek = min(max(at, first), last) if duration else max(at, first)
        _ffmpeg(
            ["-ss", _seek(seek), "-i", job.source, "-frames:v", "1", "-update", "1", target],
            progress=progress.step(index, duration=max(duration, 0.001)),
        )
        # Never report a frame that is not a whole image. An empty claim that
        # is left out of the outputs is removed by the registry after the run.
        if _is_png(target):
            outputs.append(target)
        else:
            # Empty the claim in place (never delete and recreate it), so the
            # registry's cleanup of unused empty claims takes it away.
            open(target, "wb").close()
    progress.done()
    if not outputs:
        raise RunnerFailed(
            f"ffmpeg wrote no frame from {job.source.name}", code="no_frame"
        )
    noun = "frame" if len(outputs) == 1 else "frames"
    skipped = len(times) - len(outputs)
    note = f" ({skipped} past the last frame skipped)" if skipped else ""
    return ok(outputs, f"Saved {len(outputs)} {noun}{note}{ignored}")


# --- audio tools ------------------------------------------------------------


def audio_compress(job: Job) -> Result:
    info = probe(job.source)
    _require_audio(info, job.source)
    ext = job.action.ext_for(job.source) or ""
    target = job.output_path()
    progress = _Progress(job, _duration(info))
    _ffmpeg(
        ["-i", job.source, "-vn", *_acodec(ext, bitrate=int(job.params["bitrate"]), info=info), target],
        progress=progress,
    )
    progress.done()
    before, after = job.source.stat().st_size, target.stat().st_size
    saved = f"{100 * (1 - after / before):.0f}% smaller" if before and after < before else "no smaller"
    return ok([target], f"Wrote {target.name} ({saved})")


def audio_trim(job: Job) -> Result:
    info = probe(job.source)
    _require_audio(info, job.source)
    start, end = _span(job.params, _duration(info))
    ext = job.action.ext_for(job.source) or ""
    target = job.output_path()
    progress = _Progress(job, end - start)
    _ffmpeg(
        [
            "-ss", f"{start:.3f}", "-i", job.source, "-t", f"{end - start:.3f}",
            "-vn", *_acodec(ext), target,
        ],
        progress=progress,
    )
    progress.done()
    return ok([target], f"Wrote {target.name} ({end - start:.2f}s)")


def audio_normalize(job: Job) -> Result:
    """EBU R128 loudness, the two pass way.

    One pass measures, the second applies the measurement, which is what makes
    the result exact rather than a guess from a single pass. loudnorm works at
    192 kHz internally, so the chain resamples back to the source rate or the
    encoder gets a rate it refuses.
    """
    info = probe(job.source)
    _require_audio(info, job.source)
    ext = job.action.ext_for(job.source) or ""
    rate = _sample_rate(info)
    target_lufs = float(job.params["target"])
    duration = _duration(info)
    progress = _Progress(job, duration, steps=2)

    measure = f"loudnorm=I={target_lufs}:TP=-1.5:LRA=11:print_format=json"
    log = _ffmpeg(
        ["-i", job.source, "-vn", "-af", measure, "-f", "null", os.devnull],
        progress=progress.step(1),
        want_log=True,
    )
    measured = _loudnorm_values(log)

    target = job.output_path()
    applied = (
        f"loudnorm=I={target_lufs}:TP=-1.5:LRA=11:linear=true:print_format=summary"
        f":measured_I={measured['input_i']}:measured_TP={measured['input_tp']}"
        f":measured_LRA={measured['input_lra']}:measured_thresh={measured['input_thresh']}"
        f":offset={measured['target_offset']},aresample={rate}"
    )
    _ffmpeg(
        ["-i", job.source, "-vn", "-af", applied, *_acodec(ext), target],
        progress=progress.step(2),
    )
    progress.done()
    return ok(
        [target],
        f"Wrote {target.name} at {target_lufs:g} LUFS (was {measured['input_i']} LUFS)",
    )


def _loudnorm_values(log: str) -> dict[str, str]:
    """The JSON block loudnorm prints at the end of the measure pass."""
    start = log.rfind("{")
    end = log.rfind("}")
    if start == -1 or end < start:
        raise RunnerFailed(
            "ffmpeg did not report a loudness measurement, so normalising would be a guess",
            code="no_measurement",
        )
    try:
        values = json.loads(log[start : end + 1])
    except json.JSONDecodeError:
        raise RunnerFailed("the loudness measurement could not be read", code="no_measurement") from None
    wanted = ("input_i", "input_tp", "input_lra", "input_thresh", "target_offset")
    missing = [key for key in wanted if key not in values]
    if missing:
        raise RunnerFailed(
            f"the loudness measurement is missing {missing[0]}", code="no_measurement"
        )
    # A silent file measures as -inf, which the second pass cannot use.
    for key in wanted:
        if "inf" in str(values[key]).lower():
            raise UnsupportedInput(
                "this file is silent, so there is no loudness to normalise", code="silent"
            )
    return {key: str(values[key]) for key in wanted}


def audio_bleep(job: Job) -> Result:
    """Replace the audio inside each range with a tone.

    The clean track is silenced inside the ranges and a sine source is silenced
    outside them, then the two are mixed with ``normalize=0`` so the untouched
    part keeps its level.
    """
    info = probe(job.source)
    _require_audio(info, job.source)
    duration = _duration(info)
    ranges = _ranges(job.params["ranges"], duration)
    frequency = int(job.params["frequency"])
    rate = _sample_rate(info)
    layout = "mono" if _channels(info) == 1 else "stereo"
    ext = job.action.ext_for(job.source) or ""
    span = _between(ranges)
    tone_length = duration if duration else max(end for _, end in ranges)

    target = job.output_path()
    shape = f"aformat=sample_fmts=fltp:channel_layouts={layout}:sample_rates={rate}"
    graph = (
        f"[0:a]{shape},volume=0:enable='{span}'[clean];"
        f"[1:a]{shape},volume=0:enable='not({span})'[tone];"
        f"[clean][tone]amix=inputs=2:duration=first:normalize=0[a]"
    )
    progress = _Progress(job, duration)
    _ffmpeg(
        [
            "-i", job.source,
            "-f", "lavfi", "-i",
            f"sine=frequency={frequency}:duration={tone_length + 0.5:.3f}:sample_rate={rate}",
            "-filter_complex", graph, "-map", "[a]", *_acodec(ext), target,
        ],
        progress=progress,
    )
    progress.done()
    noun = "range" if len(ranges) == 1 else "ranges"
    return ok([target], f"Wrote {target.name} with {len(ranges)} bleeped {noun}")


def audio_channels(job: Job) -> Result:
    info = probe(job.source)
    _require_audio(info, job.source)
    wanted = 1 if job.params["channels"] == "mono" else 2
    ext = job.action.ext_for(job.source) or ""
    target = job.output_path()
    progress = _Progress(job, _duration(info))
    _ffmpeg(
        ["-i", job.source, "-vn", "-ac", str(wanted), *_acodec(ext), target],
        progress=progress,
    )
    progress.done()
    return ok([target], f"Wrote {target.name} as {job.params['channels']}")


def audio_visualize(job: Job) -> Result:
    """A waveform video of the track, which is the spec's Visualize tool."""
    info = probe(job.source)
    _require_audio(info, job.source)
    width = _even(int(job.params["width"]))
    height = _even(int(job.params["height"]))
    target = job.output_path()
    graph = (
        f"[0:a]showwaves=s={width}x{height}:mode=line:rate=25:colors=0x4aa3ff,"
        f"format=yuv420p[v]"
    )
    progress = _Progress(job, _duration(info))
    _ffmpeg(
        [
            "-i", job.source, "-filter_complex", graph,
            "-map", "[v]", "-map", "0:a",
            *_vcodec("mp4", quality="medium"), *_acodec_for_video("mp4", audio=True),
            target,
        ],
        progress=progress,
    )
    progress.done()
    return ok([target], f"Wrote {target.name}")


def audio_metadata(job: Job) -> Result:
    """Edit the title, artist and album. The stream is copied, so this is lossless.

    Each field is written twice: as a file tag (MP3 ID3, M4A, FLAC, WAV INFO,
    WMA) and as a tag on the audio stream, which is where OGG and Opus keep
    their Vorbis comments and where players read them. Without the stream tag
    a stream copy keeps the old comment and the edit is invisible. AIFF only
    stores a title natively, so it gets an ID3 chunk that holds all three.

    The output is read back with ffprobe and the message names only the fields
    that actually landed. A field the container could not store is reported as
    such, and a run that stored none of them fails.
    """
    fields = {
        key: str(job.params.get(key))
        for key in ("title", "artist", "album")
        if job.params.get(key) is not None
    }
    if not fields:
        raise InvalidParams("give a title, an artist or an album to write")
    info = probe(job.source)
    _require_audio(info, job.source)
    ext = job.action.ext_for(job.source) or ""
    target = job.output_path()
    args: list[Any] = ["-i", job.source, "-map", "0", "-c", "copy"]
    if ext == "aiff":
        args += ["-write_id3v2", "1"]
    for key, value in fields.items():
        args += ["-metadata", f"{key}={value}", "-metadata:s:a:0", f"{key}={value}"]
    progress = _Progress(job, _duration(info))
    _ffmpeg(args + [target], progress=progress)
    progress.done()

    stored = _stored_tags(probe(target))
    written = sorted(k for k, v in fields.items() if stored.get(k) == [v.strip()])
    missing = sorted(set(fields) - set(written))
    if not written:
        raise RunnerFailed(
            f"{LABELS.get(ext, ext.upper())} did not keep {', '.join(missing)}",
            code="metadata_not_written",
        )
    note = (
        f"; {', '.join(missing)} not supported in {LABELS.get(ext, ext.upper())}"
        if missing else ""
    )
    return ok([target], f"Wrote {target.name} ({', '.join(written)}{note})")


def _stored_tags(info: dict[str, Any]) -> dict[str, list[str]]:
    """Every value each tag holds, in the file tags and the audio stream tags.

    A field counts as written only when every place a player could read it
    holds the new value, so a stale stream comment beside a fresh file tag is
    caught rather than reported as done.
    """
    found: dict[str, list[str]] = {}
    sources = [info.get("format", {}).get("tags") or {}]
    sources += [s.get("tags") or {} for s in _streams(info, "audio")]
    for tags in sources:
        for key, value in tags.items():
            values = found.setdefault(key.lower(), [])
            text = str(value).strip()
            if text not in values:
                values.append(text)
    return found
