"""Archives: convert between ZIP, TAR, GZIP and 7Z, extract, and zip up a drag.

The Shift wheel's Archives group (conversions) plus two Ctrl wheel tools
(Extract, and Zip for a folder or multi-file drag). ZIP, TAR and GZIP are
handled with the Python standard library; 7Z and RAR go through the 7-Zip CLI
(the ``7z`` backend, which reads RAR but never writes it). GZIP cannot hold
more than one stream, so a "to GZIP" conversion writes a gzipped tar
(``.tar.gz``), the honest multi-file form.

Converting repacks: the source is extracted into a scratch folder and a fresh
archive is built from the tree, so a round trip preserves the files rather than
nesting one archive inside another.

Security: extraction never writes outside its destination folder. ZIP and TAR
members are placed one by one through a guard that refuses an absolute path, a
``..`` escape or a link that points outside the tree (the classic zip-slip).
7-Zip sanitises paths itself on extraction.
"""

from __future__ import annotations

import os
from pathlib import Path, PurePosixPath, PureWindowsPath

from ..registry import ANY, CONVERT, TOOL, Job, Registry
from ..result import Result, RunnerFailed, ok

#: Extensions read by the standard library, and by the 7-Zip CLI.
_STDLIB_IN = ("zip", "tar", "gz", "tgz")
_SEVENZIP_IN = ("7z", "rar")


def register(registry: Registry) -> None:
    for fmt, ext, label in (
        ("zip", "zip", "ZIP"),
        ("tar", "tar", "TAR"),
        ("gzip", None, "GZIP"),
        ("7z", "7z", "7Z"),
    ):
        inputs = tuple(e for e in (*_STDLIB_IN, *_SEVENZIP_IN) if e != ext)
        registry.add(
            id=f"archive.to_{fmt}",
            family=CONVERT,
            label=label,
            group="Archives",
            inputs=inputs,
            output_ext=ext,
            backends=("7z",) if fmt == "7z" else (),
            runner=f"peel_engine.families.archive:to_{fmt}",
        )
    registry.add(
        id="archive.extract",
        family=TOOL,
        label="Extract",
        group="Archives",
        inputs=(*_STDLIB_IN, *_SEVENZIP_IN),
        output_ext=None,
        name_tag="",  # the folder is named after the archive, with no tag
        runner="peel_engine.families.archive:extract",
    )
    registry.add(
        id="archive.zip",
        family=TOOL,
        label="Zip",
        group="Archives",
        inputs=(ANY,),
        accepts_dirs=True,
        output_ext="zip",
        mode="combine",
        name_tag="",  # a new archive, named after the first item, with no tag
        runner="peel_engine.families.archive:zip_up",
    )


# --- conversions ------------------------------------------------------------


def to_zip(job: Job) -> Result:
    return _convert(job, "zip")


def to_tar(job: Job) -> Result:
    return _convert(job, "tar")


def to_gzip(job: Job) -> Result:
    return _convert(job, "gzip")


def to_7z(job: Job) -> Result:
    return _convert(job, "7z")


def _convert(job: Job, fmt: str) -> Result:
    """Extract the source archive, then repack its tree into ``fmt``."""
    ext = "tar.gz" if fmt == "gzip" else fmt
    target = job.output_path(ext=ext)
    scratch = job.scratch_dir(near=target)
    _extract_archive(job.source, scratch)
    _make_archive(scratch, target, fmt)
    return ok([target], f"Repacked to {target.name}")


# --- tools ------------------------------------------------------------------


def extract(job: Job) -> Result:
    """Extract an archive into a sibling folder named after it."""
    folder = job.output_path(ext="", directory=True)
    _extract_archive(job.source, folder)
    count = sum(1 for _ in folder.rglob("*") if _.is_file())
    noun = "file" if count == 1 else "files"
    return ok([folder], f"Extracted {count} {noun} to {folder.name}")


def zip_up(job: Job) -> Result:
    """Zip a folder or a multi-file drag into one archive beside the first item."""
    import zipfile

    target = job.output_path(source=job.source, ext="zip")
    with zipfile.ZipFile(target, "w", compression=zipfile.ZIP_DEFLATED) as zf:
        for item in job.inputs:
            if item.is_dir():
                for path in sorted(item.rglob("*")):
                    if path.is_file():
                        zf.write(path, str(Path(item.name) / path.relative_to(item)))
            else:
                zf.write(item, item.name)
    count = len(job.inputs)
    noun = "item" if count == 1 else "items"
    return ok([target], f"Zipped {count} {noun} into {target.name}")


# --- reading ----------------------------------------------------------------


def _extract_archive(source: Path, dest: Path) -> None:
    ext = source.suffix.lower().lstrip(".")
    name = source.name.lower()
    if ext == "zip":
        _extract_zip(source, dest)
    elif ext == "tar" or name.endswith((".tar.gz", ".tar.bz2", ".tar.xz")):
        _extract_tar(source, dest)
    elif ext in ("gz", "tgz"):
        _extract_gzip(source, dest)
    elif ext in _SEVENZIP_IN:
        _extract_sevenzip(source, dest)
    else:
        raise RunnerFailed(f"{source.name}: not a supported archive")


def _extract_zip(source: Path, dest: Path) -> None:
    import zipfile

    try:
        with zipfile.ZipFile(source) as zf:
            if zf.testzip() is not None:
                raise RunnerFailed(f"{source.name} is corrupt")
            for info in zf.infolist():
                target = _safe_target(dest, info.filename)
                if info.is_dir():
                    target.mkdir(parents=True, exist_ok=True)
                    continue
                target.parent.mkdir(parents=True, exist_ok=True)
                with zf.open(info) as src, open(target, "wb") as out:
                    _copy_stream(src, out)
    except zipfile.BadZipFile as exc:
        raise RunnerFailed(f"{source.name} is not a valid zip: {exc}") from None


def _extract_tar(source: Path, dest: Path) -> None:
    import tarfile

    try:
        with tarfile.open(source) as tf:
            for member in tf.getmembers():
                target = _safe_target(dest, member.name)
                if member.isdir():
                    target.mkdir(parents=True, exist_ok=True)
                    continue
                if member.issym() or member.islnk():
                    # A link whose target escapes the tree is the tar form of
                    # zip-slip; validate it lands inside before trusting it.
                    _safe_target(dest, _link_destination(member))
                    continue
                if not member.isfile():
                    continue  # devices, fifos: nothing to write
                target.parent.mkdir(parents=True, exist_ok=True)
                extracted = tf.extractfile(member)
                if extracted is None:
                    continue
                with extracted as src, open(target, "wb") as out:
                    _copy_stream(src, out)
    except tarfile.TarError as exc:
        raise RunnerFailed(f"{source.name} is not a valid tar: {exc}") from None


def _extract_gzip(source: Path, dest: Path) -> None:
    """A gzip is either a gzipped tar (extract the tar) or a single file."""
    import gzip
    import tarfile

    dest.mkdir(parents=True, exist_ok=True)
    try:
        if tarfile.is_tarfile(source):
            _extract_tar(source, dest)
            return
        inner = source.name
        for suffix in (".tgz", ".gz"):
            if inner.lower().endswith(suffix):
                inner = inner[: -len(suffix)]
                break
        target = _safe_target(dest, inner or "data")
        with gzip.open(source, "rb") as src, open(target, "wb") as out:
            _copy_stream(src, out)
    except (OSError, EOFError) as exc:
        raise RunnerFailed(f"{source.name} is not a valid gzip: {exc}") from None


def _extract_sevenzip(source: Path, dest: Path) -> None:
    from .. import backends

    exe = backends.require("7z")
    dest.mkdir(parents=True, exist_ok=True)
    _run_7z([str(exe), "x", "-y", f"-o{dest}", "--", str(source)], source)


# --- writing ----------------------------------------------------------------


def _make_archive(tree: Path, target: Path, fmt: str) -> None:
    if fmt == "zip":
        _write_zip(tree, target)
    elif fmt == "tar":
        _write_tar(tree, target, mode="w")
    elif fmt == "gzip":
        _write_tar(tree, target, mode="w:gz")
    elif fmt == "7z":
        _write_7z(tree, target)
    else:  # pragma: no cover - register() never asks for another format
        raise RunnerFailed(f"cannot write {fmt} archives")


def _members(tree: Path) -> list[tuple[Path, str]]:
    """Every file under ``tree`` as (path, archive-relative name), sorted."""
    return [
        (path, str(path.relative_to(tree).as_posix()))
        for path in sorted(tree.rglob("*"))
        if path.is_file()
    ]


def _write_zip(tree: Path, target: Path) -> None:
    import zipfile

    with zipfile.ZipFile(target, "w", compression=zipfile.ZIP_DEFLATED) as zf:
        for path, arcname in _members(tree):
            zf.write(path, arcname)


def _write_tar(tree: Path, target: Path, *, mode: str) -> None:
    import tarfile

    with tarfile.open(target, mode) as tf:
        for path, arcname in _members(tree):
            tf.add(path, arcname=arcname)


def _write_7z(tree: Path, target: Path) -> None:
    from .. import backends

    exe = backends.require("7z")
    # 7-Zip adds to an existing archive, so build in scratch and move the
    # finished file onto the claimed placeholder (which is empty, not an
    # archive). The scratch folder sits beside the target for an atomic move.
    import tempfile

    staging = Path(tempfile.mkdtemp(prefix=".peel-7z-", dir=target.parent))
    try:
        built = staging / "out.7z"
        _run_7z([str(exe), "a", "-y", "-t7z", "--", str(built), "."], target, cwd=tree)
        os.replace(built, target)
    finally:
        import shutil

        shutil.rmtree(staging, ignore_errors=True)


# --- helpers ----------------------------------------------------------------


def _safe_target(dest: Path, name: str) -> Path:
    """Resolve an archive member against ``dest``, refusing any escape."""
    if not name or name in (".", "./"):
        return dest
    # Archive member names use forward slashes; a Windows-built archive may use
    # back slashes or carry a drive letter. Reject anything absolute outright.
    if PurePosixPath(name).is_absolute() or PureWindowsPath(name).is_absolute():
        raise RunnerFailed(f"refused unsafe archive path: {name!r}")
    parts = [p for p in PureWindowsPath(name).parts if p not in ("", ".")]
    if any(p == ".." for p in parts):
        raise RunnerFailed(f"refused unsafe archive path: {name!r}")
    dest_root = dest.resolve()
    target = (dest_root / Path(*parts)).resolve()
    if target != dest_root and dest_root not in target.parents:
        raise RunnerFailed(f"refused unsafe archive path: {name!r}")
    return target


def _link_destination(member) -> str:
    """Where a tar link points, as a member-style relative name to validate."""
    if PureWindowsPath(member.linkname).is_absolute() or PurePosixPath(
        member.linkname
    ).is_absolute():
        return member.linkname
    base = PurePosixPath(member.name).parent
    return str(base / member.linkname)


def _copy_stream(src, out, chunk: int = 1 << 20) -> None:
    while True:
        block = src.read(chunk)
        if not block:
            break
        out.write(block)


def _run_7z(args: list[str], source: Path, *, cwd: Path | None = None) -> None:
    import subprocess

    try:
        proc = subprocess.run(
            args,
            cwd=str(cwd) if cwd else None,
            capture_output=True,
            text=True,
            timeout=600,
        )
    except OSError as exc:
        raise RunnerFailed(f"7-Zip could not run: {exc}") from None
    if proc.returncode != 0:
        detail = (proc.stderr or proc.stdout or "").strip().splitlines()
        last = detail[-1] if detail else f"exit code {proc.returncode}"
        raise RunnerFailed(f"7-Zip failed on {source.name}: {last}")
