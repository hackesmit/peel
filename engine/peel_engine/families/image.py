"""Image family: the SPEC.md Images conversion row and the Image file tools.

Everything here is pillow, with two helpers for the formats pillow alone does
not cover: pillow-heif for HEIC, and PyMuPDF (or ImageMagick) to rasterise an
SVG on the way in. Nothing is imported at module scope, because ``peel
options`` imports every family module and the spec budgets it well under a
second; the registry entries below are plain data and the decoders only load
inside a runner.

Two things the registry cannot express, and how they are handled here:

* ``Action.backends`` is an AND list over a whole entry, so it cannot say "this
  one INPUT format needs an extra backend". SVG and HEIC are inputs to every
  conversion, so their extra backend is checked when the run reaches that file
  and reported as a normal message naming the install, not as a hidden wheel
  segment. The OUTPUT side is expressible, so ``image.to_heic`` does declare
  ``pillow_heif`` and disappears from the wheel without it.
* The same list cannot say "PyMuPDF OR ImageMagick", which is what SVG input
  really needs, so ``svg_rasteriser`` picks whichever is installed.

AVIF is the one format whose availability depends on how pillow itself was
built: pillow 11.3 and later carry AVIF natively (and that is why pillow-heif
1.0 dropped its AVIF opener). Peel therefore wants pillow >= 11.3, or
pillow-heif < 1.0 as the older alternative.
"""

from __future__ import annotations

import io
import math
import subprocess
from pathlib import Path
from typing import Any, Sequence

from .. import backends, naming
from ..registry import CONVERT, SAME, TOOL, Job, Param, Registry
from ..result import BackendMissing, InvalidParams, Result, RunnerFailed, ok

#: Extension to pillow format name. The aliases matter: a drag brings whatever
#: the file is called, and ``.jpeg`` and ``.jpg`` are one format.
FORMATS: dict[str, str] = {
    "jpg": "JPEG",
    "jpeg": "JPEG",
    "png": "PNG",
    "webp": "WEBP",
    "heic": "HEIF",
    "heif": "HEIF",
    "tiff": "TIFF",
    "tif": "TIFF",
    "avif": "AVIF",
    "bmp": "BMP",
    "pdf": "PDF",
}

#: SPEC.md Images row, outputs column, in its order. Each is
#: (extension, wheel label, the input aliases it makes pointless, backends).
TARGETS: tuple[tuple[str, str, tuple[str, ...], tuple[str, ...]], ...] = (
    ("jpg", "JPG", ("jpg", "jpeg"), ("pillow",)),
    ("png", "PNG", ("png",), ("pillow",)),
    ("webp", "WebP", ("webp",), ("pillow",)),
    ("heic", "HEIC", ("heic", "heif"), ("pillow", "pillow_heif")),
    ("tiff", "TIFF", ("tiff", "tif"), ("pillow",)),
    ("avif", "AVIF", ("avif",), ("pillow",)),
    ("bmp", "BMP", ("bmp",), ("pillow",)),
    ("pdf", "PDF", (), ("pillow",)),
)

#: SPEC.md Images row, inputs column. Raster first, then the vector input.
RASTER_INPUTS = ("jpg", "jpeg", "png", "webp", "heic", "heif", "tiff", "tif", "avif", "bmp")
VECTOR_INPUTS = ("svg",)
ALL_INPUTS = RASTER_INPUTS + VECTOR_INPUTS

#: Formats that can hold EXIF, which is what the Metadata tool works on.
METADATA_INPUTS = ("jpg", "jpeg", "png", "webp", "tiff", "tif", "heic", "heif", "avif")

#: Formats with a quality knob. The rest ignore it and the message says so.
LOSSY = frozenset({"JPEG", "WEBP", "AVIF", "HEIF"})

#: Formats that cannot carry an alpha channel, so a transparent source is
#: flattened onto white rather than failing. BMP can technically hold a 32 bit
#: alpha channel, but most viewers ignore it and show whatever colour sits
#: under a transparent pixel, so it is flattened like JPG.
NO_ALPHA = frozenset({"JPEG", "PDF", "BMP"})

#: Pillow modes with more than 8 bits per channel: 16 bit greyscale (scanners,
#: medical and scientific images), 32 bit integer and 32 bit float.
DEEP_MODES = frozenset({"I;16", "I;16L", "I;16B", "I;16N", "I", "F"})

#: The deep modes each format can store as they are. Every other format, and
#: every other mode, gets a properly scaled 8 bit image instead of a clipped one.
DEEP_OK: dict[str, frozenset[str]] = {
    "PNG": frozenset({"I;16", "I;16L", "I;16B", "I;16N", "I"}),
    "TIFF": DEEP_MODES,
}

#: Formats that hold an animation. An animated source converted or compressed
#: into one of these keeps every frame; anything else gets the first frame and
#: the result message says so.
ANIMATED = frozenset({"WEBP", "GIF"})

#: Metadata keys of ``img.info`` that are not personal data and are kept when
#: Peel strips or redacts: the ICC colour profile (without it a Display P3 or
#: Adobe RGB photo shifts colour) and palette transparency (part of the
#: picture, not a tag).
KEEP_INFO = ("icc_profile", "transparency")

#: Re-encode quality for everything except Compress, which is given one.
QUALITY = 92

#: Pixels per inch assumed when an image becomes a PDF page. The page keeps the
#: image's exact proportions; nothing is scaled onto a paper size.
PDF_DPI = 96

#: An SVG has no natural pixel size, so it is rendered so its longer side is
#: about this many pixels, never smaller than its own point size and never
#: blown up more than SVG_MAX_ZOOM.
SVG_TARGET_PX = 1024
SVG_MAX_ZOOM = 8.0

#: Pixel budget for anything Peel draws or decodes itself, matching pillow's
#: own decompression bomb limit (``Image.MAX_IMAGE_PIXELS``, about 89 million).
#: An SVG that would render bigger is refused before anything is allocated.
PIXEL_BUDGET = 178956970 // 2

#: Default annotation ink: the same orange-red every screenshot tool uses.
DEFAULT_INK = "#ff3b30"


def register(registry: Registry) -> None:
    for ext, label, own, needed in TARGETS:
        registry.add(
            id=f"image.to_{ext}",
            family=CONVERT,
            label=label,
            group="Images",
            # Offering JPG for a JPG is a wheel segment that does nothing.
            inputs=tuple(e for e in ALL_INPUTS if e not in own),
            output_ext=ext,
            backends=needed,
            runner="peel_engine.families.image:convert",
        )

    registry.add(
        id="image.compress",
        family=TOOL,
        label="Compress",
        group="Images",
        inputs=RASTER_INPUTS,
        output_ext=SAME,
        backends=("pillow",),
        params=(
            Param("quality", "int", "Quality", default=80, minimum=1, maximum=100),
            Param(
                "max_dimension",
                "int",
                "Longest side",
                default=0,
                minimum=0,
                maximum=20000,
                help="Pixels. 0 keeps the current size. A smaller image is never enlarged.",
            ),
        ),
        runner="peel_engine.families.image:compress",
    )
    registry.add(
        id="image.crop",
        family=TOOL,
        label="Crop",
        group="Images",
        inputs=RASTER_INPUTS,
        output_ext=SAME,
        needs_editor=True,
        max_files=1,
        backends=("pillow",),
        params=(
            Param("x", "int", "Left", required=True, minimum=0),
            Param("y", "int", "Top", required=True, minimum=0),
            Param("w", "int", "Width", required=True, minimum=1),
            Param("h", "int", "Height", required=True, minimum=1),
        ),
        runner="peel_engine.families.image:crop",
    )
    registry.add(
        id="image.edit",
        family=TOOL,
        label="Edit",
        group="Images",
        inputs=RASTER_INPUTS,
        output_ext=SAME,
        needs_editor=True,
        backends=("pillow",),
        params=(
            Param("rotate", "enum", "Rotate", default=0, choices=(0, 90, 180, 270),
                  help="Degrees clockwise."),
            Param("flip", "enum", "Flip", default="none",
                  choices=("none", "horizontal", "vertical")),
            Param("brightness", "float", "Brightness", default=1.0, minimum=0.0, maximum=4.0),
            Param("contrast", "float", "Contrast", default=1.0, minimum=0.0, maximum=4.0),
            Param("saturation", "float", "Saturation", default=1.0, minimum=0.0, maximum=4.0),
        ),
        runner="peel_engine.families.image:edit",
    )
    registry.add(
        id="image.annotate",
        family=TOOL,
        label="Annotate",
        group="Images",
        inputs=RASTER_INPUTS,
        output_ext=SAME,
        needs_editor=True,
        max_files=1,
        backends=("pillow",),
        params=(
            Param(
                "shapes",
                "json",
                "Shapes",
                required=True,
                help=(
                    "A list of shapes. pen: points, color, width. arrow: from, to, "
                    "color, width. rect: x, y, w, h, color, width, fill. "
                    "text: x, y, text, color, size."
                ),
            ),
        ),
        runner="peel_engine.families.image:annotate",
    )
    registry.add(
        id="image.redact",
        family=TOOL,
        label="Redact",
        group="Images",
        inputs=RASTER_INPUTS,
        output_ext=SAME,
        needs_editor=True,
        max_files=1,
        backends=("pillow",),
        params=(
            Param("regions", "json", "Regions", required=True,
                  help="A list of {x, y, w, h} rectangles to hide."),
            Param("mode", "enum", "Hide with", default="blur", choices=("blur", "box")),
        ),
        runner="peel_engine.families.image:redact",
    )
    registry.add(
        id="image.background",
        family=TOOL,
        label="Add background",
        group="Images",
        inputs=RASTER_INPUTS,
        output_ext=SAME,
        backends=("pillow",),
        params=(Param("color", "string", "Colour", default="#FFFFFF"),),
        runner="peel_engine.families.image:background",
    )
    registry.add(
        id="image.collage",
        family=TOOL,
        label="Collage",
        group="Images",
        # The output format is fixed, so a vector input is fine here.
        inputs=ALL_INPUTS,
        output_ext="png",
        mode="combine",
        min_files=2,
        backends=("pillow",),
        params=(
            Param("columns", "int", "Columns", default=0, minimum=0, maximum=100,
                  help="0 picks a square-ish grid."),
            Param("gap", "int", "Gap", default=8, minimum=0, maximum=512),
            Param("background", "string", "Background", default="#FFFFFF"),
        ),
        runner="peel_engine.families.image:collage",
    )
    registry.add(
        id="image.create_pdf",
        family=TOOL,
        label="Create PDF",
        group="Images",
        inputs=ALL_INPUTS,
        output_ext="pdf",
        mode="combine",
        min_files=2,
        backends=("pillow",),
        runner="peel_engine.families.image:create_pdf",
    )
    registry.add(
        id="image.metadata",
        family=TOOL,
        label="Metadata",
        group="Images",
        inputs=METADATA_INPUTS,
        output_ext=SAME,
        needs_editor=True,
        # The editor shows one image, and Registry._run_each keeps only the
        # outputs of a batch, so a batched "view" would lose its answer.
        max_files=1,
        backends=("pillow",),
        params=(
            Param("action", "enum", "Action", default="view", choices=("view", "strip", "set")),
            Param("title", "string", "Title"),
            Param("author", "string", "Author"),
            Param("latitude", "float", "Latitude", minimum=-90.0, maximum=90.0),
            Param("longitude", "float", "Longitude", minimum=-180.0, maximum=180.0),
            Param("clear_location", "bool", "Clear location", default=False,
                  help="With set: remove the GPS location and keep every other tag."),
        ),
        runner="peel_engine.families.image:metadata",
    )


# --- loading ----------------------------------------------------------------

_openers_registered = False


def pillow():
    """``PIL.Image``, with the HEIC and AVIF openers registered once.

    Call this from inside a runner, never at module scope: the import is the
    expensive part of the engine and ``peel options`` must not pay it.
    """
    global _openers_registered
    from PIL import Image

    if not _openers_registered:
        _register_openers(Image)
        _openers_registered = True
    return Image


def _register_openers(Image) -> None:
    try:
        import pillow_heif
    except ImportError:
        return
    pillow_heif.register_heif_opener()
    # pillow-heif 1.0 dropped its AVIF opener because pillow 11.3 gained one.
    register_avif = getattr(pillow_heif, "register_avif_opener", None)
    if register_avif is not None and ".avif" not in Image.registered_extensions():
        register_avif()


def svg_rasteriser() -> str | None:
    """Which backend can turn an SVG into pixels here, or None.

    Probed without importing anything: ``backends.find`` uses ``find_spec`` for
    a module and ``which`` for an executable.
    """
    if backends.available("pymupdf"):
        return "pymupdf"
    if backends.available("magick"):
        return "magick"
    return None


def load(path: Path):
    """Open an image upright, loaded into memory, with its file handle closed.

    Upright means the EXIF Orientation tag is applied and dropped: a phone
    photo must come out of a conversion looking the way it looked on screen,
    and a crop box from the editor refers to what the editor showed.
    """
    from PIL import ImageOps

    path = Path(path)
    if naming.normalise_ext(path.suffix) in VECTOR_INPUTS:
        img = _rasterise_svg(path)
    else:
        img = _open_raster(path)
    upright = ImageOps.exif_transpose(img) or img
    if upright is not img:
        img.close()
    return upright


def _open_raster(path: Path):
    """Open a raster file exactly as it is stored, orientation tag included."""
    Image = pillow()
    try:
        img = Image.open(path)
        img.load()
    except OSError as exc:
        raise _cannot_read(path, exc) from None
    return img


def _cannot_read(path: Path, exc: Exception) -> Exception:
    """The message for a file pillow would not open, naming the missing part."""
    ext = naming.normalise_ext(path.suffix)
    if ext in ("heic", "heif"):
        return BackendMissing(
            "Peel cannot read HEIC images because pillow-heif is not installed, "
            "and it is needed for HEIC and AVIF images. "
            "Install it with: pip install pillow-heif",
            backend="pillow_heif",
        )
    if ext == "avif":
        return BackendMissing(
            "Peel cannot read AVIF images because this Pillow was built without "
            "AVIF support. Install it with: pip install 'pillow>=11.3'",
            backend="pillow",
        )
    return RunnerFailed(f"{path.name} is not an image Peel can read: {exc}")


def _rasterise_svg(path: Path):
    Image = pillow()
    which = svg_rasteriser()
    if which == "pymupdf":
        data = _svg_via_pymupdf(path)
    elif which == "magick":
        data = _svg_via_magick(path)
    else:
        raise BackendMissing(
            "Peel cannot read SVG files because neither PyMuPDF nor ImageMagick "
            "is installed, and one is needed to draw a vector image. "
            "Install it with: pip install pymupdf",
            backend="pymupdf",
        )
    img = Image.open(io.BytesIO(data))
    img.load()
    return img


def _svg_via_pymupdf(path: Path) -> bytes:
    import fitz

    with fitz.open(str(path)) as doc:
        if not doc.page_count:
            raise RunnerFailed(f"{path.name} has nothing to draw")
        page = doc[0]
        longest = max(page.rect.width, page.rect.height, 1.0)
        zoom = min(max(SVG_TARGET_PX / longest, 1.0), SVG_MAX_ZOOM)
        _check_budget(path, page.rect.width * zoom, page.rect.height * zoom)
        pixmap = page.get_pixmap(matrix=fitz.Matrix(zoom, zoom), alpha=True)
        return pixmap.tobytes("png")


def pixel_budget() -> int:
    """The most pixels Peel will render or decode in one image."""
    limit = pillow().MAX_IMAGE_PIXELS
    return int(limit) if limit else PIXEL_BUDGET


def _check_budget(path: Path, width: float, height: float) -> None:
    """Refuse a render bigger than the pixel budget, before it is allocated."""
    budget = pixel_budget()
    if width * height > budget:
        raise RunnerFailed(
            f"{path.name} would draw at {int(width)} by {int(height)} pixels, "
            f"more than the {budget // 1_000_000} million pixel limit Peel keeps "
            "so one file cannot exhaust the memory. Make the SVG smaller and try again."
        )


def _svg_via_magick(path: Path) -> bytes:
    magick = backends.require("magick")
    try:
        proc = subprocess.run(
            [
                str(magick),
                "-limit", "area", str(pixel_budget()),
                "-limit", "width", "65000",
                "-limit", "height", "65000",
                str(path), "-background", "none", "png:-",
            ],
            capture_output=True,
            timeout=120,
        )
    except (OSError, subprocess.SubprocessError) as exc:
        raise RunnerFailed(f"ImageMagick could not read {path.name}: {exc}") from None
    if proc.returncode != 0 or not proc.stdout:
        lines = (proc.stderr or b"").decode("utf-8", "replace").strip().splitlines()
        detail = lines[-1] if lines else "it wrote nothing"
        raise RunnerFailed(f"ImageMagick could not read {path.name}: {detail}")
    return proc.stdout


# --- saving -----------------------------------------------------------------


def save(
    img,
    target: Path,
    ext: str,
    *,
    quality: int | str | None = None,
    exif: bytes | Any = None,
    icc_profile: Any = None,
    pages: Sequence[Any] = (),
) -> None:
    """Encode ``img`` into the already claimed ``target``, filling it in place.

    The naming contract hands runners a path that exists as an empty
    placeholder, so this opens it for writing rather than creating it.
    ``pages`` adds further pages to a PDF.
    """
    fmt = FORMATS[naming.normalise_ext(ext)]
    out = _prepare(img, fmt)
    options: dict[str, Any] = {}
    if fmt in LOSSY and quality is not None:
        # "keep" reuses a JPEG's own quantisation tables, so a metadata edit
        # does not cost a generation of quality. Only valid on an untouched
        # JPEG straight from its decoder.
        options["quality"] = quality if quality == "keep" else int(quality)
    if fmt == "PNG":
        options["optimize"] = True
    if fmt == "TIFF":
        options["compression"] = "tiff_deflate"
    if exif:
        blob = exif if isinstance(exif, bytes) else exif_bytes(exif)
        if blob:
            options["exif"] = blob
    if icc_profile:
        options["icc_profile"] = icc_profile
    if fmt == "PDF":
        options["resolution"] = float(PDF_DPI)
        if pages:
            options["save_all"] = True
            options["append_images"] = [_prepare(p, fmt) for p in pages]
    _write(out, target, fmt, options)


def _write(img, target: Path, fmt: str, options: dict[str, Any]) -> None:
    """Fill the claimed path, retrying the two encodings that really do fail.

    Each attempt reopens the claim with ``"wb"``, so a half written one is
    truncated rather than appended to, and the attempts are built lazily so a
    first-try success never pays for a conversion it did not need.
    """
    attempts: list[tuple[Any, dict[str, Any]]] = [(lambda: img, options)]
    if fmt == "TIFF" and "compression" in options:
        # Any compression sends pillow through libtiff, which is handed every
        # top level tag one at a time and refuses the sub-IFD pointers in a
        # phone photo's EXIF ("Error setting from dictionary"). Pillow's own
        # uncompressed writer takes the blob whole: a bigger TIFF, but the
        # place and time the photo was taken survive the conversion.
        options = {key: value for key, value in options.items() if key != "compression"}
        attempts.append((lambda: img, options))
    if options.get("quality") == "keep":
        # Pillow refuses "keep" on anything it did not decode as a JPEG.
        options = {**options, "quality": QUALITY}
        attempts.append((lambda: img, options))
    if img.mode != "RGB":
        # Odd source modes (CMYK, YCbCr) that a given encoder refuses. A deep
        # mode is scaled down first, never clipped.
        attempts.append((lambda: to_8bit(img).convert("RGB"), options))

    last: Exception | None = None
    for build, chosen in attempts:
        try:
            with open(target, "wb") as handle:
                build().save(handle, format=fmt, **chosen)
            return
        except Exception as exc:
            last = exc
    raise RunnerFailed(f"cannot write {fmt}: {last}") from None


def _prepare(img, fmt: str):
    """Put the image in a mode the target format can actually hold."""
    if img.mode in DEEP_MODES and img.mode not in DEEP_OK.get(fmt, ()):
        img = to_8bit(img)
    if fmt not in NO_ALPHA:
        return img
    if img.mode == "P":
        img = img.convert("RGBA" if "transparency" in img.info else "RGB")
    if img.mode in ("RGBA", "LA"):
        img = flatten(img, (255, 255, 255))
    if img.mode not in ("RGB", "L", "CMYK", "1"):
        img = img.convert("RGB")
    return img


def flatten(img, rgb: tuple[int, int, int]):
    """Composite an image with alpha onto an opaque colour."""
    Image = pillow()
    base = Image.new("RGBA", img.size, tuple(rgb) + (255,))
    return Image.alpha_composite(base, to_rgba(img)).convert("RGB")


def to_8bit(img):
    """A deep (16 bit, 32 bit or float) image scaled into 8 bit greyscale.

    Pillow's own ``convert("L")`` or ``convert("RGB")`` from these modes clips
    every value above 255, so a 16 bit scan comes out almost white. The value
    range is read off the pixels: 16 bit data is divided by 256, float data in
    0..1 is multiplied by 255, data already in 0..255 is left alone, and
    anything else is stretched from its own minimum to its own maximum.
    Any other mode is returned unchanged.
    """
    if img.mode not in DEEP_MODES:
        return img
    if img.mode.startswith("I;16"):
        low, high = 0.0, 65535.0
        img = img.convert("I")
    else:
        low, high = (float(v) for v in img.getextrema())
        if not (math.isfinite(low) and math.isfinite(high)):
            img, low, high = _finite_only(img)
        if img.mode == "F" and 0.0 <= low and high <= 1.0:
            low, high = 0.0, 1.0
        elif 0.0 <= low and high <= 255.0:
            low, high = 0.0, 255.0
        elif 0.0 <= low and high <= 65535.0:
            low, high = 0.0, 65535.0
    span = (high - low) or 1.0
    scale = 255.0 / span
    offset = -low * scale
    source = img if img.mode == "F" else img.convert("F")
    return source.point(lambda v: v * scale + offset).convert("L")


def _finite_only(img):
    """A float image with NaN and Inf pixels replaced, and its finite range.

    Float TIFFs use NaN as nodata, and one NaN or Inf pixel makes
    ``getextrema`` NaN or infinite, which would scale every pixel to 0. The
    range comes from the finite pixels only; NaN and minus infinity become the
    low end (black), plus infinity the high end (white). With no finite pixel
    at all the image is all black.
    """
    Image = pillow()
    if img.mode != "F":
        img = img.convert("F")
    try:
        import numpy as np
    except ImportError:  # pragma: no cover - numpy ships with the Peel venv
        np = None
    if np is not None:
        values = np.frombuffer(img.tobytes(), dtype=np.float32)
        finite = values[np.isfinite(values)]
        low = float(finite.min()) if finite.size else 0.0
        high = float(finite.max()) if finite.size else 0.0
        clean = np.nan_to_num(values, nan=low, posinf=high, neginf=low)
        data = clean.astype(np.float32).tobytes()
    else:
        from array import array

        values = array("f", img.tobytes())
        finite = [v for v in values if math.isfinite(v)]
        low = min(finite) if finite else 0.0
        high = max(finite) if finite else 0.0
        clean = array("f", (
            v if math.isfinite(v) else (high if v > 0 else low) for v in values
        ))
        data = clean.tobytes()
    return Image.frombytes("F", img.size, data), low, high


def to_rgba(img):
    """``convert("RGBA")`` that scales a deep image instead of clipping it."""
    return to_8bit(img).convert("RGBA")


def carry_exif(img) -> bytes | None:
    """The source EXIF to write into a converted file, as bytes, or None.

    Orientation goes, because ``load`` already applied it and a second
    application by the viewer would rotate the image twice. Anything that will
    not re-encode cleanly is dropped whole rather than failing the run.
    """
    try:
        exif = img.getexif()
    except Exception:
        return None
    exif.pop(0x0112, None)
    try:
        return exif_bytes(exif)
    except Exception:
        return None


def exif_bytes(exif) -> bytes | None:
    """Serialise an EXIF block, or None when it holds nothing worth writing.

    Bytes rather than the live ``Image.Exif``, because pillow's TIFF encoder
    hands every top level tag to libtiff one at a time and chokes on the
    sub-IFD pointers (a phone photo with GPS would fail to convert to TIFF),
    while every encoder here takes a ready made blob. Emptiness is measured on
    the blob, not on ``len(exif)``: an EXIF holding only a GPS sub-IFD counts
    as zero top level tags but is not empty.
    """
    Image = pillow()
    blob = exif.tobytes()
    return blob if len(blob) > len(Image.Exif().tobytes()) else None


def _rgb_or_rgba(img):
    """Narrow any mode down to RGB or RGBA, so drawing code has two cases."""
    if img.mode in ("RGB", "RGBA"):
        return img
    return to_8bit(img).convert("RGBA" if _has_alpha(img) else "RGB")


def bare(img):
    """A copy of ``img`` carrying no metadata at all, except ``KEEP_INFO``.

    Pillow's encoders fall back to ``img.info`` for anything not passed to
    ``save`` (the JPEG writer reads ``info["comment"]``, others read EXIF, XMP
    or text chunks), so a copy with a cleared ``info`` is the only reliable way
    to write nothing the source carried.
    """
    clean = img.copy()
    clean.info = {key: img.info[key] for key in KEEP_INFO if key in img.info}
    if hasattr(clean, "_exif"):
        clean._exif = None
    return clean


def _has_alpha(img) -> bool:
    return img.mode in ("RGBA", "LA", "PA") or "transparency" in img.info


def _colour(raw: Any, what: str = "colour") -> tuple[int, int, int, int]:
    """Parse a CSS-ish colour into RGBA. Raises InvalidParams on nonsense."""
    from PIL import ImageColor

    try:
        parsed = ImageColor.getrgb(str(raw))
    except ValueError:
        raise InvalidParams(f"{raw!r} is not a colour Peel understands ({what})") from None
    if len(parsed) == 3:
        return parsed + (255,)
    return parsed


def _kb(size: int) -> str:
    if size < 1024:
        return f"{size} B"
    if size < 1024 * 1024:
        return f"{size / 1024:.0f} KB"
    return f"{size / (1024 * 1024):.1f} MB"


# --- animation --------------------------------------------------------------


def frame_count(path: Path) -> int:
    """How many frames a file holds (animated WebP, APNG, AVIF or HEIC sequence).

    Opening is lazy, so this reads only the header. A file pillow cannot open
    counts as one frame and ``load`` reports the real problem.
    """
    if naming.normalise_ext(Path(path).suffix) in VECTOR_INPUTS:
        return 1
    Image = pillow()
    try:
        with Image.open(path) as img:
            return max(1, int(getattr(img, "n_frames", 1) or 1))
    except Exception:
        return 1


def load_frames(path: Path, longest: int = 0):
    """Every frame as RGBA, with per frame durations and the loop count.

    Returns None when the frames together exceed twice the pixel budget, so the
    caller falls back to the first frame instead of exhausting the memory.
    ``longest`` caps the long edge of each frame, as Compress does.
    """
    from PIL import ImageSequence

    Image = pillow()
    try:
        img = Image.open(path)
    except OSError as exc:
        raise _cannot_read(path, exc) from None
    with img:
        count = max(1, int(getattr(img, "n_frames", 1) or 1))
        if img.width * img.height * count > 2 * pixel_budget():
            return None
        frames, durations = [], []
        for frame in ImageSequence.Iterator(img):
            # The WebP and AVIF decoders fill info["duration"] only when the
            # frame is loaded, so load first or every frame reads as 100 ms.
            frame.load()
            durations.append(int(frame.info.get("duration") or 100))
            rgba = to_rgba(frame)
            frames.append(fit_within(rgba, longest) if longest else rgba)
        info = {
            "loop": int(img.info.get("loop", 0) or 0),
            "exif": carry_exif(img),
            "icc_profile": img.info.get("icc_profile"),
            "size": img.size,
        }
    return frames, durations, info


def save_frames(frames, durations, info, target: Path, fmt: str, quality: int) -> None:
    """Write an animation into the claimed ``target``."""
    options: dict[str, Any] = {
        "save_all": True,
        "append_images": frames[1:],
        "duration": durations,
        "loop": info["loop"],
    }
    if fmt in LOSSY:
        options["quality"] = int(quality)
    if info.get("exif"):
        options["exif"] = info["exif"]
    if info.get("icc_profile"):
        options["icc_profile"] = info["icc_profile"]
    try:
        with open(target, "wb") as handle:
            frames[0].save(handle, format=fmt, **options)
    except Exception as exc:
        raise RunnerFailed(f"cannot write an animated {fmt}: {exc}") from None


def _only_first_frame(source: Path) -> tuple[str, dict[str, int]]:
    """The message note and result data for a tool that edits one frame.

    Strip, set, redact, edit and crop work on a single picture; an animated
    source gives them its first frame, and the message must say so.
    """
    count = frame_count(source)
    if count < 2:
        return "", {}
    return (
        f" {source.name} has {count} frames; Peel used only the first frame.",
        {"source_frames": count},
    )


def _first_frame_note(source: Path, count: int, fmt: str, why: str = "") -> str:
    reason = why or f"{fmt} here holds one picture"
    return (
        f" {source.name} has {count} frames and {reason}, "
        "so Peel kept only the first frame."
    )


# --- conversions ------------------------------------------------------------


def convert(job: Job) -> Result:
    """Any image (SVG included) to any of the SPEC.md output formats.

    An animated source keeps every frame when the target can animate (WebP);
    otherwise the first frame is converted and the message says so.
    """
    source = job.source
    target = job.output_path()
    ext = naming.normalise_ext(target.suffix)
    fmt = FORMATS[ext]
    count = frame_count(source)
    note = ""
    if count > 1:
        if fmt in ANIMATED:
            loaded = load_frames(source)
            if loaded is not None:
                frames, durations, info = loaded
                save_frames(frames, durations, info, target, fmt, QUALITY)
                return ok([target], f"Wrote {target.name} with all {count} frames",
                          frames=count)
            note = _first_frame_note(source, count, fmt, "they are too large to keep together")
        else:
            note = _first_frame_note(source, count, fmt)
    img = load(source)
    try:
        save(
            img,
            target,
            ext,
            quality=QUALITY,
            exif=carry_exif(img),
            icc_profile=img.info.get("icc_profile"),
        )
    finally:
        img.close()
    return ok([target], f"Wrote {target.name}.{note}" if note else f"Wrote {target.name}",
              frames=1, source_frames=count)


# --- tools ------------------------------------------------------------------


def compress(job: Job) -> Result:
    """Re-encode smaller, optionally capping the longest side first."""
    source = job.source
    quality = job.params["quality"]
    cap = job.params["max_dimension"]
    target = job.output_path()
    ext = naming.normalise_ext(target.suffix)
    count = frame_count(source)
    note = ""
    if count > 1:
        if FORMATS[ext] in ANIMATED:
            loaded = load_frames(source, cap)
            if loaded is not None:
                frames, durations, info = loaded
                save_frames(frames, durations, info, target, FORMATS[ext], quality)
                resized = frames[0].size != info["size"]
                return _compressed(source, target, ext, frames[0].size,
                                   f" All {count} frames kept.", resized)
            note = _first_frame_note(source, count, FORMATS[ext],
                                     "they are too large to keep together")
        else:
            note = _first_frame_note(source, count, FORMATS[ext],
                                     "Peel cannot write an animated "
                                     f"{FORMATS[ext]}")
    img = load(source)
    try:
        smaller = fit_within(img, cap) if cap else img
        save(
            smaller,
            target,
            ext,
            quality=quality,
            exif=carry_exif(img),
            icc_profile=img.info.get("icc_profile"),
        )
        size = smaller.size
        resized = smaller is not img
    finally:
        img.close()
    return _compressed(source, target, ext, size, note, resized)


def _compressed(source: Path, target: Path, ext: str, size, note: str,
                resized: bool = False) -> Result:
    """The Compress result, honest when the re-encode is not smaller.

    When the output is not smaller and the picture was not resized and kept
    its format, the target gets the source's bytes unchanged (as PDF compress
    does), so Compress never hands back a bigger file than it was given.
    """
    import shutil

    before = source.stat().st_size
    after = target.stat().st_size
    same_format = naming.normalise_ext(source.suffix) == ext
    if after >= before and not resized and same_format:
        with open(source, "rb") as src, open(target, "wb") as out:
            shutil.copyfileobj(src, out)
        after = target.stat().st_size
        message = (
            f"{source.name} is not smaller when re-encoded, so Peel kept the "
            f"original bytes ({_kb(before)})"
        )
        # The original bytes hold every frame, so no frame note applies.
        note = ""
    elif after >= before:
        message = f"{_kb(before)} to {_kb(after)}, not smaller"
    elif FORMATS[ext] in LOSSY:
        saved = 100 - round(after * 100 / before) if before else 0
        message = f"{_kb(before)} to {_kb(after)}, {saved}% smaller"
    else:
        message = (
            f"{_kb(before)} to {_kb(after)}. {FORMATS[ext]} is lossless, so "
            "quality does nothing here; cap the longest side to shrink it."
        )
    return ok(
        [target],
        message + note,
        bytes_before=before,
        bytes_after=after,
        width=size[0],
        height=size[1],
    )


def fit_within(img, longest: int):
    """Scale down so the longer side is ``longest``. Never scales up."""
    Image = pillow()
    width, height = img.size
    if max(width, height) <= longest:
        return img
    scale = longest / max(width, height)
    size = (max(1, round(width * scale)), max(1, round(height * scale)))
    return img.resize(size, Image.Resampling.LANCZOS)


def crop(job: Job) -> Result:
    """Cut out one rectangle. The editor supplies it in image pixels."""
    source = job.source
    x, y, w, h = (job.params[key] for key in ("x", "y", "w", "h"))
    note, extra = _only_first_frame(source)
    img = load(source)
    try:
        width, height = img.size
        if x + w > width or y + h > height:
            raise InvalidParams(
                f"the crop box {w} by {h} at {x},{y} does not fit inside the "
                f"{width} by {height} image"
            )
        target = job.output_path()
        save(
            img.crop((x, y, x + w, y + h)),
            target,
            naming.normalise_ext(target.suffix),
            quality=QUALITY,
            exif=carry_exif(img),
        )
    finally:
        img.close()
    return ok([target], f"Cropped to {w} by {h}.{note}" if note else f"Cropped to {w} by {h}",
              width=w, height=h, **extra)


def edit(job: Job) -> Result:
    """Rotate, flip and adjust brightness, contrast and saturation."""
    Image = pillow()
    from PIL import ImageEnhance

    source = job.source
    rotate = job.params["rotate"]
    flip = job.params["flip"]
    adjustments = (
        (ImageEnhance.Brightness, job.params["brightness"], "brightness"),
        (ImageEnhance.Contrast, job.params["contrast"], "contrast"),
        (ImageEnhance.Color, job.params["saturation"], "saturation"),
    )
    clockwise = {
        90: Image.Transpose.ROTATE_270,
        180: Image.Transpose.ROTATE_180,
        270: Image.Transpose.ROTATE_90,
    }
    flips = {
        "horizontal": Image.Transpose.FLIP_LEFT_RIGHT,
        "vertical": Image.Transpose.FLIP_TOP_BOTTOM,
    }

    note, extra = _only_first_frame(source)
    img = load(source)
    try:
        out = img
        done: list[str] = []
        if rotate:
            out = out.transpose(clockwise[rotate])
            done.append(f"rotated {rotate} degrees")
        if flip != "none":
            out = out.transpose(flips[flip])
            done.append(f"flipped {flip}ly")
        if any(amount != 1.0 for _enhancer, amount, _name in adjustments):
            # ImageEnhance wants a continuous-tone mode, not a palette.
            out = _rgb_or_rgba(out)
            for enhancer, amount, name in adjustments:
                if amount != 1.0:
                    out = enhancer(out).enhance(amount)
                    done.append(f"{name} {amount:g}")
        target = job.output_path()
        save(
            out,
            target,
            naming.normalise_ext(target.suffix),
            quality=QUALITY,
            exif=carry_exif(img),
        )
    finally:
        img.close()
    message = ", ".join(done).capitalize() if done else "Saved unchanged"
    return ok([target], f"{message}.{note}" if note else message, changes=done, **extra)


def annotate(job: Job) -> Result:
    """Draw pen strokes, arrows, boxes and text over the image."""
    Image = pillow()
    from PIL import ImageDraw

    source = job.source
    shapes = _check_shapes(job.params["shapes"])

    img = load(source)
    try:
        base = to_rgba(img)
        shapes = [fit for fit in (_fit_shape(shape, base.size) for shape in shapes) if fit is not None]
        overlay = Image.new("RGBA", base.size, (0, 0, 0, 0))
        draw = ImageDraw.Draw(overlay)
        for shape in shapes:
            _draw_shape(draw, shape)
        merged = Image.alpha_composite(base, overlay)
        target = job.output_path()
        save(
            merged,
            target,
            naming.normalise_ext(target.suffix),
            quality=QUALITY,
            exif=carry_exif(img),
        )
    finally:
        img.close()
    return ok([target], f"Drew {len(shapes)} shapes", shapes_drawn=len(shapes))


SHAPE_TYPES = ("pen", "arrow", "rect", "text")


def _check_shapes(raw: Any) -> list[dict[str, Any]]:
    """Validate and normalise the editor's shape list before anything is claimed."""
    if not isinstance(raw, (list, tuple)) or not raw:
        raise InvalidParams("Annotate needs a list of at least one shape")
    shapes = []
    for index, shape in enumerate(raw, start=1):
        if not isinstance(shape, dict):
            raise InvalidParams(f"shape {index} is not an object: {shape!r}")
        kind = str(shape.get("type", "")).lower()
        if kind not in SHAPE_TYPES:
            allowed = ", ".join(SHAPE_TYPES)
            raise InvalidParams(
                f"shape {index}: unknown shape type {shape.get('type')!r}. "
                f"Accepted types: {allowed}"
            )
        clean: dict[str, Any] = {
            "type": kind,
            "color": _colour(shape.get("color") or DEFAULT_INK, f"shape {index} colour"),
            "width": max(1, _whole(shape, "width", 3, index)),
        }
        if kind == "pen":
            clean["points"] = _points(shape.get("points"), index)
        elif kind == "arrow":
            clean["from"] = _point(shape.get("from"), index, "from")
            clean["to"] = _point(shape.get("to"), index, "to")
        elif kind == "rect":
            clean["box"] = _box(shape, index)
            fill = shape.get("fill")
            clean["fill"] = _colour(fill, f"shape {index} fill") if fill else None
        else:
            clean["at"] = _point([shape.get("x"), shape.get("y")], index, "position")
            clean["text"] = str(shape.get("text", ""))
            clean["size"] = max(1, _whole(shape, "size", 16, index))
            if not clean["text"]:
                raise InvalidParams(f"shape {index}: a text shape needs some text")
        shapes.append(clean)
    return shapes


def _fit_shape(shape: dict[str, Any], size: tuple[int, int]) -> dict[str, Any] | None:
    """Clamp a shape's line width and text size to the image.

    Returns None for a box shape whose box does not touch the image at all:
    pillow draws a box outline inside the box, so nothing of it would show,
    and clamping it would squash it onto the image edge (hq-plct.2 review 3).

    Nothing wider than the image's diagonal can show more than a stroke that
    covers it, and pillow draws a wide outline one pixel ring at a time, so
    an absurd width (10**9) would otherwise hang the run.
    """
    diagonal = max(1, math.ceil(math.hypot(*size)))
    fitted = dict(shape)
    fitted["width"] = min(shape["width"], diagonal)
    if "size" in shape:
        fitted["size"] = min(shape["size"], diagonal)
    if "box" in shape:
        # A box far bigger than the image (x=-1e9, w=2e9) makes pillow walk
        # every outline ring of it. Clamping each edge to the image grown by
        # the line width draws the same pixels: an edge pushed out by the
        # margin puts its whole outline off the image, as it was before.
        margin = fitted["width"]
        width, height = size
        left, top, right, bottom = shape["box"]
        lo_x, hi_x = min(left, right), max(left, right)
        lo_y, hi_y = min(top, bottom), max(top, bottom)
        if hi_x < 0 or hi_y < 0 or lo_x > width - 1 or lo_y > height - 1:
            return None
        fitted["box"] = (
            min(max(left, -margin), width + margin),
            min(max(top, -margin), height + margin),
            min(max(right, -margin), width + margin),
            min(max(bottom, -margin), height + margin),
        )
    return fitted


def _whole(shape: dict[str, Any], key: str, default: int, index: int) -> int:
    value = shape.get(key, default)
    try:
        return int(value)
    except (TypeError, ValueError):
        raise InvalidParams(f"shape {index}: {key} must be a whole number, got {value!r}") from None


def _point(raw: Any, index: int, what: str) -> tuple[int, int]:
    if isinstance(raw, dict):
        raw = [raw.get("x"), raw.get("y")]
    if not isinstance(raw, (list, tuple)) or len(raw) != 2:
        raise InvalidParams(f"shape {index}: {what} must be an x,y pair, got {raw!r}")
    try:
        return (int(raw[0]), int(raw[1]))
    except (TypeError, ValueError):
        raise InvalidParams(f"shape {index}: {what} must be two numbers, got {raw!r}") from None


def _points(raw: Any, index: int) -> list[tuple[int, int]]:
    if not isinstance(raw, (list, tuple)) or len(raw) < 2:
        raise InvalidParams(f"shape {index}: a pen stroke needs at least two points")
    return [_point(point, index, "point") for point in raw]


def _box(shape: dict[str, Any], index: int) -> tuple[int, int, int, int]:
    x, y = _point([shape.get("x"), shape.get("y")], index, "position")
    w = _whole(shape, "w", 0, index)
    h = _whole(shape, "h", 0, index)
    if w < 1 or h < 1:
        raise InvalidParams(f"shape {index}: a box needs a width and a height above zero")
    return (x, y, x + w - 1, y + h - 1)


def _draw_shape(draw, shape: dict[str, Any]) -> None:
    kind = shape["type"]
    if kind == "pen":
        draw.line(shape["points"], fill=shape["color"], width=shape["width"], joint="curve")
    elif kind == "arrow":
        _draw_arrow(draw, shape)
    elif kind == "rect":
        draw.rectangle(
            shape["box"], outline=shape["color"], width=shape["width"], fill=shape["fill"]
        )
    else:
        from PIL import ImageFont

        draw.text(
            shape["at"],
            shape["text"],
            fill=shape["color"],
            font=ImageFont.load_default(size=shape["size"]),
        )


def _draw_arrow(draw, shape: dict[str, Any]) -> None:
    (x1, y1), (x2, y2) = shape["from"], shape["to"]
    width = shape["width"]
    draw.line([(x1, y1), (x2, y2)], fill=shape["color"], width=width)
    head = max(width * 3, 8)
    angle = math.atan2(y2 - y1, x2 - x1)
    spread = 0.45
    wings = [
        (x2 - head * math.cos(angle - spread), y2 - head * math.sin(angle - spread)),
        (x2 - head * math.cos(angle + spread), y2 - head * math.sin(angle + spread)),
    ]
    draw.polygon([(x2, y2), *wings], fill=shape["color"])


#: The smallest pixelation block image.redact uses, in pixels. Text set at
#: ordinary sizes has strokes and counters well under this, so a block this
#: big averages a glyph away. A region smaller than one block becomes a single
#: averaged block.
REDACT_MIN_BLOCK = 12


def redact(job: Job) -> Result:
    """Hide regions for good, with a blur that cannot be undone or a black box.

    The output carries no metadata: no EXIF, XMP, JPEG comment or text chunk.
    An image redacted in the pixels but still holding the original EXIF
    (place, time, embedded thumbnail) or a comment naming the subject is not
    redacted. The ICC colour profile is the one thing kept, because it is not
    personal data and without it the colours shift.
    """
    from PIL import ImageDraw, ImageFilter

    Image = pillow()
    source = job.source
    mode = job.params["mode"]
    wanted = _regions(job.params["regions"])
    note, extra = _only_first_frame(source)

    img = load(source)
    try:
        out = _rgb_or_rgba(img).copy()
        width, height = out.size
        boxes = [box for box in (_clamp(box, width, height) for box in wanted) if box]
        if not boxes:
            raise InvalidParams(
                f"none of the {len(wanted)} regions overlap the {width} by {height} image"
            )
        black = (0, 0, 0) if out.mode == "RGB" else (0, 0, 0, 255)
        for box in boxes:
            if mode == "box":
                ImageDraw.Draw(out).rectangle(box, fill=black)
                continue
            left, top, right, bottom = box
            region = out.crop(box)
            span_w, span_h = right - left, bottom - top
            # The block size and the blur follow the LARGER side, with a floor.
            # Taken from the shorter side, a 600 by 20 line of text got 3
            # pixel cells and a 3 pixel blur, and its letters stayed legible.
            # Six blocks across the long side and at least REDACT_MIN_BLOCK
            # pixels each leave nothing glyph sized in any direction.
            step = max(REDACT_MIN_BLOCK, max(span_w, span_h) // 6)
            small = region.resize(
                (max(1, span_w // step), max(1, span_h // step)), Image.Resampling.BOX
            )
            mosaic = small.resize((span_w, span_h), Image.Resampling.NEAREST)
            out.paste(mosaic.filter(ImageFilter.GaussianBlur(radius=max(2, step))), box)
        target = job.output_path()
        save(
            bare(out),
            target,
            naming.normalise_ext(target.suffix),
            quality=QUALITY,
            icc_profile=img.info.get("icc_profile"),
        )
        noun = "region" if len(boxes) == 1 else "regions"
        message = f"Hid {len(boxes)} {noun} with a {mode}"
        return ok([target], f"{message}.{note}" if note else message,
                  regions=len(boxes), **extra)
    finally:
        img.close()


def _regions(raw: Any) -> list[tuple[int, int, int, int]]:
    if not isinstance(raw, (list, tuple)) or not raw:
        raise InvalidParams("Redact needs a list of at least one region to hide")
    boxes = []
    for index, region in enumerate(raw, start=1):
        if isinstance(region, (list, tuple)) and len(region) == 4:
            region = dict(zip(("x", "y", "w", "h"), region))
        if not isinstance(region, dict):
            raise InvalidParams(f"region {index} is not an x,y,w,h object: {region!r}")
        try:
            x, y, w, h = (int(region[key]) for key in ("x", "y", "w", "h"))
        except (KeyError, TypeError, ValueError):
            raise InvalidParams(
                f"region {index} needs whole numbers for x, y, w and h, got {region!r}"
            ) from None
        if w < 1 or h < 1:
            raise InvalidParams(f"region {index} has no area: {region!r}")
        boxes.append((x, y, x + w, y + h))
    return boxes


def _clamp(box: tuple[int, int, int, int], width: int, height: int):
    left, top, right, bottom = box
    left, top = max(0, left), max(0, top)
    right, bottom = min(width, right), min(height, bottom)
    if right - left < 1 or bottom - top < 1:
        return None
    return (left, top, right, bottom)


def background(job: Job) -> Result:
    """Fill transparency with a solid colour, for a logo dropped on a slide."""
    Image = pillow()
    source = job.source
    colour = _colour(job.params["color"])

    img = load(source)
    try:
        rgba = to_rgba(img)
        had = rgba.getchannel("A").getextrema()[0] < 255
        base = Image.new("RGBA", rgba.size, colour[:3] + (255,))
        flat = Image.alpha_composite(base, rgba).convert("RGB")
        target = job.output_path()
        save(
            flat,
            target,
            naming.normalise_ext(target.suffix),
            quality=QUALITY,
            exif=carry_exif(img),
        )
    finally:
        img.close()
    message = (
        f"Filled the transparency with {job.params['color']}"
        if had
        else f"Nothing was transparent, so {target.name} is the same picture on {job.params['color']}"
    )
    return ok([target], message, had_transparency=had)


def collage(job: Job) -> Result:
    """Lay several images out in a grid, each centred in a cell.

    The cell is the size of the largest input and nothing is scaled, so a
    collage never softens a photo to make it fit.
    """
    Image = pillow()
    columns = job.params["columns"]
    gap = job.params["gap"]
    colour = _colour(job.params["background"])

    images: list[Any] = []
    try:
        for path in job.inputs:
            images.append(load(path))
        count = len(images)
        columns = max(1, min(columns or math.ceil(math.sqrt(count)), count))
        rows = math.ceil(count / columns)
        cell_w = max(img.width for img in images)
        cell_h = max(img.height for img in images)
        canvas = Image.new(
            "RGBA",
            (gap + columns * (cell_w + gap), gap + rows * (cell_h + gap)),
            colour[:3] + (255,),
        )
        for index, img in enumerate(images):
            tile = to_rgba(img)
            column, row = index % columns, index // columns
            canvas.alpha_composite(
                tile,
                (
                    gap + column * (cell_w + gap) + (cell_w - tile.width) // 2,
                    gap + row * (cell_h + gap) + (cell_h - tile.height) // 2,
                ),
            )
        target = job.output_path()
        save(canvas, target, "png")
    finally:
        for img in images:
            img.close()
    return ok(
        [target],
        f"{count} images in a {columns} by {rows} grid",
        columns=columns,
        rows=rows,
        count=count,
    )


def create_pdf(job: Job) -> Result:
    """One PDF with a page per image, each page the shape of its image."""
    images: list[Any] = []
    try:
        for path in job.inputs:
            images.append(load(path))
        target = job.output_path()
        save(images[0], target, "pdf", pages=images[1:])
    finally:
        for img in images:
            img.close()
    pages = len(images)
    return ok([target], f"{pages} pages in {target.name}", pages=pages)


# --- metadata ---------------------------------------------------------------

#: EXIF tags Peel writes. XP* are the Windows Explorer "Title"/"Authors"
#: columns, stored as UTF-16; the plain tags are what everything else reads.
TAG_DESCRIPTION = 0x010E
TAG_ARTIST = 0x013B
TAG_XP_TITLE = 0x9C9B
TAG_XP_AUTHOR = 0x9C9D
TAG_GPS_IFD = 0x8825
TAG_XMP = 700


def metadata(job: Job) -> Result:
    """View, strip or set the metadata of one image.

    ``view`` writes nothing: the editor calls it to fill the form, then calls
    back with ``set`` or ``strip``.

    ``strip`` removes EXIF, XMP, comments and text chunks, and keeps the ICC
    colour profile on purpose: it describes the colours, not the person, and
    without it a wide gamut photo shifts colour. Because the Orientation tag
    goes too, the pixels are turned upright first, so a phone photo still
    displays the way it did. ``set`` re-encodes a JPEG with its own
    quantisation tables (quality "keep"), so changing a title costs nothing.
    """
    action = job.params["action"]
    source = job.source
    # Deliberately not load(): the stored orientation is part of what the
    # editor shows, and setting a title must not silently rotate the file.
    img = _open_raster(source)
    try:
        if action == "view":
            return ok([], f"Read the metadata of {source.name}", metadata=_read(img, source))
        ext = naming.normalise_ext(source.suffix)
        note, extra = _only_first_frame(source)
        if action == "strip":
            from PIL import ImageOps

            upright = ImageOps.exif_transpose(img) or img
            clean = bare(upright)
            target = job.output_path()
            save(clean, target, ext, quality=QUALITY,
                 icc_profile=img.info.get("icc_profile"))
            return ok(
                [target],
                f"Removed every tag from {target.name} (the colour profile is kept).{note}"
                if note else
                f"Removed every tag from {target.name} (the colour profile is kept)",
                **extra,
            )
        exif = _edited_exif(img, job.params)
        if job.params.get("clear_location"):
            _drop_xmp_location(img)
        target = job.output_path()
        save(
            img,
            target,
            ext,
            quality="keep" if img.format == "JPEG" else QUALITY,
            exif=exif,
            icc_profile=img.info.get("icc_profile"),
        )
        if job.params.get("clear_location") and _location_left(target):
            target.unlink(missing_ok=True)
            raise RunnerFailed(
                f"the location could not be removed from {source.name}; nothing was written"
            )
        message = f"Wrote the new metadata into {target.name}"
        return ok([target], f"{message}.{note}" if note else message, **extra)
    finally:
        img.close()


def _edited_exif(img, params: dict[str, Any]):
    title = params["title"]
    author = params["author"]
    latitude = params["latitude"]
    longitude = params["longitude"]
    # set keeps every tag it is not given, so without this a user who empties
    # the location boxes in the editor would save a file that still says where
    # it was taken. clear_location drops the GPS directory and nothing else.
    clear_location = bool(params.get("clear_location"))
    if (latitude is None) != (longitude is None):
        raise InvalidParams("a location needs both a latitude and a longitude")
    if clear_location and latitude is not None:
        raise InvalidParams("clear_location and a new location contradict each other; send one")
    if not title and not author and latitude is None and not clear_location:
        raise InvalidParams("give a title, an author, a location or clear_location to set")

    exif = img.getexif()
    if clear_location and TAG_GPS_IFD in exif:
        del exif[TAG_GPS_IFD]
    if clear_location and TAG_XMP in exif:
        # A TIFF keeps its XMP packet in IFD0, so getexif() hands it back and
        # it would be written out again with exif:GPSLatitude still inside.
        packet = _xmp_without_location(exif[TAG_XMP])
        if packet is None:
            del exif[TAG_XMP]
        else:
            exif[TAG_XMP] = packet
    if title:
        exif[TAG_DESCRIPTION] = title
        exif[TAG_XP_TITLE] = title.encode("utf-16-le") + b"\x00\x00"
    if author:
        exif[TAG_ARTIST] = author
        exif[TAG_XP_AUTHOR] = author.encode("utf-16-le") + b"\x00\x00"
    if latitude is not None:
        gps = exif.get_ifd(TAG_GPS_IFD)
        gps[1] = "N" if latitude >= 0 else "S"
        gps[2] = _dms(abs(latitude))
        gps[3] = "E" if longitude >= 0 else "W"
        gps[4] = _dms(abs(longitude))
    try:
        return exif_bytes(exif)
    except Exception as exc:
        raise RunnerFailed(f"that metadata cannot be written: {exc}") from None


# Any XMP property named GPS<something> (exif:GPSLatitude, exifEX:GPS...), as
# an attribute or as an element with or without content.
_XMP_GPS_ATTRIBUTE = rb"""\s[\w.-]+:GPS[\w.-]*\s*=\s*(?:"[^"]*"|'[^']*')"""
_XMP_GPS_ELEMENT = (
    rb"<([\w.-]+:GPS[\w.-]*)\b[^>]*/>"
    rb"|<([\w.-]+:GPS[\w.-]*)\b[^>]*>.*?</\2\s*>"
)


def _xmp_without_location(value: Any) -> bytes | None:
    """The XMP packet with every GPS property taken out, None to drop it whole.

    Scrubbing keeps the rest of the packet (an XMP orientation, a rating); when
    the packet is not bytes or a GPS mention survives the scrub, dropping it is
    the only answer that cannot leave the place behind.
    """
    import re

    if isinstance(value, str):
        value = value.encode("utf-8")
    if not isinstance(value, bytes):
        return None
    packet = re.sub(_XMP_GPS_ELEMENT, b"", value, flags=re.DOTALL)
    packet = re.sub(_XMP_GPS_ATTRIBUTE, b"", packet)
    return None if b"GPS" in packet else packet


def _drop_xmp_location(img) -> None:
    """Scrub the XMP copies pillow would write back on its own.

    Pillow's TIFF writer copies tag 700 from the source's own directory
    (``tag_v2``, which the legacy ``tag`` view shares) and other writers fall
    back to ``info["xmp"]``, whatever EXIF blob ``save`` is handed.
    """
    if "xmp" in img.info:
        packet = _xmp_without_location(img.info["xmp"])
        if packet is None:
            del img.info["xmp"]
        else:
            img.info["xmp"] = packet
    for directory in (getattr(img, "tag_v2", None), getattr(img, "tag", None)):
        if directory is None or TAG_XMP not in directory:
            continue
        value = directory[TAG_XMP]
        if isinstance(value, tuple) and len(value) == 1:
            value = value[0]
        packet = _xmp_without_location(value)
        if packet is None:
            del directory[TAG_XMP]
        else:
            directory[TAG_XMP] = packet


def _location_left(target: Path) -> bool:
    """True when the written file still says where it was taken, anywhere."""
    Image = pillow()
    with Image.open(target) as out:
        exif = out.getexif()
        if dict(exif.get_ifd(TAG_GPS_IFD)):
            return True
        for packet in (exif.get(TAG_XMP), out.info.get("xmp"), out.info.get("XML:com.adobe.xmp")):
            if isinstance(packet, str):
                packet = packet.encode("utf-8")
            if isinstance(packet, bytes) and b"GPS" in packet:
                return True
    return False


def _dms(value: float) -> tuple[float, float, float]:
    """Decimal degrees to the degrees, minutes, seconds triple EXIF wants.

    Split from whole seconds, not by taking fractions of the degrees: binary
    floats turn a clean 116.6 into 35 minutes and 60 seconds that way, and 60
    is not a second a strict EXIF reader accepts.
    """
    whole_seconds = round(value * 3600, 2)
    degrees, rest = divmod(whole_seconds, 3600)
    minutes, seconds = divmod(rest, 60)
    return (float(degrees), float(minutes), round(seconds, 4))


def _decimal(dms: Any, ref: Any) -> float | None:
    """The degrees, minutes, seconds triple back to signed decimal degrees."""
    if not dms or len(dms) != 3:
        return None
    try:
        degrees, minutes, seconds = (float(part) for part in dms)
    except (TypeError, ValueError):
        return None
    value = degrees + minutes / 60 + seconds / 3600
    return -value if str(ref).upper() in ("S", "W") else value


def _read(img, source: Path) -> dict[str, Any]:
    from PIL import ExifTags

    exif = img.getexif()
    tags = {
        ExifTags.TAGS.get(tag, f"0x{tag:04x}"): _printable(value)
        for tag, value in exif.items()
    }
    gps = exif.get_ifd(TAG_GPS_IFD)
    return {
        "file": source.name,
        "format": img.format,
        "mode": img.mode,
        "width": img.width,
        "height": img.height,
        "title": _text(exif.get(TAG_DESCRIPTION)) or _xp(exif.get(TAG_XP_TITLE)),
        "author": _text(exif.get(TAG_ARTIST)) or _xp(exif.get(TAG_XP_AUTHOR)),
        "latitude": _decimal(gps.get(2), gps.get(1)),
        "longitude": _decimal(gps.get(4), gps.get(3)),
        "exif": tags,
    }


def _printable(value: Any) -> Any:
    """A JSON safe, bounded form of one EXIF value for the editor to show."""
    if isinstance(value, bytes):
        return f"{len(value)} bytes"
    if isinstance(value, (int, float, str)):
        return value if not isinstance(value, str) else value[:200]
    return str(value)[:200]


def _text(value: Any) -> str | None:
    if isinstance(value, bytes):
        value = value.decode("utf-8", "replace")
    text = str(value).strip().strip("\x00") if value is not None else ""
    return text or None


def _xp(value: Any) -> str | None:
    """Decode a Windows XP* EXIF tag, which is UTF-16 in a byte string."""
    if value is None:
        return None
    if isinstance(value, (tuple, list)):
        value = bytes(bytearray(int(b) & 0xFF for b in value))
    if not isinstance(value, bytes):
        return _text(value)
    return value.decode("utf-16-le", "replace").strip("\x00").strip() or None
