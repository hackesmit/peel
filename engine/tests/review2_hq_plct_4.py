"""Adversarial review checks for hq-plct.4 (docs family), round 2.

Not collected by default (no test_ prefix). Run:

    cd engine && python3 -m pytest -q tests/review2_hq_plct_4.py

Each BLOCK test fails against f9dcf24. NOTE tests are xfail(strict=False).
"""

from __future__ import annotations

import base64
import os
import subprocess
import zipfile
from pathlib import Path

import pytest

from peel_engine import backends
from peel_engine.registry import load_registry


def needs(*names: str):
    missing = backends.missing(tuple(names))
    return pytest.mark.skipif(bool(missing), reason=f"needs {', '.join(missing)}")


@pytest.fixture(scope="module")
def registry():
    return load_registry(force=True)


NEEDLE = b"PRIVATE-KEY-MATERIAL"


def _secret(tmp_path: Path) -> Path:
    private = tmp_path / "private"
    private.mkdir()
    secret = private / "secret.png"
    secret.write_bytes(b"\x89PNG\r\n\x1a\nPRIVATE-KEY-MATERIAL-0123456789")
    return secret


def _leaked(out: Path, secret: Path) -> bool:
    data = out.read_bytes()
    if out.suffix in (".docx", ".epub", ".odt"):
        with zipfile.ZipFile(out) as z:
            data = b"".join(z.read(n) for n in z.namelist())
    return NEEDLE in data or base64.b64encode(secret.read_bytes())[:24] in data


def _md(tmp_path: Path, body: str, extra: dict[str, str] | None = None) -> Path:
    folder = tmp_path / "downloads"
    folder.mkdir()
    for name, text in (extra or {}).items():
        (folder / name).write_text(text, encoding="utf-8")
    source = folder / "readme.md"
    source.write_text("# hi\n\n" + body + "\n", encoding="utf-8")
    return source


# --- BLOCK R2-1: the resource filter is bypassed by raw HTML forms ----------
# pandoc's --embed-resources (HTML) and the EPUB writer re-parse raw HTML with
# a real HTML parser, which decodes entities and accepts <img/src=...>. The
# Lua filter matches attributes with a literal Lua pattern, so it sees neither.


@needs("pandoc")
@pytest.mark.parametrize("action", ["docs.doc_to_html", "docs.doc_to_epub"])
def test_block_entity_encoded_img_src_cannot_embed_an_outside_file(registry, tmp_path, action):
    secret = _secret(tmp_path)
    encoded = "".join(f"&#{ord(c)};" for c in secret.as_posix())
    source = _md(tmp_path, f'<img src="{encoded}">')
    result = registry.run(action, [source], None)
    assert result.ok, result.message
    assert not _leaked(result.outputs[0], secret), "outside file embedded via &#..; src"


@needs("pandoc")
@pytest.mark.parametrize("action", ["docs.doc_to_html", "docs.doc_to_epub"])
def test_block_slash_separated_img_src_cannot_embed_an_outside_file(registry, tmp_path, action):
    secret = _secret(tmp_path)
    source = _md(tmp_path, f'<img/src="{secret.as_posix()}">')
    result = registry.run(action, [source], None)
    assert result.ok, result.message
    assert not _leaked(result.outputs[0], secret), "outside file embedded via <img/src=>"


@needs("pandoc")
def test_block_a_stylesheet_inside_the_folder_cannot_pull_an_outside_file(registry, tmp_path):
    """A .css beside the .md (inside the folder, so allowed) whose url() names
    an absolute path. --embed-resources follows it recursively."""
    secret = _secret(tmp_path)
    source = _md(
        tmp_path,
        '<link rel="stylesheet" href="evil.css">\n\ntext',
        {"evil.css": f"body{{background:url({secret.as_posix()})}}"},
    )
    result = registry.run("docs.doc_to_html", [source], None)
    assert result.ok, result.message
    assert not _leaked(result.outputs[0], secret), "outside file embedded via a local css url()"


# --- BLOCK R2-2: regression from the B1 fix ---------------------------------
# A formula whose cached result is an empty string (=IF(A1>5,"big","")) is
# the commonest formula in real workbooks. _formula_gaps treats the cached ""
# as "no cached value", recalculates, still gets "", and then writes the
# formula text into the CSV where Excel shows a blank cell.


@needs("openpyxl", "soffice")
def test_block_cached_empty_string_formula_exports_as_a_blank_cell(registry, tmp_path):
    from openpyxl import Workbook

    book = Workbook()
    sheet = book.active
    sheet["A1"], sheet["B1"] = 1, '=IF(A1>5,"big","")'
    sheet["A2"], sheet["B2"] = 9, '=IF(A2>5,"big","")'
    raw = tmp_path / "raw" / "book.xlsx"
    raw.parent.mkdir()
    book.save(raw)
    # Give it cached values, as any workbook saved by Excel or LibreOffice has.
    cached = tmp_path / "cached"
    cached.mkdir()
    subprocess.run(
        [str(backends.require("soffice")), f"-env:UserInstallation={(tmp_path / 'p').as_uri()}",
         "--headless", "--convert-to", "xlsx", "--outdir", str(cached), str(raw)],
        capture_output=True, check=True, timeout=300,
    )
    result = registry.run("docs.sheet_to_csv", [cached / "book.xlsx"], None)
    assert result.ok, result.message
    lines = result.outputs[0].read_text("utf-8-sig").splitlines()
    assert lines == ["1,", "9,big"], f"formula text leaked into CSV: {lines}"


# --- NOTE: symlinked subfolder (author declared residual) --------------------


@needs("pandoc")
@pytest.mark.skipif(os.name == "nt", reason="symlink creation needs privilege on Windows")
@pytest.mark.xfail(strict=False, reason="NOTE: lexical check follows a symlinked subfolder")
def test_note_symlinked_subfolder_pointing_out_is_not_followed(registry, tmp_path):
    secret = _secret(tmp_path)
    source = _md(tmp_path, "![x](link/secret.png)")
    os.symlink(secret.parent, source.parent / "link")
    result = registry.run("docs.doc_to_docx", [source], None)
    assert result.ok
    assert not _leaked(result.outputs[0], secret)
