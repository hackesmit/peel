"""Output naming rules.

Two rules from the spec, and they are the whole module:

* output goes next to the source (unless an explicit out dir is given),
* nothing is ever overwritten: a taken name grows a " 2", " 3", ... suffix.

A conversion writes ``<name>.<ext>``; a tool writes ``<name> (<Tool>).<ext>``.

"Never overwrite" has to hold across processes too (Daniel drops the same file
twice, or a slow ffmpeg run overlaps a second action). Checking ``exists()``
and writing later is a race, so a name is CLAIMED: ``unique_path`` creates the
file with an exclusive create (``O_CREAT | O_EXCL``), or the folder with
``mkdir``, and moves on to the next suffix when that fails. Whoever gets the
create owns the name. The caller receives a path that already exists as an
empty placeholder and must fill it in place: open it for writing, pass the
tool its overwrite flag (``ffmpeg -y``, ``7z -y``), or build the result
elsewhere and ``os.replace`` it onto the claim. ``release`` removes a claim
that was never filled. Registry.Job wraps all of this for runners.

Names within one batch are compared case-insensitively, as Windows does, so a
batch cannot promise both ``a.md`` and ``A.md``.
"""

from __future__ import annotations

import os
from pathlib import Path
from typing import Iterable

#: Where the " 2", " 3", ... search gives up. A directory with this many
#: collisions is a bug somewhere else, not a naming problem.
MAX_SUFFIX = 10000


def normalise_ext(ext: str) -> str:
    """Return a bare lowercase extension: ``.JPG`` and ``jpg`` both give ``jpg``."""
    return ext.strip().lstrip(".").lower()


def path_key(path: Path | str) -> str:
    """Comparison key for a path: case-insensitive, as on Windows."""
    return os.path.normpath(str(path)).casefold()


def claim(path: Path, *, directory: bool = False) -> bool:
    """Atomically create ``path`` (empty file or folder). False if it exists."""
    try:
        if directory:
            os.mkdir(path)
        else:
            fd = os.open(path, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o666)
            os.close(fd)
    except FileExistsError:
        return False
    except IsADirectoryError:
        return False
    except PermissionError:
        # Windows reports a name held by a folder or a file pending delete
        # this way; anything else is a real error for the caller.
        if path.exists():
            return False
        raise
    return True


def release(path: Path, *, only_if_empty: bool = True) -> None:
    """Remove a claim. By default only while it is still an empty placeholder."""
    path = Path(path)
    try:
        if path.is_dir():
            if not any(path.iterdir()):
                path.rmdir()
        elif path.is_file():
            if not only_if_empty or path.stat().st_size == 0:
                path.unlink()
    except OSError:
        pass


def unique_path(
    path: Path, reserved: Iterable[Path] = (), *, directory: bool = False
) -> Path:
    """Claim ``path`` if it is free, else the same name with " 2", " 3", ...

    The returned path exists when this returns (see the module docstring).
    ``reserved`` holds paths already promised to another output of the same
    batch, compared case-insensitively.
    """
    path = Path(path)
    taken = {path_key(p) for p in reserved}

    def candidates() -> Iterable[Path]:
        yield path
        stem, suffix = path.stem, path.suffix
        for n in range(2, MAX_SUFFIX):
            yield path.parent / f"{stem} {n}{suffix}"

    for candidate in candidates():
        if path_key(candidate) in taken:
            continue
        if claim(candidate, directory=directory):
            return candidate
    raise RuntimeError(f"no free name for {path} after {MAX_SUFFIX} tries")


def output_path(
    source: Path,
    ext: str,
    tag: str | None = None,
    out_dir: Path | None = None,
    reserved: Iterable[Path] = (),
    directory: bool = False,
) -> Path:
    """Claim the never-overwriting output path for one source file.

    ``tag`` is the tool name that goes in parentheses; conversions pass None.
    The path exists as an empty placeholder when this returns.
    """
    source = Path(source)
    ext = normalise_ext(ext)
    stem = source.stem if source.suffix else source.name
    name = f"{stem} ({tag})" if tag else stem
    folder = Path(out_dir) if out_dir is not None else source.parent
    target = folder / (f"{name}.{ext}" if ext else name)
    return unique_path(target, reserved, directory=directory)
