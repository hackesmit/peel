"""PDF page tools: compress, merge, split, organize.

The Ctrl wheel's PDF group. Every entry leans on PyMuPDF (the ``pymupdf``
backend, imported as ``fitz`` for discovery), and the import stays inside the
runners so listing options never pulls the library in (the speed test guards
that).

Naming follows the engine contract: a tool writes ``<name> (<Tool>).pdf`` next
to the source and never overwrites. Split decides its own output names (one per
page or per range) through ``job.output_path(suffix=...)``.
"""

from __future__ import annotations

from ..registry import SAME, TOOL, Job, Param, Registry
from ..result import InvalidParams, Result, RunnerFailed, ok


def register(registry: Registry) -> None:
    registry.add(
        id="pdf.compress",
        family=TOOL,
        label="Compress",
        group="PDF",
        inputs=("pdf",),
        output_ext=SAME,
        backends=("pymupdf",),
        params=(
            Param(
                "image_dpi",
                "int",
                "Target image resolution (DPI)",
                default=150,
                minimum=0,
                maximum=1200,
                help="Images at a higher resolution are rescaled down to this DPI. 0 leaves images untouched.",
            ),
        ),
        runner="peel_engine.families.pdf_tools:compress",
    )
    registry.add(
        id="pdf.merge",
        family=TOOL,
        label="Merge",
        group="PDF",
        inputs=("pdf",),
        output_ext="pdf",
        mode="combine",
        min_files=2,
        backends=("pymupdf",),
        runner="peel_engine.families.pdf_tools:merge",
    )
    registry.add(
        id="pdf.split",
        family=TOOL,
        label="Split",
        group="PDF",
        inputs=("pdf",),
        output_ext=None,
        max_files=1,
        needs_editor=True,
        backends=("pymupdf",),
        params=(
            Param(
                "ranges",
                "string",
                "Page ranges",
                default="",
                help='Leave empty to write one file per page, or give ranges like "1-3,5,8-10".',
            ),
        ),
        runner="peel_engine.families.pdf_tools:split",
    )
    registry.add(
        id="pdf.organize",
        family=TOOL,
        label="Organize",
        group="PDF",
        inputs=("pdf",),
        output_ext=SAME,
        max_files=1,
        needs_editor=True,
        backends=("pymupdf",),
        params=(
            Param(
                "pages",
                "list",
                "Page order",
                required=True,
                help="1-based page numbers in the order to keep. Pages left out are deleted.",
            ),
            Param(
                "rotate",
                "json",
                "Rotations",
                default=None,
                help='Per-page clockwise rotation, keyed by source page, e.g. {"1": 90, "3": 180}.',
            ),
        ),
        runner="peel_engine.families.pdf_tools:organize",
    )


def _open(job: Job):
    """Open the job's source as a PDF, failing with a clean message."""
    import pymupdf

    try:
        doc = pymupdf.open(job.source)
    except Exception as exc:  # a corrupt or password-locked file
        raise RunnerFailed(f"Cannot open {job.source.name}: {exc}") from None
    if not doc.is_pdf:
        doc.close()
        raise RunnerFailed(f"{job.source.name} is not a PDF")
    if doc.needs_pass:
        doc.close()
        raise RunnerFailed(f"{job.source.name} is password protected")
    return doc


def _save(doc, target) -> None:
    """Save with the standard lossless shrink: collect garbage, deflate streams."""
    doc.save(
        str(target),
        garbage=4,
        clean=True,
        deflate=True,
        deflate_images=True,
        deflate_fonts=True,
    )


def compress(job: Job) -> Result:
    """Shrink a PDF: optionally downsample oversized images, then deflate."""
    doc = _open(job)
    try:
        dpi = job.params["image_dpi"]
        if dpi:
            # rewrite_images downsamples images above the threshold to the
            # target, and insists the target be strictly smaller, so the
            # threshold sits one DPI above the resolution we aim for.
            doc.rewrite_images(dpi_threshold=dpi + 1, dpi_target=dpi, quality=80)
        target = job.output_path()
        _save(doc, target)
    finally:
        doc.close()
    return ok([target], f"Compressed to {target.name}")


def merge(job: Job) -> Result:
    """Join every input PDF into one, in the order given."""
    import pymupdf

    out = pymupdf.open()
    try:
        for path in job.inputs:
            try:
                part = pymupdf.open(path)
            except Exception as exc:
                raise RunnerFailed(f"Cannot open {path.name}: {exc}") from None
            with part:
                if not part.is_pdf:
                    raise RunnerFailed(f"{path.name} is not a PDF")
                if part.needs_pass:
                    raise RunnerFailed(f"{path.name} is password protected")
                out.insert_pdf(part)
        target = job.output_path()
        _save(out, target)
    finally:
        out.close()
    return ok([target], f"Merged {len(job.inputs)} PDFs into {target.name}")


def split(job: Job) -> Result:
    """Write one PDF per page, or one per range when ``ranges`` is given."""
    import pymupdf

    doc = _open(job)
    try:
        count = doc.page_count
        groups = _parse_ranges(job.params["ranges"], count)
        targets: list = []
        for label, pages in groups:
            part = pymupdf.open()
            try:
                for index in pages:
                    part.insert_pdf(doc, from_page=index, to_page=index)
                target = job.output_path(ext="pdf", suffix=label)
                _save(part, target)
            finally:
                part.close()
            targets.append(target)
    finally:
        doc.close()
    noun = "page" if len(targets) == 1 else "files"
    return ok(targets, f"Split into {len(targets)} {noun}")


def organize(job: Job) -> Result:
    """Reorder, rotate and delete pages. ``pages`` lists what to keep, in order."""
    doc = _open(job)
    try:
        count = doc.page_count
        order = _page_order(job.params["pages"], count)
        rotations = _rotations(job.params["rotate"], count)
        for index, degrees in rotations.items():
            doc[index].set_rotation(degrees)
        doc.select(order)
        target = job.output_path()
        _save(doc, target)
    finally:
        doc.close()
    return ok([target], f"Reorganised {len(order)} pages into {target.name}")


def _parse_ranges(spec: str, count: int) -> list[tuple[str, list[int]]]:
    """Turn ``"1-3,5"`` into labelled 0-based page lists; empty means each page.

    The label is the ``(Split <label>)`` tag on the output name. Pages are
    1-based in the spec and bounds are checked against the document.
    """
    text = (spec or "").strip()
    if not text:
        return [(f"page {n}", [n - 1]) for n in range(1, count + 1)]
    groups: list[tuple[str, list[int]]] = []
    for chunk in text.split(","):
        part = chunk.strip()
        if not part:
            continue
        if "-" in part:
            start_s, _, end_s = part.partition("-")
            start, end = _one_based(start_s, count), _one_based(end_s, count)
            if end < start:
                raise InvalidParams(f"range {part!r} ends before it starts")
            groups.append((part, list(range(start - 1, end))))
        else:
            page = _one_based(part, count)
            groups.append((part, [page - 1]))
    if not groups:
        raise InvalidParams("no pages selected")
    return groups


def _one_based(value: str, count: int) -> int:
    try:
        page = int(value.strip())
    except (TypeError, ValueError):
        raise InvalidParams(f"{value!r} is not a page number") from None
    if page < 1 or page > count:
        raise InvalidParams(f"page {page} is out of range (1 to {count})")
    return page


def _page_order(pages, count: int) -> list[int]:
    if not pages:
        raise InvalidParams("give at least one page to keep")
    order: list[int] = []
    for value in pages:
        order.append(_one_based(str(value), count) - 1)
    return order


def _rotations(raw, count: int) -> dict[int, int]:
    if not raw:
        return {}
    if not isinstance(raw, dict):
        raise InvalidParams("rotations must be a map of page number to degrees")
    result: dict[int, int] = {}
    for key, value in raw.items():
        index = _one_based(str(key), count) - 1
        try:
            degrees = int(value)
        except (TypeError, ValueError):
            raise InvalidParams(f"rotation for page {key} is not a number") from None
        if degrees % 90 != 0:
            raise InvalidParams(f"rotation {degrees} must be a multiple of 90")
        result[index] = degrees % 360
    return result
