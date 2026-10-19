"""Output naming rules.

Two rules from the spec, and they are the whole module:

* output goes next to the source (unless an explicit out dir is given),
* nothing is ever overwritten: a taken name grows a " 2", " 3", ... suffix.

A conversion writes ``<name>.<ext>``; a tool writes ``<name> (<Tool>).<ext>``.
"""

from __future__ import annotations

from pathlib import Path
from typing import Iterable

#: Where the " 2", " 3", ... search gives up. A directory with this many
#: collisions is a bug somewhere else, not a naming problem.
MAX_SUFFIX = 10000


def normalise_ext(ext: str) -> str:
    """Return a bare lowercase extension: ``.JPG`` and ``jpg`` both give ``jpg``."""
    return ext.strip().lstrip(".").lower()


def unique_path(path: Path, reserved: Iterable[Path] = ()) -> Path:
    """Return ``path`` if it is free, else the same name with " 2", " 3", ...

    ``reserved`` holds paths that do not exist on disk yet but are already
    promised to an output of the same job, so a batch cannot collide with
    itself.
    """
    path = Path(path)
    taken = {Path(p) for p in reserved}
    if not path.exists() and path not in taken:
        return path

    stem, suffix = path.stem, path.suffix
    parent = path.parent
    for n in range(2, MAX_SUFFIX):
        candidate = parent / f"{stem} {n}{suffix}"
        if not candidate.exists() and candidate not in taken:
            return candidate
    raise RuntimeError(f"no free name for {path} after {MAX_SUFFIX} tries")


def output_path(
    source: Path,
    ext: str,
    tag: str | None = None,
    out_dir: Path | None = None,
    reserved: Iterable[Path] = (),
) -> Path:
    """Build the never-overwriting output path for one source file.

    ``tag`` is the tool name that goes in parentheses; conversions pass None.
    """
    source = Path(source)
    ext = normalise_ext(ext)
    stem = source.stem if source.suffix else source.name
    name = f"{stem} ({tag})" if tag else stem
    directory = Path(out_dir) if out_dir is not None else source.parent
    return unique_path(directory / f"{name}.{ext}", reserved)
