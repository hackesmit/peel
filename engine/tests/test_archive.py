"""Archive conversions, extraction and zip-up, plus the zip-slip guard.

ZIP, TAR and GZIP fixtures are built at run time with the standard library, so
the suite needs no binary and no backend for the core cases. The 7Z and RAR
cases are skipped when the 7-Zip CLI is not installed. The zip-slip refusal
test does not need any backend and always runs.
"""

from __future__ import annotations

import gzip
import os
import io
import tarfile
import zipfile
from pathlib import Path

import pytest

from peel_engine import backends
from peel_engine.registry import load_registry


def entries(options: dict, family: str) -> dict[str, dict]:
    for wheel in options["wheels"]:
        if wheel["family"] == family:
            return {e["id"]: e for group in wheel["groups"] for e in group["entries"]}
    return {}


def ids(options: dict) -> set[str]:
    return {
        e["id"]
        for wheel in options["wheels"]
        for group in wheel["groups"]
        for e in group["entries"]
    }

has_7z = backends.available("7z")
needs_7z = pytest.mark.skipif(not has_7z, reason="7-Zip CLI not installed")

PAYLOAD = {"a.txt": "alpha\n", "sub/b.txt": "beta\n", "sub/deep/c.txt": "gamma\n"}


@pytest.fixture
def real():
    return load_registry(force=True)


def make_tree(root: Path, files: dict[str, str] = PAYLOAD) -> Path:
    for rel, text in files.items():
        path = root / rel
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8")
    return root


def make_zip(path: Path, files: dict[str, str] = PAYLOAD) -> Path:
    with zipfile.ZipFile(path, "w", compression=zipfile.ZIP_DEFLATED) as zf:
        for rel, text in files.items():
            zf.writestr(rel, text)
    return path


def make_tar(path: Path, files: dict[str, str] = PAYLOAD, *, mode: str = "w") -> Path:
    with tarfile.open(path, mode) as tf:
        for rel, text in files.items():
            data = text.encode("utf-8")
            info = tarfile.TarInfo(rel)
            info.size = len(data)
            tf.addfile(info, io.BytesIO(data))
    return path


def make_7z(path: Path, files: dict[str, str] = PAYLOAD) -> Path:
    import subprocess

    exe = backends.require("7z")
    src = path.parent / (path.stem + "_src")
    make_tree(src, files)
    subprocess.run(
        [str(exe), "a", "-y", "-t7z", "--", str(path), "."],
        cwd=str(src),
        capture_output=True,
        check=True,
    )
    return path


def read_archive(real, path: Path, tmp_path: Path) -> dict[str, str]:
    """Extract an archive with the engine and read its tree back as a dict."""
    result = real.run("archive.extract", [path])
    assert result.ok, result.message
    folder = result.outputs[0]
    out: dict[str, str] = {}
    for file in sorted(folder.rglob("*")):
        if file.is_file():
            out[file.relative_to(folder).as_posix()] = file.read_text(encoding="utf-8")
    return out


# --- registration -----------------------------------------------------------


def test_archive_conversions_are_on_the_shift_wheel(real, tmp_path):
    src = make_zip(tmp_path / "pack.zip")
    converts = entries(real.options([src], include_unavailable=True), "convert")
    assert {"archive.to_tar", "archive.to_gzip", "archive.to_7z"} <= set(converts)
    # A zip never offers to convert to itself.
    assert "archive.to_zip" not in converts


def test_extract_and_zip_are_tools(real, tmp_path):
    src = make_zip(tmp_path / "pack.zip")
    tools = ids(real.options([src]))
    assert "archive.extract" in tools
    assert "archive.zip" in tools


def test_to_7z_is_hidden_without_the_backend(real, tmp_path, fake_backends):
    fake_backends(**{"7z": False})
    src = make_zip(tmp_path / "pack.zip")
    assert "archive.to_7z" not in ids(real.options([src]))


def test_zip_works_on_a_folder(real, tmp_path):
    folder = make_tree(tmp_path / "album")
    assert "archive.zip" in ids(real.options([folder]))


# --- conversions ------------------------------------------------------------


def test_zip_converts_to_tar_and_keeps_every_file(real, tmp_path):
    src = make_zip(tmp_path / "data.zip")
    result = real.run("archive.to_tar", [src])
    assert result.ok
    assert result.outputs[0].name == "data.tar"
    assert read_archive(real, result.outputs[0], tmp_path) == PAYLOAD


def test_tar_converts_to_zip(real, tmp_path):
    src = make_tar(tmp_path / "data.tar")
    result = real.run("archive.to_zip", [src])
    assert result.ok
    assert result.outputs[0].name == "data.zip"
    assert read_archive(real, result.outputs[0], tmp_path) == PAYLOAD


def test_to_gzip_writes_a_gzipped_tar(real, tmp_path):
    src = make_zip(tmp_path / "data.zip")
    result = real.run("archive.to_gzip", [src])
    assert result.ok
    assert result.outputs[0].name == "data.tar.gz"
    assert tarfile.is_tarfile(result.outputs[0])
    assert read_archive(real, result.outputs[0], tmp_path) == PAYLOAD


def test_a_single_file_gzip_extracts_to_that_file(real, tmp_path):
    raw = tmp_path / "notes.txt.gz"
    with gzip.open(raw, "wb") as out:
        out.write(b"just one stream\n")
    result = real.run("archive.extract", [raw])
    assert result.ok
    inside = list(result.outputs[0].rglob("*"))
    assert [p.name for p in inside] == ["notes.txt"]
    assert inside[0].read_text(encoding="utf-8") == "just one stream\n"


@needs_7z
def test_zip_converts_to_7z_and_back(real, tmp_path):
    src = make_zip(tmp_path / "data.zip")
    made = real.run("archive.to_7z", [src])
    assert made.ok
    assert made.outputs[0].name == "data.7z"
    # Read the 7z back out again through a conversion to zip.
    back = real.run("archive.to_zip", [made.outputs[0]])
    assert back.ok
    assert read_archive(real, back.outputs[0], tmp_path) == PAYLOAD


@needs_7z
def test_a_7z_source_can_be_extracted(real, tmp_path):
    src = make_7z(tmp_path / "bundle.7z")
    assert read_archive(real, src, tmp_path) == PAYLOAD


@needs_7z
def test_a_rar_input_is_routed_to_7zip(real, tmp_path):
    """RAR is read-only and goes through 7-Zip. We cannot author a real RAR
    without non-free software, so this proves the routing and the clean error
    on a file that only claims to be one."""
    fake = tmp_path / "not-really.rar"
    fake.write_bytes(b"Rar!\x1a\x07\x00 nonsense")
    result = real.run("archive.extract", [fake])
    assert result.ok is False
    assert result.code == "runner_failed"
    assert "7-Zip" in result.message


# --- extraction -------------------------------------------------------------


def test_extract_writes_a_sibling_folder_with_the_tree(real, tmp_path):
    src = make_zip(tmp_path / "pack.zip")
    result = real.run("archive.extract", [src])
    assert result.ok
    folder = result.outputs[0]
    assert folder == tmp_path / "pack"
    assert (folder / "a.txt").read_text(encoding="utf-8") == "alpha\n"
    assert (folder / "sub" / "deep" / "c.txt").read_text(encoding="utf-8") == "gamma\n"


def test_extract_reports_a_corrupt_zip(real, tmp_path):
    bad = tmp_path / "broken.zip"
    bad.write_bytes(b"PK\x03\x04 not a real zip")
    result = real.run("archive.extract", [bad])
    assert result.ok is False
    assert result.code == "runner_failed"
    assert "Traceback" not in result.message


# --- the zip-slip guard -----------------------------------------------------


def test_a_zip_slip_member_is_refused(real, tmp_path):
    evil = tmp_path / "evil.zip"
    with zipfile.ZipFile(evil, "w") as zf:
        zf.writestr("../escaped.txt", "pwned")
    result = real.run("archive.extract", [evil])
    assert result.ok is False
    assert result.code == "runner_failed"
    assert "unsafe" in result.message
    assert not (tmp_path / "escaped.txt").exists()


def test_a_tar_slip_member_is_refused(real, tmp_path):
    evil = tmp_path / "evil.tar"
    with tarfile.open(evil, "w") as tf:
        data = b"pwned"
        info = tarfile.TarInfo("../escaped.txt")
        info.size = len(data)
        tf.addfile(info, io.BytesIO(data))
    result = real.run("archive.extract", [evil])
    assert result.ok is False
    assert result.code == "runner_failed"
    assert not (tmp_path / "escaped.txt").exists()


def test_an_absolute_member_is_refused(real, tmp_path):
    evil = tmp_path / "abs.zip"
    with zipfile.ZipFile(evil, "w") as zf:
        zf.writestr("/tmp/peel-escaped.txt", "pwned")
    result = real.run("archive.extract", [evil])
    assert result.ok is False
    assert result.code == "runner_failed"
    assert not Path("/tmp/peel-escaped.txt").exists()


# --- zip up -----------------------------------------------------------------


def test_zip_up_a_folder_nests_it_under_its_name(real, tmp_path):
    folder = make_tree(tmp_path / "album")
    result = real.run("archive.zip", [folder])
    assert result.ok
    assert result.outputs[0].name == "album.zip"
    with zipfile.ZipFile(result.outputs[0]) as zf:
        assert set(zf.namelist()) == {
            "album/a.txt",
            "album/sub/b.txt",
            "album/sub/deep/c.txt",
        }


def test_zip_up_a_multi_file_drag_makes_one_archive(real, tmp_path):
    one = tmp_path / "one.txt"
    one.write_text("1\n", encoding="utf-8")
    two = tmp_path / "two.txt"
    two.write_text("2\n", encoding="utf-8")
    result = real.run("archive.zip", [one, two])
    assert result.ok
    assert result.outputs[0].name == "one.zip"
    with zipfile.ZipFile(result.outputs[0]) as zf:
        assert set(zf.namelist()) == {"one.txt", "two.txt"}


# --- decompression bombs (hq-plct.5 round 1) --------------------------------

from peel_engine.families import archive as archive_mod  # noqa: E402

MIB = 1 << 20


def _zeros(size: int, chunk: int = MIB):
    """Yield ``size`` zero bytes in chunks, so no fixture sits in memory whole."""
    left = size
    while left > 0:
        n = min(chunk, left)
        yield bytes(n)
        left -= n


def make_zip_bomb(path: Path, size: int = 32 * MIB) -> Path:
    with zipfile.ZipFile(path, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as zf:
        with zf.open("zeros.bin", "w", force_zip64=True) as out:
            for block in _zeros(size):
                out.write(block)
    return path


class _ZeroReader(io.RawIOBase):
    def __init__(self, size: int) -> None:
        self.left = size

    def readable(self) -> bool:
        return True

    def readinto(self, buf) -> int:
        n = min(len(buf), self.left)
        buf[:n] = bytes(n)
        self.left -= n
        return n


def make_tar_bomb(path: Path, size: int = 32 * MIB, mode: str = "w:gz") -> Path:
    with tarfile.open(path, mode) as tf:
        info = tarfile.TarInfo("zeros.bin")
        info.size = size
        tf.addfile(info, io.BufferedReader(_ZeroReader(size)))
    return path


def assert_refused(result, tmp_path: Path, leftover: str) -> None:
    assert result.ok is False, result.message
    assert result.code == "archive_too_large", result.message
    assert "Refused" in result.message or "disk space" in result.message
    assert not (tmp_path / leftover).exists(), "a refused extract left its folder behind"


def test_a_zip_bomb_is_refused_by_the_default_ratio(real, tmp_path):
    src = make_zip_bomb(tmp_path / "bomb.zip")
    assert src.stat().st_size < 100_000
    result = real.run("archive.extract", [src])
    assert_refused(result, tmp_path, "bomb")
    assert "max_ratio" in result.message


def test_a_zip_over_the_total_limit_is_refused_on_streamed_bytes(real, tmp_path):
    src = tmp_path / "many.zip"
    with zipfile.ZipFile(src, "w", compression=zipfile.ZIP_DEFLATED) as zf:
        for n in range(3):
            zf.writestr(f"part{n}.bin", os.urandom(MIB))  # incompressible, ratio 1
    result = real.run("archive.extract", [src], {"max_total_mb": 2})
    assert_refused(result, tmp_path, "many")
    assert "max_total_mb" in result.message


def test_a_zip_with_too_many_members_is_refused_before_writing(real, tmp_path):
    src = make_zip(tmp_path / "crowd.zip", {f"f{n}.txt": "x" for n in range(20)})
    result = real.run("archive.extract", [src], {"max_members": 10})
    assert_refused(result, tmp_path, "crowd")
    assert "max_members" in result.message


def test_a_zip_member_over_the_member_limit_is_refused(real, tmp_path):
    src = tmp_path / "big.zip"
    with zipfile.ZipFile(src, "w") as zf:
        zf.writestr("big.bin", os.urandom(2 * MIB))
    result = real.run("archive.extract", [src], {"max_member_mb": 1})
    assert_refused(result, tmp_path, "big")
    assert "max_member_mb" in result.message


def test_a_gzip_tar_bomb_is_refused(real, tmp_path):
    src = make_tar_bomb(tmp_path / "bomb.tar.gz")
    assert src.stat().st_size < 100_000
    result = real.run("archive.extract", [src])
    assert_refused(result, tmp_path, "bomb")


def test_a_bzip2_tar_bomb_is_refused_early(real, tmp_path):
    src = make_tar_bomb(tmp_path / "deep.tar.bz2", size=256 * MIB, mode="w:bz2")
    assert src.stat().st_size < 10_000
    result = real.run("archive.extract", [src])
    assert_refused(result, tmp_path, "deep")


def test_a_single_file_gzip_bomb_is_refused(real, tmp_path):
    src = tmp_path / "disk.img.gz"
    with gzip.open(src, "wb", compresslevel=9) as out:
        for block in _zeros(32 * MIB):
            out.write(block)
    result = real.run("archive.extract", [src])
    assert_refused(result, tmp_path, "disk.img")


def test_a_bomb_conversion_is_refused_and_leaves_no_output(real, tmp_path):
    src = make_zip_bomb(tmp_path / "bomb.zip")
    result = real.run("archive.to_tar", [src])
    assert result.ok is False
    assert result.code == "archive_too_large"
    assert sorted(p.name for p in tmp_path.iterdir()) == ["bomb.zip"]


def test_free_disk_space_caps_the_total(real, tmp_path, monkeypatch):
    monkeypatch.setattr(archive_mod, "_free_bytes", lambda path: archive_mod.GIB + MIB)
    src = tmp_path / "fits.zip"
    with zipfile.ZipFile(src, "w") as zf:
        zf.writestr("a.bin", os.urandom(2 * MIB))
    result = real.run("archive.extract", [src])
    assert_refused(result, tmp_path, "fits")
    assert "free disk space" in result.message


def test_the_limits_can_be_raised_for_a_genuine_file(real, tmp_path):
    src = make_zip_bomb(tmp_path / "zeros.zip", size=4 * MIB)
    assert real.run("archive.extract", [src]).ok is False
    result = real.run("archive.extract", [src], {"max_ratio": 0})
    assert result.ok, result.message
    assert (result.outputs[0] / "zeros.bin").stat().st_size == 4 * MIB


def test_the_limits_are_params_with_desktop_defaults(real):
    for action_id in ("archive.extract", "archive.to_zip", "archive.to_7z"):
        params = {p.name: p.default for p in real.get(action_id).params}
        assert params == {
            "max_total_mb": 20480,
            "max_member_mb": 20480,
            "max_members": 1_000_000,
            "max_ratio": 1000,
        }


def test_zip_extraction_does_not_run_testzip(real, tmp_path, monkeypatch):
    """The old full decompress pass before extraction is gone."""

    def boom(self):  # pragma: no cover - must not be called
        raise AssertionError("testzip called")

    monkeypatch.setattr(zipfile.ZipFile, "testzip", boom)
    result = real.run("archive.extract", [make_zip(tmp_path / "pack.zip")])
    assert result.ok, result.message


def _make_7z_of_zeros(path: Path, size: int) -> Path:
    import subprocess

    exe = backends.require("7z")
    src = path.parent / (path.stem + "_src")
    src.mkdir()
    with open(src / "zeros.bin", "wb") as out:
        for block in _zeros(size):
            out.write(block)
    subprocess.run(
        [str(exe), "a", "-y", "-t7z", "-mx=9", "--", str(path), "zeros.bin"],
        cwd=str(src),
        capture_output=True,
        check=True,
    )
    (src / "zeros.bin").unlink()
    src.rmdir()
    return path


@needs_7z
def test_a_7z_bomb_is_refused_from_its_listing(real, tmp_path):
    src = _make_7z_of_zeros(tmp_path / "bomb.7z", 64 * MIB)
    assert src.stat().st_size < 100_000
    result = real.run("archive.extract", [src])
    assert_refused(result, tmp_path, "bomb")


@needs_7z
def test_a_7z_that_lies_in_its_listing_is_stopped_on_written_bytes(real, tmp_path, monkeypatch):
    """Headers can lie: the folder is measured while 7-Zip runs."""
    monkeypatch.setattr(archive_mod, "_parse_7z_listing", lambda text: [])
    src = _make_7z_of_zeros(tmp_path / "liar.7z", 16 * MIB)
    result = real.run("archive.extract", [src], {"max_total_mb": 1, "max_ratio": 0})
    assert_refused(result, tmp_path, "liar")
    assert "max_total_mb" in result.message


def test_the_7z_listing_parser_reads_sizes():
    text = (
        "Path = x.7z\nType = 7z\n\n----------\n"
        "Path = a.txt\nSize = 12\nFolder = -\n\n"
        "Path = sub\nSize = 0\nFolder = +\n\n"
        "Path = sub/b.bin\nSize = 4096\n"
    )
    assert archive_mod._parse_7z_listing(text) == [("a.txt", 12), ("sub", 0), ("sub/b.bin", 4096)]


# --- tar links and odd members ----------------------------------------------


def _tar(path: Path, members: list[tuple[str, str, str | bytes]]) -> Path:
    """members: (kind, name, data or linkname); kind is file, sym, hard, fifo, chr."""
    with tarfile.open(path, "w") as tf:
        for kind, name, value in members:
            info = tarfile.TarInfo(name)
            if kind == "file":
                data = value.encode() if isinstance(value, str) else value
                info.size = len(data)
                tf.addfile(info, io.BytesIO(data))
                continue
            info.type = {
                "sym": tarfile.SYMTYPE,
                "hard": tarfile.LNKTYPE,
                "fifo": tarfile.FIFOTYPE,
                "chr": tarfile.CHRTYPE,
                "dir": tarfile.DIRTYPE,
            }[kind]
            if kind in ("sym", "hard"):
                info.linkname = value
            tf.addfile(info)
    return path


def test_internal_symlinks_become_copies(real, tmp_path):
    src = _tar(
        tmp_path / "links.tar",
        [
            ("file", "a.txt", "alpha\n"),
            ("file", "sub/b.txt", "beta\n"),
            ("sym", "sub/up.txt", "../a.txt"),
            ("sym", "alias", "sub"),
            ("sym", "chain.txt", "sub/up.txt"),
        ],
    )
    result = real.run("archive.extract", [src])
    assert result.ok, result.message
    folder = result.outputs[0]
    assert not (folder / "sub" / "up.txt").is_symlink()
    assert (folder / "sub" / "up.txt").read_text() == "alpha\n"
    assert (folder / "alias" / "b.txt").read_text() == "beta\n"
    assert (folder / "chain.txt").read_text() == "alpha\n"
    assert "Skipped" not in result.message


def test_internal_symlinks_survive_a_conversion(real, tmp_path):
    src = _tar(tmp_path / "links.tar", [("file", "a.txt", "alpha\n"), ("sym", "b.txt", "a.txt")])
    result = real.run("archive.to_zip", [src])
    assert result.ok, result.message
    with zipfile.ZipFile(result.outputs[0]) as zf:
        assert zf.read("b.txt") == b"alpha\n"


def test_an_escaping_symlink_is_refused(real, tmp_path):
    src = _tar(tmp_path / "evil.tar", [("file", "a.txt", "x"), ("sym", "sub/out", "../../etc/passwd")])
    result = real.run("archive.extract", [src])
    assert result.ok is False
    assert "unsafe" in result.message
    assert not (tmp_path / "evil").exists()


def test_an_escaping_hard_link_is_refused(real, tmp_path):
    src = _tar(tmp_path / "evil.tar", [("hard", "x", "../outside.txt")])
    result = real.run("archive.extract", [src])
    assert result.ok is False
    assert "unsafe" in result.message


def test_devices_fifos_and_dangling_links_are_listed_as_skipped(real, tmp_path):
    src = _tar(
        tmp_path / "odd.tar",
        [
            ("file", "a.txt", "alpha\n"),
            ("fifo", "pipe", ""),
            ("chr", "dev/null", ""),
            ("sym", "gone.txt", "missing.txt"),
        ],
    )
    result = real.run("archive.extract", [src])
    assert result.ok, result.message
    assert "Skipped 3" in result.message
    assert "pipe (fifo)" in result.message
    assert "dev/null (device)" in result.message
    assert "gone.txt (link to a missing file)" in result.message


def test_a_failed_tar_extract_removes_its_folder(real, tmp_path):
    src = _tar(tmp_path / "half.tar", [("file", "good.txt", "fine\n"), ("file", "../evil.txt", "x")])
    result = real.run("archive.extract", [src])
    assert result.ok is False
    assert not (tmp_path / "half").exists()
    assert not (tmp_path / "evil.txt").exists()


def test_a_failed_extract_keeps_a_folder_it_did_not_claim(real, tmp_path):
    mine = tmp_path / "half"
    mine.mkdir()
    (mine / "keep.txt").write_text("mine\n")
    src = _tar(tmp_path / "half.tar", [("file", "good.txt", "fine\n"), ("file", "../evil.txt", "x")])
    result = real.run("archive.extract", [src])
    assert result.ok is False
    assert (mine / "keep.txt").read_text() == "mine\n"
    assert not (tmp_path / "half 2").exists()


# --- zip up name collisions -------------------------------------------------


def test_zip_up_keeps_two_files_with_the_same_name(real, tmp_path):
    (tmp_path / "one").mkdir()
    (tmp_path / "two").mkdir()
    a = tmp_path / "one" / "notes.txt"
    b = tmp_path / "two" / "Notes.TXT"
    a.write_bytes(b"first\n")
    b.write_bytes(b"second\n")
    result = real.run("archive.zip", [a, b])
    assert result.ok, result.message
    with zipfile.ZipFile(result.outputs[0]) as zf:
        names = zf.namelist()
        assert len({n.casefold() for n in names}) == len(names) == 2
        assert zf.read("notes.txt") == b"first\n"
        assert zf.read("Notes 2.TXT") == b"second\n"


def test_zip_up_keeps_two_folders_with_the_same_name(real, tmp_path):
    a = tmp_path / "x" / "album"
    b = tmp_path / "y" / "Album"
    for folder, data in ((a, b"1\n"), (b, b"2\n")):
        folder.mkdir(parents=True)
        (folder / "a.txt").write_bytes(data)
    result = real.run("archive.zip", [a, b])
    assert result.ok, result.message
    with zipfile.ZipFile(result.outputs[0]) as zf:
        assert zf.read("album/a.txt") == b"1\n"
        assert zf.read("Album 2/a.txt") == b"2\n"


# --- double extensions ------------------------------------------------------


def test_a_tar_gz_is_named_as_one_extension(real, tmp_path):
    src = make_tar(tmp_path / "bundle.tar.gz", mode="w:gz")
    extracted = real.run("archive.extract", [src])
    assert extracted.ok, extracted.message
    assert extracted.outputs[0].name == "bundle"
    zipped = real.run("archive.to_zip", [src])
    assert zipped.outputs[0].name == "bundle.zip"
    assert read_archive(real, zipped.outputs[0], tmp_path) == PAYLOAD


@pytest.mark.parametrize("name,mode", [("pack.tgz", "w:gz"), ("pack.tar.bz2", "w:bz2"), ("pack.tar.xz", "w:xz")])
def test_other_compressed_tars_extract_and_name_cleanly(real, tmp_path, name, mode):
    src = make_tar(tmp_path / name, mode=mode)
    result = real.run("archive.to_zip", [src])
    assert result.ok, result.message
    assert result.outputs[0].name == "pack.zip"


def test_to_gzip_is_not_offered_for_a_gzip(real, tmp_path):
    src = make_tar(tmp_path / "bundle.tar.gz", mode="w:gz")
    converts = entries(real.options([src], include_unavailable=True), "convert")
    assert "archive.to_gzip" not in converts
    assert "archive.to_zip" in converts


def test_a_corrupt_gzip_reports_one_clean_line(real, tmp_path):
    bad = tmp_path / "broken.gz"
    bad.write_bytes(b"\x1f\x8b\x08\x00garbage that is not deflate data at all")
    result = real.run("archive.extract", [bad])
    assert result.ok is False
    assert "\n" not in result.message
    assert "Traceback" not in result.message
