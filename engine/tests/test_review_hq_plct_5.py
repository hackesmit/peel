"""Adversarial review checks for hq-plct.5 (round 1).

Run: cd engine && python3 -m pytest -q tests/test_review_hq_plct_5.py
Each test states the expected behavior; a failure is a blocking finding.
"""

from __future__ import annotations

import io
import os
import subprocess
import tarfile
import zipfile
from pathlib import Path

import pytest

from peel_engine import backends
from peel_engine.registry import load_registry

needs_7z = pytest.mark.skipif(not backends.available("7z"), reason="7-Zip CLI not installed")


@pytest.fixture
def real():
    return load_registry(force=True)


def _tar_with_hardlink(path: Path) -> Path:
    """A tar like GNU tar writes for a tree where b.txt is a hard link to a.txt."""
    buf = io.BytesIO()
    with tarfile.open(fileobj=buf, mode="w") as tf:
        data = b"alpha\n"
        info = tarfile.TarInfo("a.txt")
        info.size = len(data)
        info.mtime = 1_700_000_000
        tf.addfile(info, io.BytesIO(data))
        link = tarfile.TarInfo("b.txt")
        link.type = tarfile.LNKTYPE
        link.linkname = "a.txt"
        link.mtime = 1_700_000_000
        tf.addfile(link)
    path.write_bytes(buf.getvalue())
    return path


def test_tar_to_zip_keeps_hard_linked_files(real, tmp_path):
    """B1: a hard-linked member is a real file; converting must not drop it silently."""
    src = _tar_with_hardlink(tmp_path / "pack.tar")
    result = real.run("archive.to_zip", [src])
    assert result.ok, result.message
    with zipfile.ZipFile(result.outputs[0]) as zf:
        names = set(zf.namelist())
        assert names == {"a.txt", "b.txt"}, names
        assert zf.read("b.txt") == b"alpha\n"


def test_extract_tar_keeps_hard_linked_files(real, tmp_path):
    """B1 (extract form): Extract reports success but b.txt is missing."""
    src = _tar_with_hardlink(tmp_path / "pack.tar")
    result = real.run("archive.extract", [src])
    assert result.ok, result.message
    folder = result.outputs[0]
    assert (folder / "b.txt").read_bytes() == b"alpha\n"


def test_zip_up_accepts_a_file_dated_before_1980(real, tmp_path):
    """B2: zipfile's strict timestamps make Zip fail on any file with mtime < 1980."""
    item = tmp_path / "old scan.txt"
    item.write_text("x", encoding="utf-8")
    os.utime(item, (0, 0))  # epoch mtime: reproducible builds, bad camera clocks, some copies
    result = real.run("archive.zip", [item])
    assert result.ok, result.message


@needs_7z
def test_7z_with_epoch_mtimes_converts_to_zip(real, tmp_path):
    """B2 (conversion form): 7-Zip restores the member mtime, then the ZIP writer refuses it."""
    tree = tmp_path / "tree"
    tree.mkdir()
    member = tree / "a.txt"
    member.write_bytes(b"alpha\n")
    os.utime(member, (0, 0))
    src = tmp_path / "old.7z"
    exe = str(backends.require("7z"))
    subprocess.run([exe, "a", "-t7z", str(src), "a.txt"], cwd=tree, check=True, capture_output=True)
    result = real.run("archive.to_zip", [src])
    assert result.ok, result.message
    with zipfile.ZipFile(result.outputs[0]) as zf:
        assert zf.read("a.txt") == b"alpha\n"
