"""Archive conversions, extraction and zip-up, plus the zip-slip guard.

ZIP, TAR and GZIP fixtures are built at run time with the standard library, so
the suite needs no binary and no backend for the core cases. The 7Z and RAR
cases are skipped when the 7-Zip CLI is not installed. The zip-slip refusal
test does not need any backend and always runs.
"""

from __future__ import annotations

import gzip
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
