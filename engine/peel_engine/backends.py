"""Locating the external backends the families shell out to.

Lookup order for an executable backend:

1. ``PEEL_BACKEND_<NAME>`` in the environment, pointing at the executable.
2. ``PATH`` (``shutil.which``).
3. The standard Windows install directories, as glob patterns. Folder
   variables (%ProgramFiles%, %ProgramFiles(x86)%, %LOCALAPPDATA%) come from
   the environment, with the stock locations searched as a fallback.

Module backends (pillow, PyMuPDF, ...) are probed with ``find_spec``, which
locates without importing, so ``peel options`` stays cheap.

Nothing here imports a backend. Family runners import their heavy dependencies
inside the runner function, never at module scope.
"""

from __future__ import annotations

import os
import re
import shutil
import subprocess
from dataclasses import dataclass, field
from importlib.util import find_spec
from pathlib import Path

EXE = "exe"
MODULE = "module"


@dataclass(frozen=True)
class Backend:
    name: str
    kind: str
    label: str
    used_for: str
    #: Executable names to try, in order (exe backends).
    commands: tuple[str, ...] = ()
    #: Glob patterns of standard Windows install locations (exe backends).
    windows_globs: tuple[str, ...] = ()
    #: Importable top level module name (module backends).
    module: str = ""
    #: Argument that makes the executable print its version.
    version_arg: str = "--version"
    #: What to install, shown by ``peel doctor`` when the backend is missing.
    install: str = ""


BACKENDS: tuple[Backend, ...] = (
    Backend(
        name="ffmpeg",
        kind=EXE,
        label="FFmpeg",
        used_for="audio and video conversion and tools",
        commands=("ffmpeg",),
        windows_globs=(
            r"%ProgramFiles%\ffmpeg\bin",
            r"C:\ffmpeg\bin",
            r"%LOCALAPPDATA%\Microsoft\WinGet\Links",
            r"%LOCALAPPDATA%\Microsoft\WinGet\Packages\Gyan.FFmpeg*\*\bin",
        ),
        version_arg="-version",
        install="winget install Gyan.FFmpeg",
    ),
    Backend(
        name="ffprobe",
        kind=EXE,
        label="FFprobe",
        used_for="reading media duration and streams",
        commands=("ffprobe",),
        windows_globs=(
            r"%ProgramFiles%\ffmpeg\bin",
            r"C:\ffmpeg\bin",
            r"%LOCALAPPDATA%\Microsoft\WinGet\Links",
            r"%LOCALAPPDATA%\Microsoft\WinGet\Packages\Gyan.FFmpeg*\*\bin",
        ),
        version_arg="-version",
        install="winget install Gyan.FFmpeg",
    ),
    Backend(
        name="magick",
        kind=EXE,
        label="ImageMagick",
        used_for="image conversion fallback",
        commands=("magick",),
        windows_globs=(r"%ProgramFiles%\ImageMagick*",),
        install="winget install ImageMagick.ImageMagick",
    ),
    Backend(
        name="vips",
        kind=EXE,
        label="libvips",
        used_for="fast image conversion",
        commands=("vips",),
        windows_globs=(r"%ProgramFiles%\vips*\bin", r"C:\vips\bin"),
        install="winget install libvips.libvips",
    ),
    Backend(
        name="soffice",
        kind=EXE,
        label="LibreOffice",
        used_for="documents, slides and spreadsheets",
        commands=("soffice", "libreoffice"),
        windows_globs=(
            r"%ProgramFiles%\LibreOffice\program",
            r"%ProgramFiles(x86)%\LibreOffice\program",
        ),
        install="winget install TheDocumentFoundation.LibreOffice",
    ),
    Backend(
        name="pandoc",
        kind=EXE,
        label="Pandoc",
        used_for="document conversion",
        commands=("pandoc",),
        windows_globs=(r"%ProgramFiles%\Pandoc", r"%LOCALAPPDATA%\Pandoc"),
        install="winget install JohnMacFarlane.Pandoc",
    ),
    Backend(
        name="7z",
        kind=EXE,
        label="7-Zip",
        used_for="archives",
        commands=("7z", "7zz", "7za"),
        windows_globs=(r"%ProgramFiles%\7-Zip", r"%ProgramFiles(x86)%\7-Zip"),
        version_arg="",
        install="winget install 7zip.7zip",
    ),
    Backend(
        name="pillow",
        kind=MODULE,
        label="Pillow",
        used_for="image conversion and image tools",
        module="PIL",
        install="pip install pillow",
    ),
    Backend(
        name="pillow_heif",
        kind=MODULE,
        label="pillow-heif",
        used_for="HEIC and AVIF images",
        module="pillow_heif",
        install="pip install pillow-heif",
    ),
    Backend(
        name="pymupdf",
        kind=MODULE,
        label="PyMuPDF",
        used_for="PDF reading, rasterising and page tools",
        module="fitz",
        install="pip install pymupdf",
    ),
    Backend(
        name="pdf2docx",
        kind=MODULE,
        label="pdf2docx",
        used_for="PDF to DOCX",
        module="pdf2docx",
        install="pip install pdf2docx",
    ),
    Backend(
        name="openpyxl",
        kind=MODULE,
        label="openpyxl",
        used_for="spreadsheet reading and writing",
        module="openpyxl",
        install="pip install openpyxl",
    ),
    Backend(
        name="python_pptx",
        kind=MODULE,
        label="python-pptx",
        used_for="writing PPTX slides",
        module="pptx",
        install="pip install python-pptx",
    ),
)

BY_NAME: dict[str, Backend] = {b.name: b for b in BACKENDS}

_cache: dict[str, Path | None] = {}


def clear_cache() -> None:
    """Forget resolved locations. Tests and ``peel doctor`` use this."""
    _cache.clear()


def _env_override(name: str) -> Path | None:
    raw = os.environ.get(f"PEEL_BACKEND_{name.upper().replace('-', '_')}")
    if not raw:
        return None
    path = Path(os.path.expandvars(raw)).expanduser()
    return path if path.exists() else None


#: Where a Windows folder variable points when the environment does not say
#: (a service account, a stripped environment). The real variable wins and the
#: default is searched as well, so a non-default install and a default one are
#: both found.
_WINDOWS_DEFAULTS: dict[str, tuple[str, ...]] = {
    "PROGRAMFILES": (r"C:\Program Files",),
    "PROGRAMFILES(X86)": (r"C:\Program Files (x86)",),
    "PROGRAMW6432": (r"C:\Program Files",),
    "LOCALAPPDATA": (),  # derived from USERPROFILE below
}

_VAR = re.compile(r"%([^%]+)%")


def _env(name: str) -> str | None:
    """Case-insensitive environment lookup, as Windows does it."""
    wanted = name.upper()
    for key, value in os.environ.items():
        if key.upper() == wanted and value:
            return value
    return None


def _var_values(name: str) -> list[str]:
    values: list[str] = []
    real = _env(name)
    if real:
        values.append(real)
    defaults = list(_WINDOWS_DEFAULTS.get(name.upper(), ()))
    if name.upper() == "LOCALAPPDATA":
        profile = _env("USERPROFILE")
        if profile:
            defaults.append(profile.rstrip("\\/") + r"\AppData\Local")
    for value in defaults:
        if value.lower() not in (v.lower() for v in values):
            values.append(value)
    return values


def expand_windows(pattern: str) -> list[str]:
    """Every concrete form of a pattern with %VAR% folders, real value first.

    A variable that is unset and has no default drops the pattern.
    """
    results = [pattern]
    for name in _VAR.findall(pattern):
        values = _var_values(name)
        results = [r.replace(f"%{name}%", v) for r in results for v in values]
    seen: list[str] = []
    for r in results:
        if r.lower() not in (x.lower() for x in seen):
            seen.append(r)
    return seen


def _search_windows(backend: Backend) -> Path | None:
    for pattern in backend.windows_globs:
        for expanded in expand_windows(pattern):
            found = _search_windows_dir(backend, expanded)
            if found is not None:
                return found
    return None


def _search_windows_dir(backend: Backend, expanded: str) -> Path | None:
    root = Path(expanded[:3]) if len(expanded) > 2 and expanded[1] == ":" else Path("/")
    try:
        relative = str(Path(expanded).relative_to(root))
    except ValueError:
        return None
    for directory in sorted(root.glob(relative), reverse=True):
        for command in backend.commands:
            candidate = directory / f"{command}.exe"
            if candidate.exists():
                return candidate
    return None


def find(name: str) -> Path | None:
    """Return the location of a backend, or None when it is not installed.

    For a module backend the "location" is the directory of its spec, which is
    enough to prove it is importable.
    """
    if name in _cache:
        return _cache[name]
    backend = BY_NAME.get(name)
    if backend is None:
        raise KeyError(f"unknown backend: {name}")

    found: Path | None = None
    if backend.kind == MODULE:
        try:
            spec = find_spec(backend.module)
        except (ImportError, ValueError):
            spec = None
        if spec is not None:
            origin = spec.origin or (list(spec.submodule_search_locations or []) or [""])[0]
            found = Path(origin) if origin else Path(backend.module)
    else:
        found = _env_override(name)
        if found is None:
            for command in backend.commands:
                which = shutil.which(command)
                if which:
                    found = Path(which)
                    break
        if found is None:
            found = _search_windows(backend)

    _cache[name] = found
    return found


def available(name: str) -> bool:
    return find(name) is not None


def missing(names: tuple[str, ...]) -> list[str]:
    """Return the subset of ``names`` that is not installed."""
    return [n for n in names if not available(n)]


def require(name: str) -> Path:
    """Return the backend location, or raise a BackendMissing with a hint."""
    from .result import BackendMissing

    found = find(name)
    if found is None:
        backend = BY_NAME[name]
        hint = f" Install it with: {backend.install}" if backend.install else ""
        raise BackendMissing(
            f"{backend.label} is not installed, and it is needed for "
            f"{backend.used_for}.{hint}",
            backend=name,
        )
    return found


def version(name: str) -> str:
    """Best effort one line version string. Empty when it cannot be read."""
    backend = BY_NAME.get(name)
    found = find(name)
    if backend is None or found is None or backend.kind == MODULE:
        return _module_version(backend) if backend and backend.kind == MODULE else ""
    args = [str(found)] + ([backend.version_arg] if backend.version_arg else [])
    try:
        proc = subprocess.run(args, capture_output=True, text=True, timeout=10)
    except (OSError, subprocess.SubprocessError):
        return ""
    text = (proc.stdout or proc.stderr or "").strip()
    return text.splitlines()[0].strip() if text else ""


def _module_version(backend: Backend) -> str:
    from importlib.metadata import PackageNotFoundError, version as dist_version

    for dist in (backend.name.replace("_", "-"), backend.label.lower(), backend.module):
        try:
            return dist_version(dist)
        except (PackageNotFoundError, ValueError):
            continue
    return ""
