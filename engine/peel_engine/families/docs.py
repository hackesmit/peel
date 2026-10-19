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
from ..result import Result, RunnerFailed, failure
from ..result import ok as _plain_ok

import contextvars

#: Links the LibreOffice gate removed during the current run, named in the
#: result message so a removal is never silent (hq-plct.4 verifier note N1).
_GATE_NOTES: contextvars.ContextVar[tuple[str, ...]] = contextvars.ContextVar(
    "peel_docs_gate_notes", default=())


def ok(outputs: Any, message: str = "", **kwargs: Any) -> Result:
    """``result.ok`` plus a note for any links the LibreOffice gate removed."""
    notes = _GATE_NOTES.get()
    if notes:
        _GATE_NOTES.set(())
        message = f"{message}. " if message and not message.endswith(".") else (message + " " if message else "")
        message += "Removed links to files outside the document: " + "; ".join(notes) + "."
    return _plain_ok(outputs, message, **kwargs)

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

#: Document targets a web image can end up inside. Plain text has no
#: images, and a Markdown output keeps a web image as the link it already is.
REMOTE_TARGETS = ("docx", "epub", "odt", "rtf", "pdf", "html")

ALLOW_REMOTE = Param(
    "allow_remote",
    "bool",
    "Download web images",
    default=False,
    help="Peel works offline: images on the web stay links unless this is on.",
)


# --- registration -----------------------------------------------------------


def register(registry: Registry) -> None:
    _register_documents(registry)
    _register_pdf(registry)
    _register_slides(registry)
    _register_spreadsheets(registry)
    _register_subtitles(registry)
    _register_tools(registry)
    _scope_gate_notes(registry)


def _scope_gate_notes(registry: Registry) -> None:
    """Give every docs runner a fresh set of gate notes for its own job.

    The notes are reset when a runner starts and again when it ends, however
    it ends, so a failed job can never lend its notes to a later result.
    """
    prefix = f"{RUNNER}:"
    names = {a.runner[len(prefix):] for a in registry.all()
             if isinstance(getattr(a, "runner", None), str) and a.runner.startswith(prefix)}
    module = globals()
    for name in names:
        func = module.get(name)
        if func is None or getattr(func, "_peel_gate_scoped", False):
            continue

        @functools.wraps(func)
        def scoped(job: Job, _func: Any = func) -> Result:
            token = _GATE_NOTES.set(())
            try:
                return _func(job)
            finally:
                _GATE_NOTES.reset(token)

        scoped._peel_gate_scoped = True  # type: ignore[attr-defined]
        module[name] = scoped


def _register_documents(registry: Registry) -> None:
    for target in DOC_FORMATS + DOC_EXTRA_TARGETS:
        registry.add(
            id=f"docs.doc_to_{target}",
            family=CONVERT,
            label=target.upper(),
            group="Documents",
            inputs=tuple(e for e in DOC_FORMATS if e != target),
            output_ext=target,
            params=(ALLOW_REMOTE,) if target in REMOTE_TARGETS else (),
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


def _run(args: Sequence[str], *, timeout: int, cwd: str | None = None,
         env: dict[str, str] | None = None) -> subprocess.CompletedProcess[str]:
    """Run a backend. A failure becomes a message, never a traceback.

    The backend runs in its own process group (its own session on Linux), so a
    timeout stops the whole tree. LibreOffice's ``soffice`` is only a launcher
    for ``soffice.bin``; killing just the launcher used to leave the real
    converter running, and it went on to write into a scratch folder beside the
    source after Peel had already reported the failure.
    """
    name = Path(args[0]).stem
    popen: dict[str, Any] = {}
    if os.name == "nt":
        popen["creationflags"] = getattr(subprocess, "CREATE_NEW_PROCESS_GROUP", 0)
    else:
        popen["start_new_session"] = True
    try:
        proc = subprocess.Popen(
            list(args), stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True,
            encoding="utf-8", errors="replace", cwd=cwd, env=env, **popen,
        )
    except OSError as exc:
        raise RunnerFailed(f"{name} could not start: {exc.strerror or exc}") from None
    try:
        stdout, stderr = proc.communicate(timeout=timeout)
    except subprocess.TimeoutExpired:
        _kill_tree(proc)
        raise RunnerFailed(
            f"{name} took longer than {timeout} seconds and was stopped.",
            code="backend_timeout",
        ) from None
    done = subprocess.CompletedProcess(list(args), proc.returncode, stdout, stderr)
    if done.returncode != 0:
        raise RunnerFailed(f"{name} failed: {_last_line(stderr or stdout)}")
    return done


def _kill_tree(proc: subprocess.Popen) -> None:
    """Stop a backend and every process it started, then reap it."""
    try:
        if os.name == "nt":
            subprocess.run(["taskkill", "/T", "/F", "/PID", str(proc.pid)],
                           capture_output=True, check=False, timeout=30)
        else:
            import signal

            os.killpg(proc.pid, signal.SIGKILL)
    except (OSError, subprocess.SubprocessError):
        pass
    with contextlib.suppress(Exception):
        proc.kill()
    with contextlib.suppress(Exception):
        proc.communicate(timeout=30)


#: A pandoc Lua filter that keeps a document to its own folder.
#:
#: A Markdown or HTML file can name any path as an image (``/etc/hostname``,
#: ``C:/Users/danie/...``, ``../../secret.png``), and pandoc would copy that
#: file's bytes into the DOCX, EPUB or self contained HTML it writes: a file
#: the user then sends to someone. So every local reference must resolve inside
#: the source file's folder or one of its subfolders; anything else is left out
#: and reported. Peel is local only, so a web image is not fetched either
#: unless the run asks for it (``allow_remote``); it stays in the document as a
#: link. The check here is lexical (``..`` segments, absolute paths, drive
#: letters, percent escapes, ``file:`` URLs) and exists to say what was left
#: out. The confinement itself is structural: pandoc runs with ``--sandbox``
#: on a staged copy and can only get the files ``_stage`` copied (see there),
#: which this filter loads into pandoc's media bag from the manifest.
_RESOURCE_FILTER = r"""
local root_ok = os.getenv("PEEL_RESOURCE_ROOT") ~= nil
local allow_remote = os.getenv("PEEL_ALLOW_REMOTE") == "1"

-- pandoc runs with --sandbox, so it reads no file but its input. The files
-- Peel staged for this document (and any web image it downloaded) are handed
-- over here, through the media bag, by the manifest Peel wrote.
local manifest = os.getenv("PEEL_MANIFEST")
if manifest then
  local list = io.open(manifest, "rb")
  if list then
    for line in list:lines() do
      local key, path, mime = line:match("^([^\t]*)\t([^\t]*)\t([^\t]*)$")
      if key and key ~= "" then
        local f = io.open(path, "rb")
        if f then
          local contents = f:read("a")
          f:close()
          pandoc.mediabag.insert(key, mime ~= "" and mime or nil, contents)
        end
      end
    end
    list:close()
  end
end

local function report(kind, target)
  io.stderr:write("PEEL-RESOURCE\t" .. kind .. "\t" .. (target or "") .. "\n")
end

local function unescape(s)
  return (s:gsub("%%(%x%x)", function(h) return string.char(tonumber(h, 16)) end))
end

local function escapes(p)
  p = p:gsub("\\", "/")
  if p:match("^/") or p:match("^%a:") or p:match("^~") then return true end
  local depth = 0
  for part in p:gmatch("[^/]+") do
    if part == ".." then
      depth = depth - 1
      if depth < 0 then return true end
    elseif part ~= "." then
      depth = depth + 1
    end
  end
  return false
end

-- "ok", "remote" or "outside"
local function classify(src)
  if src == nil then return "ok" end
  local s = src:gsub("^%s+", ""):gsub("%s+$", "")
  if s == "" or s:match("^#") then return "ok" end
  local lower = s:lower()
  if lower:match("^data:") then return "ok" end
  -- Only a real web scheme is remote. //host/share and \\host\share are
  -- Windows network paths: local files to pandoc, so they count as outside.
  if lower:match("^https?:") or lower:match("^ftps?:") then
    return "remote"
  end
  if lower:match("^%a[%w+.-]+:") then return "outside" end
  if not root_ok then return "outside" end
  local bare = s:gsub("[?#].*$", "")
  if escapes(s) or escapes(unescape(s)) or escapes(bare) or escapes(unescape(bare)) then
    return "outside"
  end
  return "ok"
end

function Image(img)
  local verdict = classify(img.src)
  if verdict == "ok" or (verdict == "remote" and allow_remote) then return nil end
  report(verdict, img.src)
  local words = img.caption
  if verdict == "remote" then
    if #words == 0 then words = { pandoc.Str(img.src) } end
    return pandoc.Link(words, img.src)
  end
  if #words > 0 then return pandoc.Span(words) end
  return {}
end

local function clean_html(text)
  local function check(value, keep_remote)
    local verdict = classify(value)
    if verdict == "ok" or (verdict == "remote" and (allow_remote or keep_remote)) then
      return nil
    end
    report(verdict, value)
    return ""
  end
  text = text:gsub("<([%w:-]+)(.-)>", function(tag, attrs)
    local is_anchor = tag:lower() == "a"
    local function attr(lead, name, q, value)
      local n = name:lower()
      if n == "src" or n == "href" or n == "poster" or n == "data" or n == "background"
        or n == "xlink:href" or n == "srcset" or n == "action" then
        if is_anchor and n == "href" then return nil end
        local bad = false
        if n == "srcset" then
          for item in value:gmatch("[^,]+") do
            if check(item:match("^%s*(%S*)")) ~= nil then bad = true end
          end
        else
          bad = check(value) ~= nil
        end
        if bad then return lead .. name .. "=" .. q .. q end
      end
      return nil
    end
    attrs = attrs:gsub('(%s)([%w:-]+)%s*=%s*(")([^"]*)"', attr)
    attrs = attrs:gsub("(%s)([%w:-]+)%s*=%s*(')([^']*)'", attr)
    attrs = attrs:gsub("(%s)([%w:-]+)%s*=%s*()([^%s\"'>][^%s>]*)", function(lead, name, _, value)
      return attr(lead, name, "", value)
    end)
    return "<" .. tag .. attrs .. ">"
  end)
  text = text:gsub("url%(%s*(['\"]?)(.-)%1%s*%)", function(q, value)
    if check(value) ~= nil then return "url()" end
    return nil
  end)
  text = text:gsub("@import%s+(['\"])(.-)%1", function(q, value)
    if check(value) ~= nil then return "@import " .. q .. q end
    return nil
  end)
  return text
end

local function is_html(format)
  local f = format:lower()
  return f == "html" or f == "html4" or f == "html5" or f == "epub"
end

function RawInline(el)
  if is_html(el.format) then el.text = clean_html(el.text); return el end
end

function RawBlock(el)
  if is_html(el.format) then el.text = clean_html(el.text); return el end
end

local RESOURCE_META = { "css", "stylesheet", "cover-image", "epub-cover-image",
                        "header-includes", "include-before", "include-after" }

function Meta(meta)
  for _, key in ipairs(RESOURCE_META) do
    if meta[key] ~= nil then
      report("meta", key)
      meta[key] = nil
    end
  end
  return meta
end
"""

#: Where the filter above is written, once per process.
_FILTER_PATH: Path | None = None


def _resource_filter() -> Path:
    global _FILTER_PATH
    if _FILTER_PATH is None or not _FILTER_PATH.is_file():
        import atexit
        import shutil
        import tempfile

        folder = Path(tempfile.mkdtemp(prefix=f"peel-filter-{os.getpid()}-"))
        atexit.register(shutil.rmtree, folder, ignore_errors=True)
        path = folder / "peel-resources.lua"
        path.write_text(_RESOURCE_FILTER, encoding="utf-8")
        _FILTER_PATH = path
    return _FILTER_PATH


@dataclass
class Dropped:
    """What the resource filter kept out of a document, for the result message."""

    outside: list[str]
    remote: list[str]
    meta: list[str]

    def __bool__(self) -> bool:
        return bool(self.outside or self.remote or self.meta)

    def extend(self, other: "Dropped") -> None:
        self.outside += other.outside
        self.remote += other.remote
        self.meta += other.meta

    def note(self) -> str:
        parts = []
        if self.outside:
            names = ", ".join(sorted({_short(t) for t in self.outside})[:3])
            parts.append(
                f"Left out {len(self.outside)} linked file(s) outside the document's "
                f"folder ({names})."
            )
        if self.remote:
            parts.append(f"Kept {len(self.remote)} web image(s) as links; Peel does not "
                         "download them.")
        if self.meta:
            parts.append(f"Ignored the document's {', '.join(sorted(set(self.meta)))} "
                         "setting(s), which can pull in outside files.")
        return " ".join(parts)


def _short(target: str) -> str:
    name = re.split(r"[\\/]", target.rstrip("\\/"))[-1]
    return (name or target)[:60]


def _with_note(message: str, dropped: Dropped | None) -> str:
    return f"{message}. {dropped.note()}" if dropped else message


def _parse_dropped(stderr: str) -> Dropped:
    dropped = Dropped([], [], [])
    for line in (stderr or "").splitlines():
        if not line.startswith("PEEL-RESOURCE\t"):
            continue
        _, kind, target = (line.split("\t", 2) + [""])[:3]
        {"remote": dropped.remote, "meta": dropped.meta}.get(kind, dropped.outside).append(target)
    return dropped


#: Source formats that link to other files by name, so are scanned for them.
#: DOCX, ODT, EPUB and RTF carry their pictures inside the file.
_LINKING_SOURCES = ("md", "txt", "html", "htm")
#: Limits on what one document may pull in.
_STAGE_MAX_FILES = 500
_STAGE_MAX_BYTES = 200 * 2**20
_REMOTE_MAX_FILES = 50
_REMOTE_MAX_BYTES = 20 * 2**20
_REMOTE_TIMEOUT = 20

#: Pieces of a document that could be a file name: quoted strings, the inside
#: of brackets (Markdown links, CSS url()), and runs between separators. This
#: is deliberately loose. It only decides what is copied into the staged
#: folder; a name it misses is simply not available to pandoc.
_QUOTED = re.compile(r'"([^"\r\n]*)"|\'([^\'\r\n]*)\'|\(([^()\r\n]*)\)|<([^<>\r\n]*)>')
_SEPARATORS = re.compile(r'[\s"\'()<>\[\]=,;{}|*`]+')


def _references(text: str) -> set[str]:
    import html
    from urllib.parse import unquote

    found: set[str] = set()
    for body in (text, html.unescape(text)):
        for match in _QUOTED.finditer(body):
            found.add(next(g for g in match.groups() if g is not None))
        found.update(_SEPARATORS.split(body))
    out: set[str] = set()
    for ref in found:
        ref = ref.strip()
        if not ref or len(ref) > 1024 or "\t" in ref or "\x00" in ref:
            continue
        for variant in (ref, unquote(ref)):
            out.add(variant)
            out.add(re.split(r"[?#]", variant, maxsplit=1)[0])
    out.discard("")
    return out


def _inside(path: str, root: str) -> bool:
    try:
        return os.path.commonpath([os.path.normcase(path), os.path.normcase(root)]) == \
            os.path.normcase(root)
    except ValueError:  # another drive
        return False


def _local_file(root: str, base: str, ref: str) -> str | None:
    """The real path ``ref`` names, if it is a file inside ``root``.

    Absolute paths, drive letters, network paths and every URL scheme are
    refused outright; a relative path is resolved with symlinks followed, so a
    link inside the folder that points out of it is refused too.
    """
    if re.match(r"^[a-z][\w+.-]*:", ref, re.I) or ref.startswith(("/", "\\", "~")):
        return None
    path = os.path.realpath(os.path.join(base, ref.replace("\\", "/")))
    if not _inside(path, root) or not os.path.isfile(path):
        return None
    return path


def _download(url: str, folder: Path, index: int) -> tuple[Path, str] | None:
    """A web image for a run that allows it, or None when it cannot be had."""
    import urllib.request

    try:
        request = urllib.request.Request(url, headers={"User-Agent": "Peel"})
        with urllib.request.urlopen(request, timeout=_REMOTE_TIMEOUT) as response:  # noqa: S310
            data = response.read(_REMOTE_MAX_BYTES + 1)
            mime = response.headers.get_content_type() or ""
    except Exception:  # noqa: BLE001 - an unreachable image stays a link
        return None
    if len(data) > _REMOTE_MAX_BYTES:
        return None
    folder.mkdir(exist_ok=True)
    path = folder / f"web-{index}"
    path.write_bytes(data)
    return path, mime


def _stage(job: Job, source: Path, near: Path, *, src_ext: str,
           allow_remote: bool) -> tuple[Path, Path, list[tuple[str, Path, str]]]:
    """Copy ``source`` and the files it names into a fresh scratch folder.

    Returns (staged folder, staged source, manifest entries as (name in the
    document, staged copy, media type)). Only files that resolve
    (real path, symlinks followed) inside the source's own folder are copied;
    a stylesheet among them is scanned in turn, relative to itself. pandoc then
    runs with ``--sandbox`` on the staged copy, and the staged files reach it
    only through the manifest, so whatever a document names (an absolute path,
    an entity encoded ``src``, a ``url()`` in a stylesheet) it can only get a
    file that was copied here. Nothing in the document is rewritten.

    The copies get plain ASCII names (``res/0.png``): pandoc's Lua opens them
    with the C library, which on Windows reads a path in the ANSI code page,
    so an accented folder or file name would not open. The name the document
    uses is kept as the media bag key instead.
    """
    import mimetypes
    import shutil

    scratch = job.scratch_dir(near)
    stage = scratch / "stage"
    stage.mkdir()
    staged_source = stage / (source.name or "document")
    shutil.copyfile(_abs(source), staged_source)
    root = os.path.realpath(os.path.dirname(_abs(source)))
    entries: list[tuple[str, Path, str]] = []

    if src_ext in _LINKING_SOURCES:
        copied: dict[str, Path] = {}
        total = 0
        queue: list[tuple[str, str]] = [(root, _read_text(source))]
        remote: list[str] = []
        while queue:
            base, text = queue.pop(0)
            for ref in sorted(_references(text)):
                if ref.lower().startswith(("http://", "https://")):
                    if allow_remote and base == root and ref not in remote:
                        remote.append(ref)
                    continue
                real = _local_file(root, base, ref)
                if real is None:
                    continue
                relative = os.path.relpath(real, root).replace(os.sep, "/")
                if real not in copied:
                    size = os.path.getsize(real)
                    if len(copied) >= _STAGE_MAX_FILES or total + size > _STAGE_MAX_BYTES:
                        continue
                    total += size
                    suffix = re.sub(r"[^A-Za-z0-9.]", "", Path(real).suffix)[:10]
                    copy = stage / "res" / f"{len(copied)}{suffix}"
                    copy.parent.mkdir(exist_ok=True)
                    shutil.copyfile(real, copy)
                    copied[real] = copy
                    mime = mimetypes.guess_type(real)[0] or ""
                    entries.append((relative, copy, mime))
                    if real.lower().endswith(".css"):
                        queue.append((os.path.dirname(real), _read_text(Path(real))))
                if base == root:
                    entries.append((ref, copied[real], mimetypes.guess_type(real)[0] or ""))
        for index, url in enumerate(remote[:_REMOTE_MAX_FILES]):
            got = _download(url, scratch / "web", index)
            if got is not None:
                entries.append((url, got[0], got[1]))

    return stage, staged_source, entries


def _write_manifest(stage: Path, entries: list[tuple[str, Path, str]], cwd: str) -> str:
    """Write the manifest beside ``stage``; its path relative to ``cwd``.

    Paths in it are relative to pandoc's working folder and so pure ASCII (see
    ``_stage``). Written as bytes, so Windows does not add CR to line ends.
    """
    manifest = stage.parent / "manifest.tsv"
    lines = "".join(
        f"{key}\t{os.path.relpath(_abs(path), cwd).replace(os.sep, '/')}\t{mime}\n"
        for key, path, mime in entries
    )
    manifest.write_bytes(lines.encode("utf-8"))
    return os.path.relpath(_abs(manifest), cwd).replace(os.sep, "/")


def _pandoc(source: Path, target: Path, *, job: Job, src_ext: str = "", out_ext: str = "",
            media: Path | None = None, reader: str = "",
            allow_remote: bool = False) -> Dropped:
    """Convert with pandoc straight onto ``target``.

    ``media`` is a folder images are extracted into. pandoc is then run from
    the target's folder and given the folder's bare name, so the links it
    writes are relative to the document and the pair still works after a move.

    pandoc never sees the user's folder: it converts a staged copy (see
    ``_stage``) with ``--sandbox``. Every run also goes through the resource
    filter above, which reports what the document named outside its folder or
    on the web; that comes back so the result message can say so.
    """
    exe = backends.require("pandoc")
    reader = reader or PANDOC_READER.get(src_ext or _ext(source), "")
    writer = PANDOC_WRITER.get(out_ext or _ext(target))
    if not reader or writer is None:
        raise RunnerFailed(
            f"Peel cannot convert {src_ext or _ext(source)} to {out_ext or _ext(target)}.",
            code="unsupported_pair",
        )
    stage, staged, entries = _stage(job, source, target, src_ext=src_ext or _ext(source),
                                    allow_remote=allow_remote)
    # With a media folder pandoc runs from the target's folder (the scratch
    # folder is a child of it, so the relative paths stay ASCII); otherwise
    # from the staged folder.
    cwd = os.path.dirname(_abs(target)) if media is not None else _abs(stage)
    manifest = _write_manifest(stage, entries, cwd)
    args = [
        str(exe),
        "--sandbox",
        "--from", reader,
        "--to", writer,
        "--output", _abs(target),
        f"--resource-path={_abs(stage)}",
        f"--lua-filter={_abs(_resource_filter())}",
    ]
    if writer in ("html", "epub"):
        args += ["--standalone", "--metadata", f"title={source.stem}"]
    if writer == "html":
        # One self contained file: images become data URIs rather than a
        # sidecar folder the user has to keep next to the document.
        args += ["--embed-resources"]
    if media is not None:
        args += [f"--extract-media={Path(media).name}"]
    args += [_abs(staged)]
    env = dict(os.environ)
    env["PEEL_RESOURCE_ROOT"] = _abs(stage)
    env["PEEL_ALLOW_REMOTE"] = "1" if allow_remote else "0"
    env["PEEL_MANIFEST"] = manifest
    proc = _run(args, timeout=PANDOC_TIMEOUT, env=env, cwd=cwd)
    return _parse_dropped(proc.stderr)


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
    because both are on the same drive. Every source passes ``_soffice_gate``
    first, so no route reaches LibreOffice with a link out of its package.
    """
    exe = backends.require("soffice")
    source, default_filter = _soffice_gate(job, source, target)
    infilter = infilter or default_filter
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


def _csv_filter(delimiter: str, quotechar: str = '"') -> str:
    """The StarCalc text filter for a detected dialect.

    Tokens: separator, text delimiter, charset 76 (UTF-8), first line, cell
    formats, language, quoted field as text, detect special numbers (dates, as
    LibreOffice does by default), three export-only tokens, and last
    "evaluate formulas" off: a CSV cell such as ``=WEBSERVICE(...)`` would
    otherwise run while the file is converted.
    """
    return (f"{_CSV_FILTER}:{ord(delimiter)},{ord(quotechar)},76,1,,,"
            "false,true,false,false,false,-1,false")


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
    allow_remote = out_ext in REMOTE_TARGETS and bool(job.params.get("allow_remote", False))

    if out_ext in LO_DOC_TARGETS:
        given, dropped = _as_soffice_input(job, source, src_ext, target, allow_remote)
        _soffice(given, target, SOFFICE_CONVERT_TO[out_ext], job)
        return ok([target], _with_note(f"Wrote {target.name}", dropped))

    media = _claim_media(job, target) if out_ext == "md" else None
    dropped = _pandoc(source, target, job=job, src_ext=src_ext, out_ext=out_ext,
                      media=media, allow_remote=allow_remote)
    outputs = [target] + _media_outputs(media)
    return ok(outputs, _with_note(f"Wrote {target.name}", dropped))


def _as_soffice_input(job: Job, source: Path, src_ext: str, near: Path,
                      allow_remote: bool = False) -> tuple[Path, Dropped | None]:
    """``source`` itself, or a scratch ODT made by pandoc.

    The pandoc detour is taken when LibreOffice cannot read the format, and
    also when the source links to files LibreOffice would load by itself: an
    HTML page always can, an RTF can hide a linked picture or text in ways no
    scan reads reliably, and a DOCX or ODT can carry a linked (not embedded)
    picture, object or field. Going through pandoc puts those links through
    the same folder check as every other pandoc run. Whatever reaches
    LibreOffice afterwards still passes the package gate in ``_soffice``.
    """
    if src_ext in SOFFICE_DOC_IN and not _links_outside_files(source, src_ext):
        return source, None
    bridge = job.scratch_dir(near) / f"{source.stem or 'document'}.odt"
    dropped = _pandoc(source, bridge, job=job, src_ext=src_ext, out_ext="odt",
                      allow_remote=allow_remote)
    return bridge, dropped


def _links_outside_files(source: Path, src_ext: str) -> bool:
    """Whether LibreOffice would load anything this document only links to.

    True for HTML and RTF always (neither can be checked structurally), and
    for a DOCX or ODT with any link out of the package, removable or not. A
    package that cannot be read at all raises ``RunnerFailed``.
    """
    if src_ext in ("html", "htm", "rtf"):
        return True
    if src_ext not in _OOXML + _ODF:
        return False
    found = _inspect_package(source)
    return bool(found.blockers or found.rewrites)


# --- LibreOffice confinement --------------------------------------------------
#
# LibreOffice loads whatever a document links to: a picture or OLE object
# linked by a DOCX/PPTX/XLSX relationship, an ODF xlink:href, a DOCX
# INCLUDEPICTURE field, an external workbook reference in a formula, a DDE
# link. Nothing in its command line turns that off, so every file handed to it
# passes ``_soffice_gate`` first. The gate parses each XML part of the package
# with a real parser (no DTD, so no entity tricks; character references are
# decoded by the parser, so ``&#46;./`` is ``../``), removes the links it can
# remove into a scratch copy, and refuses everything it cannot read or clean.

_OOXML = ("docx", "pptx", "xlsx")
_ODF = ("odt", "ods", "odp")

#: Explicit import filters, so LibreOffice never sniffs a file into a format
#: the gate did not check (a ``.txt`` that is really an RTF, say).
_SOFFICE_INFILTER = {
    "docx": "MS Word 2007 XML",
    "pptx": "Impress MS PowerPoint 2007 XML",
    "xlsx": "Calc MS Excel 2007 XML",
    "odt": "writer8",
    "ods": "calc8",
    "odp": "impress8",
}
_TEXT_INFILTER = "Text (encoded):UTF8,LF,,"

#: Binary or RTF formats no parser here reads, and what to save them as.
_RESAVE_AS = {"rtf": "DOCX", "doc": "DOCX", "ppt": "PPTX", "pps": "PPTX", "xls": "XLSX"}

_REL_NS = "http://schemas.openxmlformats.org/package/2006/relationships"
#: Elements whose link is a hyperlink: shown as a link, never loaded.
_OOXML_HYPERLINKS = {"hyperlink", "hlinkClick", "hlinkHover", "hlinkMouseOver"}
_ODF_HYPERLINKS = {"a"}  # text:a and draw:a
#: Attributes that carry a URL LibreOffice may load.
_URL_ATTRS = {"href", "src"}
#: Live data links: nothing to remove safely, so the file is refused.
_LIVE_LINK_ELEMENTS = {
    "ddeLink", "dde-source", "dde-link", "dde-connection-decl", "dde-connection",
    "webPr", "textPr", "dbPr", "olapPr", "altChunk",
}
#: Word field codes that pull another file in.
_LINK_FIELDS = re.compile(r"\b(?:INCLUDEPICTURE|INCLUDETEXT|INCLUDE|LINK|DDE|DDEAUTO|IMPORT)\b", re.I)
#: Formula text that reaches another file or the network.
_RISKY_FORMULA = re.compile(
    r"file:|://|\\\\|(?<![A-Za-z])[A-Za-z]:[\\/]|'[^']*'#|"
    r"\b(?:WEBSERVICE|INDIRECT|DDE|FILTERXML)\s*\(",
    re.I,
)
_FORMULA_ELEMENTS = {"f", "definedName", "formula", "formula1", "formula2"}
_FORMULA_ATTRS = ("formula", "expression", "condition", "range-address", "cell-address")
_CSS_URL = re.compile(r"url\(\s*['\"]?([^'\")]*)", re.I)

_PACKAGE_MAX_PARTS = 10000
_PACKAGE_MAX_BYTES = 512 * 2**20
_SCHEME = re.compile(r"^[a-z][a-z0-9+.\-]*:", re.I)
_UNSEEN = re.compile(r"[\x00-\x20\x7f]+")


class _Unsafe(Exception):
    """A package part the gate cannot read, so it refuses the file."""


@dataclass
class _Findings:
    rewrites: dict[str, bytes]
    blockers: list[str]
    odf: bool


def _local(name: str) -> str:
    return name.rsplit("}", 1)[-1]


def _ns(name: str) -> str:
    return name[1:].split("}", 1)[0] if name.startswith("{") else ""


def _outside_ref(value: str, base: str = "", *, rooted: bool = False) -> bool:
    """Whether a link resolves outside the package.

    Outside is an absolute path, a drive letter, a UNC path, any URL scheme
    (file:, http:, smb:, vnd.sun.star.*), or ``..`` climbing above the package
    root. Percent escapes are decoded too, and control characters and spaces
    removed, as URL parsers do. ``rooted`` allows ``/part/name``, which in an
    OOXML relationship is a part of the package itself.
    """
    from urllib.parse import unquote
    import posixpath

    seen = value
    for _ in range(4):
        text = _UNSEEN.sub("", seen).replace("\\", "/")
        if text and not text.startswith("#"):
            if text.startswith("//") or _SCHEME.match(text):
                return True
            if text.startswith("/"):
                if not rooted:
                    return True
                text = text.lstrip("/")
                start = ""
            else:
                start = base
            joined = posixpath.normpath(posixpath.join(start, text))
            if joined == ".." or joined.startswith("../") or joined.startswith("/"):
                return True
        decoded = unquote(seen)
        if decoded == seen:
            return False
        seen = decoded
    return True


_XML_PART_SUFFIXES = (".xml", ".rels", ".xhtml", ".htm", ".html", ".svg", ".vml", ".opf", ".ncx")


def _xml_kind(data: bytes) -> str:
    """"xml", "gzip", "zip", "rtf", or "" (a binary part such as a PNG)."""
    head = data[:8]
    if head.startswith((b"\x00\x00\xfe\xff", b"\xff\xfe\x00\x00", b"\x00\x00\x00<", b"<\x00\x00\x00")):
        raise _Unsafe("is stored in UTF-32")
    if head.startswith((b"\xff\xfe", b"\xfe\xff", b"\xef\xbb\xbf", b"\x00<", b"<\x00")):
        return "xml"
    # Leading whitespace of any length is legal before the root element, and
    # LibreOffice reads such a part as XML, so never look at a fixed prefix.
    stripped = data.lstrip(b" \t\r\n")
    if stripped.startswith(b"<"):
        return "xml"
    if head.startswith(b"\x1f\x8b"):
        return "gzip"
    if head.startswith(b"PK\x03\x04"):
        return "zip"
    if stripped.startswith(b"{\\rt"):
        return "rtf"
    return ""


def _parse_xml(data: bytes):
    """Parse one part. No DTD, no entity declarations, no stylesheet PI."""
    import xml.etree.ElementTree as ET
    from xml.parsers import expat

    def refuse(*_args: Any) -> None:
        raise _Unsafe("declares a DTD or entities")

    def instruction(target: str, _data: str) -> None:
        if target.lower() == "xml-stylesheet":
            raise _Unsafe("links a stylesheet")

    probe = expat.ParserCreate()
    probe.StartDoctypeDeclHandler = refuse
    probe.EntityDeclHandler = refuse
    probe.UnparsedEntityDeclHandler = refuse
    probe.ProcessingInstructionHandler = instruction
    try:
        probe.Parse(data, True)
        return ET.fromstring(data)
    except (expat.ExpatError, ET.ParseError, ValueError) as exc:
        raise _Unsafe(f"is not well-formed XML ({exc})") from None


def _namespaces(data: bytes) -> list[tuple[str, str]]:
    import io
    import xml.etree.ElementTree as ET

    return [ns for _, ns in ET.iterparse(io.BytesIO(data), events=("start-ns",))]


def _serialise(root: Any, data: bytes) -> bytes:
    """Write a cleaned part back, keeping the prefixes it declared."""
    import xml.etree.ElementTree as ET

    for prefix, uri in _namespaces(data):
        if prefix and prefix != "xml":
            with contextlib.suppress(ValueError):
                ET.register_namespace(prefix, uri)
    return ET.tostring(root, encoding="UTF-8", xml_declaration=True)


def _inspect_package(source: Path, *, depth: int = 0) -> _Findings:
    """Every link out of an OOXML or ODF package, found structurally.

    Raises ``RunnerFailed`` when the package cannot be read (damaged, password
    protected, a part that is not well-formed or declares a DTD).
    """
    import zipfile

    try:
        with zipfile.ZipFile(source) as package:
            return _inspect_zip(package, depth)
    except _Unsafe as exc:
        raise RunnerFailed(
            f"{source.name} was not converted: a part of it {exc}, so Peel cannot "
            "check what LibreOffice would load from it.",
            code="unsafe_input",
        ) from None
    except (OSError, zipfile.BadZipFile, zipfile.LargeZipFile, RuntimeError,
            NotImplementedError, EOFError) as exc:
        raise RunnerFailed(
            f"{source.name} could not be read as a document package. It may be "
            "password protected or damaged.",
            code="unsafe_input",
        ) from exc


def _inspect_zip(package: Any, depth: int) -> _Findings:
    import gzip
    import io
    import zipfile

    infos = package.infolist()
    if len(infos) > _PACKAGE_MAX_PARTS or sum(i.file_size for i in infos) > _PACKAGE_MAX_BYTES:
        raise _Unsafe("is too large to check")
    names = [i.filename.replace("\\", "/").casefold() for i in infos]
    if len(set(names)) != len(names):
        raise _Unsafe("is stored twice under one name")
    if any(i.flag_bits & 0x1 for i in infos):
        raise _Unsafe("is encrypted")

    odf = any(i.filename == "mimetype" for i in infos)
    blockers: list[str] = []
    parsed: dict[str, tuple[Any, bytes]] = {}
    rels: dict[str, tuple[Any, bytes]] = {}
    for info in infos:
        if info.is_dir():
            continue
        data = package.read(info)
        kind = _xml_kind(data)
        if kind == "gzip":
            try:
                data = gzip.GzipFile(fileobj=io.BytesIO(data)).read(_PACKAGE_MAX_BYTES + 1)
            except (OSError, EOFError):
                raise _Unsafe("is a damaged compressed part") from None
            if len(data) > _PACKAGE_MAX_BYTES:
                raise _Unsafe("is too large to check")
            if _xml_kind(data) == "xml":
                inner: list[str] = []
                if _part_links(info.filename, _parse_xml(data), odf, inner)[0] or inner:
                    blockers.append(f"a compressed picture ({info.filename}) links to another file")
            continue
        if kind == "zip":
            if depth >= 2:
                raise _Unsafe("nests packages too deeply")
            try:
                inner = _inspect_zip(zipfile.ZipFile(io.BytesIO(data)), depth + 1)
            except zipfile.BadZipFile:
                continue  # an embedded binary that only looks like a zip header
            if inner.blockers or inner.rewrites:
                blockers.append(f"an embedded document ({info.filename}) links to other files")
            continue
        if kind == "rtf":
            blockers.append(f"an embedded RTF part ({info.filename}) cannot be checked")
            continue
        if kind != "xml":
            # Fail closed: a part named like XML that is not recognised as XML
            # is not trusted to be harmless binary data.
            if info.filename.lower().endswith(_XML_PART_SUFFIXES):
                raise _Unsafe(f"has a part named like XML that is not XML ({info.filename})")
            continue
        root = _parse_xml(data)
        if _local(root.tag) == "encryption-data" or any(
                _local(e.tag) == "encryption-data" for e in root.iter()):
            raise _Unsafe("is encrypted")
        if _local(root.tag) == "Relationships":
            rels[info.filename] = (root, data)
        else:
            parsed[info.filename] = (root, data)

    rewrites: dict[str, bytes] = {}
    used_ids: dict[str, set[str]] = {}
    for name, (root, data) in parsed.items():
        changed, found, ids = _part_links(name, root, odf, blockers)
        used_ids[name] = ids
        if changed:
            if odf or _local(root.tag) == "svg":
                rewrites[name] = _serialise(root, data)
            else:
                blockers.append(f"{name} links to {found[0]!r}")
    for name, (root, _data) in rels.items():
        cleaned = _clean_rels(name, root, used_ids)
        if cleaned is not None:
            rewrites[name] = cleaned
    return _Findings(rewrites=rewrites, blockers=blockers, odf=odf)


def _part_links(name: str, root: Any, odf: bool, blockers: Any) -> tuple[bool, list[str], set[str]]:
    """Remove outside links from one parsed part, in place.

    Returns whether anything was removed, what was found, and every attribute
    value used by a non-hyperlink element (to tell a relationship used as a
    hyperlink from one used to load a picture). Live links and risky formulas
    go to ``blockers``.
    """
    hyperlinks = _ODF_HYPERLINKS if odf else _OOXML_HYPERLINKS
    found: list[str] = []
    ids: set[str] = set()
    instructions: list[str] = []
    for element in root.iter():
        if not isinstance(element.tag, str):
            continue
        local = _local(element.tag)
        if local in _LIVE_LINK_ELEMENTS:
            blockers.append(f"{name} has a live link ({local})")
        if local == "instrText" and element.text:
            instructions.append(element.text)
        if local in _FORMULA_ELEMENTS and element.text and _RISKY_FORMULA.search(element.text):
            blockers.append(f"{name} has a formula that reads another file")
        if local == "style" and element.text and _css_outside(element.text):
            found.append("a stylesheet link")
            element.text = ""
        for attr, value in list(element.attrib.items()):
            attr_local = _local(attr)
            if local not in hyperlinks:
                ids.add(value)
            if attr_local == "instr":
                instructions.append(value)
            if attr_local.endswith(_FORMULA_ATTRS) and _RISKY_FORMULA.search(value):
                blockers.append(f"{name} has a formula that reads another file")
            if attr_local == "style" and _css_outside(value):
                found.append("a style link")
                del element.attrib[attr]
                continue
            if attr_local in _URL_ATTRS and local not in hyperlinks and _outside_ref(value):
                found.append(value)
                del element.attrib[attr]
    if _LINK_FIELDS.search("".join(instructions)):
        blockers.append(f"{name} has a field that includes another file")
    return bool(found), found, ids


def _css_outside(text: str) -> bool:
    if "@import" in text.lower():
        return True
    return any(not url.strip().startswith("#") and _outside_ref(url)
               for url in _CSS_URL.findall(text))


def _clean_rels(name: str, root: Any, used_ids: dict[str, set[str]]) -> bytes | None:
    """A relationships part without the links that leave the package.

    None when nothing needs removing. A hyperlink relationship stays, unless
    something other than a hyperlink uses its id (a picture's ``r:link``).
    """
    import posixpath
    from xml.sax.saxutils import quoteattr

    folder = posixpath.dirname(posixpath.dirname(name))
    owner = posixpath.join(folder, posixpath.basename(name)[: -len(".rels")]) if name.endswith(".rels") else ""
    users = used_ids.get(owner, set())
    keep: list[dict[str, str]] = []
    removed = False
    for rel in root:
        if not isinstance(rel.tag, str):
            continue
        attrs = {_local(k): v for k, v in rel.attrib.items() if not _ns(k)}
        if _local(rel.tag) != "Relationship" or any(_ns(k) for k in rel.attrib):
            removed = True
            continue
        target = attrs.get("Target", "")
        external = attrs.get("TargetMode", "").strip().casefold() == "external"
        hyperlink = attrs.get("Type", "").rstrip("/").endswith("/hyperlink")
        if external:
            outside = not (hyperlink and attrs.get("Id", "") not in users)
        else:
            outside = _outside_ref(target, folder, rooted=True)
        if outside:
            removed = True
        else:
            keep.append(attrs)
    if not removed:
        return None
    body = "".join(
        "<Relationship " + " ".join(f"{k}={quoteattr(v)}" for k, v in attrs.items()) + "/>"
        for attrs in keep
    )
    return ('<?xml version="1.0" encoding="UTF-8" standalone="yes"?>\n'
            f'<Relationships xmlns="{_REL_NS}">{body}</Relationships>').encode("utf-8")


def _write_package(source: Path, target: Path, rewrites: dict[str, bytes]) -> None:
    """Copy a package part for part, replacing the cleaned ones, order kept
    (an ODF ``mimetype`` must stay first and stored)."""
    import zipfile

    with zipfile.ZipFile(source) as src, zipfile.ZipFile(target, "w") as out:
        for info in src.infolist():
            data = rewrites.get(info.filename)
            out.writestr(info, src.read(info) if data is None else data)


def _soffice_gate(job: Job, source: Path, near: Path) -> tuple[Path, str | None]:
    """What LibreOffice may open in place of ``source``, and its import filter.

    A package with no outside links is passed as it is; one whose links can be
    removed is copied without them into a scratch folder; anything else is
    refused with ``RunnerFailed``.
    """
    ext = _ext(source)
    if ext in _RESAVE_AS:
        raise RunnerFailed(
            f"{source.name} is a {ext.upper()} file. Peel cannot check it for links "
            "to other files before LibreOffice opens it, so it is not converted. "
            f"Open it, save it as {_RESAVE_AS[ext]}, and convert that.",
            code="unsafe_input",
        )
    if ext in DELIMITED:
        return source, None  # read with the CSV filter, formulas kept as text
    if ext == "txt":
        copy = job.scratch_dir(near) / f"{source.stem or 'text'}.txt"
        copy.write_text(_read_text(source), encoding="utf-8")
        return copy, _TEXT_INFILTER
    if ext not in _SOFFICE_INFILTER:
        raise RunnerFailed(f"Peel does not hand .{ext} files to LibreOffice.", code="unsafe_input")
    found = _inspect_package(source)
    if found.blockers:
        raise RunnerFailed(
            f"{source.name} was not converted: {found.blockers[0]}, and Peel cannot "
            "remove that link safely. Embed the linked file or remove the link, "
            "save, and try again.",
            code="unsafe_input",
        )
    if not found.rewrites:
        return source, _SOFFICE_INFILTER[ext]
    copy = job.scratch_dir(near) / source.name
    _write_package(source, copy, found.rewrites)
    _GATE_NOTES.set(_GATE_NOTES.get() + (f"{source.name} ({', '.join(sorted(found.rewrites))})",))
    return copy, _SOFFICE_INFILTER[ext]


def extract_text(job: Job) -> Result:
    """The Extract Text and Extract Markdown tools: the words, no formatting."""
    target = job.output_path()
    dropped = _pandoc(job.source, target, job=job, src_ext=_ext(job.source),
                      out_ext=_ext(target))
    return ok([target], _with_note(f"Wrote {target.name}", dropped))


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
    texted = False
    try:
        book = Workbook()
        book.remove(book.active)
        for number, page in enumerate(doc, start=1):
            sheet = book.create_sheet(title=f"Page {number}"[:31])
            row = 1
            for table in page.find_tables().tables:
                tables_found += 1
                for cells in _drop_empty_columns(table.extract()):
                    _write_row(sheet, row, (_as_number(c) for c in cells))
                    row += 1
                row += 1
            text = page.get_text()
            texted = texted or bool(text.strip())
            if row == 1:
                for line in text.splitlines():
                    _write_row(sheet, row, [line])
                    row += 1
        book.save(_abs(target))
    finally:
        doc.close()
    noun = "table" if tables_found == 1 else "tables"
    message = f"Wrote {target.name} ({tables_found} {noun} found)"
    return ok([target], message + _no_text_note(texted))


def _drop_empty_columns(rows: Sequence[Sequence[Any]]) -> list[list[Any]]:
    """A table without the columns that are empty in every row.

    PyMuPDF 2.x reports a phantom column between real ones on some tables
    (``['name', '', 'qty']``); written as is, every value after it lands one
    column to the right of its header's neighbour. A column with nothing in
    any row carries no data, so dropping it is safe with either version.
    """
    rows = [list(row) for row in rows]
    width = max((len(row) for row in rows), default=0)

    def blank(value: Any) -> bool:
        return value is None or (isinstance(value, str) and not value.strip())

    keep = [c for c in range(width)
            if any(c < len(row) and not blank(row[c]) for row in rows)]
    return [[row[c] if c < len(row) else None for c in keep] for row in rows]


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
    return ok([target], f"Wrote {target.name}" + _no_text_note(bool(body.strip())))


def _no_text_note(found: bool) -> str:
    """A warning for a PDF with no text layer: success, but say why it is empty."""
    if found:
        return ""
    return (". No text was found in the PDF: it is probably scanned pages, "
            "which need OCR before their words can be extracted")


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
        texted = any(page.get_text().strip() for page in doc)
    finally:
        doc.close()
    bridge = job.scratch_dir(target) / f"{job.source.stem or 'document'}.html"
    bridge.write_text(
        "<html><head><meta charset=\"utf-8\"><title>"
        f"{_escape(job.source.stem)}</title></head><body>{body}</body></html>",
        encoding="utf-8",
    )
    media = _claim_media(job, target) if out_ext == "md" else None
    _pandoc(bridge, target, job=job, src_ext="html", out_ext=out_ext, media=media,
            reader=PDF_MARKUP_READER)
    return ok([target] + _media_outputs(media), f"Wrote {target.name}" + _no_text_note(texted))


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
    infilter = None
    if src_ext in DELIMITED:
        # The same dialect the Python routes use, on a UTF-8 copy, so a
        # semicolon CSV splits into the same columns whatever the target.
        text = _read_text(source)
        infilter = _csv_filter(*_csv_dialect(text, DELIMITED[src_ext]))
        copy = job.scratch_dir(target) / f"{source.stem or 'table'}.{src_ext}"
        copy.write_text(text, encoding="utf-8", newline="")
        source = copy
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

    sheets = _read_workbook(book_path, job)
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
    _check_xlsx_limits(source, rows)
    target = job.output_path()
    book = Workbook()
    sheet = book.active
    sheet.title = _sheet_title(source.stem)
    # At least one column whenever there are records, so a file of blank lines
    # still keeps one (empty) row per record.
    width = max(max((len(cells) for cells in rows), default=0), 1 if rows else 0)
    for number, cells in enumerate(rows, start=1):
        cells = list(cells) + [""] * (width - len(cells))
        for column, text in enumerate(cells, start=1):
            value = _as_number(text)
            cell = sheet.cell(row=number, column=column)
            cell.value = value
            # Every string is text: openpyxl would otherwise turn "=..." into a
            # formula and "#N/A" or "#DIV/0!" into an error cell. Empty strings
            # are written too, so trailing empty records and columns survive.
            if isinstance(value, str):
                cell.data_type = "s"
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


#: What one XLSX cell and sheet can hold.
XLSX_MAX_CELL = 32767
XLSX_MAX_ROWS = 1048576
XLSX_MAX_COLUMNS = 16384
#: Characters XML 1.0, and so XLSX, cannot store at all.
_NOT_IN_XLSX = re.compile("[\x00-\x08\x0b\x0c\x0e-\x1f\ufffe\uffff\ud800-\udfff]")


def _column_name(index: int) -> str:
    name = ""
    while index:
        index, rest = divmod(index - 1, 26)
        name = chr(65 + rest) + name
    return name


def _check_xlsx_limits(source: Path, rows: Sequence[Sequence[str]]) -> None:
    """Refuse, naming the cell, rather than let a value be cut or changed.

    Text is written as it is, newlines and surrounding spaces included, so the
    only ways to lose data are a cell longer than Excel allows, a character
    XLSX cannot store, or more rows or columns than a sheet has.
    """
    if len(rows) > XLSX_MAX_ROWS:
        raise RunnerFailed(f"{source.name} has {len(rows)} rows; an XLSX sheet holds "
                           f"{XLSX_MAX_ROWS}. Nothing was written.", code="too_large")
    for number, cells in enumerate(rows, start=1):
        if len(cells) > XLSX_MAX_COLUMNS:
            raise RunnerFailed(f"Row {number} of {source.name} has {len(cells)} columns; "
                               f"an XLSX sheet holds {XLSX_MAX_COLUMNS}. Nothing was written.",
                               code="too_large")
        for column, text in enumerate(cells, start=1):
            where = f"Cell {_column_name(column)}{number} of {source.name}"
            if len(text) > XLSX_MAX_CELL:
                raise RunnerFailed(f"{where} has {len(text)} characters; an XLSX cell holds "
                                   f"{XLSX_MAX_CELL}. Nothing was written.", code="cell_too_long")
            bad = _NOT_IN_XLSX.search(text)
            if bad:
                raise RunnerFailed(f"{where} contains character U+{ord(bad.group()):04X}, which "
                                   "an XLSX file cannot store. Nothing was written.",
                                   code="bad_character")


def _csv_dialect(text: str, delimiter: str) -> tuple[str, str]:
    """The separator and quote character, detected once for every route.

    A ``.csv`` whose first record is one single column with commas is sniffed
    for another separator: Excel in a locale with a decimal comma (es-ES, de,
    fr) saves CSV with semicolons.
    """
    import csv
    import io

    if delimiter != ",":
        return delimiter, '"'
    first = next(csv.reader(io.StringIO(text, newline=""), delimiter=","), [])
    if len(first) > 1:
        return ",", '"'
    try:
        dialect = csv.Sniffer().sniff(text[:65536], delimiters=";\t|")
    except csv.Error:
        return ",", '"'
    other = next(csv.reader(io.StringIO(text, newline=""), dialect), [])
    if len(other) > 1:
        return dialect.delimiter, dialect.quotechar or '"'
    return ",", '"'


def _read_delimited(source: Path, delimiter: str) -> list[list[str]]:
    """Rows of a CSV or TSV, quoted fields that span lines included.

    The reader is given a stream rather than a list of lines: a field like
    ``"first<newline>second"`` is one value, and splitting the text into lines
    first would hand the reader two half rows and join them into ``firstsecond``.
    ``newline=""`` keeps the line endings for the reader to interpret, as the
    csv module's own documentation requires.

    The separator comes from ``_csv_dialect``, the same detection the
    LibreOffice routes are given.
    """
    import csv
    import io

    text = _read_text(source)
    found, quotechar = _csv_dialect(text, delimiter)
    return list(csv.reader(io.StringIO(text, newline=""), delimiter=found, quotechar=quotechar))


def _write_delimited(target: Path, rows: Iterable[Sequence[Any]], delimiter: str) -> None:
    """Write with a BOM: Excel on Windows reads a plain UTF-8 CSV as cp1252."""
    import csv

    with open(target, "w", encoding="utf-8-sig", newline="") as handle:
        csv.writer(handle, delimiter=delimiter).writerows(
            ["" if cell is None else cell for cell in row] for row in rows
        )


def _read_workbook(path: Path, job: Job | None = None) -> list[tuple[str, list[list[Any]]]]:
    """Every sheet of an XLSX as (title, rows): the values, not the formulas.

    A workbook saved by a script and never opened in Excel has formulas with no
    cached result, and asking for values alone gives back an empty cell. Rather
    than report success over silently dropped data, such a workbook is
    recalculated by LibreOffice when it is installed, and any formula still
    without a value is written as its formula text. Trailing blank rows are
    trimmed only after that, so a total row made of nothing but an uncached
    formula is never mistaken for blank and dropped.
    """
    values = _sheet_rows(path, data_only=True)
    formulas = _sheet_rows(path, data_only=False)
    if _formula_gaps(values, formulas) and job is not None and not backends.missing(("soffice",)):
        recalculated = job.scratch_dir(path) / f"{path.stem or 'sheet'}.xlsx"
        with contextlib.suppress(RunnerFailed):
            _soffice(path, recalculated, "xlsx", job)
            values = _sheet_rows(recalculated, data_only=True)
    for (_, rows), (_, backup) in zip(values, formulas):
        for index, row in enumerate(rows):
            if index >= len(backup):
                break
            for column, cell in enumerate(row):
                if cell is None and column < len(backup[index]) and _is_formula(backup[index][column]):
                    row[column] = backup[index][column]
    return [(title, _trim([[_cell_text(cell) for cell in row] for row in rows]))
            for title, rows in values]


def _is_formula(value: Any) -> bool:
    if not isinstance(value, str):
        value = getattr(value, "text", None)  # an openpyxl ArrayFormula
    return isinstance(value, str) and value.startswith("=")


def _formula_gaps(values: list[tuple[str, list[list[Any]]]],
                  formulas: list[tuple[str, list[list[Any]]]]) -> bool:
    """Whether some formula cell has no cached value.

    No cached value is ``None``. A cached empty string (``=IF(A1>5,"big","")``
    that came out false) is a real result, shown blank in Excel, and is not a
    gap.
    """
    for (_, rows), (_, backup) in zip(values, formulas):
        for row, brow in zip(rows, backup):
            for cell, formula in zip(row, brow):
                if cell is None and _is_formula(formula):
                    return True
    return False


def _trim(rows: list[list[Any]]) -> list[list[Any]]:
    while rows and not any(str(cell).strip() for cell in rows[-1]):
        rows.pop()
    return rows


def _cell_text(value: Any) -> Any:
    """A cell as Excel itself would export it to CSV.

    Booleans are TRUE and FALSE, not Python's True and False, and a date with
    no time of day is just the date, not ``2026-01-05 00:00:00``.
    """
    import datetime as dt

    if value is None:
        return ""
    if isinstance(value, bool):
        return "TRUE" if value else "FALSE"
    if isinstance(value, dt.datetime):
        if value.time() == dt.time(0, 0):
            return value.date().isoformat()
        return value.isoformat(sep=" ")
    if isinstance(value, (dt.date, dt.time)):
        return value.isoformat()
    if not isinstance(value, (str, int, float)):
        text = getattr(value, "text", None)  # an openpyxl ArrayFormula
        return text if isinstance(text, str) else str(value)
    return value


def _sheet_rows(path: Path, *, data_only: bool) -> list[tuple[str, list[list[Any]]]]:
    """Every sheet as (title, rows) of raw openpyxl values.

    ``None`` is kept as ``None`` (an empty cell, or a formula with no cached
    result) so it stays distinct from a cached empty string; ``_cell_text``
    turns both into text only after the formula merge. openpyxl reads both a
    missing and an empty cached result as ``None``; the cell's type tells
    them apart: a formula whose cached result is text has type ``str``, and
    one never calculated (as openpyxl itself saves it) has none.
    """
    from openpyxl import load_workbook

    def value(cell: Any) -> Any:
        if cell.value is None and getattr(cell, "data_type", None) == "str":
            return ""
        return cell.value

    book = load_workbook(_abs(path), read_only=True, data_only=data_only)
    try:
        return [
            (sheet.title, [[value(cell) for cell in row] for row in sheet.iter_rows()])
            for sheet in book.worksheets
        ]
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
#: WebVTT markup SRT players do not know: voice spans, classes (colours),
#: language spans, ruby and karaoke timestamps. SRT keeps b, i, u and font.
_VTT_ONLY_TAGS = re.compile(r"</?(?:v|c|lang|ruby|rt)(?:[.\s][^>]*)?>|<\d+:[\d:.]+>")
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
    # Only SRT numbers its cues. In VTT a numeric last line is cue text.
    srt = _ext(source) == "srt"
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
            if _is_timing(lines[index]):
                # The blank line before the next cue is missing. Stop here, and
                # give back an SRT index number that belongs to the next cue.
                if srt and body and body[-1].strip().isdigit():
                    body.pop()
                break
            body.append(lines[index].rstrip())
            index += 1
        if start is None or end is None:
            continue
        cues.append(Cue(start, max(start, end), "\n".join(body).strip()))
    return cues


def _is_timing(line: str) -> bool:
    """Whether a line is a cue timing line (``start --> end``)."""
    left, arrow, _ = line.partition("-->")
    return bool(arrow) and parse_timecode(left) is not None


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
        f"{format_timecode(cue.end, ',')}\n{_VTT_ONLY_TAGS.sub('', cue.text)}\n"
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
