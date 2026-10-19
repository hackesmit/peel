"""Verifier checks for the hq-plct.5 narrow final fix (commit 32aa2d9).

Run on Linux and on George (Windows, NTFS). Each case: no regular file is
emptied or changed, and every link whose name clashes in Windows form is
listed as Skipped, for Extract and for a tar to zip conversion.
"""

from __future__ import annotations

import io
import os
import subprocess
import sys
import tarfile
import zipfile
from pathlib import Path

import pytest

from peel_engine.families import archive
from peel_engine.registry import load_registry

WINDOWS = sys.platform == "win32"


@pytest.fixture
def real():
    return load_registry(force=True)


def _tar(path: Path, members) -> Path:
    buf = io.BytesIO()
    with tarfile.open(fileobj=buf, mode="w", format=tarfile.GNU_FORMAT) as tf:
        for kind, name, value in members:
            info = tarfile.TarInfo(name)
            if kind == "file":
                info.size = len(value)
                tf.addfile(info, io.BytesIO(value))
                continue
            info.type = {"dir": tarfile.DIRTYPE, "sym": tarfile.SYMTYPE, "hard": tarfile.LNKTYPE}[kind]
            info.linkname = value if kind != "dir" else ""
            tf.addfile(info)
    path.write_bytes(buf.getvalue())
    return path


# (members, files that must keep their bytes, link names that must be Skipped)
CASES = {
    "sym_case": ([("file", "a.txt", b"ORIG"), ("sym", "A.TXT", "a.txt")], {"a.txt": b"ORIG"}, ["A.TXT"]),
    "hard_case": ([("file", "a.txt", b"ORIG"), ("hard", "A.TXT", "a.txt")], {"a.txt": b"ORIG"}, ["A.TXT"]),
    "hard_case_rev": ([("file", "A.TXT", b"ORIG"), ("hard", "a.txt", "A.TXT")], {"A.TXT": b"ORIG"}, ["a.txt"]),
    "trailing_dot": ([("file", "a.txt", b"ORIG"), ("sym", "a.txt.", "a.txt")], {"a.txt": b"ORIG"}, ["a.txt."]),
    "trailing_space": ([("file", "a.txt", b"ORIG"), ("sym", "a.txt ", "a.txt")], {"a.txt": b"ORIG"}, ["a.txt "]),
    "dir_link_case": ([("file", "lib/x.txt", b"X"), ("sym", "LIB", "lib")], {"lib/x.txt": b"X"}, ["LIB"]),
    "dir_link_dot": ([("file", "lib/x.txt", b"X"), ("sym", "lib.", "lib")], {"lib/x.txt": b"X"}, ["lib."]),
    "link_before_target": ([("sym", "A.TXT", "a.txt"), ("file", "a.txt", b"ORIG")], {"a.txt": b"ORIG"}, ["A.TXT"]),
    "hard_before_target": ([("hard", "A.TXT", "a.txt"), ("file", "a.txt", b"ORIG")], {"a.txt": b"ORIG"}, ["A.TXT"]),
    "link_onto_other_file": (
        [("file", "a.txt", b"AAA"), ("file", "b.txt", b"BBB"), ("sym", "B.TXT", "a.txt")],
        {"a.txt": b"AAA", "b.txt": b"BBB"},
        ["B.TXT"],
    ),
    "two_links_same_win_name": (
        [("file", "a.txt", b"AAA"), ("file", "b.txt", b"BBB"), ("sym", "c.txt", "a.txt"), ("sym", "C.TXT", "b.txt")],
        {"a.txt": b"AAA", "b.txt": b"BBB"},
        ["C.TXT"],
    ),
    "link_in_alias_folder": (
        [("file", "lib/x.txt", b"X"), ("sym", "LIB/x.txt", "../lib/y.txt"), ("file", "lib/y.txt", b"Y")],
        {"lib/x.txt": b"X", "lib/y.txt": b"Y"},
        ["LIB/x.txt"],
    ),
    "dir_link_onto_other_folder": (
        [("file", "lib/x.txt", b"X"), ("file", "other/x.txt", b"OTHER"), ("sym", "Other", "lib")],
        {"lib/x.txt": b"X", "other/x.txt": b"OTHER"},
        ["Other"],
    ),
    "two_dir_links": (
        [("file", "lib/x.txt", b"X"), ("sym", "copy", "lib"), ("sym", "COPY", "lib")],
        {"lib/x.txt": b"X", "copy/x.txt": b"X"},
        ["COPY"],
    ),
    "case_link_inside_copied_dir": (
        [("file", "lib/x.txt", b"X"), ("sym", "lib/X.TXT", "x.txt"), ("sym", "copy", "lib")],
        {"lib/x.txt": b"X"},
        ["lib/X.TXT"],
    ),
}


def _skipped_part(message: str) -> str:
    return message.partition("Skipped")[2]


@pytest.mark.parametrize("case", sorted(CASES))
def test_extract_collisions(real, tmp_path, case):
    members, keep, skipped = CASES[case]
    src = _tar(tmp_path / f"{case}.tar", members)
    result = real.run("archive.extract", [src])
    assert result.ok, result.message
    folder = result.outputs[0]
    for name, data in keep.items():
        assert (folder / name).read_bytes() == data, (name, (folder / name).read_bytes(), result.message)
    for p in folder.rglob("*"):
        if p.is_file():
            assert p.stat().st_size > 0, (p, result.message)
    for name in skipped:
        assert name.rstrip() in _skipped_part(result.message), result.message


@pytest.mark.parametrize("case", sorted(CASES))
def test_to_zip_collisions(real, tmp_path, case):
    members, keep, skipped = CASES[case]
    src = _tar(tmp_path / f"{case}.tar", members)
    result = real.run("archive.to_zip", [src])
    assert result.ok, result.message
    with zipfile.ZipFile(result.outputs[0]) as zf:
        data = {i.filename: zf.read(i) for i in zf.infolist() if not i.is_dir()}
    for name, body in keep.items():
        # Windows may fold a name; the bytes under the stored name must hold.
        match = [v for k, v in data.items() if k.casefold() == name.casefold()]
        assert match and all(v == body for v in match), (name, data, result.message)
    for k, v in data.items():
        assert v, (k, data, result.message)
    for name in skipped:
        assert name.rstrip() in _skipped_part(result.message), result.message


# --- cleanup confinement ------------------------------------------------------


def _outside_link(link: Path, target: Path) -> None:
    if WINDOWS:
        subprocess.run(["cmd", "/c", "mklink", "/J", str(link), str(target)], check=True, capture_output=True)
    else:
        os.symlink(target, link, target_is_directory=True)


@pytest.mark.parametrize("deep", [False, True])
def test_remove_tree_never_leaves_the_folder(tmp_path, deep):
    outside = tmp_path / "outside"
    outside.mkdir()
    (outside / "precious.txt").write_bytes(b"P")
    (outside / "sub").mkdir()
    (outside / "sub/more.txt").write_bytes(b"M")
    claimed = tmp_path / "claimed"
    claimed.mkdir()
    _outside_link(claimed / "j0", outside)
    if deep and not WINDOWS:
        # Deep enough that shutil.rmtree on 3.12 fails and the fd walk runs.
        fd = os.open(claimed, os.O_RDONLY)
        try:
            for i in range(3000):
                os.mkdir("d", dir_fd=fd)
                child = os.open("d", os.O_RDONLY, dir_fd=fd)
                if i in (5, 2999):
                    os.symlink(str(outside), "jl", dir_fd=child)
                    os.symlink(str(outside / "precious.txt"), "fl", dir_fd=child)
                os.close(fd)
                fd = child
        finally:
            os.close(fd)
    else:
        (claimed / "inner").mkdir()
        _outside_link(claimed / "inner/j1", outside)
    archive._remove_tree(claimed)
    assert not claimed.exists()
    assert (outside / "precious.txt").read_bytes() == b"P"
    assert (outside / "sub/more.txt").read_bytes() == b"M"


def test_7z_folder_link_drop_never_removes_a_real_folder(real, tmp_path):
    """_drop_7z_folder_links resolves Path through _safe_target, which follows a
    link 7-Zip wrote; a link to an empty real folder must not cost that folder."""
    seven = None
    from peel_engine import backends

    try:
        seven = backends.require("7z")
    except Exception:
        pytest.skip("no 7z")
    if WINDOWS:
        pytest.skip("7-Zip on Windows does not write links without -snl")
    work = tmp_path / "w"
    work.mkdir()
    (work / "keep").mkdir()
    (work / "f.txt").write_bytes(b"F")
    os.symlink("keep", work / "ln")
    src = tmp_path / "t.7z"
    subprocess.run([str(seven), "a", "-snl", str(src), "keep", "f.txt", "ln"], cwd=work, check=True, capture_output=True)
    result = real.run("archive.extract", [src])
    assert result.ok, result.message
    folder = result.outputs[0]
    assert (folder / "keep").is_dir() and not (folder / "keep").is_symlink(), (
        sorted(p.name for p in folder.iterdir()),
        result.message,
    )


def test_deep_link_name_memory_is_bounded(real, tmp_path):
    """PRE-EXISTING since round 3 (f649237), not added by 32aa2d9, which only
    amplifies it (_NameClaims copies every tree.children key). One symlink
    member named a/a/... 20000 deep (a 40 KB tar, far under the 1 MiB PAX cap)
    costs about 800 MB here (496 MB on 57ad030): _TarTree._register stores
    every prefix, so memory grows with depth squared."""
    import tracemalloc

    buf = io.BytesIO()
    with tarfile.open(fileobj=buf, mode="w", format=tarfile.PAX_FORMAT) as tf:
        info = tarfile.TarInfo("a/" * 20000 + "l")
        info.type = tarfile.SYMTYPE
        info.linkname = "x"
        tf.addfile(info)
    src = tmp_path / "deep.tar"
    src.write_bytes(buf.getvalue())
    tracemalloc.start()
    try:
        real.run("archive.extract", [src])
        _, peak = tracemalloc.get_traced_memory()
    finally:
        tracemalloc.stop()
    assert peak < 64 * (1 << 20), f"peak {peak >> 20} MB for a {src.stat().st_size >> 10} KB archive"
