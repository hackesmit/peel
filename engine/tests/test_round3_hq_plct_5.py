"""Round 3 fixes for hq-plct.5: tar link tree, extended-header bombs, entry
accounting, zip_up self-exclusion, 7-Zip folder links in conversions."""

from __future__ import annotations

import gzip
import io
import os
import subprocess
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


def _tar(path: Path, members, mode: str = "w") -> Path:
    with tarfile.open(path, mode) as tf:
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
    return path


# --- 1. directory links copy the resolved logical subtree -------------------


def test_dir_link_copy_includes_a_link_written_after_it(real, tmp_path):
    """lucy r2 #2: alias copies sub before sub/up.txt is materialized."""
    src = _tar(
        tmp_path / "late.tar",
        [
            ("file", "a.txt", b"A"),
            ("file", "sub/base.txt", b"B"),
            ("sym", "alias", "sub"),
            ("sym", "sub/up.txt", "../a.txt"),
        ],
    )
    result = real.run("archive.extract", [src])
    assert result.ok, result.message
    folder = result.outputs[0]
    assert (folder / "alias/up.txt").read_bytes() == b"A"
    assert (folder / "alias/base.txt").read_bytes() == b"B"
    assert "Skipped" not in result.message


def test_dir_link_to_a_dir_link_resolves_through_the_chain(real, tmp_path):
    src = _tar(
        tmp_path / "chain.tar",
        [
            ("sym", "outer", "middle"),
            ("sym", "middle", "real"),
            ("file", "real/f.txt", b"F"),
            ("sym", "real/g.txt", "f.txt"),
        ],
    )
    result = real.run("archive.extract", [src])
    assert result.ok, result.message
    folder = result.outputs[0]
    assert (folder / "outer/f.txt").read_bytes() == b"F"
    assert (folder / "outer/g.txt").read_bytes() == b"F"
    assert (folder / "middle/g.txt").read_bytes() == b"F"


def test_cycle_between_linked_folders_is_listed_as_skipped(real, tmp_path):
    src = _tar(
        tmp_path / "cycle.tar",
        [
            ("file", "a/f.txt", b"a"),
            ("sym", "a/to_b", "../b"),
            ("file", "b/g.txt", b"b"),
            ("sym", "b/to_a", "../a"),
            ("sym", "entry", "a"),
        ],
    )
    result = real.run("archive.extract", [src])
    assert result.ok, result.message
    folder = result.outputs[0]
    assert (folder / "entry/f.txt").read_bytes() == b"a"
    assert (folder / "entry/to_b/g.txt").read_bytes() == b"b"
    assert "Skipped" in result.message
    assert "entry/to_b/to_a (link to its own folder)" in result.message


def test_link_through_a_linked_folder_in_its_path_resolves(real, tmp_path):
    src = _tar(
        tmp_path / "via.tar",
        [("sym", "lib", "usr/lib"), ("file", "usr/lib/x.so.1", b"X"), ("sym", "x", "lib/x.so.1")],
    )
    result = real.run("archive.extract", [src])
    assert result.ok, result.message
    assert (result.outputs[0] / "x").read_bytes() == b"X"


# --- 2. extended-header bombs -------------------------------------------------


def _pax_bomb(path: Path, size: int) -> Path:
    with tarfile.open(path, "w:gz", format=tarfile.PAX_FORMAT) as tf:
        info = tarfile.TarInfo("small.txt")
        info.size = 1
        info.pax_headers = {"comment": "z" * size}
        tf.addfile(info, io.BytesIO(b"x"))
    return path


def test_a_large_pax_header_is_refused_before_it_is_read(real, tmp_path, monkeypatch):
    src = _pax_bomb(tmp_path / "pax.tar.gz", 8 * MIB)
    monkeypatch.setattr(tarfile, "is_tarfile", lambda *a, **k: pytest.fail("unbounded probe"))
    result = real.run("archive.extract", [src], {"max_total_mb": 1, "max_member_mb": 1})
    assert result.ok is False
    assert result.code == "archive_too_large", result.message
    assert "extended tar header" in result.message
    assert not (tmp_path / "pax").exists()


def test_a_large_gnu_long_name_is_refused(real, tmp_path):
    src = tmp_path / "gnu.tar.gz"
    with tarfile.open(src, "w:gz", format=tarfile.GNU_FORMAT) as tf:
        info = tarfile.TarInfo("n" * (2 * MIB))
        info.size = 1
        tf.addfile(info, io.BytesIO(b"x"))
    result = real.run("archive.extract", [src], {"max_total_mb": 1})
    assert result.ok is False and result.code == "archive_too_large", result.message


def test_the_stream_budget_charges_header_bytes(real, tmp_path, monkeypatch):
    """With the header cap lifted, the bytes tarfile reads still hit max_total_mb."""
    monkeypatch.setattr(archive_mod, "MAX_TAR_META", 1 << 40)
    src = _pax_bomb(tmp_path / "pax.tar.gz", 40 * MIB)
    seen = []
    orig = archive_mod._BudgetReader.read

    def spy(self, size=-1):
        data = orig(self, size)
        seen.append(self.read_bytes)
        return data

    monkeypatch.setattr(archive_mod._BudgetReader, "read", spy)
    result = real.run("archive.extract", [src], {"max_total_mb": 1, "max_ratio": 0})
    assert result.ok is False and result.code == "archive_too_large", result.message
    assert "max_total_mb" in result.message
    assert max(seen) < 1 * MIB + archive_mod.STREAM_SLACK + 64 * 1024


def test_a_plain_tar_with_a_gzip_body_is_still_read(real, tmp_path):
    src = tmp_path / "liar.tar"
    _tar(tmp_path / "real.tar.gz", [("file", "f.txt", b"F")], mode="w:gz")
    (tmp_path / "real.tar.gz").rename(src)
    result = real.run("archive.extract", [src])
    assert result.ok, result.message
    assert (result.outputs[0] / "f.txt").read_bytes() == b"F"


# --- 3. entry accounting ------------------------------------------------------


def test_implicit_zip_folders_count_against_max_members(real, tmp_path):
    src = tmp_path / "deep.zip"
    with zipfile.ZipFile(src, "w") as zf:
        zf.writestr("a/b/c/file.txt", "x")
    refused = real.run("archive.extract", [src], {"max_members": 1})
    assert refused.ok is False and "max_members" in refused.message
    assert not (tmp_path / "deep").exists()
    fine = real.run("archive.extract", [src], {"max_members": 4})
    assert fine.ok, fine.message


def test_implicit_tar_folders_count_against_max_members(real, tmp_path):
    src = _tar(tmp_path / "deep.tar", [("file", "a/b/c/file.txt", b"x")])
    assert real.run("archive.extract", [src], {"max_members": 3}).ok is False
    assert real.run("archive.extract", [src], {"max_members": 4}).ok


def test_an_explicit_folder_is_charged_once(real, tmp_path):
    src = _tar(tmp_path / "d.tar", [("dir", "d", None), ("file", "d/f", b"x")])
    assert real.run("archive.extract", [src], {"max_members": 2}).ok


def test_a_hard_link_is_not_charged_twice(real, tmp_path):
    """lucy r2 #4: a.txt plus b.txt -> a.txt is two entries."""
    src = _tar(tmp_path / "two.tar", [("file", "a.txt", b"A"), ("hard", "b.txt", "a.txt")])
    result = real.run("archive.extract", [src], {"max_members": 2})
    assert result.ok, result.message
    assert (result.outputs[0] / "b.txt").read_bytes() == b"A"


def test_a_folder_link_copy_charges_each_created_entry(real, tmp_path):
    src = _tar(tmp_path / "c.tar", [("file", "d/f", b"x"), ("sym", "e", "d")])
    # d, d/f, e, e/f: four entries created.
    assert real.run("archive.extract", [src], {"max_members": 3}).ok is False
    assert real.run("archive.extract", [src], {"max_members": 4}).ok


# --- 4. zip_up excludes its own output and staging -----------------------------


def test_zip_up_never_reads_its_claim_or_staging(real, tmp_path):
    proj = tmp_path / "proj"
    (proj / "inner").mkdir(parents=True)
    (proj / "inner/a.txt").write_text("a")
    result = real.run("archive.zip", [proj / "inner/a.txt", proj])
    assert result.ok, result.message
    with zipfile.ZipFile(result.outputs[0]) as zf:
        names = zf.namelist()
    assert sorted(names) == ["a.txt", "proj/inner/a.txt"], names
    assert not [p for p in tmp_path.rglob(".peel-*")]


# --- 5. 7-Zip folder links in conversions --------------------------------------


def test_flatten_links_lists_folder_links_and_copies_file_links(tmp_path):
    tree = tmp_path / "t"
    (tree / "d").mkdir(parents=True)
    (tree / "d/f.txt").write_text("F")
    os.symlink("d", tree / "dlink")
    os.symlink("d/f.txt", tree / "flink")
    os.symlink("nowhere", tree / "dangling")
    budget = archive_mod._Budget(total=MIB, member=MIB, members=100, ratio=0, packed=1, name="t")
    skipped: list[str] = []
    archive_mod._flatten_links(tree, budget, skipped)
    assert "dlink (folder link)" in skipped
    assert "dangling (link to a missing file)" in skipped
    assert not (tree / "flink").is_symlink() and (tree / "flink").read_text() == "F"
    assert not os.path.lexists(tree / "dlink")


@needs_7z
def test_7z_folder_link_is_listed_as_skipped_in_a_conversion(real, tmp_path):
    src_tree = tmp_path / "src"
    (src_tree / "d").mkdir(parents=True)
    (src_tree / "d/f.txt").write_text("F")
    os.symlink("d", src_tree / "dlink")
    archive = tmp_path / "links.7z"
    exe = str(backends.require("7z"))
    subprocess.run([exe, "a", "-snl", "-t7z", str(archive), "."], cwd=src_tree, check=True,
                   stdout=subprocess.DEVNULL)
    result = real.run("archive.to_zip", [archive])
    assert result.ok, result.message
    with zipfile.ZipFile(result.outputs[0]) as zf:
        names = set(zf.namelist())
    assert "d/f.txt" in names
    if not any(n.startswith("dlink/") for n in names):
        assert "dlink (folder link)" in result.message, result.message
