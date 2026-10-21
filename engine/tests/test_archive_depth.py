"""Archive names nested too deep, and 7-Zip folder links that are symlinks.

A tar member named ``a/a/.../l`` thousands of folders deep costs memory with
the square of its depth in the logical tree, so such names are refused before
they are resolved or registered. On Linux 7-Zip writes a stored folder link
as a real symlink; dropping empty folder links must never follow it and
remove the real folder it points at.
"""

from __future__ import annotations

import io
import os
import subprocess
import sys
import tarfile
import time
import tracemalloc
from pathlib import Path

import pytest

from peel_engine.families import archive
from peel_engine.registry import load_registry
from peel_engine.result import RunnerFailed


@pytest.fixture
def real():
    return load_registry(force=True)


def _tar(path: Path, name: str, kind: str = "sym") -> Path:
    buf = io.BytesIO()
    with tarfile.open(fileobj=buf, mode="w", format=tarfile.PAX_FORMAT) as tf:
        info = tarfile.TarInfo(name)
        if kind == "sym":
            info.type = tarfile.SYMTYPE
            info.linkname = "x"
            tf.addfile(info)
        else:
            info.size = 1
            tf.addfile(info, io.BytesIO(b"F"))
    path.write_bytes(buf.getvalue())
    return path


@pytest.mark.parametrize("kind", ["sym", "file"])
def test_deep_member_name_is_refused_fast_and_small(real, tmp_path, kind):
    src = _tar(tmp_path / "deep.tar", "a/" * 10000 + "l", kind)  # under MAX_NAME_CHARS
    tracemalloc.start()
    started = time.monotonic()
    try:
        result = real.run("archive.extract", [src])
        _, peak = tracemalloc.get_traced_memory()
    finally:
        tracemalloc.stop()
    assert time.monotonic() - started < 10
    assert peak < 16 * (1 << 20), peak
    assert not result.ok
    assert "deeper than" in result.message
    assert "Traceback" not in result.message


def test_deep_link_target_is_refused(real, tmp_path):
    buf = io.BytesIO()
    with tarfile.open(fileobj=buf, mode="w", format=tarfile.PAX_FORMAT) as tf:
        info = tarfile.TarInfo("l")
        info.type = tarfile.SYMTYPE
        info.linkname = "a/" * 5000 + "x"
        tf.addfile(info)
    src = tmp_path / "deeplink.tar"
    src.write_bytes(buf.getvalue())
    result = real.run("archive.extract", [src])
    assert not result.ok
    assert "deeper than" in result.message


def test_name_at_the_depth_limit_still_extracts(real, tmp_path):
    name = "/".join(["d"] * (archive.MAX_NAME_DEPTH - 1) + ["f.txt"])
    src = _tar(tmp_path / "ok.tar", name, "file")
    result = real.run("archive.extract", [src])
    assert "deeper than" not in result.message
    if sys.platform == "win32" and "WinError 206" in result.message:
        pytest.skip("this Windows has no long path support")
    assert result.ok, result.message
    assert (result.outputs[0] / name).read_bytes() == b"F"


def test_safe_target_counts_depth_after_dropping_dots(tmp_path):
    name = "./" * 1000 + "/".join(["d"] * archive.MAX_NAME_DEPTH)
    assert archive._safe_target(tmp_path, name) == tmp_path.resolve().joinpath(*["d"] * archive.MAX_NAME_DEPTH)
    with pytest.raises(RunnerFailed, match="deeper than"):
        archive._safe_target(tmp_path, name + "/d")


def test_overlong_name_is_refused(tmp_path):
    with pytest.raises(RunnerFailed, match="longer than"):
        archive._safe_target(tmp_path, "n" * (archive.MAX_NAME_CHARS + 1))


# --- 7-Zip folder links -------------------------------------------------------


def _listing(path: str) -> str:
    return f"Listing archive: t.7z\n\n----------\nPath = {path}\nAttributes = A_ -rwxrwxrwx\n\n".replace(
        "A_ -rwx", "A_ lrwx"
    )


@pytest.mark.skipif(sys.platform == "win32", reason="needs a plain symlink")
def test_drop_never_follows_a_symlink_7z_wrote(tmp_path):
    dest = tmp_path / "out"
    (dest / "keep").mkdir(parents=True)
    os.symlink("keep", dest / "ln")
    skipped: list[str] = []
    archive._drop_7z_folder_links(_listing("ln"), dest, skipped)
    assert (dest / "keep").is_dir()
    assert os.path.islink(dest / "ln")
    assert skipped == []


@pytest.mark.skipif(sys.platform == "win32", reason="needs a plain symlink")
def test_drop_never_follows_a_symlinked_parent(tmp_path):
    dest = tmp_path / "out"
    (dest / "real/inner").mkdir(parents=True)
    os.symlink("real", dest / "ln")
    skipped: list[str] = []
    archive._drop_7z_folder_links(_listing("ln/inner"), dest, skipped)
    assert (dest / "real/inner").is_dir()
    assert skipped == []


def test_drop_still_removes_an_empty_plain_folder_link(tmp_path):
    dest = tmp_path / "out"
    (dest / "ln").mkdir(parents=True)
    skipped: list[str] = []
    archive._drop_7z_folder_links(_listing("ln"), dest, skipped)
    assert not (dest / "ln").exists()
    assert skipped == ["ln (folder link)"]


def test_7z_symlink_to_empty_folder_keeps_the_folder(real, tmp_path):
    from peel_engine import backends

    try:
        seven = backends.require("7z")
    except Exception:
        pytest.skip("no 7z")
    if sys.platform == "win32":
        pytest.skip("7-Zip on Windows does not write links without -snl")
    work = tmp_path / "w"
    (work / "keep").mkdir(parents=True)
    (work / "f.txt").write_bytes(b"F")
    os.symlink("keep", work / "ln")
    src = tmp_path / "t.7z"
    subprocess.run([str(seven), "a", "-snl", str(src), "keep", "f.txt", "ln"], cwd=work, check=True, capture_output=True)
    result = real.run("archive.extract", [src])
    assert result.ok, result.message
    folder = result.outputs[0]
    assert (folder / "keep").is_dir() and not (folder / "keep").is_symlink()
    assert (folder / "f.txt").read_bytes() == b"F"
