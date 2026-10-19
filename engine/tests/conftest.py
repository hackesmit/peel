"""Shared fixtures.

The demo family below is the stand in for the real families that land in later
beads. It is registered through the public ``Registry.add`` call and its
runners are resolved through the same dotted ``module:function`` path, so the
tests exercise the real mechanism rather than a shortcut around it.
"""

from __future__ import annotations

from pathlib import Path

import pytest

from peel_engine import backends
from peel_engine.registry import ACTION, ANY, CONVERT, SAME, SHELL, TOOL, Job, Param, Registry
from peel_engine.result import Result, ok

RUNNER = "conftest"


# --- demo runners -----------------------------------------------------------


def write_upper(job: Job) -> Result:
    target = job.output_path()
    target.write_text(job.source.read_text(encoding="utf-8").upper(), encoding="utf-8")
    return ok([target], f"Wrote {target.name}")


def compress(job: Job) -> Result:
    target = job.output_path()
    quality = job.params["quality"]
    target.write_text(f"q={quality}\n{job.source.read_text(encoding='utf-8')}", encoding="utf-8")
    return ok([target], f"Compressed to {target.name}")


def crop(job: Job) -> Result:
    target = job.output_path()
    box = (job.params["x"], job.params["y"], job.params["w"], job.params["h"])
    target.write_text(f"crop={box}", encoding="utf-8")
    return ok([target], f"Cropped to {target.name}")


def merge(job: Job) -> Result:
    target = job.output_path()
    target.write_text(
        "".join(p.read_text(encoding="utf-8") for p in job.inputs), encoding="utf-8"
    )
    return ok([target], f"Merged {len(job.inputs)} files")


def explode(job: Job) -> Result:
    """A runner that decides its own extension and writes several files."""
    targets = [job.output_path(ext="part", suffix=str(n)) for n in (1, 2)]
    for n, target in enumerate(targets, start=1):
        target.write_text(f"part {n}", encoding="utf-8")
    return ok(targets, f"Wrote {len(targets)} parts")


def describe(job: Job) -> Result:
    return ok([], f"{len(job.inputs)} items", names=[p.name for p in job.inputs])


def fail_on_bad(job: Job) -> Result:
    from peel_engine.result import failure

    if "bad" in job.source.name:
        return failure(f"{job.source.name} is bad", code="bad_file")
    target = job.output_path()
    target.write_text("fine", encoding="utf-8")
    return ok([target], "fine")


def boom(job: Job) -> Result:
    raise ValueError("backend exploded")


def bad_return(job: Job) -> Result:
    return "not a Result"  # type: ignore[return-value]


def needs_magick(job: Job) -> Result:
    return ok([], "never reached")


def half_write_then_boom(job: Job) -> Result:
    """Claims two names, half writes one, then dies like a crashed ffmpeg."""
    first = job.output_path()
    job.output_path(suffix="extra")
    first.write_text("half", encoding="utf-8")
    raise RuntimeError("encoder crashed")


def claim_two_report_one(job: Job) -> Result:
    used = job.output_path()
    job.output_path(suffix="unused")
    used.write_text("used", encoding="utf-8")
    return ok([used], "one of two")


def external_tool(job: Job) -> Result:
    """The soffice pattern: the tool picks its own name, the runner moves it
    onto the claim with os.replace."""
    import os

    target = job.output_path()
    scratch = target.parent / ".peel-scratch-output"
    scratch.write_text("from the tool", encoding="utf-8")
    os.replace(scratch, target)
    return ok([target], f"Wrote {target.name}")


def extract_to_folder(job: Job) -> Result:
    folder = job.output_path(ext="", directory=True)
    (folder / "inside.txt").write_text("x", encoding="utf-8")
    return ok([folder], f"Extracted to {folder.name}")


# --- fixtures ---------------------------------------------------------------


@pytest.fixture
def make_file(tmp_path: Path):
    """Create a file under tmp_path and return its path."""

    def _make(name: str, text: str = "hello\n") -> Path:
        path = tmp_path / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8")
        return path

    return _make


@pytest.fixture
def registry() -> Registry:
    """A registry holding only the demo family."""
    reg = Registry()
    reg.add(
        id="demo.to_up",
        family=CONVERT,
        label="UP",
        group="Demo",
        inputs=("txt",),
        output_ext="up",
        runner=f"{RUNNER}:write_upper",
    )
    reg.add(
        id="demo.compress",
        family=TOOL,
        label="Compress",
        group="Demo",
        inputs=("txt", "log"),
        output_ext=SAME,
        params=(
            Param("quality", "int", "Quality", default=80, minimum=1, maximum=100),
        ),
        runner=f"{RUNNER}:compress",
    )
    reg.add(
        id="demo.crop",
        family=TOOL,
        label="Crop",
        group="Demo",
        inputs=("txt",),
        output_ext=SAME,
        needs_editor=True,
        max_files=1,
        params=tuple(
            Param(name, "int", name.upper(), required=True) for name in ("x", "y", "w", "h")
        ),
        runner=f"{RUNNER}:crop",
    )
    reg.add(
        id="demo.merge",
        family=TOOL,
        label="Merge",
        group="Demo",
        inputs=("txt",),
        output_ext="txt",
        mode="combine",
        min_files=2,
        runner=f"{RUNNER}:merge",
    )
    reg.add(
        id="demo.explode",
        family=TOOL,
        label="Explode",
        group="Demo",
        inputs=("txt",),
        output_ext=None,
        runner=f"{RUNNER}:explode",
    )
    reg.add(
        id="demo.describe",
        family=ACTION,
        label="Describe",
        group="Files",
        inputs=(ANY,),
        accepts_dirs=True,
        mode="combine",
        runner=f"{RUNNER}:describe",
    )
    reg.add(
        id="demo.float",
        family=ACTION,
        label="Float",
        group="Files",
        inputs=(ANY,),
        accepts_dirs=True,
        handled_by=SHELL,
    )
    reg.add(
        id="demo.fail_on_bad",
        family=TOOL,
        label="Picky",
        group="Demo",
        inputs=("txt",),
        output_ext="out",
        runner=f"{RUNNER}:fail_on_bad",
    )
    reg.add(
        id="demo.boom",
        family=TOOL,
        label="Boom",
        group="Demo",
        inputs=("txt",),
        output_ext="out",
        runner=f"{RUNNER}:boom",
    )
    reg.add(
        id="demo.bad_return",
        family=TOOL,
        label="Bad return",
        group="Demo",
        inputs=("txt",),
        output_ext="out",
        runner=f"{RUNNER}:bad_return",
    )
    reg.add(
        id="demo.needs_magick",
        family=CONVERT,
        label="Magick",
        group="Demo",
        inputs=("txt",),
        output_ext="out",
        backends=("magick",),
        runner=f"{RUNNER}:needs_magick",
    )
    reg.add(
        id="demo.half_write",
        family=TOOL,
        label="Crash",
        group="Demo",
        inputs=("txt",),
        output_ext="out",
        runner=f"{RUNNER}:half_write_then_boom",
    )
    reg.add(
        id="demo.claim_two",
        family=TOOL,
        label="Two claims",
        group="Demo",
        inputs=("txt",),
        output_ext="out",
        runner=f"{RUNNER}:claim_two_report_one",
    )
    reg.add(
        id="demo.external",
        family=CONVERT,
        label="External",
        group="Demo",
        inputs=("txt",),
        output_ext="ext",
        runner=f"{RUNNER}:external_tool",
    )
    reg.add(
        id="demo.extract",
        family=ACTION,
        label="Extract",
        group="Demo",
        inputs=("txt",),
        runner=f"{RUNNER}:extract_to_folder",
    )
    return reg


@pytest.fixture
def fake_backends():
    """Pin backend lookups so tests do not depend on what hqbox has installed."""

    def _set(**found: bool) -> None:
        backends.clear_cache()
        for name, present in found.items():
            backends._cache[name] = Path(f"/fake/{name}") if present else None

    backends.clear_cache()
    yield _set
    backends.clear_cache()
