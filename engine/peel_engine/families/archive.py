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
  A tar is read as a stream through a reader that charges every decompressed
  byte (headers and skipped data too), and a PAX or GNU extended header over
  1 MiB is refused before tarfile reads it. Every entry created on disk,
  implicit parent folders included, counts once against max_members.
* Links. Tar hard links and symlinks are resolved against the archive's own
  namespace once every member is read, and written as copies of their target
  (Windows cannot create symlinks without developer mode); a folder link
  copies the target's whole resolved subtree. Members that are not files
  (devices, fifos) and links that are missing, loop or point into their own
  folder are listed as skipped in the result message, never dropped silently.
  Conversions do the same for the symlinks 7-Zip writes.
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
#: Bytes a decompressed tar stream may run past max_total_mb (headers,
#: padding, the end-of-archive record) before it is refused.
STREAM_SLACK = 16 * MIB
#: Largest PAX or GNU extended header read, and the longest chain of them
#: before one member. Real ones are a few hundred bytes, one or two deep.
MAX_TAR_META = MIB
MAX_TAR_META_CHAIN = 8

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
    budget = _Budget.for_job(job, scratch)
    skipped = _extract_archive(job.source, scratch, budget)
    # 7-Zip may write real symlinks; the writers below see plain files only.
    _flatten_links(scratch, budget, skipped)
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
        _remove_tree(folder)
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

    from ..naming import path_key

    target = job.output_path(source=job.source, ext="zip")
    # The archive is built in a scratch folder and moved onto the claim at the
    # end. Both can sit inside a dragged folder (the output goes beside the
    # first item, which may live in a later item's tree), so the walk skips
    # the claim and everything under the scratch folder: the zip never reads
    # itself or its own staging file.
    scratch = job.scratch_dir(near=target)
    staging = scratch / "out.zip"
    target_key = path_key(target)
    scratch_key = path_key(scratch)

    def ours(path: Path) -> bool:
        key = path_key(path)
        return key == target_key or key == scratch_key or key.startswith(scratch_key + os.sep)

    taken: set[str] = set()
    with zipfile.ZipFile(
        staging, "w", compression=zipfile.ZIP_DEFLATED, strict_timestamps=False
    ) as zf:
        for item in job.inputs:
            top = _unique_name(item.name, taken, folder=item.is_dir())
            if item.is_dir():
                for path in sorted(item.rglob("*")):
                    if path.is_file() and not ours(path):
                        zf.write(path, f"{top}/{path.relative_to(item).as_posix()}")
            else:
                zf.write(item, top)
    os.replace(staging, target)
    count = len(job.inputs)
    noun = "item" if count == 1 else "items"
    return ok([target], f"Zipped {count} {noun} into {target.name}")


def _remove_tree(folder: Path) -> None:
    """Remove ``folder`` however deep it goes.

    shutil.rmtree recurses once per level (Python 3.12) and joins full paths,
    so a folder thousands of levels deep stops it. The fallback walks down and
    back up through directory descriptors, one level open at a time.
    """
    try:
        shutil.rmtree(folder)
        return
    except FileNotFoundError:
        return
    except (OSError, RecursionError):
        pass
    if os.name != "posix" or not os.path.isdir(folder):
        shutil.rmtree(folder, ignore_errors=True)
        return
    flags = os.O_RDONLY | os.O_DIRECTORY
    names: list[str] = []
    fd = os.open(folder, flags)
    try:
        while True:
            sub = None
            with os.scandir(fd) as it:
                entries = list(it)
            for entry in entries:
                if entry.is_dir(follow_symlinks=False):
                    sub = entry.name
                    break
                os.unlink(entry.name, dir_fd=fd)
            if sub is not None:
                child = os.open(sub, flags | os.O_NOFOLLOW, dir_fd=fd)
                os.close(fd)
                fd = child
                names.append(sub)
                continue
            if not names:
                break
            parent = os.open("..", flags, dir_fd=fd)
            os.close(fd)
            fd = parent
            os.rmdir(names.pop(), dir_fd=fd)
    except OSError:
        pass
    finally:
        os.close(fd)
    try:
        os.rmdir(folder)
    except OSError:
        pass


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
    parsed: int = 0
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
        """Charge ``n`` entries created on disk (files, folders, implicit parents)."""
        self.count += n
        if self.count > self.members:
            self.refuse(f"it holds more than {self.members:,} entries (max_members)")

    def note_parsed(self, n: int = 1) -> None:
        """Count ``n`` entries read from the archive's index or headers.

        A separate counter from ``add_member``: an entry is parsed once and
        created once, and each side is held to max_members on its own, so a
        link or a file is never charged twice against the same limit.
        """
        self.parsed += n
        if self.parsed > self.members:
            self.refuse(f"it holds more than {self.members:,} entries (max_members)")

    def check_stream(self, read: int) -> None:
        """Charge bytes read from a decompressed tar stream, headers included.

        The stream carries file data plus 512-byte headers, padding and
        extended headers, so it may run a little past the unpacked total; the
        allowance is a fixed slack plus 1 KiB per entry parsed so far.
        """
        allowance = self.total + STREAM_SLACK + 1024 * self.parsed
        if read > allowance:
            self.check_total(self.total + 1)
        self.check_ratio(read, self.packed, "the archive")

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
        _extract_sevenzip(source, dest, budget, skipped)
    else:
        raise RunnerFailed(f"{source.name}: not a supported archive")
    return skipped


def _extract_zip(source: Path, dest: Path, budget: _Budget) -> None:
    import zipfile
    import zlib

    root = dest.resolve()
    try:
        with zipfile.ZipFile(source) as zf:
            infos = zf.infolist()
            budget.note_parsed(len(infos))
            # Every name is checked before anything is written.
            plan = [(info, _safe_target(dest, info.filename)) for info in infos]
            for info, target in plan:
                if info.is_dir():
                    _make_dirs(root, target, budget)
                    continue
                budget.check_member(info.filename, info.file_size)
                _make_dirs(root, target.parent, budget)
                if not os.path.lexists(target):
                    budget.add_member()
                with zf.open(info) as src, open(target, "wb") as out:
                    budget.copy(src, out, info.filename, packed=info.compress_size)
    except zipfile.BadZipFile as exc:
        raise RunnerFailed(f"{source.name} is not a valid zip: {_clean(exc)}") from None
    except (zlib.error, EOFError, NotImplementedError) as exc:
        raise RunnerFailed(f"{source.name} is corrupt: {_clean(exc)}") from None
    except RuntimeError as exc:  # an encrypted member
        raise RunnerFailed(f"Cannot extract {source.name}: {_clean(exc)}") from None


def _extract_tar(source: Path, dest: Path, budget: _Budget, skipped: list[str], opener=None) -> None:
    """Extract a tar, plain or compressed, as a stream read against the budget.

    The decompressed bytes go through ``_BudgetReader``, so every byte tarfile
    reads (headers, skipped member data, extended headers) is charged before
    the next block is decompressed, and ``_bounded_tarinfo`` refuses an
    oversized PAX or GNU extended header before tarfile allocates it. Files
    are written as they stream past; links are recorded in a logical tree and
    written once the whole archive has been read.
    """
    import lzma
    import tarfile
    import zlib

    root = dest.resolve()
    tree = _TarTree()
    try:
        with (opener or _tar_opener(source))(source, "rb") as raw:
            reader = _BudgetReader(raw, budget)
            with tarfile.open(fileobj=reader, mode="r|", tarinfo=_bounded_tarinfo()) as tf:
                for member in tf:
                    _read_tar_member(tf, member, dest, root, tree, budget, skipped)
                    # Stream-mode tarfile keeps every TarInfo (PAX headers
                    # included) in tf.members; drop each one once it is
                    # processed so memory stays bounded by one member.
                    tf.members.clear()
    except _TarMetaRefused as exc:
        budget.refuse(str(exc))
    except (tarfile.TarError, EOFError, lzma.LZMAError, zlib.error) as exc:
        raise RunnerFailed(f"{source.name} is not a valid tar: {_clean(exc)}") from None
    except OSError as exc:
        if isinstance(exc, (FileNotFoundError, PermissionError)) or exc.filename:
            raise
        # gzip, bz2 and lzma report a damaged stream as a bare OSError.
        raise RunnerFailed(f"{source.name} is corrupt: {_clean(exc)}") from None
    _materialize_links(root, tree, budget, skipped)


def _read_tar_member(tf, member, dest: Path, root: Path, tree: "_TarTree", budget: _Budget, skipped: list[str]) -> None:
    budget.note_parsed()
    target = _safe_target(dest, member.name)
    key = _logical_key(dest, member.name)
    if member.isdir():
        tree.add(key, "dir", skipped)
        _make_dirs(root, target, budget)
        return
    if member.issym() or member.islnk():
        # A link whose target escapes the tree is the tar form of zip-slip; it
        # is refused here, before anything is written.
        kind = "sym" if member.issym() else "hard"
        tree.add_link(key, kind, _link_key(dest, member), skipped)
        return
    if not member.isfile():
        skipped.append(f"{member.name} ({_tar_kind(member)})")
        tree.add(key, "other", skipped)
        return
    budget.check_member(member.name, member.size)
    _make_dirs(root, target.parent, budget)
    if not os.path.lexists(target):
        budget.add_member()
    tree.add(key, "file", skipped)
    extracted = tf.extractfile(member)
    if extracted is None:  # pragma: no cover - isfile() above
        skipped.append(f"{member.name} (unreadable)")
        return
    with extracted as src, open(target, "wb") as out:
        budget.copy(src, out, member.name)


def _tar_opener(source: Path):
    """The opener for a ``.tar`` file: plain, or compressed despite its name."""
    import bz2
    import gzip
    import lzma

    with open(source, "rb") as fh:
        magic = fh.read(6)
    if magic.startswith(b"\x1f\x8b"):
        return gzip.open
    if magic.startswith(b"BZh"):
        return bz2.open
    if magic.startswith(b"\xfd7zXZ\x00"):
        return lzma.open
    return open


class _BudgetReader:
    """A read-only stream that charges every byte read against the budget."""

    def __init__(self, raw, budget: _Budget) -> None:
        self.raw = raw
        self.budget = budget
        self.read_bytes = 0

    def read(self, size: int = -1) -> bytes:
        if size is None or size < 0:
            parts = []
            while True:
                block = self.read(MIB)
                if not block:
                    return b"".join(parts)
                parts.append(block)
        data = self.raw.read(size)
        self.read_bytes += len(data)
        self.budget.check_stream(self.read_bytes)
        return data


class _TarMetaRefused(Exception):
    """An extended tar header too large (or chained too deep) to read."""


_BOUNDED_TARINFO = None


def _bounded_tarinfo():
    """A TarInfo class that refuses oversized PAX and GNU extended headers.

    tarfile reads an extended header's whole declared payload into memory
    before it returns the member it describes, so the size is checked here,
    on the header block alone, before that read.
    """
    global _BOUNDED_TARINFO
    if _BOUNDED_TARINFO is not None:
        return _BOUNDED_TARINFO
    import tarfile

    extended = (
        tarfile.GNUTYPE_LONGNAME,
        tarfile.GNUTYPE_LONGLINK,
        tarfile.XHDTYPE,
        tarfile.XGLTYPE,
        tarfile.SOLARIS_XHDTYPE,
    )

    class BoundedTarInfo(tarfile.TarInfo):
        def _proc_member(self, tf):
            if self.type not in extended:
                return super()._proc_member(tf)
            if self.size > MAX_TAR_META:
                raise _TarMetaRefused(
                    f"it has an extended tar header of {self.size:,} bytes "
                    f"(peel reads at most {MAX_TAR_META // MIB} MB)"
                )
            depth = getattr(tf, "_peel_meta_depth", 0)
            if depth >= MAX_TAR_META_CHAIN:
                raise _TarMetaRefused(
                    f"it chains more than {MAX_TAR_META_CHAIN} extended tar headers"
                )
            tf._peel_meta_depth = depth + 1
            try:
                return super()._proc_member(tf)
            finally:
                tf._peel_meta_depth = depth

    _BOUNDED_TARINFO = BoundedTarInfo
    return BoundedTarInfo


def _looks_like_tar(head: bytes) -> bool:
    """True when ``head`` is one valid tar header block (checksum and all).

    This parses the 512 bytes only, never what an extended header declares,
    so probing an untrusted stream costs one block.
    """
    import tarfile

    if len(head) < 512 or not any(head):
        return False
    try:
        tarfile.TarInfo.frombuf(head[:512], "utf-8", "surrogateescape")
    except tarfile.HeaderError:
        return False
    return True


def _extract_compressed(source: Path, dest: Path, budget: _Budget, skipped: list[str]) -> None:
    """A gz/bz2/xz is either a compressed tar (extract the tar) or one file."""
    import bz2
    import gzip
    import lzma

    dest.mkdir(parents=True, exist_ok=True)
    ext = source.suffix.lower().lstrip(".")
    opener = {"gz": gzip.open, "tgz": gzip.open, "bz2": bz2.open, "xz": lzma.open}[ext]
    try:
        with opener(source, "rb") as probe:
            head = probe.read(512)
        is_tar = _looks_like_tar(head)
    except (OSError, EOFError, lzma.LZMAError):
        is_tar = False
    if is_tar:
        _extract_tar(source, dest, budget, skipped, opener=opener)
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


def _extract_sevenzip(source: Path, dest: Path, budget: _Budget, skipped: list[str]) -> None:
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
    _drop_7z_folder_links(listing, dest, skipped)


def _drop_7z_folder_links(listing: str, dest: Path, skipped: list[str]) -> None:
    """List a stored folder link that 7-Zip wrote as a plain empty folder.

    7-Zip on Windows writes a stored link (attribute L, or a Unix ``l`` mode)
    as an empty ordinary folder, so the link would vanish with no note. Such a
    folder is removed and listed as skipped, as ``_flatten_links`` does for
    the real links 7-Zip writes elsewhere.
    """
    _, sep, body = listing.partition("\n----------")
    for block in body.split("\n\n") if sep else ():
        fields: dict[str, str] = {}
        for line in block.splitlines():
            key, eq, value = line.partition(" = ")
            if eq:
                fields[key.strip()] = value.strip()
        path = fields.get("Path")
        win, _, unix = fields.get("Attributes", "").partition(" ")
        if not path or not ("L" in win or unix.startswith("l")):
            continue
        rel = path.replace("\\", "/")
        try:
            out = _safe_target(dest, rel)
        except RunnerFailed:
            continue
        if not os.path.isdir(out) or os.path.islink(out):
            continue
        with os.scandir(out) as it:
            empty = next(it, None) is None
        if empty:
            os.rmdir(out)
            skipped.append(f"{rel} (folder link)")


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
                        if os.name == "nt":
                            # scandir's cached size on NTFS stays stale while
                            # 7-Zip still holds the file open; a real stat
                            # reads the live size through a handle.
                            size = os.stat(entry.path, follow_symlinks=False).st_size
                        else:
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
        budget.used = used
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


class _Unresolved(Exception):
    """A link that leads nowhere; the text is the reason shown as Skipped."""


_VISITING = object()
#: Most links followed while resolving one path, as on Linux.
MAX_LINK_HOPS = 40


def _within(path: str, folder: str) -> bool:
    """True when logical ``path`` is ``folder`` or lies under it."""
    return folder == "" or path == folder or path.startswith(folder + "/")


class _TarTree:
    """The archive's own namespace: every member by its logical path.

    Keys are member names normalised to forward slashes, "" for the root.
    Parents that no member names are implicit folders. Links are resolved
    here, against the archive, never against the partly written disk, so a
    directory link sees every link inside its target whatever the member
    order, and a loop is found instead of followed.
    """

    def __init__(self) -> None:
        self.nodes: dict[str, tuple[str, str | None]] = {}
        self.children: dict[str, set[str]] = {}
        self.links: list[str] = []
        self._memo: dict[str, object] = {}

    def _register(self, key: str) -> None:
        parts = key.split("/")
        for i, part in enumerate(parts):
            self.children.setdefault("/".join(parts[:i]), set()).add(part)

    def add(self, key: str, kind: str, skipped: list[str]) -> None:
        if not key:
            return
        old = self.nodes.get(key)
        if old is not None and old[0] in ("sym", "hard"):
            skipped.append(f"{key} (link over an existing name)")
        self.nodes[key] = (kind, None)
        self._register(key)

    def add_link(self, key: str, kind: str, target: str, skipped: list[str]) -> None:
        if not key or key in self.nodes or key in self.children:
            skipped.append(f"{key or '.'} (link over an existing name)")
            return
        self.nodes[key] = (kind, target)
        self.links.append(key)
        self._register(key)

    def kind(self, key: str) -> str | None:
        if key == "":
            return "dir"
        node = self.nodes.get(key)
        if node is not None:
            if node[0] in ("sym", "hard") and key in self.children:
                # Later members were written under the link's name, so on
                # disk it is a real folder and the link cannot be placed.
                return "dir"
            return node[0]
        return "dir" if key in self.children else None

    def is_link(self, key: str) -> bool:
        return self.kind(key) in ("sym", "hard")

    def resolve(self, path: str, hops: int = 0) -> str:
        """The logical path of the real entry ``path`` leads to."""
        current = ""
        parts = [p for p in path.split("/") if p]
        for i, part in enumerate(parts):
            candidate = f"{current}/{part}" if current else part
            kind = self.kind(candidate)
            if kind is None:
                raise _Unresolved("link to a missing file")
            if kind in ("sym", "hard"):
                candidate = self.resolve_link(candidate, hops + 1)
                kind = self.kind(candidate)
            if i < len(parts) - 1 and kind != "dir":
                raise _Unresolved("link to a missing file")
            current = candidate
        return current

    def resolve_link(self, key: str, hops: int = 0) -> str:
        memo = self._memo.get(key)
        if memo is _VISITING:
            raise _Unresolved("link loop")
        if isinstance(memo, _Unresolved):
            raise memo
        if isinstance(memo, str):
            return memo
        if hops > MAX_LINK_HOPS:
            raise _Unresolved("link loop")
        self._memo[key] = _VISITING
        try:
            result = self.resolve(self.nodes[key][1] or "", hops)
        except _Unresolved as exc:
            self._memo[key] = exc
            raise
        self._memo[key] = result
        return result


def _logical_key(dest: Path, name: str) -> str:
    """A member name as a logical tree key (validated like ``_safe_target``)."""
    _safe_target(dest, name)
    if not name or name in (".", "./"):
        return ""
    parts = [p for p in PureWindowsPath(name).parts if p not in ("", ".")]
    return "/".join(parts)


def _link_key(dest: Path, member) -> str:
    """The logical path a tar link points at, refusing a link out of the tree.

    A hard link names its target from the archive root; a symlink is relative
    to the folder that holds it. Absolute targets are refused either way.
    """
    # A Windows-built archive may use back slashes in names and targets;
    # normalise both before taking the link's folder. Escape checks follow.
    link = member.linkname.replace("\\", "/")
    if not link or PurePosixPath(link).is_absolute() or PureWindowsPath(link).is_absolute():
        raise RunnerFailed(f"refused unsafe archive link: {member.name!r} -> {member.linkname!r}")
    if member.issym():
        link = posixpath.join(posixpath.dirname(member.name.replace("\\", "/")), link)
    joined = posixpath.normpath(link)
    if joined == ".." or joined.startswith("../"):
        raise RunnerFailed(f"refused unsafe archive link: {member.name!r} -> {member.linkname!r}")
    return _logical_key(dest, joined)


def _make_dirs(root: Path, folder: Path, budget: _Budget) -> None:
    """Create ``folder`` and any missing parents, charging each one created."""
    missing = []
    path = folder
    while path != root and not os.path.lexists(path):
        missing.append(path)
        path = path.parent
    if not path.is_dir():
        clash = path.relative_to(root).as_posix() if root in path.parents else path.name
        raise RunnerFailed(f"Cannot extract {budget.name}: {clash} is both a file and a folder")
    if missing:
        budget.add_member(len(missing))
        for path in reversed(missing):
            path.mkdir()


def _materialize_links(root: Path, tree: _TarTree, budget: _Budget, skipped: list[str]) -> None:
    """Write each link as a copy of what it resolves to in the archive.

    A file link copies the file. A folder link copies the target's whole
    logical subtree, links inside it included (resolved the same way), so the
    copy is complete whatever order the members came in. A link that leads to
    a missing entry, round in a loop, or into its own folder is listed as
    skipped, never dropped silently.
    """
    names = _NameClaims(tree)
    for key in tree.links:
        if not tree.is_link(key):
            skipped.append(f"{key} (link over an existing name)")
            continue
        try:
            canon = tree.resolve_link(key)
        except _Unresolved as exc:
            skipped.append(f"{key} ({exc})")
            continue
        kind = tree.kind(canon)
        out = _safe_target(root, key)
        if kind == "dir":
            if _within(key, canon):
                skipped.append(f"{key} (link to its own folder)")
                continue
            _copy_logical_dir(root, tree, canon, out, key, budget, skipped, names)
        elif kind == "file":
            if names.claim(out, key, skipped):
                _copy_logical_file(root, canon, out, key, budget, skipped)
        else:
            skipped.append(f"{key} (link to a skipped entry)")


def _win_name(path: str) -> str:
    """``path`` as Windows compares it: case folded, trailing dots and spaces cut."""
    return "/".join(part.rstrip(" .").casefold() for part in path.split("/"))


class _NameClaims:
    """The names a link copy may still take without hitting another entry.

    On Windows ``A.TXT`` and ``a.txt`` (or ``lib.``) are one file, so a link
    copy written under such a name would truncate the entry it copies from.
    Every name the stream wrote, and every name a link copy has taken, is
    held here in its Windows form; a link copy that matches one, or whose
    destination already exists on disk, is listed as skipped instead.
    """

    def __init__(self, tree: _TarTree) -> None:
        self.taken: set[str] = set()
        for key, (kind, _) in tree.nodes.items():
            if kind not in ("sym", "hard"):
                self._take(key, parents=True)
        for key in tree.children:
            if key:
                self.taken.add(_win_name(key))

    def _take(self, key: str, parents: bool = False) -> None:
        parts = key.split("/")
        for i in range(1 if parents else len(parts), len(parts) + 1):
            self.taken.add(_win_name("/".join(parts[:i])))

    def claim(self, out: Path, name: str, skipped: list[str]) -> bool:
        if _win_name(name) in self.taken or os.path.lexists(out):
            skipped.append(f"{name} (name clash with an existing entry on Windows)")
            return False
        self._take(name)
        return True


def _copy_logical_file(
    root: Path, canon: str, out: Path, name: str, budget: _Budget, skipped: list[str]
) -> None:
    _make_dirs(root, out.parent, budget)
    src_path = _safe_target(root, canon)
    if os.path.lexists(out):
        try:
            same = os.path.samefile(src_path, out)
        except OSError:
            same = False
        if same:
            # Opening ``out`` for writing would truncate the source itself.
            skipped.append(f"{name} (name clash with an existing entry on Windows)")
            return
    else:
        budget.add_member()
    with open(src_path, "rb") as src, open(out, "wb") as out_file:
        budget.copy(src, out_file, name)


def _copy_logical_dir(
    root: Path,
    tree: _TarTree,
    canon: str,
    out: Path,
    name: str,
    budget: _Budget,
    skipped: list[str],
    names: _NameClaims,
) -> None:
    """Copy the logical folder ``canon`` to ``out``, following its links.

    ``anchors`` holds the folders each link in the current chain jumped to;
    a link back to one of them (or to a folder above the current one) is a
    loop and is skipped. Each copied entry is charged to the budget, so a
    link that doubles a tree at every level stops at the limits.
    """
    work = [(canon, out, name, (canon,))]
    while work:
        folder, out_dir, out_name, anchors = work.pop()
        if not names.claim(out_dir, out_name, skipped):
            continue
        _make_dirs(root, out_dir, budget)
        for child in sorted(tree.children.get(folder, ()), reverse=True):
            src = f"{folder}/{child}" if folder else child
            dst = out_dir / child
            dst_name = f"{out_name}/{child}"
            chain = anchors
            kind = tree.kind(src)
            if kind in ("sym", "hard"):
                try:
                    src = tree.resolve_link(src)
                except _Unresolved as exc:
                    skipped.append(f"{dst_name} ({exc})")
                    continue
                kind = tree.kind(src)
                if kind == "dir":
                    if _within(folder, src) or any(_within(a, src) for a in anchors):
                        skipped.append(f"{dst_name} (link to its own folder)")
                        continue
                    chain = anchors + (src,)
            if kind == "dir":
                work.append((src, dst, dst_name, chain))
            elif kind == "file":
                if names.claim(dst, dst_name, skipped):
                    _copy_logical_file(root, src, dst, dst_name, budget, skipped)
            else:
                skipped.append(f"{dst_name} (not a file)")


def _flatten_links(tree: Path, budget: _Budget, skipped: list[str]) -> None:
    """Replace the symlinks 7-Zip may write in a scratch tree before repacking.

    A link to a file inside the tree becomes a copy of it. A folder link, a
    dangling link or one that leaves the tree is removed and listed as
    skipped, so a conversion never drops an entry silently.
    """
    root = os.path.realpath(tree)
    for folder, dirs, files in os.walk(tree, followlinks=False):
        for name in sorted(dirs + files):
            path = os.path.join(folder, name)
            if not os.path.islink(path):
                continue
            rel = Path(path).relative_to(tree).as_posix()
            real = os.path.realpath(path)
            inside = real == root or real.startswith(root + os.sep)
            os.unlink(path)
            if inside and os.path.isfile(real):
                with open(real, "rb") as src, open(path, "wb") as out:
                    budget.copy(src, out, rel)
            elif inside and os.path.isdir(real):
                skipped.append(f"{rel} (folder link)")
            elif inside:
                skipped.append(f"{rel} (link to a missing file)")
            else:
                skipped.append(f"{rel} (link out of the archive)")


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
