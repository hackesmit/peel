"""Final (round 3) review checks for hq-plct.5, collected with the suite."""

from __future__ import annotations

import bz2
import gzip
import io
import lzma
import os
import sys
import tarfile
import zipfile
from pathlib import Path

import pytest

from peel_engine.registry import load_registry

MIB = 1 << 20
WINDOWS = sys.platform == "win32"


@pytest.fixture
def real():
    return load_registry(force=True)


def _tar_bytes(members, fmt=tarfile.GNU_FORMAT) -> bytes:
    buf = io.BytesIO()
    with tarfile.open(fileobj=buf, mode="w", format=fmt) as tf:
        for kind, name, value in members:
            info = tarfile.TarInfo(name)
            if kind == "file":
                info.size = len(value)
                tf.addfile(info, io.BytesIO(value))
                continue
            info.type = {"dir": tarfile.DIRTYPE, "sym": tarfile.SYMTYPE, "hard": tarfile.LNKTYPE}[kind]
            if kind in ("sym", "hard"):
                info.linkname = value
            tf.addfile(info)
    return buf.getvalue()


def _tar(path: Path, members) -> Path:
    path.write_bytes(_tar_bytes(members))
    return path


def _zip_names(path: Path) -> set[str]:
    with zipfile.ZipFile(path) as zf:
        return set(zf.namelist())


# --- directory links in every order -----------------------------------------

MERGED_USR = [
    ("sym", "lib", "usr/lib"),
    ("file", "usr/lib/libfoo.so.1", b"FOO"),
    ("sym", "usr/lib/libfoo.so", "libfoo.so.1"),
]


@pytest.mark.parametrize("order", ["link_first", "target_first"])
@pytest.mark.parametrize("tool", ["archive.extract", "archive.to_zip"])
def test_merged_usr_in_both_orders(real, tmp_path, order, tool):
    members = MERGED_USR if order == "link_first" else MERGED_USR[1:] + MERGED_USR[:1]
    src = _tar(tmp_path / "root.tar", members)
    result = real.run(tool, [src])
    assert result.ok, result.message
    assert "Skipped" not in result.message, result.message
    if tool == "archive.extract":
        folder = result.outputs[0]
        assert (folder / "lib/libfoo.so").read_bytes() == b"FOO"
        assert (folder / "lib/libfoo.so.1").read_bytes() == b"FOO"
    else:
        assert {"lib/libfoo.so", "lib/libfoo.so.1", "usr/lib/libfoo.so"} <= _zip_names(result.outputs[0])


def test_nested_linked_dirs(real, tmp_path):
    src = _tar(
        tmp_path / "nest.tar",
        [("sym", "a", "b"), ("sym", "b/c", "../d"), ("sym", "d/e", "../f"), ("file", "f/x.txt", b"X")],
    )
    result = real.run("archive.extract", [src])
    assert result.ok, result.message
    folder = result.outputs[0]
    assert (folder / "a/c/e/x.txt").read_bytes() == b"X"
    assert (folder / "b/c/e/x.txt").read_bytes() == b"X"


def test_link_to_a_link_of_a_dir_target_first(real, tmp_path):
    src = _tar(
        tmp_path / "chain.tar",
        [("file", "real/f.txt", b"F"), ("sym", "middle", "real"), ("sym", "outer", "middle")],
    )
    result = real.run("archive.extract", [src])
    assert result.ok, result.message
    assert (result.outputs[0] / "outer/f.txt").read_bytes() == b"F"


@pytest.mark.parametrize(
    "members,expect",
    [
        ([("sym", "a", "b"), ("sym", "b", "a"), ("file", "x.txt", b"x")], ["a (link loop)", "b (link loop)"]),
        ([("sym", "self", "."), ("file", "x.txt", b"x")], ["self (link to its own folder)"]),
        ([("file", "d/x.txt", b"x"), ("sym", "d/up", "..")], ["d/up (link to its own folder)"]),
        ([("sym", "s", "s"), ("file", "x.txt", b"x")], ["s (link loop)"]),
    ],
)
def test_cycles_are_skipped_not_followed(real, tmp_path, members, expect):
    result = real.run("archive.extract", [_tar(tmp_path / "cyc.tar", members)])
    assert result.ok, result.message
    for text in expect:
        assert text in result.message, result.message


# --- extended-header bombs ---------------------------------------------------


@pytest.mark.parametrize(
    "suffix,compress",
    [("tar.gz", gzip.compress), ("tar.bz2", bz2.compress), ("tar.xz", lzma.compress), ("tar", lzma.compress)],
)
def test_pax_bomb_in_every_container(real, tmp_path, suffix, compress):
    buf = io.BytesIO()
    with tarfile.open(fileobj=buf, mode="w", format=tarfile.PAX_FORMAT) as tf:
        info = tarfile.TarInfo("small.txt")
        info.size = 1
        info.pax_headers = {"comment": "z" * (16 * MIB)}
        tf.addfile(info, io.BytesIO(b"x"))
    src = tmp_path / f"pax.{suffix}"
    src.write_bytes(compress(buf.getvalue()))
    result = real.run("archive.extract", [src], {"max_total_mb": 1})
    assert result.ok is False and result.code == "archive_too_large", result.message
    assert not (tmp_path / "pax").exists()


def _ext_header(kind: bytes, payload: bytes) -> bytes:
    info = tarfile.TarInfo("././@LongLink" if kind != tarfile.XGLTYPE else "pax_global_header")
    info.type = kind
    info.size = len(payload)
    head = info.tobuf(tarfile.USTAR_FORMAT if kind == tarfile.XGLTYPE else tarfile.GNU_FORMAT)
    pad = (-len(payload)) % 512
    return head + payload + b"\0" * pad


def _pax_record(key: str, value: str) -> bytes:
    body = f" {key}={value}\n"
    n = len(body) + 1
    while len(str(n)) + len(body) != n:
        n = len(str(n)) + len(body)
    return f"{n}{body}".encode()


@pytest.mark.parametrize("kind", ["gnu", "pax_global"])
def test_long_extended_header_chain_is_refused(real, tmp_path, kind):
    if kind == "gnu":
        chain = b"".join(_ext_header(tarfile.GNUTYPE_LONGNAME, b"n" * 100 + b"\0") for _ in range(50))
    else:
        chain = b"".join(_ext_header(tarfile.XGLTYPE, _pax_record("comment", "c")) for _ in range(50))
    tail = _tar_bytes([("file", "end.txt", b"E")])
    src = tmp_path / "chain.tar.gz"
    src.write_bytes(gzip.compress(chain + tail))
    result = real.run("archive.extract", [src])
    assert result.ok is False and result.code == "archive_too_large", result.message


def test_one_gnu_long_name_still_works(real, tmp_path):
    name = "/".join(["segment"] * 30) + "/file.txt"
    src = tmp_path / "long.tar.gz"
    src.write_bytes(gzip.compress(_tar_bytes([("file", name, b"L")])))
    result = real.run("archive.extract", [src])
    if WINDOWS and not result.ok:
        # Without LongPathsEnabled Windows refuses a path past MAX_PATH; the
        # final review accepted a clean runner_failed that leaves nothing.
        assert result.code == "runner_failed" and "206" in result.message, result.message
        assert not (tmp_path / "long").exists()
        return
    assert result.ok, result.message
    assert (result.outputs[0] / name).read_bytes() == b"L"


# --- members-only bombs --------------------------------------------------------


def test_deep_implicit_dirs_fail_cleanly(real, tmp_path):
    """A member 3000 folders deep: refused or failed with a clean message, folder removed."""
    name = "a/" * 3000 + "f.txt"
    src = tmp_path / "deep.tar.gz"
    src.write_bytes(gzip.compress(_tar_bytes([("file", name, b"D")])))
    result = real.run("archive.extract", [src])
    assert result.ok is False, result.message
    assert result.code is not None, result
    assert not (tmp_path / "deep").exists()


def test_many_members_with_implicit_dirs_hit_max_members(real, tmp_path):
    members = [("file", f"d{i}/a/b/c/d/e/f/g/h/x", b"") for i in range(500)]
    src = tmp_path / "wide.tar.gz"
    src.write_bytes(gzip.compress(_tar_bytes(members)))
    result = real.run("archive.extract", [src], {"max_members": 2000})
    assert result.ok is False and result.code == "archive_too_large", result.message
    assert not (tmp_path / "wide").exists()


# --- zip_up with ancestor inputs ---------------------------------------------


@pytest.mark.parametrize("ancestor", ["parent", "grandparent"])
def test_zip_up_with_ancestors(real, tmp_path, ancestor):
    work = tmp_path / "work"
    (work / "proj/sub").mkdir(parents=True)
    (work / "proj/sub/a.txt").write_bytes(b"a")
    first = work / "proj/sub/a.txt"
    other = work / "proj/sub" if ancestor == "parent" else work
    result = real.run("archive.zip", [first, other])
    assert result.ok, result.message
    names = _zip_names(result.outputs[0])
    assert not any(n.endswith(".zip") or ".peel-" in n for n in names), names
    assert "a.txt" in names
    leftovers = [p for p in work.rglob("*") if ".peel-" in p.name]
    assert not leftovers, leftovers


# --- Windows path semantics in the logical tree ------------------------------


def test_case_only_hard_link_never_truncates_its_target(real, tmp_path):
    src = _tar(tmp_path / "case.tar", [("file", "a.txt", b"ORIGINAL"), ("hard", "A.TXT", "a.txt")])
    result = real.run("archive.extract", [src])
    assert result.ok, result.message
    folder = result.outputs[0]
    for p in folder.iterdir():
        assert p.read_bytes() == b"ORIGINAL", (p.name, p.read_bytes(), result.message)


def test_case_only_dir_link_onto_its_target(real, tmp_path):
    """usr/LIB -> lib where lib is a real folder: on Windows both are one folder."""
    src = _tar(tmp_path / "case2.tar", [("file", "lib/x.txt", b"X"), ("sym", "LIB", "lib")])
    result = real.run("archive.extract", [src])
    assert result.ok, result.message
    folder = result.outputs[0]
    assert (folder / "lib/x.txt").read_bytes() == b"X", result.message


@pytest.mark.parametrize("name", ["CON", "nul.txt", "aux/x.txt", "COM1.log"])
def test_reserved_windows_names_are_not_lost_silently(real, tmp_path, name):
    src = tmp_path / "res.zip"
    with zipfile.ZipFile(src, "w") as zf:
        zf.writestr("keep.txt", b"K")
        zf.writestr(name, b"R" * 10)
    result = real.run("archive.extract", [src])
    if not result.ok:
        return  # refused with a message: explicit, acceptable
    folder = result.outputs[0]
    on_disk = sorted(p.relative_to(folder).as_posix() for p in folder.rglob("*") if p.is_file())
    assert name in on_disk or "Skipped" in result.message, (on_disk, result.message)


def test_backslash_names_and_links(real, tmp_path):
    src = _tar(
        tmp_path / "bs.tar",
        [("file", "dir\\f.txt", b"B"), ("sym", "dir\\g.txt", "f.txt"), ("sym", "alias", "dir")],
    )
    result = real.run("archive.extract", [src])
    assert result.ok, result.message
    folder = result.outputs[0]
    assert (folder / "dir/g.txt").read_bytes() == b"B"
    assert (folder / "alias/g.txt").read_bytes() == b"B"


# --- extended headers retained in memory -------------------------------------


def test_many_pax_headers_do_not_pile_up_in_memory(real, tmp_path):
    """lucy r2 #1 in aggregate: each header is under the 1 MiB cap, but stream-mode
    tarfile keeps every TarInfo (with its pax_headers) in tf.members, so memory grows
    with the archive: 300 headers of 1 MiB in a 325 KB .tar.gz hold about 300 MB."""
    import tracemalloc

    buf = io.BytesIO()
    with tarfile.open(fileobj=buf, mode="w", format=tarfile.PAX_FORMAT) as tf:
        for i in range(128):
            info = tarfile.TarInfo(f"f{i}.txt")
            info.size = 0
            info.pax_headers = {"comment": "z" * (MIB - 100)}
            tf.addfile(info)
    src = tmp_path / "many.tar.gz"
    src.write_bytes(gzip.compress(buf.getvalue(), 9))
    del buf
    tracemalloc.start()
    try:
        result = real.run("archive.extract", [src])
        _, peak = tracemalloc.get_traced_memory()
    finally:
        tracemalloc.stop()
    if result.ok:
        assert peak < 32 * MIB, f"held {peak / MIB:.0f} MB for a {src.stat().st_size // 1024} KB archive"
