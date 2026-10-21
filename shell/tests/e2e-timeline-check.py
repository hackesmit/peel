"""Fixtures and assertions for the timeline and page editor end to end run.

The driver is PowerShell; what it asserts is about seconds, sound levels,
blurred pixels and PDF pages, so it asks this script, run with the python of
the engine venv under test. Media is made and read with the ffmpeg and
ffprobe the engine itself finds (peel_engine.backends), and PDFs with
PyMuPDF, the library pdf.organize writes them with.

    python e2e-timeline-check.py make   <folder>
    python e2e-timeline-check.py probe  <file>
    python e2e-timeline-check.py level  <file> <from> <to>
    python e2e-timeline-check.py detail <file> <seconds> <x> <y> <w> <h>
    python e2e-timeline-check.py pdf    <file>

Every subcommand prints one JSON object on stdout.
"""

import io
import json
import math
import subprocess
import sys
from array import array
from pathlib import Path

from peel_engine import backends


def ffmpeg() -> str:
    return str(backends.require("ffmpeg"))


def ffprobe() -> str:
    return str(backends.require("ffprobe"))


def run(args: list) -> bytes:
    return subprocess.run(args, check=True, capture_output=True).stdout


def make(folder: Path) -> dict:
    """One source per scenario, so no two outputs can collide."""
    folder.mkdir(parents=True, exist_ok=True)
    made = {}

    def video(name: str, seconds: float) -> None:
        # testsrc under heavy grain: every pixel differs from its neighbours,
        # so a blur anywhere in the frame flattens the edges there (an edge
        # mean near 80 drops below 10), where testsrc's flat bars alone
        # barely change.
        run([ffmpeg(), "-v", "error", "-y",
             "-f", "lavfi", "-i", f"testsrc=duration={seconds}:size=320x240:rate=25",
             "-f", "lavfi", "-i", f"sine=frequency=440:duration={seconds}",
             "-vf", "noise=alls=80:allf=t", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest",
             str(folder / name)])
        made[name] = f"{seconds}s 320x240 grainy testsrc with a tone"

    for name in ("trim.mp4", "split.mp4", "redact.mp4", "crop.mp4", "keep.mp4"):
        video(name, 4)

    # Silence, so a bleep is the only sound in the output.
    run([ffmpeg(), "-v", "error", "-y", "-f", "lavfi", "-i", "anullsrc=r=44100:cl=mono",
         "-t", "4", "-c:a", "libmp3lame", "-b:a", "128k", str(folder / "quiet.mp3")])
    made["quiet.mp3"] = "4s of silence"

    run([ffmpeg(), "-v", "error", "-y", "-f", "lavfi", "-i", "sine=frequency=330:duration=4",
         "-c:a", "libmp3lame", "-b:a", "128k", str(folder / "clip.mp3")])
    made["clip.mp3"] = "4s tone"

    run([ffmpeg(), "-v", "error", "-y", "-f", "lavfi", "-i", "sine=frequency=550:duration=2",
         "-metadata", "title=Old title", "-metadata", "artist=Old artist",
         "-c:a", "libmp3lame", "-b:a", "128k", str(folder / "song.mp3")])
    made["song.mp3"] = "2s tone tagged Old title / Old artist"

    import pymupdf

    def pdf(name: str, pages: int) -> None:
        doc = pymupdf.open()
        for number in range(1, pages + 1):
            page = doc.new_page()
            page.insert_text((72, 200), f"PAGE {number}", fontsize=64)
        doc.save(str(folder / name))
        doc.close()
        made[name] = f"{pages} pages, each saying PAGE n"

    pdf("pages.pdf", 3)
    pdf("parts.pdf", 4)
    return {"folder": str(folder), "files": made}


def probe(path: Path) -> dict:
    info = json.loads(run([ffprobe(), "-v", "error", "-show_format", "-show_streams",
                           "-of", "json", str(path)]))
    video = [s for s in info.get("streams", []) if s.get("codec_type") == "video"]
    tags = {}
    for source in [info.get("format", {}).get("tags") or {}] + [
            s.get("tags") or {} for s in info.get("streams", []) if s.get("codec_type") == "audio"]:
        for key, value in source.items():
            tags.setdefault(key.lower(), value)
    return {
        "duration": float(info.get("format", {}).get("duration") or 0),
        "width": int(video[0]["width"]) if video else 0,
        "height": int(video[0]["height"]) if video else 0,
        "title": tags.get("title"),
        "artist": tags.get("artist"),
        "album": tags.get("album"),
    }


def level(path: Path, start: float, end: float) -> dict:
    """RMS of the sound between two times, 0 to 1."""
    raw = run([ffmpeg(), "-v", "error", "-ss", f"{start:.3f}", "-t", f"{end - start:.3f}",
               "-i", str(path), "-ac", "1", "-ar", "8000", "-f", "s16le", "-"])
    samples = array("h")
    samples.frombytes(raw[: len(raw) // 2 * 2])
    if not samples:
        return {"rms": 0.0, "samples": 0}
    rms = math.sqrt(sum(s * s for s in samples) / len(samples)) / 32768.0
    return {"rms": round(rms, 5), "samples": len(samples)}


def detail(path: Path, seconds: float, x: int, y: int, w: int, h: int) -> dict:
    """How busy a rectangle of one frame is: the mean of its edge map.

    Measure well inside a blurred box: its border is a seam between blurred
    and sharp pixels, which is an edge of its own.
    """
    from PIL import Image, ImageFilter, ImageStat

    png = run([ffmpeg(), "-v", "error", "-ss", f"{seconds:.3f}", "-i", str(path),
               "-frames:v", "1", "-f", "image2pipe", "-vcodec", "png", "-"])
    frame = Image.open(io.BytesIO(png)).convert("L")
    edges = frame.crop((x, y, x + w, y + h)).filter(ImageFilter.FIND_EDGES)
    return {"edges": round(ImageStat.Stat(edges).mean[0], 3), "size": list(frame.size)}


def pdf(path: Path) -> dict:
    import pymupdf

    doc = pymupdf.open(str(path))
    try:
        return {
            "pages": doc.page_count,
            "texts": [" ".join(page.get_text().split()) for page in doc],
            "rotations": [page.rotation for page in doc],
        }
    finally:
        doc.close()


def main(argv: list) -> int:
    command = argv[1]
    if command == "make":
        out = make(Path(argv[2]))
    elif command == "probe":
        out = probe(Path(argv[2]))
    elif command == "level":
        out = level(Path(argv[2]), float(argv[3]), float(argv[4]))
    elif command == "detail":
        out = detail(Path(argv[2]), float(argv[3]), *(int(v) for v in argv[4:8]))
    elif command == "pdf":
        out = pdf(Path(argv[2]))
    else:
        out = {"error": f"unknown command {command}"}
    print(json.dumps(out))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
