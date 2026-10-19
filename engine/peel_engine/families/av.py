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
                  help="Optional. Overrides quality: the bitrate is fitted to this size."),
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


def probe(path: Path) -> dict[str, Any]:
    """ffprobe's format and streams for one file, as a dict."""
    args = [
        _exe("ffprobe"), "-v", "error", "-show_format", "-show_streams",
        "-of", "json", str(path),
    ]
    try:
        proc = subprocess.run(args, capture_output=True, text=True, timeout=120)
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


def _frame_size(info: dict[str, Any], path: Path) -> tuple[int, int]:
    stream = _require_video(info, path)
    try:
        return int(stream["width"]), int(stream["height"])
    except (KeyError, TypeError, ValueError):
        raise UnsupportedInput(f"{path.name} does not report a frame size") from None


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
    command += [str(a) for a in args]

    with tempfile.TemporaryFile(mode="w+", encoding="utf-8", errors="replace") as log:
        try:
            proc = subprocess.Popen(
                command,
                stdin=subprocess.DEVNULL,
                stdout=subprocess.PIPE if progress is not None else subprocess.DEVNULL,
                stderr=log,
                text=True,
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


def _crop_filter(params: dict[str, Any], width: int, height: int) -> tuple[str, int, int, int, int]:
    """Validate a rectangle against the frame and return the crop filter."""
    x, y = int(params["x"]), int(params["y"])
    w, h = _even(int(params["w"])), _even(int(params["h"]))
    if w <= 0 or h <= 0:
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
        total_kbps = float(target_mb) * 8 * 1024 / duration
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
    progress = _Progress(job, duration)
    _ffmpeg(
        [
            "-i", job.source,
            *_vcodec(ext, quality=job.params["quality"], video_kbps=video_kbps), *_filters(),
            *audio_args,
            target,
        ],
        progress=progress,
    )
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


def _cut_points(params: dict[str, Any], duration: float) -> list[tuple[float, float]]:
    """Segment boundaries from ``at`` timestamps or an ``every`` interval."""
    from ..registry import _parse_timecode

    if duration <= 0:
        raise RunnerFailed("the duration cannot be read, so the file cannot be split",
                           code="no_duration")
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
        position = step
        while position < duration - 0.05:
            cuts.append(position)
            position += step
    else:
        raise InvalidParams("give timestamps to cut at, or an interval in seconds")
    inside = sorted({round(c, 3) for c in cuts if 0 < c < duration})
    if not inside:
        raise InvalidParams(
            f"no cut falls inside the file, which is {duration:.2f}s long"
        )
    bounds = [0.0] + inside + [duration]
    return [(bounds[i], bounds[i + 1]) for i in range(len(bounds) - 1)]


def video_split(job: Job) -> Result:
    info = probe(job.source)
    _require_video(info, job.source)
    duration = _duration(info)
    segments = _cut_points(job.params, duration)
    ext = job.action.ext_for(job.source) or ""
    has_audio = bool(_streams(info, "audio"))
    quality = job.params["quality"]
    progress = _Progress(job, duration, steps=len(segments))
    outputs: list[Path] = []
    for index, (start, end) in enumerate(segments, start=1):
        target = job.output_path(suffix=str(index))
        _ffmpeg(
            [
                "-ss", f"{start:.3f}", "-i", job.source, "-t", f"{end - start:.3f}",
                *_vcodec(ext, quality=quality), *_filters(),
                *_acodec_for_video(ext, audio=has_audio),
                target,
            ],
            progress=progress.step(index, duration=end - start),
        )
        outputs.append(target)
    progress.done()
    return ok(outputs, f"Wrote {len(outputs)} parts")


def _join_signature(info: dict[str, Any]) -> tuple:
    video = _streams(info, "video")[:1]
    audio = _streams(info, "audio")[:1]
    return (
        tuple(
            (s.get("codec_name"), s.get("width"), s.get("height"), s.get("pix_fmt"))
            for s in video
        ),
        tuple((s.get("codec_name"), s.get("sample_rate"), s.get("channels")) for s in audio),
    )


def video_join(job: Job) -> Result:
    """Concatenate clips. Identical streams are copied; anything else is
    re-encoded to the first clip's shape, which is what the spec's "join
    (multi-file)" has to mean when a drag mixes a phone clip and a screen grab.
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
    every_has_audio = all(_streams(i, "audio") for i in infos)

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
        if every_has_audio:
            legs.append(
                f"[{index}:a]aresample=48000,"
                f"aformat=sample_fmts=fltp:channel_layouts=stereo[a{index}]"
            )
            labels.append(f"[a{index}]")
    tail = f"concat=n={len(job.inputs)}:v=1:a={1 if every_has_audio else 0}"
    tail += "[v][a]" if every_has_audio else "[v]"
    graph = ";".join(legs + ["".join(labels) + tail])

    args: list[str] = []
    for path in job.inputs:
        args += ["-i", str(path)]
    args += ["-filter_complex", graph, "-map", "[v]"]
    if every_has_audio:
        args += ["-map", "[a]"]
    args += [
        *_vcodec(ext, quality=job.params["quality"]),
        *_acodec_for_video(ext, audio=every_has_audio),
        str(target),
    ]
    _ffmpeg(args, progress=progress)
    progress.done()
    note = "" if every_has_audio else " (a clip had no audio, so the join is silent)"
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


def video_redact(job: Job) -> Result:
    """Blur a rectangle, over the whole clip or only inside a time range."""
    info = probe(job.source)
    width, height = _frame_size(info, job.source)
    crop, x, y, w, h = _crop_filter(job.params, width, height)
    duration = _duration(info)
    ext = job.action.ext_for(job.source) or ""
    has_audio = bool(_streams(info, "audio"))
    strength = int(job.params["strength"])

    window = ""
    if job.params.get("start") is not None or job.params.get("end") is not None:
        start, end = _span(job.params, duration, label="Redact")
        window = f":enable='between(t,{start:.3f},{end:.3f})'"

    target = job.output_path()
    graph = (
        f"[0:v]{crop},boxblur={strength}:2[blur];"
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
    duration = _duration(info)
    every = job.params.get("every")
    if every:
        step = float(every)
        times = []
        position = 0.0
        while position < duration or (not duration and not times):
            times.append(position)
            position += step
            if len(times) > 2000:
                raise InvalidParams(
                    f"an interval of {step:g}s over {duration:.0f}s would be more than "
                    "2000 frames. Use a longer interval."
                )
    else:
        at = float(job.params.get("time") or 0.0)
        if duration and at >= duration:
            raise InvalidParams(
                f"{at:g}s is past the end of the file ({duration:.2f}s)"
            )
        times = [at]

    progress = _Progress(job, duration, steps=len(times))
    outputs: list[Path] = []
    for index, at in enumerate(times, start=1):
        suffix = str(index) if len(times) > 1 else ""
        target = job.output_path(suffix=suffix)
        _ffmpeg(
            ["-ss", f"{at:.3f}", "-i", job.source, "-frames:v", "1", "-update", "1", target],
            progress=progress.step(index, duration=max(duration, 0.001)),
        )
        outputs.append(target)
    progress.done()
    noun = "frame" if len(outputs) == 1 else "frames"
    return ok(outputs, f"Saved {len(outputs)} {noun}")


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
    """Edit the title, artist and album. The stream is copied, so this is lossless."""
    fields = {
        key: job.params.get(key)
        for key in ("title", "artist", "album")
        if job.params.get(key) is not None
    }
    if not fields:
        raise InvalidParams("give a title, an artist or an album to write")
    info = probe(job.source)
    _require_audio(info, job.source)
    target = job.output_path()
    args: list[str] = ["-i", str(job.source), "-map", "0", "-c", "copy"]
    for key, value in fields.items():
        args += ["-metadata", f"{key}={value}"]
    progress = _Progress(job, _duration(info))
    _ffmpeg(args + [str(target)], progress=progress)
    progress.done()
    written = ", ".join(sorted(fields))
    return ok([target], f"Wrote {target.name} ({written})")
