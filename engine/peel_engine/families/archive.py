"""Archives: convert between ZIP, TAR, GZIP and 7Z, extract, and zip up a drag.

The Shift wheel's Archives group (conversions) plus two Ctrl wheel tools
(Extract, and Zip for a folder or multi-file drag). ZIP, TAR and GZIP (and
bzip2/xz compressed tars) are handled with the Python standard library; 7Z and
RAR go through the 7-Zip CLI (the ``7z`` backend, which reads RAR but never
writes it). GZIP cannot hold more than one stream, so a "to GZIP" conversion
writes a gzipped tar (``.tar.gz``), the honest multi-file form.

Converting repacks: the source is extracted into a scratch folder and a fresh
archive is built from the tree, so a round trip preserves the files rather than
nesting one archive inside another. A ``.tar.gz`` (``.tar.bz2``, ``.tar.xz``)
counts as one extension for naming: ``bundle.tar.gz`` extracts to ``bundle``
and converts to ``bundle.zip``.

Security:

* Path traversal. Extraction never writes outside its destination folder. ZIP
  and TAR members go through a guard that refuses an absolute path, a ``..``
  escape or a link that points outside the tree (the classic zip-slip). ZIP
  names are all checked before the first byte is written. 7-Zip sanitises
  paths itself on extraction.
* Decompression bombs. Every extraction runs against a budget (the
  ``max_total_mb``, ``max_member_mb``, ``max_members`` and ``max_ratio``
  params): total unpacked bytes (never more than the free disk space minus
  1 GiB), bytes per member, member count, and the unpacked to packed ratio for
  anything over 1 MiB. The ZIP and TAR paths count the bytes actually
  streamed, never the header sizes, and stop before writing past a limit. The
  7-Zip path refuses when the listed sizes exceed the budget, then watches the
  extract folder while 7-Zip runs and kills it on overflow.
* Links. Tar hard links and symlinks that resolve inside the tree are written
  as copies of their target (Windows cannot create symlinks without developer
  mode). Members that are not files (devices, fifos) and links whose target is
  missing are listed as skipped in the result message, never dropped silently.
* A failed or refused Extract removes the folder this run claimed, so nothing
  half filled is left next to the source.
"""

from __future__ import annotations

import os
import posixpath
import shutil
from dataclasses import dataclass, field
from pathlib import Path, PurePosixPath, PureWindowsPath

from ..registry import ANY, CONVERT, TOOL, Job, Param, Registry
from ..result import Result, RunnerFailed, ok

#: Extensions read by the standard library, and by the 7-Zip CLI.
_STDLIB_IN = ("zip", "tar", "gz", "tgz", "bz2", "xz")
_SEVENZIP_IN = ("7z", "rar")
#: Single-stream compressors; each may wrap a tar or one plain file.
_COMPRESSED = ("gz", "tgz", "bz2", "xz")
#: Double extensions that name one archive format.
_TAR_DOUBLE = (".tar.gz", ".tar.bz2", ".tar.xz")

MIB = 1 << 20
GIB = 1 << 30
#: The ratio check only applies past this many unpacked bytes, so a tiny file
#: of zeros (ratio in the thousands, harmless) still extracts.
RATIO_FLOOR = MIB
#: Free disk space always kept back, whatever max_total_mb says.
DISK_RESERVE = GIB

#: The extraction budget, as params on every entry that unpacks an archive.
LIMIT_PARAMS = (
    Param(
        "max_total_mb",
        "int",
        "Largest total to unpack (MB)",
        default=20480,
        minimum=1,
        help="Refuse an archive that unpacks to more than this. The free disk space minus 1 GB always caps it too.",
    ),
    Param(
        "max_member_mb",
        "int",
        "Largest single file to unpack (MB)",
        default=20480,
        minimum=1,
        help="Refuse an archive holding one file that unpacks to more than this.",
    ),
    Param(
        "max_members",
        "int",
        "Most entries to unpack",
        default=1_000_000,
        minimum=1,
        help="Refuse an archive with more files and folders than this.",
    ),
    Param(
        "max_ratio",
        "int",
        "Largest compression ratio",
        default=1000,
        minimum=0,
        help="Refuse an archive that unpacks to more than this many times its packed size, once past 1 MB (a decompression bomb). 0 turns the check off.",
    ),
)


def register(registry: Registry) -> None:
    for fmt, ext, label in (
        ("zip", "zip", "ZIP"),
        ("tar", "tar", "TAR"),
        ("gzip", None, "GZIP"),
        ("7z", "7z", "7Z"),
    ):
        # A format never converts to itself; a gzip source to GZIP is a no-op.
        same = ("gz", "tgz") if fmt == "gzip" else (ext,)
        inputs = tuple(e for e in (*_STDLIB_IN, *_SEVENZIP_IN) if e not in same)
        registry.add(
            id=f"archive.to_{fmt}",
            family=CONVERT,
            label=label,
            group="Archives",
            inputs=inputs,
            output_ext=ext,
            params=LIMIT_PARAMS,
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
        params=LIMIT_PARAMS,
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
    target = job.output_path(source=_naming_source(job.source), ext=ext)
    scratch = job.scratch_dir(near=target)
    skipped = _extract_archive(job.source, scratch, _Budget.for_job(job, scratch))
    _make_archive(scratch, target, fmt)
    return ok([target], f"Repacked to {target.name}{_skipped_note(skipped)}")


# --- tools ------------------------------------------------------------------


def extract(job: Job) -> Result:
    """Extract an archive into a sibling folder named after it."""
    folder = job.output_path(source=_naming_source(job.source), ext="", directory=True)
    try:
        skipped = _extract_archive(job.source, folder, _Budget.for_job(job, folder))
    except BaseException:
        # This run claimed the folder with an exclusive mkdir, so everything in
        # it is ours: a refused or failed extract leaves nothing behind.
        shutil.rmtree(folder, ignore_errors=True)
        raise
    count = sum(1 for _ in folder.rglob("*") if _.is_file())
    noun = "file" if count == 1 else "files"
    return ok([folder], f"Extracted {count} {noun} to {folder.name}{_skipped_note(skipped)}")


def zip_up(job: Job) -> Result:
    """Zip a folder or a multi-file drag into one archive beside the first item.

    Each dragged item becomes one top-level entry. Two items with the same name
    (compared case-insensitively, as Windows does) never share an entry: the
    later one gets " 2", " 3", ... before its extension.
    """
    import zipfile

    target = job.output_path(source=job.source, ext="zip")
    taken: set[str] = set()
    with zipfile.ZipFile(
        target, "w", compression=zipfile.ZIP_DEFLATED, strict_timestamps=False
    ) as zf:
        for item in job.inputs:
            top = _unique_name(item.name, taken, folder=item.is_dir())
            if item.is_dir():
                for path in sorted(item.rglob("*")):
                    if path.is_file():
                        zf.write(path, f"{top}/{path.relative_to(item).as_posix()}")
            else:
                zf.write(item, top)
    count = len(job.inputs)
    noun = "item" if count == 1 else "items"
    return ok([target], f"Zipped {count} {noun} into {target.name}")


def _unique_name(name: str, taken: set[str], *, folder: bool) -> str:
    """``name``, or ``name 2``, ``name 3``... (before the extension for a file)."""
    stem, ext = (name, "") if folder else os.path.splitext(name)
    candidate, n = name, 1
    while candidate.casefold() in taken:
        n += 1
        candidate = f"{stem} {n}{ext}"
    taken.add(candidate.casefold())
    return candidate


def _naming_source(source: Path) -> Path:
    """The path naming should see: ``bundle.tar.gz`` names outputs ``bundle``."""
    lower = source.name.lower()
    for double in _TAR_DOUBLE:
        if lower.endswith(double) and len(lower) > len(double):
            return source.with_name(source.name[: -len(double)] + ".tar")
    return source


def _skipped_note(skipped: list[str]) -> str:
    if not skipped:
        return ""
    shown = ", ".join(skipped[:5])
    more = f" and {len(skipped) - 5} more" if len(skipped) > 5 else ""
    return f". Skipped {len(skipped)}: {shown}{more}"


# --- the extraction budget --------------------------------------------------


def _free_bytes(path: Path) -> int:
    return shutil.disk_usage(path).free


@dataclass
class _Budget:
    """Limits for one extraction, charged with the bytes actually written."""

    total: int
    member: int
    members: int
    ratio: int
    packed: int  # size of the archive on disk, for the archive-level ratio
    name: str
    used: int = 0
    count: int = 0
    _total_is_disk: bool = field(default=False, repr=False)

    @classmethod
    def for_job(cls, job: Job, dest: Path) -> "_Budget":
        params = job.params
        total = params.get("max_total_mb", 20480) * MIB
        room = _free_bytes(dest) - DISK_RESERVE
        if room <= 0:
            raise RunnerFailed(
                f"Not enough free disk space to extract {job.source.name} "
                "(peel keeps 1 GB free)",
                code="archive_too_large",
            )
        budget = cls(
            total=min(total, room),
            member=params.get("max_member_mb", 20480) * MIB,
            members=params.get("max_members", 1_000_000),
            ratio=params.get("max_ratio", 1000),
            packed=max(job.source.stat().st_size, 1),
            name=job.source.name,
        )
        budget._total_is_disk = room < total
        return budget

    def refuse(self, why: str) -> None:
        raise RunnerFailed(f"Refused {self.name}: {why}", code="archive_too_large")

    def _mb(self, n: int) -> str:
        return f"{n / MIB:,.0f} MB" if n >= MIB else f"{n:,} bytes"

    def check_total(self, used: int) -> None:
        if used > self.total:
            where = (
                "the free disk space (minus 1 GB)"
                if self._total_is_disk
                else f"the {self._mb(self.total)} limit (max_total_mb)"
            )
            self.refuse(f"it unpacks to more than {where}")

    def check_member(self, name: str, size: int) -> None:
        if size > self.member:
            self.refuse(
                f"{name} unpacks to more than the {self._mb(self.member)} limit (max_member_mb)"
            )

    def check_ratio(self, unpacked: int, packed: int, what: str) -> None:
        if self.ratio and unpacked > RATIO_FLOOR and unpacked > packed * self.ratio:
            self.refuse(
                f"{what} unpacks to more than {self.ratio} times its packed size "
                "(max_ratio), which looks like a decompression bomb"
            )

    def add_member(self, n: int = 1) -> None:
        self.count += n
        if self.count > self.members:
            self.refuse(f"it holds more than {self.members:,} entries (max_members)")

    def copy(self, src, out, name: str, packed: int | None = None, chunk: int = MIB) -> None:
        """Stream ``src`` to ``out``, refusing before any write past a limit.

        ``packed`` is the member's compressed size when the format records one
        (ZIP); the archive-level ratio applies always.
        """
        written = 0
        while True:
            block = src.read(chunk)
            if not block:
                break
            written += len(block)
            self.used += len(block)
            self.check_member(name, written)
            self.check_total(self.used)
            if packed is not None:
                self.check_ratio(written, max(packed, 1), name)
            self.check_ratio(self.used, self.packed, "the archive")
            out.write(block)


# --- reading ----------------------------------------------------------------


def _extract_archive(source: Path, dest: Path, budget: _Budget) -> list[str]:
    """Extract ``source`` into ``dest``. Returns the members skipped, described."""
    ext = source.suffix.lower().lstrip(".")
    skipped: list[str] = []
    if ext == "zip":
        _extract_zip(source, dest, budget)
    elif ext == "tar":
        _extract_tar(source, dest, budget, skipped)
    elif ext in _COMPRESSED:
        _extract_compressed(source, dest, budget, skipped)
    elif ext in _SEVENZIP_IN:
        _extract_sevenzip(source, dest, budget)
    else:
        raise RunnerFailed(f"{source.name}: not a supported archive")
    return skipped


def _extract_zip(source: Path, dest: Path, budget: _Budget) -> None:
    import zipfile
    import zlib

    try:
        with zipfile.ZipFile(source) as zf:
            infos = zf.infolist()
            budget.add_member(len(infos))
            # Every name is checked before anything is written.
            plan = [(info, _safe_target(dest, info.filename)) for info in infos]
            for info, target in plan:
                if info.is_dir():
                    target.mkdir(parents=True, exist_ok=True)
                    continue
                budget.check_member(info.filename, info.file_size)
                target.parent.mkdir(parents=True, exist_ok=True)
                with zf.open(info) as src, open(target, "wb") as out:
                    budget.copy(src, out, info.filename, packed=info.compress_size)
    except zipfile.BadZipFile as exc:
        raise RunnerFailed(f"{source.name} is not a valid zip: {_clean(exc)}") from None
    except (zlib.error, EOFError, NotImplementedError) as exc:
        raise RunnerFailed(f"{source.name} is corrupt: {_clean(exc)}") from None
    except RuntimeError as exc:  # an encrypted member
        raise RunnerFailed(f"Cannot extract {source.name}: {_clean(exc)}") from None


def _extract_tar(source: Path, dest: Path, budget: _Budget, skipped: list[str]) -> None:
    import tarfile

    links: list[tuple[str, Path, Path]] = []
    try:
        with tarfile.open(source) as tf:
            for member in tf:
                budget.add_member()
                target = _safe_target(dest, member.name)
                if member.isdir():
                    target.mkdir(parents=True, exist_ok=True)
                    continue
                if member.issym() or member.islnk():
                    # A link whose target escapes the tree is the tar form of
                    # zip-slip; it is refused here, before anything is copied.
                    links.append((member.name, target, _link_origin(dest, member)))
                    continue
                if not member.isfile():
                    skipped.append(f"{member.name} ({_tar_kind(member)})")
                    continue
                budget.check_member(member.name, member.size)
                target.parent.mkdir(parents=True, exist_ok=True)
                extracted = tf.extractfile(member)
                if extracted is None:  # pragma: no cover - isfile() above
                    skipped.append(f"{member.name} (unreadable)")
                    continue
                with extracted as src, open(target, "wb") as out:
                    budget.copy(src, out, member.name)
    except (tarfile.TarError, EOFError) as exc:
        raise RunnerFailed(f"{source.name} is not a valid tar: {_clean(exc)}") from None
    except OSError as exc:
        if isinstance(exc, (FileNotFoundError, PermissionError)) or exc.filename:
            raise
        # gzip, bz2 and lzma report a damaged stream as a bare OSError.
        raise RunnerFailed(f"{source.name} is corrupt: {_clean(exc)}") from None
    _materialize_links(dest, links, budget, skipped)


def _extract_compressed(source: Path, dest: Path, budget: _Budget, skipped: list[str]) -> None:
    """A gz/bz2/xz is either a compressed tar (extract the tar) or one file."""
    import bz2
    import gzip
    import lzma
    import tarfile

    dest.mkdir(parents=True, exist_ok=True)
    ext = source.suffix.lower().lstrip(".")
    opener = {"gz": gzip.open, "tgz": gzip.open, "bz2": bz2.open, "xz": lzma.open}[ext]
    try:
        # tarfile reads a zero block as an empty archive, so a compressed disk
        # image (or a stream of zeros) would "extract" to nothing. A tar starts
        # with a real header block.
        with opener(source, "rb") as probe:
            head = probe.read(512)
        is_tar = any(head) and tarfile.is_tarfile(source)
    except (OSError, EOFError, lzma.LZMAError):
        is_tar = False
    if is_tar:
        _extract_tar(source, dest, budget, skipped)
        return
    inner = source.name[: -len(source.suffix)] or "data"
    budget.add_member()
    target = _safe_target(dest, inner)
    try:
        with opener(source, "rb") as src, open(target, "wb") as out:
            budget.copy(src, out, inner)
    except RunnerFailed:
        raise
    except (OSError, EOFError, lzma.LZMAError) as exc:
        if isinstance(exc, (FileNotFoundError, PermissionError)):
            raise
        raise RunnerFailed(f"{source.name} is not a valid {ext} file: {_clean(exc)}") from None


def _extract_sevenzip(source: Path, dest: Path, budget: _Budget) -> None:
    from .. import backends

    exe = backends.require("7z")
    dest.mkdir(parents=True, exist_ok=True)
    # 1. The listing: refuse up front when the declared sizes break the budget.
    listing = _run_7z([str(exe), "l", "-slt", "--", str(source)], source)
    entries = _parse_7z_listing(listing)
    budget.add_member(len(entries))
    declared = 0
    for path, size in entries:
        budget.check_member(path, size)
        declared += size
    budget.check_total(declared)
    budget.check_ratio(declared, budget.packed, "the archive")
    # 2. The extraction, watched: headers can lie, so the folder itself is
    # measured while 7-Zip writes and 7-Zip is killed on overflow.
    _watch_7z([str(exe), "x", "-y", f"-o{dest}", "--", str(source)], source, dest, budget)


def _parse_7z_listing(text: str) -> list[tuple[str, int]]:
    """(path, unpacked size) for each entry of a ``7z l -slt`` listing."""
    _, sep, body = text.partition("\n----------")
    if not sep:
        return []
    entries: list[tuple[str, int]] = []
    for block in body.split("\n\n"):
        fields: dict[str, str] = {}
        for line in block.splitlines():
            key, eq, value = line.partition(" = ")
            if eq:
                fields[key.strip()] = value.strip()
        if "Path" not in fields:
            continue
        try:
            size = int(fields.get("Size") or 0)
        except ValueError:
            size = 0
        entries.append((fields["Path"], size))
    return entries


def _tree_usage(root: Path) -> tuple[int, int, str, int]:
    """(total bytes, entry count, largest file name, its size) under ``root``."""
    total = count = biggest = 0
    biggest_name = ""
    stack = [str(root)]
    while stack:
        try:
            with os.scandir(stack.pop()) as it:
                for entry in it:
                    count += 1
                    try:
                        if entry.is_dir(follow_symlinks=False):
                            stack.append(entry.path)
                            continue
                        size = entry.stat(follow_symlinks=False).st_size
                    except OSError:
                        continue
                    total += size
                    if size > biggest:
                        biggest, biggest_name = size, entry.name
        except OSError:
            continue
    return total, count, biggest_name, biggest


def _watch_7z(args: list[str], source: Path, dest: Path, budget: _Budget, timeout: float = 600) -> None:
    import subprocess
    import tempfile
    import time

    def check() -> None:
        used, count, name, size = _tree_usage(dest)
        budget.count = 0
        budget.add_member(count)
        budget.check_member(name, size)
        budget.check_total(used)
        budget.check_ratio(used, budget.packed, "the archive")

    with tempfile.TemporaryFile() as err:
        try:
            proc = subprocess.Popen(
                args, stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=err
            )
        except OSError as exc:
            raise RunnerFailed(f"7-Zip could not run: {_clean(exc)}") from None
        deadline = time.monotonic() + timeout
        try:
            interval = 0.02
            while proc.poll() is None:
                time.sleep(interval)
                started = time.monotonic()
                check()
                # Measuring a big tree costs time; never spend most of it there.
                interval = min(1.0, max(0.02, 4 * (time.monotonic() - started)))
                if time.monotonic() > deadline:
                    raise RunnerFailed(f"7-Zip timed out on {source.name}")
            check()
        except BaseException:
            proc.kill()
            proc.wait()
            raise
        if proc.returncode != 0:
            err.seek(0)
            detail = err.read().decode("utf-8", "replace").strip().splitlines()
            last = detail[-1].strip() if detail else f"exit code {proc.returncode}"
            raise RunnerFailed(f"7-Zip failed on {source.name}: {last}")


# --- links ------------------------------------------------------------------


def _link_origin(dest: Path, member) -> Path:
    """The extracted path a tar link copies, refusing a link out of the tree.

    A hard link names its target from the archive root; a symlink is relative
    to the folder that holds it. Absolute targets are refused either way.
    """
    link = member.linkname
    if not link or PurePosixPath(link).is_absolute() or PureWindowsPath(link).is_absolute():
        raise RunnerFailed(f"refused unsafe archive link: {member.name!r} -> {link!r}")
    if member.issym():
        link = posixpath.join(posixpath.dirname(member.name), link)
    joined = posixpath.normpath(link)
    if joined == ".." or joined.startswith("../"):
        raise RunnerFailed(f"refused unsafe archive link: {member.name!r} -> {member.linkname!r}")
    return _safe_target(dest, joined)


def _materialize_links(
    dest: Path, links: list[tuple[str, Path, Path]], budget: _Budget, skipped: list[str]
) -> None:
    """Write each link as a copy of what it points at.

    Links to links resolve over several rounds; whatever never resolves is a
    link to a missing member and is reported as skipped.
    """
    pending = links
    while pending:
        rest = []
        for name, target, origin in pending:
            if os.path.lexists(target):
                skipped.append(f"{name} (link over an existing name)")
            elif origin.is_file():
                target.parent.mkdir(parents=True, exist_ok=True)
                with open(origin, "rb") as src, open(target, "wb") as out:
                    budget.add_member()
                    budget.copy(src, out, name)
            elif origin.is_dir():
                if origin == target or origin in target.parents:
                    skipped.append(f"{name} (link to its own folder)")
                else:
                    _copy_tree(origin, target, budget, name)
            else:
                rest.append((name, target, origin))
        if len(rest) == len(pending):
            skipped.extend(f"{name} (link to a missing file)" for name, _, _ in rest)
            break
        pending = rest


def _copy_tree(origin: Path, target: Path, budget: _Budget, name: str) -> None:
    target.mkdir(parents=True, exist_ok=True)
    for path in sorted(origin.rglob("*")):
        rel = path.relative_to(origin)
        out_path = target / rel
        budget.add_member()
        if path.is_dir():
            out_path.mkdir(parents=True, exist_ok=True)
        elif path.is_file():
            out_path.parent.mkdir(parents=True, exist_ok=True)
            with open(path, "rb") as src, open(out_path, "wb") as out:
                budget.copy(src, out, f"{name}/{rel.as_posix()}")


def _tar_kind(member) -> str:
    if member.ischr() or member.isblk():
        return "device"
    if member.isfifo():
        return "fifo"
    return "unsupported entry type"


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

    # strict_timestamps=False clamps a pre-1980 mtime instead of failing.
    with zipfile.ZipFile(
        target, "w", compression=zipfile.ZIP_DEFLATED, strict_timestamps=False
    ) as zf:
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


def _clean(exc: BaseException) -> str:
    """One line of an exception's text, without the blank padding some carry."""
    text = " ".join(str(exc).split())
    return text[:300] if text else type(exc).__name__


def _run_7z(args: list[str], source: Path, *, cwd: Path | None = None) -> str:
    import subprocess

    try:
        proc = subprocess.run(
            args,
            cwd=str(cwd) if cwd else None,
            stdin=subprocess.DEVNULL,
            capture_output=True,
            text=True,
            timeout=600,
        )
    except OSError as exc:
        raise RunnerFailed(f"7-Zip could not run: {_clean(exc)}") from None
    except subprocess.TimeoutExpired:
        raise RunnerFailed(f"7-Zip timed out on {source.name}") from None
    if proc.returncode != 0:
        detail = (proc.stderr or proc.stdout or "").strip().splitlines()
        last = detail[-1].strip() if detail else f"exit code {proc.returncode}"
        raise RunnerFailed(f"7-Zip failed on {source.name}: {last}")
    return proc.stdout
