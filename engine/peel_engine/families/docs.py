"""Documents, PDF, slides, spreadsheets and subtitles: the paper half of Peel.

Five conversion groups from the spec table, plus the three Ctrl wheel extract
tools. Every entry is data; the routing lives in the runners, so adding a
format later is a tuple edit and never a new code path.

Which backend does what, and why:

* **pandoc** converts every document pair (DOCX, Markdown, TXT, EPUB, RTF, ODT
  and HTML). It is fast, needs no display and keeps structure.
* **LibreOffice headless** writes the two targets pandoc cannot do well: PDF
  (pandoc would need a LaTeX engine) and RTF (pandoc's RTF writer loses
  layout). It also owns everything spreadsheet and slide shaped, because those
  binary formats are its own. A source LibreOffice cannot open (Markdown,
  EPUB) is run through pandoc into ODT in a scratch folder first, so the
  Documents group really is complete rather than complete-minus-two-corners.
* **PyMuPDF** reads PDF: text, XHTML, tables and page rasters.
* **pdf2docx** is the only free thing that turns a PDF back into a real DOCX
  with text frames rather than one picture per page.
* **python-pptx** and **openpyxl** write the PPTX and XLSX that PDF converts
  into, and openpyxl also reads XLSX so a to-CSV run can export every sheet
  instead of the first one, which is all LibreOffice's CSV filter gives.

An entry declares the union of the backends its routes may need, so a machine
missing LibreOffice loses the whole Documents-to-PDF segment rather than
offering it and failing at the click. ``peel doctor`` names what to install.

Output contract: every runner claims its path through ``job.output_path()``
first. Python writers (openpyxl, python-pptx, PyMuPDF) write onto the claim
directly; pandoc is given it as ``--output``; LibreOffice and its fixed naming
work in ``job.scratch_dir()`` and the result is moved onto the claim with
``os.replace``. See the output contract at the top of registry.py.
"""

from __future__ import annotations

import contextlib
import functools
import os
import re
import subprocess
import sys
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Iterable, Sequence

from .. import backends, naming
from ..registry import CONVERT, TOOL, Job, Param, Registry
from ..result import Result, RunnerFailed, failure, ok

RUNNER = "peel_engine.families.docs"

# --- format tables ----------------------------------------------------------

#: The Documents row of the spec: every one of these is both an input and an
#: output, so the group is the full matrix minus the diagonal.
DOC_FORMATS = ("docx", "md", "txt", "epub", "rtf", "odt")
#: Documents converts to each other plus these two.
DOC_EXTRA_TARGETS = ("pdf", "html")
#: Targets LibreOffice writes instead of pandoc.
LO_DOC_TARGETS = ("pdf", "rtf")
#: Document formats LibreOffice opens by itself. Markdown and EPUB are not
#: among them, so they take the pandoc-to-ODT detour.
SOFFICE_DOC_IN = ("docx", "odt", "rtf", "txt", "html")

#: pandoc reader and writer names per extension. pandoc has no plain text
#: reader, and plain text is valid Markdown, so TXT is read as Markdown.
PANDOC_READER = {
    "docx": "docx",
    "epub": "epub",
    "html": "html",
    "md": "markdown",
    "odt": "odt",
    "rtf": "rtf",
    "txt": "markdown",
}
#: Reader for the XHTML PyMuPDF makes from a PDF. It wraps every page in a
#: ``<div id="pageN">``; turning off native divs and spans makes pandoc inline
#: that content instead of passing the tags through as raw HTML.
PDF_MARKUP_READER = "html-native_divs-native_spans"

PANDOC_WRITER = {
    "docx": "docx",
    "epub": "epub",
    "html": "html",
    "md": "gfm",
    "odt": "odt",
    "rtf": "rtf",
    "txt": "plain",
}

SLIDE_FORMATS = ("pptx", "ppt")

#: The Spreadsheets row. XLS is input only (nobody wants a new XLS).
SHEET_FORMATS = ("xlsx", "ods", "csv", "tsv", "xls")
SHEET_TARGETS = ("xlsx", "ods", "csv", "tsv", "pdf")
DELIMITED = {"csv": ",", "tsv": "\t"}

SUBTITLE_FORMATS = ("srt", "vtt")

#: LibreOffice ``--convert-to`` names. The CSV one carries filter options:
#: field separator, text delimiter, charset 76 (UTF-8), first line.
SOFFICE_CONVERT_TO = {
    "docx": "docx",
    "odt": "odt",
    "ods": "ods",
    "pdf": "pdf",
    "rtf": "rtf",
    "xlsx": "xlsx",
}

#: Reading a delimited file needs an explicit filter: LibreOffice would open a
#: .tsv in Writer otherwise, and a .csv with the wrong separator guess.
_CSV_FILTER = "Text - txt - csv (StarCalc)"

#: How long a backend may take before it is treated as hung. LibreOffice
#: bootstraps a profile on its first run, so it gets the longer budget.
PANDOC_TIMEOUT = 300
SOFFICE_TIMEOUT = 600

DPI = Param(
    "dpi",
    "int",
    "Resolution (DPI)",
    default=300,
    minimum=36,
    maximum=1200,
    help="Dots per inch each page is rendered at.",
)
SLIDE_DPI = Param(
    "dpi",
    "int",
    "Resolution (DPI)",
    default=150,
    minimum=36,
    maximum=600,
    help="Dots per inch each page is rendered at.",
)


# --- registration -----------------------------------------------------------


def register(registry: Registry) -> None:
    _register_documents(registry)
    _register_pdf(registry)
    _register_slides(registry)
    _register_spreadsheets(registry)
    _register_subtitles(registry)
    _register_tools(registry)


def _register_documents(registry: Registry) -> None:
    for target in DOC_FORMATS + DOC_EXTRA_TARGETS:
        registry.add(
            id=f"docs.doc_to_{target}",
            family=CONVERT,
            label=target.upper(),
            group="Documents",
            inputs=tuple(e for e in DOC_FORMATS if e != target),
            output_ext=target,
            backends=("pandoc", "soffice") if target in LO_DOC_TARGETS else ("pandoc",),
            runner=f"{RUNNER}:doc_convert",
        )


#: target, runner, backends, params. Order follows the spec row.
PDF_TARGETS: tuple[tuple[str, str, tuple[str, ...], tuple[Param, ...]], ...] = (
    ("docx", "pdf_to_docx", ("pdf2docx",), ()),
    ("pptx", "pdf_to_pptx", ("pymupdf", "python_pptx"), (SLIDE_DPI,)),
    ("xlsx", "pdf_to_xlsx", ("pymupdf", "openpyxl"), ()),
    ("jpg", "pdf_to_image", ("pymupdf",), (DPI,)),
    ("png", "pdf_to_image", ("pymupdf",), (DPI,)),
    ("epub", "pdf_to_markup", ("pymupdf", "pandoc"), ()),
    ("txt", "pdf_to_text", ("pymupdf",), ()),
    ("md", "pdf_to_markup", ("pymupdf", "pandoc"), ()),
)


def _register_pdf(registry: Registry) -> None:
    for target, runner, needed, params in PDF_TARGETS:
        registry.add(
            id=f"docs.pdf_to_{target}",
            family=CONVERT,
            label=target.upper(),
            group="PDF",
            inputs=("pdf",),
            output_ext=target,
            params=params,
            backends=needed,
            runner=f"{RUNNER}:{runner}",
        )


def _register_slides(registry: Registry) -> None:
    registry.add(
        id="docs.slides_to_pdf",
        family=CONVERT,
        label="PDF",
        group="Slides",
        inputs=SLIDE_FORMATS,
        output_ext="pdf",
        backends=("soffice",),
        runner=f"{RUNNER}:slides_to_pdf",
    )
    for target in ("jpg", "png"):
        registry.add(
            id=f"docs.slides_to_{target}",
            family=CONVERT,
            label=target.upper(),
            group="Slides",
            inputs=SLIDE_FORMATS,
            output_ext=target,
            params=(SLIDE_DPI,),
            backends=("soffice", "pymupdf"),
            runner=f"{RUNNER}:slides_to_image",
        )


def _register_spreadsheets(registry: Registry) -> None:
    for target in SHEET_TARGETS:
        if target in DELIMITED:
            needed: tuple[str, ...] = ("soffice", "openpyxl")
        elif target == "xlsx":
            needed = ("soffice", "openpyxl")
        else:
            needed = ("soffice",)
        registry.add(
            id=f"docs.sheet_to_{target}",
            family=CONVERT,
            label=target.upper(),
            group="Spreadsheets",
            inputs=tuple(e for e in SHEET_FORMATS if e != target),
            output_ext=target,
            backends=needed,
            runner=f"{RUNNER}:sheet_convert",
        )


def _register_subtitles(registry: Registry) -> None:
    for target in SUBTITLE_FORMATS + ("txt",):
        registry.add(
            id=f"docs.sub_to_{target}",
            family=CONVERT,
            label=target.upper(),
            group="Subtitles",
            inputs=tuple(e for e in SUBTITLE_FORMATS if e != target),
            output_ext=target,
            runner=f"{RUNNER}:subtitle_convert",
        )


def _register_tools(registry: Registry) -> None:
    registry.add(
        id="docs.extract_text",
        family=TOOL,
        label="Extract Text",
        group="Documents",
        inputs=("docx", "md", "epub", "rtf", "odt", "html"),
        output_ext="txt",
        backends=("pandoc",),
        runner=f"{RUNNER}:extract_text",
    )
    registry.add(
        id="docs.extract_markdown",
        family=TOOL,
        label="Extract Markdown",
        group="Documents",
        inputs=("docx", "txt", "epub", "rtf", "odt", "html"),
        output_ext="md",
        backends=("pandoc",),
        runner=f"{RUNNER}:extract_text",
    )
    registry.add(
        id="docs.extract_subtitle_text",
        family=TOOL,
        label="Extract Subtitle Text",
        group="Subtitles",
        inputs=SUBTITLE_FORMATS,
        output_ext="txt",
        runner=f"{RUNNER}:extract_subtitle_text",
    )


# --- backend plumbing -------------------------------------------------------


def quiet_stdout(runner):
    """Runner wrapper: whatever a backend prints goes to stderr instead.

    ``peel`` promises exactly one JSON object on stdout and the Windows shell
    parses it. PyMuPDF prints advice about optional packages when it analyses
    tables, and pdf2docx prints its progress, both on stdout, and either line
    would turn that JSON into a parse error in the shell. Nothing is lost: the
    shell keeps stderr in its log.
    """

    @functools.wraps(runner)
    def wrapper(job: Job) -> Result:
        with contextlib.redirect_stdout(sys.stderr):
            return runner(job)

    return wrapper


def _abs(path: Path | str) -> str:
    """Absolute string form. Backends are given absolute paths only: they run
    with their own working directory and LibreOffice resolves relative names
    against its own."""
    return os.path.abspath(str(path))


def _ext(path: Path) -> str:
    return naming.normalise_ext(Path(path).suffix)


def _last_line(text: str) -> str:
    """The most useful line of a backend's noise, short enough for a toast."""
    lines = [line.strip() for line in (text or "").splitlines() if line.strip()]
    return (lines[-1] if lines else "no output")[:200]


def _run(args: Sequence[str], *, timeout: int,
         cwd: str | None = None) -> subprocess.CompletedProcess[str]:
    """Run a backend. A failure becomes a message, never a traceback."""
    name = Path(args[0]).stem
    try:
        proc = subprocess.run(
            list(args), capture_output=True, text=True, timeout=timeout, check=False, cwd=cwd
        )
    except subprocess.TimeoutExpired:
        raise RunnerFailed(
            f"{name} took longer than {timeout} seconds and was stopped.",
            code="backend_timeout",
        ) from None
    except OSError as exc:
        raise RunnerFailed(f"{name} could not start: {exc.strerror or exc}") from None
    if proc.returncode != 0:
        raise RunnerFailed(f"{name} failed: {_last_line(proc.stderr or proc.stdout)}")
    return proc


def _pandoc(source: Path, target: Path, *, src_ext: str = "", out_ext: str = "",
            media: Path | None = None, reader: str = "") -> None:
    """Convert with pandoc straight onto ``target``.

    ``media`` is a folder images are extracted into. pandoc is run from the
    target's folder and given the folder's bare name, so the links it writes
    are relative to the document and the pair still works after a move.
    """
    exe = backends.require("pandoc")
    reader = reader or PANDOC_READER.get(src_ext or _ext(source), "")
    writer = PANDOC_WRITER.get(out_ext or _ext(target))
    if not reader or writer is None:
        raise RunnerFailed(
            f"Peel cannot convert {src_ext or _ext(source)} to {out_ext or _ext(target)}.",
            code="unsupported_pair",
        )
    args = [
        str(exe),
        "--from", reader,
        "--to", writer,
        "--output", _abs(target),
        # Where to look for a file the document links to relatively. pandoc
        # searches its working directory by default, which is wherever the
        # shell happened to start peel, so an image beside the source would be
        # silently dropped without this.
        f"--resource-path={os.path.dirname(_abs(source))}{os.pathsep}.",
    ]
    if writer in ("html", "epub"):
        args += ["--standalone", "--metadata", f"title={source.stem}"]
    if writer == "html":
        # One self contained file: images become data URIs rather than a
        # sidecar folder the user has to keep next to the document.
        args += ["--embed-resources"]
    if media is not None:
        args += [f"--extract-media={Path(media).name}"]
    args += [_abs(source)]
    _run(args, timeout=PANDOC_TIMEOUT,
         cwd=os.path.dirname(_abs(target)) if media is not None else None)


#: The LibreOffice user profile for this process, made on first use.
_SOFFICE_PROFILE: Path | None = None


def _soffice_profile() -> Path:
    """A LibreOffice user profile private to this process.

    Private because two LibreOffice instances sharing a profile block on its
    lock file, and Peel runs one process per drop. One per process rather than
    one per conversion because building the profile is the slow part of a
    LibreOffice run. In the system temp folder, not beside the claim, because
    it is tens of megabytes and must not appear next to the user's documents.
    """
    global _SOFFICE_PROFILE
    if _SOFFICE_PROFILE is None:
        import atexit
        import shutil
        import tempfile

        folder = Path(tempfile.mkdtemp(prefix=f"peel-soffice-{os.getpid()}-"))
        atexit.register(shutil.rmtree, folder, ignore_errors=True)
        _SOFFICE_PROFILE = folder
    return _SOFFICE_PROFILE


def _soffice(source: Path, target: Path, convert_to: str, job: Job,
             *, infilter: str | None = None) -> None:
    """Convert with LibreOffice headless and move the result onto the claim.

    LibreOffice names its own output, so it writes into a scratch folder beside
    the claim and the result is moved on with ``os.replace``, which is atomic
    because both are on the same drive.
    """
    exe = backends.require("soffice")
    work = job.scratch_dir(target)
    profile = _soffice_profile() / "profile"
    args = [
        str(exe),
        f"-env:UserInstallation={profile.as_uri()}",
        "--headless",
        "--invisible",
        "--norestore",
        "--nolockcheck",
        "--nodefault",
        "--nologo",
    ]
    if infilter:
        args.append(f"--infilter={infilter}")
    args += ["--convert-to", convert_to, "--outdir", _abs(work), _abs(source)]
    _run(args, timeout=SOFFICE_TIMEOUT)

    wanted = convert_to.split(":", 1)[0]
    produced = _produced_file(Path(work), wanted)
    os.replace(produced, target)


def _produced_file(work: Path, ext: str) -> Path:
    """The single file LibreOffice wrote, ignoring the profile folder."""
    files = [p for p in work.iterdir() if p.is_file()]
    exact = [p for p in files if _ext(p) == naming.normalise_ext(ext)]
    if len(exact) == 1:
        return exact[0]
    if len(files) == 1:
        return files[0]
    raise RunnerFailed(
        "LibreOffice did not write a converted file. The document may be "
        "password protected or damaged.",
        code="backend_empty",
    )


def _csv_filter(ext: str) -> str:
    """The StarCalc text filter with the separator for this extension."""
    separator = 9 if naming.normalise_ext(ext) == "tsv" else 44
    return f"{_CSV_FILTER}:{separator},34,76,1"


def _claim_media(job: Job, target: Path) -> Path:
    """Claim the sidecar folder a Markdown output's images go in.

    Hyphenated, not " (media)" like a tool output: the name ends up inside a
    Markdown link, and a space there is a link some readers do not follow.
    """
    return job.reserve(target.parent / f"{target.stem}-media", directory=True)


def _media_outputs(media: Path | None) -> list[Path]:
    """A media folder pandoc actually filled, so it is reported and kept."""
    if media is None or not media.is_dir():
        return []
    return [media] if any(media.rglob("*")) else []


# --- documents --------------------------------------------------------------


def doc_convert(job: Job) -> Result:
    """One document format to another: pandoc, or LibreOffice for PDF and RTF."""
    source = job.source
    src_ext = _ext(source)
    target = job.output_path()
    out_ext = _ext(target)

    if out_ext in LO_DOC_TARGETS:
        _soffice(_as_soffice_input(job, source, src_ext, target),
                 target, SOFFICE_CONVERT_TO[out_ext], job)
        return ok([target], f"Wrote {target.name}")

    media = _claim_media(job, target) if out_ext == "md" else None
    _pandoc(source, target, src_ext=src_ext, out_ext=out_ext, media=media)
    outputs = [target] + _media_outputs(media)
    return ok(outputs, f"Wrote {target.name}")


def _as_soffice_input(job: Job, source: Path, src_ext: str, near: Path) -> Path:
    """``source`` itself, or a scratch ODT when LibreOffice cannot read it."""
    if src_ext in SOFFICE_DOC_IN:
        return source
    bridge = job.scratch_dir(near) / f"{source.stem or 'document'}.odt"
    _pandoc(source, bridge, src_ext=src_ext, out_ext="odt")
    return bridge


def extract_text(job: Job) -> Result:
    """The Extract Text and Extract Markdown tools: the words, no formatting."""
    target = job.output_path()
    _pandoc(job.source, target, src_ext=_ext(job.source), out_ext=_ext(target))
    return ok([target], f"Wrote {target.name}")


# --- PDF --------------------------------------------------------------------


def _open_pdf(source: Path):
    """Open a PDF, turning an unreadable or encrypted one into a message."""
    import pymupdf

    try:
        doc = pymupdf.open(_abs(source))
    except Exception as exc:
        raise RunnerFailed(f"{source.name} is not a readable PDF: {exc}",
                           code="bad_pdf") from None
    if doc.needs_pass:
        doc.close()
        raise RunnerFailed(f"{source.name} is password protected.", code="pdf_encrypted")
    if doc.page_count == 0:
        doc.close()
        raise RunnerFailed(f"{source.name} has no pages.", code="pdf_empty")
    return doc


@quiet_stdout
def pdf_to_docx(job: Job) -> Result:
    """PDF to a real DOCX with editable text frames, through pdf2docx."""
    from pdf2docx import Converter

    target = job.output_path()
    _open_pdf(job.source).close()  # fail early, with our own message
    converter = Converter(_abs(job.source))
    try:
        converter.convert(_abs(target))
    except Exception as exc:
        raise RunnerFailed(
            f"Could not rebuild {job.source.name} as a Word document: {exc}"
        ) from None
    finally:
        converter.close()
    if target.stat().st_size == 0:
        raise RunnerFailed(f"Could not rebuild {job.source.name} as a Word document.",
                           code="backend_empty")
    return ok([target], f"Wrote {target.name}")


@quiet_stdout
def pdf_to_pptx(job: Job) -> Result:
    """One slide per page, the page rendered edge to edge on it.

    A PDF has no slide structure to recover, so the honest result is a deck
    that looks exactly like the PDF rather than a guess at text boxes.
    """
    from pptx import Presentation
    from pptx.util import Emu

    target = job.output_path()
    dpi = job.params["dpi"]
    doc = _open_pdf(job.source)
    pages = doc.page_count
    try:
        scratch = job.scratch_dir(target)
        deck = Presentation()
        first = doc[0].rect
        deck.slide_width = Emu(int(round(first.width * 12700)))
        deck.slide_height = Emu(int(round(first.height * 12700)))
        blank = deck.slide_layouts[6]
        for number, page in enumerate(doc, start=1):
            image = scratch / f"page-{number}.png"
            page.get_pixmap(dpi=dpi).save(_abs(image))
            slide = deck.slides.add_slide(blank)
            slide.shapes.add_picture(
                _abs(image), 0, 0, width=deck.slide_width, height=deck.slide_height
            )
        deck.save(_abs(target))
    finally:
        doc.close()
    noun = "slide" if pages == 1 else "slides"
    return ok([target], f"Wrote {target.name} ({pages} {noun})")


@quiet_stdout
def pdf_to_xlsx(job: Job) -> Result:
    """One sheet per page: its tables if PyMuPDF finds any, else its lines."""
    from openpyxl import Workbook

    target = job.output_path()
    doc = _open_pdf(job.source)
    tables_found = 0
    try:
        book = Workbook()
        book.remove(book.active)
        for number, page in enumerate(doc, start=1):
            sheet = book.create_sheet(title=f"Page {number}"[:31])
            row = 1
            for table in page.find_tables().tables:
                tables_found += 1
                for cells in table.extract():
                    _write_row(sheet, row, (_as_number(c) for c in cells))
                    row += 1
                row += 1
            if row == 1:
                for line in page.get_text().splitlines():
                    _write_row(sheet, row, [line])
                    row += 1
        book.save(_abs(target))
    finally:
        doc.close()
    noun = "table" if tables_found == 1 else "tables"
    return ok([target], f"Wrote {target.name} ({tables_found} {noun} found)")


#: Significant digits a spreadsheet can hold without rounding, because every
#: number in it is a double.
EXCEL_DIGITS = 15

#: Characters Excel refuses in a sheet name. A file name may legally contain
#: brackets, so this is stricter than the file name cleaner below.
_BAD_IN_TITLE = re.compile(r"[\\/*?:\[\]]")

#: Characters Excel refuses in a cell. Keeping tab and newline would break the
#: row, so every control character goes.
_CONTROL = re.compile(r"[\x00-\x08\x0b-\x1f\x7f]")


def _write_row(sheet: Any, row: int, cells: Iterable[Any]) -> None:
    """Write one row, keeping numbers numeric and text text.

    A cell that starts with ``=`` is forced to text: a word lifted out of a PDF
    table is not a formula, and Excel would show #NAME? if it guessed one.
    """
    for column, value in enumerate(cells, start=1):
        if value is None or value == "":
            continue
        if isinstance(value, str):
            value = _CONTROL.sub("", value).replace("\n", " ").strip()
            if not value:
                continue
        cell = sheet.cell(row=row, column=column)
        cell.value = value
        if isinstance(value, str) and value.startswith("="):
            cell.data_type = "s"


@quiet_stdout
def pdf_to_image(job: Job) -> Result:
    """Every page as a JPG or PNG. One page keeps the plain name; several get
    ``(page N)``, so a one page PDF converts like any other file."""
    target_ext = job.action.ext_for(job.source)
    dpi = job.params["dpi"]
    doc = _open_pdf(job.source)
    outputs: list[Path] = []
    try:
        count = doc.page_count
        width = len(str(count))
        for number, page in enumerate(doc, start=1):
            suffix = "" if count == 1 else f"page {number:0{width}d}"
            path = job.output_path(ext=target_ext, suffix=suffix)
            page.get_pixmap(dpi=dpi).save(_abs(path), jpg_quality=92)
            outputs.append(path)
    finally:
        doc.close()
    noun = "page" if len(outputs) == 1 else "pages"
    return ok(outputs, f"Wrote {len(outputs)} {noun} at {dpi} DPI")


@quiet_stdout
def pdf_to_text(job: Job) -> Result:
    """The text of every page, pages separated by a blank line."""
    target = job.output_path()
    doc = _open_pdf(job.source)
    try:
        pages = [page.get_text() for page in doc]
    finally:
        doc.close()
    body = "\n\n".join(page.strip() for page in pages).strip() + "\n"
    target.write_text(body, encoding="utf-8", newline="\n")
    return ok([target], f"Wrote {target.name}")


@quiet_stdout
def pdf_to_markup(job: Job) -> Result:
    """PDF to Markdown or EPUB, through PyMuPDF's XHTML and pandoc.

    PyMuPDF recovers the headings, lists and images as XHTML; pandoc turns that
    into the target. Going straight from PDF text would lose all of it.
    """
    target = job.output_path()
    out_ext = _ext(target)
    doc = _open_pdf(job.source)
    try:
        body = "".join(page.get_text("xhtml") for page in doc)
    finally:
        doc.close()
    bridge = job.scratch_dir(target) / f"{job.source.stem or 'document'}.html"
    bridge.write_text(
        "<html><head><meta charset=\"utf-8\"><title>"
        f"{_escape(job.source.stem)}</title></head><body>{body}</body></html>",
        encoding="utf-8",
    )
    media = _claim_media(job, target) if out_ext == "md" else None
    _pandoc(bridge, target, src_ext="html", out_ext=out_ext, media=media,
            reader=PDF_MARKUP_READER)
    return ok([target] + _media_outputs(media), f"Wrote {target.name}")


def _escape(text: str) -> str:
    from html import escape

    return escape(text)


# --- slides -----------------------------------------------------------------


def slides_to_pdf(job: Job) -> Result:
    target = job.output_path()
    _soffice(job.source, target, "pdf", job)
    return ok([target], f"Wrote {target.name}")


@quiet_stdout
def slides_to_image(job: Job) -> Result:
    """One image per slide: LibreOffice makes the PDF, PyMuPDF rasters it.

    LibreOffice's own image export only writes the first slide, so the PDF
    detour is what makes "one image per slide" true.
    """
    target_ext = job.action.ext_for(job.source)
    dpi = job.params["dpi"]
    scratch = job.scratch_dir(job.source)
    bridge = scratch / f"{job.source.stem or 'slides'}.pdf"
    _soffice(job.source, bridge, "pdf", job)

    doc = _open_pdf(bridge)
    outputs: list[Path] = []
    try:
        count = doc.page_count
        width = len(str(count))
        for number, page in enumerate(doc, start=1):
            suffix = "" if count == 1 else f"slide {number:0{width}d}"
            path = job.output_path(ext=target_ext, suffix=suffix)
            page.get_pixmap(dpi=dpi).save(_abs(path), jpg_quality=92)
            outputs.append(path)
    finally:
        doc.close()
    noun = "slide" if len(outputs) == 1 else "slides"
    return ok(outputs, f"Wrote {len(outputs)} {noun} at {dpi} DPI")


# --- spreadsheets -----------------------------------------------------------


def sheet_convert(job: Job) -> Result:
    """Spreadsheets to each other and to PDF.

    CSV and TSV are handled in Python (exact control of separator, quoting and
    encoding); XLSX is read with openpyxl so every sheet is exported, not just
    the first one LibreOffice's CSV filter would give; everything else is
    LibreOffice.
    """
    source = job.source
    src_ext = _ext(source)
    out_ext = job.action.ext_for(source) or ""

    if out_ext in DELIMITED:
        return _sheet_to_delimited(job, source, src_ext, out_ext)
    if out_ext == "xlsx" and src_ext in DELIMITED:
        return _delimited_to_xlsx(job, source, src_ext)

    target = job.output_path()
    infilter = _csv_filter(src_ext) if src_ext in DELIMITED else None
    _soffice(source, target, SOFFICE_CONVERT_TO[out_ext], job, infilter=infilter)
    return ok([target], f"Wrote {target.name}")


def _sheet_to_delimited(job: Job, source: Path, src_ext: str, out_ext: str) -> Result:
    """To CSV or TSV. Every sheet is exported; extra sheets get their own file."""
    if src_ext in DELIMITED:
        rows = _read_delimited(source, DELIMITED[src_ext])
        target = job.output_path()
        _write_delimited(target, rows, DELIMITED[out_ext])
        return ok([target], f"Wrote {target.name}")

    book_path = source
    if src_ext != "xlsx":
        bridge = job.scratch_dir(source) / f"{source.stem or 'sheet'}.xlsx"
        _soffice(source, bridge, "xlsx", job)
        book_path = bridge

    sheets = _read_workbook(book_path)
    if not sheets:
        return failure(f"{source.name} has no sheets to export.", code="empty_input")

    outputs: list[Path] = []
    for index, (title, rows) in enumerate(sheets):
        suffix = "" if len(sheets) == 1 else _safe_suffix(title, index)
        path = job.output_path(ext=out_ext, suffix=suffix)
        _write_delimited(path, rows, DELIMITED[out_ext])
        outputs.append(path)
    noun = "sheet" if len(outputs) == 1 else "sheets"
    return ok(outputs, f"Wrote {len(outputs)} {noun}")


def _safe_suffix(title: str, index: int) -> str:
    """A sheet name usable in a file name, or a numbered fallback."""
    cleaned = re.sub(r'[\\/:*?"<>|]', " ", title or "").strip()
    cleaned = re.sub(r"\s+", " ", cleaned)
    return cleaned[:60] if cleaned else f"sheet {index + 1}"


def _sheet_title(text: str) -> str:
    """A worksheet name Excel accepts: no \\ / * ? : [ ], 31 characters, not empty."""
    cleaned = _BAD_IN_TITLE.sub(" ", text or "")
    cleaned = re.sub(r"\s+", " ", cleaned).strip().strip("'")[:31].strip()
    return cleaned or "Sheet1"


def _delimited_to_xlsx(job: Job, source: Path, src_ext: str) -> Result:
    from openpyxl import Workbook

    rows = _read_delimited(source, DELIMITED[src_ext])
    target = job.output_path()
    book = Workbook()
    sheet = book.active
    sheet.title = _sheet_title(source.stem)
    for number, cells in enumerate(rows, start=1):
        _write_row(sheet, number, (_as_number(c) for c in cells))
    book.save(_abs(target))
    return ok([target], f"Wrote {target.name} ({len(rows)} rows)")


def _as_number(text: Any) -> Any:
    """A number when the cell clearly is one, so Excel can sum the column.

    Deliberately conservative. A leading zero means an identifier (a zip code,
    an order number, a part code) and stays text, because turning 00731 into
    731 is data loss no undo gets back. So do an underscore, which Python would
    happily read as a digit group separator, and anything ending in a letter.

    More than 15 significant digits stays text too: a spreadsheet keeps numbers
    as doubles, so a 19 digit order number would come back out of Excel as
    1234567890123460000.
    """
    if not isinstance(text, str):
        return text
    stripped = text.strip()
    if not stripped or stripped[-1].isalpha() or "_" in stripped:
        return text
    digits = stripped.lstrip("+-")
    if len(digits) > 1 and digits[0] == "0" and not digits.startswith("0."):
        return text
    if len(digits.replace(".", "").replace(",", "")) > EXCEL_DIGITS:
        return text
    try:
        return int(stripped)
    except ValueError:
        pass
    try:
        value = float(stripped)
    except ValueError:
        return text
    return value if value == value and abs(value) != float("inf") else text


def _read_delimited(source: Path, delimiter: str) -> list[list[str]]:
    """Rows of a CSV or TSV, quoted fields that span lines included.

    The reader is given a stream rather than a list of lines: a field like
    ``"first<newline>second"`` is one value, and splitting the text into lines
    first would hand the reader two half rows and join them into ``firstsecond``.
    ``newline=""`` keeps the line endings for the reader to interpret, as the
    csv module's own documentation requires.
    """
    import csv
    import io

    stream = io.StringIO(_read_text(source), newline="")
    return list(csv.reader(stream, delimiter=delimiter))


def _write_delimited(target: Path, rows: Iterable[Sequence[Any]], delimiter: str) -> None:
    """Write with a BOM: Excel on Windows reads a plain UTF-8 CSV as cp1252."""
    import csv

    with open(target, "w", encoding="utf-8-sig", newline="") as handle:
        csv.writer(handle, delimiter=delimiter).writerows(
            ["" if cell is None else cell for cell in row] for row in rows
        )


def _read_workbook(path: Path) -> list[tuple[str, list[list[Any]]]]:
    """Every sheet of an XLSX as (title, rows): the values, not the formulas.

    A workbook saved by a script and never opened in Excel has formulas with no
    cached result, and asking for values alone gives back an empty cell. Rather
    than report success over silently dropped data, the formula text is read in
    a second pass and used wherever the value is missing. The second pass only
    happens when the first one found a gap.
    """
    values = _sheet_rows(path, data_only=True)
    if not any(cell == "" for _, rows in values for row in rows for cell in row):
        return values
    formulas = _sheet_rows(path, data_only=False)
    for (_, rows), (_, backup) in zip(values, formulas):
        for index, row in enumerate(rows):
            if index >= len(backup):
                break
            for column, cell in enumerate(row):
                if cell == "" and column < len(backup[index]):
                    row[column] = backup[index][column]
    return values


def _sheet_rows(path: Path, *, data_only: bool) -> list[tuple[str, list[list[Any]]]]:
    from openpyxl import load_workbook

    book = load_workbook(_abs(path), read_only=True, data_only=data_only)
    try:
        sheets = []
        for sheet in book.worksheets:
            rows = [
                ["" if cell is None else cell for cell in row]
                for row in sheet.iter_rows(values_only=True)
            ]
            while rows and not any(str(cell).strip() for cell in rows[-1]):
                rows.pop()
            sheets.append((sheet.title, rows))
        return sheets
    finally:
        book.close()


def _read_text(source: Path) -> str:
    """Decode a text file the way a Windows user's file is actually encoded.

    Byte order marks first, because Notepad and several subtitle tools write
    UTF-16; then UTF-8; then cp1252, which is what a Windows export without a
    mark almost always is. cp1252 decodes any byte, so this never raises.
    """
    raw = source.read_bytes()
    for mark, encoding in ((b"\xff\xfe", "utf-16"), (b"\xfe\xff", "utf-16")):
        if raw.startswith(mark):
            return raw.decode(encoding)
    for encoding in ("utf-8-sig", "cp1252"):
        try:
            return raw.decode(encoding)
        except UnicodeDecodeError:
            continue
    return raw.decode("utf-8", errors="replace")


# --- subtitles --------------------------------------------------------------


@dataclass(frozen=True)
class Cue:
    """One subtitle: when it starts, when it ends, what it says."""

    start: float
    end: float
    text: str


#: ``[HH:]MM:SS[.mmm]``, with either a dot (VTT) or a comma (SRT).
_TIMECODE = re.compile(r"(?:(\d+):)?(\d{1,3}):(\d{2})(?:[.,](\d{1,3}))?\s*$")
#: Blocks a VTT may carry that are not cues.
_VTT_BLOCKS = ("NOTE", "STYLE", "REGION")
#: Inline markup to drop from plain text output.
_TAGS = re.compile(r"</?[a-zA-Z][^>]*>|\{\\[^}]*\}")


def subtitle_convert(job: Job) -> Result:
    """SRT and VTT to each other and to plain text. No backend needed."""
    source = job.source
    target_ext = job.action.ext_for(source) or "txt"
    cues = read_cues(source)
    if not cues:
        return failure(f"{source.name} has no subtitles in it.", code="empty_input")
    target = job.output_path()
    target.write_text(_render(cues, target_ext), encoding="utf-8", newline="\n")
    noun = "subtitle" if len(cues) == 1 else "subtitles"
    return ok([target], f"Wrote {len(cues)} {noun} to {target.name}")


def extract_subtitle_text(job: Job) -> Result:
    """The Extract Subtitle Text tool: the spoken lines, no timings."""
    return subtitle_convert(job)


def _render(cues: Sequence[Cue], ext: str) -> str:
    if ext == "srt":
        return write_srt(cues)
    if ext == "vtt":
        return write_vtt(cues)
    return write_text(cues)


def read_cues(source: Path) -> list[Cue]:
    """Parse SRT or VTT. One parser: the two differ only in the decimal mark.

    Anything that is not a cue (the WEBVTT header, SRT index numbers, VTT cue
    identifiers, NOTE, STYLE and REGION blocks) is skipped, so a file that
    mixes conventions still reads.
    """
    lines = _read_text(source).replace("\r\n", "\n").replace("\r", "\n").split("\n")
    cues: list[Cue] = []
    index = 0
    while index < len(lines):
        line = lines[index].strip()
        index += 1
        if line.split(" ", 1)[0] in _VTT_BLOCKS:
            while index < len(lines) and lines[index].strip():
                index += 1
            continue
        if "-->" not in line:
            continue
        left, _, right = line.partition("-->")
        start = parse_timecode(left)
        end = parse_timecode(right.strip().split(" ")[0] if right.strip() else "")
        body: list[str] = []
        while index < len(lines) and lines[index].strip():
            body.append(lines[index].rstrip())
            index += 1
        if start is None or end is None:
            continue
        cues.append(Cue(start, max(start, end), "\n".join(body).strip()))
    return cues


def parse_timecode(text: str) -> float | None:
    """Seconds from ``[HH:]MM:SS[.mmm]``, or None when it is not a timecode."""
    match = _TIMECODE.match(text.strip())
    if match is None:
        return None
    hours, minutes, seconds, millis = match.groups()
    return (
        int(hours or 0) * 3600
        + int(minutes) * 60
        + int(seconds)
        + int((millis or "0").ljust(3, "0")) / 1000
    )


def format_timecode(seconds: float, decimal: str) -> str:
    total = int(round(max(0.0, seconds) * 1000))
    hours, total = divmod(total, 3_600_000)
    minutes, total = divmod(total, 60_000)
    whole, millis = divmod(total, 1000)
    return f"{hours:02d}:{minutes:02d}:{whole:02d}{decimal}{millis:03d}"


def write_srt(cues: Sequence[Cue]) -> str:
    blocks = [
        f"{number}\n{format_timecode(cue.start, ',')} --> "
        f"{format_timecode(cue.end, ',')}\n{cue.text}\n"
        for number, cue in enumerate(cues, start=1)
    ]
    return "\n".join(blocks)


def write_vtt(cues: Sequence[Cue]) -> str:
    blocks = [
        f"{format_timecode(cue.start, '.')} --> "
        f"{format_timecode(cue.end, '.')}\n{cue.text}\n"
        for cue in cues
    ]
    return "WEBVTT\n\n" + "\n".join(blocks)


def write_text(cues: Sequence[Cue]) -> str:
    """The spoken lines, markup stripped and a repeated line not repeated.

    Rolling captions repeat the previous line in the next cue; keeping every
    one makes the transcript unreadable.
    """
    out: list[str] = []
    for cue in cues:
        text = _TAGS.sub("", cue.text).strip()
        if text and (not out or out[-1] != text):
            out.append(text)
    return "\n".join(out) + ("\n" if out else "")
