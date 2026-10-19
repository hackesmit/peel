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
#: flattened onto white rather than failing.
NO_ALPHA = frozenset({"JPEG", "PDF"})

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
        pixmap = page.get_pixmap(matrix=fitz.Matrix(zoom, zoom), alpha=True)
        return pixmap.tobytes("png")


def _svg_via_magick(path: Path) -> bytes:
    magick = backends.require("magick")
    try:
        proc = subprocess.run(
            [str(magick), str(path), "-background", "none", "png:-"],
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
    quality: int | None = None,
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
        options["quality"] = int(quality)
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
    if img.mode != "RGB":
        # Odd source modes (CMYK, 16 bit, YCbCr) that a given encoder refuses.
        attempts.append((lambda: img.convert("RGB"), options))

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
    return Image.alpha_composite(base, img.convert("RGBA")).convert("RGB")


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
    return img.convert("RGBA" if _has_alpha(img) else "RGB")


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


# --- conversions ------------------------------------------------------------


def convert(job: Job) -> Result:
    """Any image (SVG included) to any of the SPEC.md output formats."""
    source = job.source
    target = job.output_path()
    img = load(source)
    try:
        save(
            img,
            target,
            naming.normalise_ext(target.suffix),
            quality=QUALITY,
            exif=carry_exif(img),
            icc_profile=img.info.get("icc_profile"),
        )
    finally:
        img.close()
    return ok([target], f"Wrote {target.name}")


# --- tools ------------------------------------------------------------------


def compress(job: Job) -> Result:
    """Re-encode smaller, optionally capping the longest side first."""
    source = job.source
    quality = job.params["quality"]
    cap = job.params["max_dimension"]
    target = job.output_path()
    ext = naming.normalise_ext(target.suffix)
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
    finally:
        img.close()

    before = source.stat().st_size
    after = target.stat().st_size
    if FORMATS[ext] in LOSSY:
        saved = 100 - round(after * 100 / before) if before else 0
        message = f"{_kb(before)} to {_kb(after)}, {saved}% smaller"
    else:
        message = (
            f"{_kb(before)} to {_kb(after)}. {FORMATS[ext]} is lossless, so "
            "quality does nothing here; cap the longest side to shrink it."
        )
    return ok(
        [target],
        message,
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
    return ok([target], f"Cropped to {w} by {h}", width=w, height=h)


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
    return ok([target], ", ".join(done).capitalize() if done else "Saved unchanged",
              changes=done)


def annotate(job: Job) -> Result:
    """Draw pen strokes, arrows, boxes and text over the image."""
    Image = pillow()
    from PIL import ImageDraw

    source = job.source
    shapes = _check_shapes(job.params["shapes"])

    img = load(source)
    try:
        base = img.convert("RGBA")
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


def redact(job: Job) -> Result:
    """Hide regions for good, with a blur that cannot be undone or a black box.

    The output carries no metadata. An image redacted in the pixels but still
    holding the original EXIF (place, time, embedded thumbnail) is not redacted.
    """
    from PIL import ImageDraw, ImageFilter

    Image = pillow()
    source = job.source
    mode = job.params["mode"]
    wanted = _regions(job.params["regions"])

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
            step = max(2, min(span_w, span_h) // 6)
            small = region.resize(
                (max(1, span_w // step), max(1, span_h // step)), Image.Resampling.BOX
            )
            mosaic = small.resize((span_w, span_h), Image.Resampling.NEAREST)
            out.paste(mosaic.filter(ImageFilter.GaussianBlur(radius=max(2, step))), box)
        target = job.output_path()
        save(out, target, naming.normalise_ext(target.suffix), quality=QUALITY)
        noun = "region" if len(boxes) == 1 else "regions"
        return ok([target], f"Hid {len(boxes)} {noun} with a {mode}", regions=len(boxes))
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
        rgba = img.convert("RGBA")
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
            tile = img.convert("RGBA")
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


def metadata(job: Job) -> Result:
    """View, strip or set the metadata of one image.

    ``view`` writes nothing: the editor calls it to fill the form, then calls
    back with ``set`` or ``strip``.
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
        if action == "strip":
            clean = img.copy()
            clean.info = {}
            target = job.output_path()
            save(clean, target, ext, quality=QUALITY)
            return ok([target], f"Removed every tag from {target.name}")
        exif = _edited_exif(img, job.params)
        target = job.output_path()
        save(
            img,
            target,
            ext,
            quality=QUALITY,
            exif=exif,
            icc_profile=img.info.get("icc_profile"),
        )
        return ok([target], f"Wrote the new metadata into {target.name}")
    finally:
        img.close()


def _edited_exif(img, params: dict[str, Any]):
    title = params["title"]
    author = params["author"]
    latitude = params["latitude"]
    longitude = params["longitude"]
    if (latitude is None) != (longitude is None):
        raise InvalidParams("a location needs both a latitude and a longitude")
    if not title and not author and latitude is None:
        raise InvalidParams("give a title, an author or a location to set")

    exif = img.getexif()
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
