"""Adversarial review checks for hq-plct.5 (round 2).

Run: cd engine && python3 -m pytest -q tests/review2_hq_plct_5.py
Each test states the expected behavior; a failure is a finding.
"""

from __future__ import annotations

import bz2
import io
import tarfile
import zipfile
from pathlib import Path

import pytest

from peel_engine import backends
from peel_engine.families import archive as archive_mod
from peel_engine.registry import load_registry

needs_7z = pytest.mark.skipif(not backends.available("7z"), reason="7-Zip CLI not installed")
MIB = 1 << 20


@pytest.fixture
def real():
    return load_registry(force=True)


def _tar(path: Path, members) -> Path:
    with tarfile.open(path, "w") as tf:
        for kind, name, value in members:
            info = tarfile.TarInfo(name)
            info.mtime = 1_700_000_000
            if kind == "file":
                info.size = len(value)
                tf.addfile(info, io.BytesIO(value))
            elif kind == "dir":
                info.type = tarfile.DIRTYPE
                tf.addfile(info)
            elif kind == "sym":
                info.type = tarfile.SYMTYPE
                info.linkname = value
                tf.addfile(info)
            elif kind == "hard":
                info.type = tarfile.LNKTYPE
                info.linkname = value
                tf.addfile(info)
    return path


# --- BLOCKING: link materialization order loses files silently --------------

ROOTFS = [
    # The shape of every merged-/usr Linux root tarball: lib -> usr/lib comes
    # first, the .so symlinks inside usr/lib come later.
    ("dir", "usr", None),
    ("dir", "usr/lib", None),
    ("sym", "lib", "usr/lib"),
    ("file", "usr/lib/libfoo.so.1", b"ELF-ish\n"),
    ("sym", "usr/lib/libfoo.so", "libfoo.so.1"),
]


def test_extract_dir_link_keeps_links_inside_its_target(real, tmp_path):
    """lib is a copy of usr/lib, so it must hold libfoo.so too, or say it was skipped."""
    src = _tar(tmp_path / "rootfs.tar", ROOTFS)
    result = real.run("archive.extract", [src])
    assert result.ok, result.message
    folder = result.outputs[0]
    assert (folder / "usr/lib/libfoo.so").read_bytes() == b"ELF-ish\n"
    if "Skipped" not in result.message:
        assert (folder / "lib/libfoo.so").read_bytes() == b"ELF-ish\n", (
            "lib/libfoo.so silently missing; message: " + result.message
        )


def test_tar_to_zip_dir_link_keeps_links_inside_its_target(real, tmp_path):
    src = _tar(tmp_path / "rootfs.tar", ROOTFS)
    result = real.run("archive.to_zip", [src])
    assert result.ok, result.message
    with zipfile.ZipFile(result.outputs[0]) as zf:
        names = set(zf.namelist())
    assert "usr/lib/libfoo.so" in names
    if "Skipped" not in result.message:
        assert "lib/libfoo.so" in names, sorted(names)


# --- probes that pass (kept as regression evidence) -------------------------


def test_link_loop_is_reported_not_hung(real, tmp_path):
    src = _tar(tmp_path / "loop.tar", [("file", "x", b"x"), ("sym", "a", "b"), ("sym", "b", "a")])
    result = real.run("archive.extract", [src])
    assert result.ok, result.message
    assert "Skipped 2" in result.message


def test_link_to_ancestor_is_skipped(real, tmp_path):
    src = _tar(
        tmp_path / "anc.tar",
        [("dir", "d", None), ("file", "d/f", b"f"), ("sym", "d/e/up", "../.."), ("sym", "d/self", ".")],
    )
    result = real.run("archive.extract", [src])
    assert result.ok, result.message
    assert "own folder" in result.message


def test_link_whose_target_comes_later_resolves(real, tmp_path):
    src = _tar(
        tmp_path / "late.tar",
        [("sym", "first", "chain"), ("hard", "chain", "real"), ("file", "real", b"R")],
    )
    result = real.run("archive.extract", [src])
    assert result.ok, result.message
    folder = result.outputs[0]
    assert (folder / "first").read_bytes() == b"R"
    assert (folder / "chain").read_bytes() == b"R"


def test_doubling_link_bomb_is_budgeted(real, tmp_path):
    """Each level copies the previous twice: 2**20 x 64 KiB if unbudgeted."""
    members = [("dir", "d0", None), ("file", "d0/blob", b"\0" * 65536)]
    for n in range(1, 21):
        members += [("sym", f"d{n}/a", f"../d{n-1}"), ("sym", f"d{n}/b", f"../d{n-1}")]
    src = _tar(tmp_path / "doubler.tar", members)
    result = real.run("archive.extract", [src], {"max_total_mb": 64})
    assert result.ok is False
    assert result.code == "archive_too_large", result.message
    assert not (tmp_path / "doubler").exists()


@needs_7z
def test_bz2_named_7z_bomb_overshoot_is_small(real, tmp_path, monkeypatch):
    """7-Zip lists a bz2 with no size, so only the watcher stands; measure its overshoot."""
    raw = bz2.compress(b"\0" * (512 * MIB), 9)
    src = tmp_path / "liar.7z"
    src.write_bytes(raw)
    seen = []
    orig = archive_mod._tree_usage

    def spy(root):
        out = orig(root)
        seen.append(out[0])
        return out

    monkeypatch.setattr(archive_mod, "_tree_usage", spy)
    result = real.run("archive.extract", [src], {"max_total_mb": 1, "max_ratio": 0})
    assert result.ok is False and result.code == "archive_too_large", result.message
    assert not (tmp_path / "liar").exists()
    assert max(seen) < 128 * MIB, f"7-Zip wrote {max(seen) / MIB:.0f} MB past a 1 MB limit"


def test_zip_up_with_an_ancestor_does_not_swallow_its_own_output(real, tmp_path):
    proj = tmp_path / "proj"
    proj.mkdir()
    (proj / "a.txt").write_text("a")
    result = real.run("archive.zip", [proj / "a.txt", proj])
    assert result.ok, result.message
    with zipfile.ZipFile(result.outputs[0]) as zf:
        names = zf.namelist()
    assert not any(n.endswith(".zip") for n in names), names
