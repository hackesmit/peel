"""PDF page tools: compress, merge, split, organize.

Every fixture PDF is built at run time with PyMuPDF and Pillow, so the suite
ships no binary. The tests drive the real registry, so a typo in a dotted
runner or a wheel-label clash fails here as well as in test_core.
"""

from __future__ import annotations

import io
from pathlib import Path

import pytest

pymupdf = pytest.importorskip("pymupdf")

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


def make_pdf(path: Path, pages: int = 1, *, labels: bool = False) -> Path:
    """A PDF with ``pages`` blank pages, optionally stamped ``P1``, ``P2`` ..."""
    doc = pymupdf.open()
    for n in range(1, pages + 1):
        page = doc.new_page()
        if labels:
            page.insert_text((72, 72), f"P{n}", fontsize=48)
    doc.save(str(path))
    doc.close()
    return path


def make_pdf_with_image(path: Path, size_px: int = 3000) -> Path:
    """A one-page PDF filling the page with a large gradient image (high DPI)."""
    from PIL import Image

    image = Image.new("RGB", (size_px, size_px))
    pixels = image.load()
    for y in range(size_px):
        for x in range(0, size_px, 64):
            pixels[x, y] = ((x // 12) % 256, (y // 12) % 256, 128)
    buffer = io.BytesIO()
    image.save(buffer, format="PNG")
    doc = pymupdf.open()
    page = doc.new_page(width=72, height=72)  # one inch square
    page.insert_image(page.rect, stream=buffer.getvalue())
    doc.save(str(path))
    doc.close()
    return path


def page_count(path: Path) -> int:
    with pymupdf.open(path) as doc:
        return doc.page_count


def page_texts(path: Path) -> list[str]:
    with pymupdf.open(path) as doc:
        return [page.get_text().strip() for page in doc]


@pytest.fixture
def real():
    return load_registry(force=True)


# --- registration -----------------------------------------------------------


def test_the_pdf_tools_are_on_the_ctrl_wheel_for_a_pdf(real, tmp_path):
    one = ids(real.options([make_pdf(tmp_path / "a.pdf")]))
    assert {"pdf.compress", "pdf.split", "pdf.organize"} <= one
    # Merge needs two files, so it only appears for a multi-file drag.
    assert "pdf.merge" not in one
    two = ids(real.options([make_pdf(tmp_path / "b.pdf"), make_pdf(tmp_path / "c.pdf")]))
    assert "pdf.merge" in two


def test_the_pdf_tools_hide_when_pymupdf_is_missing(real, tmp_path, fake_backends):
    fake_backends(pymupdf=False)
    assert "pdf.compress" not in ids(real.options([make_pdf(tmp_path / "a.pdf")]))
    shown = real.options([make_pdf(tmp_path / "b.pdf")], include_unavailable=True)
    assert entries(shown, "tool")["pdf.compress"]["missing_backends"] == ["pymupdf"]


def test_split_and_organize_want_an_editor_and_one_file(real, tmp_path):
    tools = entries(real.options([make_pdf(tmp_path / "a.pdf")]), "tool")
    assert tools["pdf.split"]["needs_editor"] is True
    assert tools["pdf.organize"]["needs_editor"] is True
    assert real.get("pdf.merge").min_files == 2


# --- compress ---------------------------------------------------------------


def test_compress_keeps_the_pages_and_writes_a_sibling(real, tmp_path):
    src = make_pdf(tmp_path / "report.pdf", pages=3)
    result = real.run("pdf.compress", [src])
    assert result.ok
    assert result.outputs[0].name == "report (Compress).pdf"
    assert page_count(result.outputs[0]) == 3


def test_compress_downsamples_a_big_image(real, tmp_path):
    src = make_pdf_with_image(tmp_path / "photo.pdf", size_px=3000)
    before = src.stat().st_size
    result = real.run("pdf.compress", [src], {"image_dpi": "72"})
    assert result.ok
    assert result.outputs[0].stat().st_size < before


def test_compress_reports_a_corrupt_pdf_as_a_message(real, tmp_path):
    bad = tmp_path / "broken.pdf"
    bad.write_bytes(b"%PDF-1.7\nnot really a pdf\n")
    result = real.run("pdf.compress", [bad])
    assert result.ok is False
    assert result.code == "runner_failed"
    assert "Traceback" not in result.message


# --- merge ------------------------------------------------------------------


def test_merge_joins_pdfs_in_order(real, tmp_path):
    first = make_pdf(tmp_path / "first.pdf", pages=2, labels=True)
    second = make_pdf(tmp_path / "second.pdf", pages=1, labels=True)
    result = real.run("pdf.merge", [first, second])
    assert result.ok
    assert result.outputs[0].name == "first (Merge).pdf"
    assert page_count(result.outputs[0]) == 3


def test_merge_needs_at_least_two_files(real, tmp_path):
    from peel_engine.result import UnsupportedInput

    with pytest.raises(UnsupportedInput):
        real.run("pdf.merge", [make_pdf(tmp_path / "lonely.pdf")])


# --- split ------------------------------------------------------------------


def test_split_with_no_ranges_writes_one_file_per_page(real, tmp_path):
    src = make_pdf(tmp_path / "deck.pdf", pages=4)
    result = real.run("pdf.split", [src])
    assert result.ok
    names = sorted(p.name for p in result.outputs)
    assert names == [
        "deck (Split page 1).pdf",
        "deck (Split page 2).pdf",
        "deck (Split page 3).pdf",
        "deck (Split page 4).pdf",
    ]
    assert all(page_count(p) == 1 for p in result.outputs)


def test_split_honours_ranges(real, tmp_path):
    src = make_pdf(tmp_path / "book.pdf", pages=6)
    result = real.run("pdf.split", [src], {"ranges": "1-3,5"})
    assert result.ok
    by_name = {p.name: p for p in result.outputs}
    assert set(by_name) == {"book (Split 1-3).pdf", "book (Split 5).pdf"}
    assert page_count(by_name["book (Split 1-3).pdf"]) == 3
    assert page_count(by_name["book (Split 5).pdf"]) == 1


def test_split_rejects_a_page_past_the_end(real, tmp_path):
    src = make_pdf(tmp_path / "short.pdf", pages=2)
    result = real.run("pdf.split", [src], {"ranges": "5"})
    assert result.ok is False
    assert result.code == "invalid_params"
    assert "out of range" in result.message


def test_split_rejects_a_backwards_range(real, tmp_path):
    src = make_pdf(tmp_path / "back.pdf", pages=5)
    result = real.run("pdf.split", [src], {"ranges": "4-2"})
    assert result.ok is False
    assert result.code == "invalid_params"


# --- organize ---------------------------------------------------------------


def test_organize_reorders_deletes_and_rotates(real, tmp_path):
    src = make_pdf(tmp_path / "pages.pdf", pages=3, labels=True)
    result = real.run("pdf.organize", [src], {"pages": "3,1", "rotate": '{"1": 90}'})
    assert result.ok
    out = result.outputs[0]
    assert out.name == "pages (Organize).pdf"
    assert page_count(out) == 2
    assert page_texts(out) == ["P3", "P1"]  # page 2 deleted, order swapped
    with pymupdf.open(out) as doc:
        assert doc[1].rotation == 90  # the kept source page 1 is rotated


def test_organize_requires_the_page_list(real, tmp_path):
    from peel_engine.result import InvalidParams

    src = make_pdf(tmp_path / "need.pdf", pages=2)
    with pytest.raises(InvalidParams):
        real.run("pdf.organize", [src], {})


def test_organize_rejects_a_bad_rotation(real, tmp_path):
    src = make_pdf(tmp_path / "rot.pdf", pages=2)
    result = real.run("pdf.organize", [src], {"pages": "1,2", "rotate": '{"1": 45}'})
    assert result.ok is False
    assert result.code == "invalid_params"
    assert "multiple of 90" in result.message


# --- round 1 review fixes (hq-plct.5) ---------------------------------------


def test_split_keeps_a_link_between_pages_of_one_range(real, tmp_path):
    src = tmp_path / "linked.pdf"
    doc = pymupdf.open()
    doc.new_page()
    doc.new_page()
    doc[0].insert_link({"kind": pymupdf.LINK_GOTO, "from": pymupdf.Rect(72, 72, 200, 100), "page": 1})
    doc.save(str(src))
    doc.close()
    result = real.run("pdf.split", [src], {"ranges": "1-2"})
    assert result.ok, result.message
    with pymupdf.open(result.outputs[0]) as out:
        links = out[0].get_links()
        assert [(link["kind"], link["page"]) for link in links] == [(pymupdf.LINK_GOTO, 1)]


def test_organize_rotation_adds_to_the_current_rotation(real, tmp_path):
    src = tmp_path / "turned.pdf"
    doc = pymupdf.open()
    doc.new_page().set_rotation(90)
    doc.save(str(src))
    doc.close()
    result = real.run("pdf.organize", [src], {"pages": "1", "rotate": '{"1": 90}'})
    assert result.ok, result.message
    with pymupdf.open(result.outputs[0]) as out:
        assert out[0].rotation == 180
    help_text = {p.name: p.help for p in real.get("pdf.organize").params}["rotate"]
    assert "current rotation" in help_text


def test_compress_keeps_the_original_when_it_cannot_shrink(real, tmp_path):
    first = real.run("pdf.compress", [make_pdf(tmp_path / "doc.pdf", pages=2, labels=True)])
    assert first.ok
    compact = first.outputs[0]
    again = real.run("pdf.compress", [compact])
    assert again.ok, again.message
    assert "already compact" in again.message
    assert again.outputs[0].read_bytes() == compact.read_bytes()
    assert again.outputs[0].name == "doc (Compress) (Compress).pdf"


def test_a_garbage_pdf_error_is_one_trimmed_line(real, tmp_path):
    good = make_pdf(tmp_path / "good.pdf")
    bad = tmp_path / "bad.pdf"
    bad.write_bytes(b"this is not a pdf at all" * 10)
    result = real.run("pdf.merge", [good, bad])
    assert result.ok is False
    assert "\n" not in result.message
    assert result.message == result.message.strip()
    assert "  " not in result.message
