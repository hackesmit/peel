"""Image family: the SPEC.md Images conversion row and the Image file tools.

Every fixture image is generated here at run time (CLAUDE.md: no committed
binary fixtures), and every assertion opens the produced file with a real
decoder rather than trusting the runner's own message: pillow for the raster
formats, PyMuPDF for the PDFs. That is the only way a "wrote a file" claim
means "wrote a valid file".

A format whose backend is not installed on this machine skips rather than
fails, so the suite is honest on a bare box; on a fully provisioned one
nothing skips, which is what the bead receipt is taken from.
"""

from __future__ import annotations

import io
import json
import subprocess
import sys
from pathlib import Path

import pytest

from peel_engine.cli import main
from peel_engine.families import image
from peel_engine.families.image import FORMATS
from peel_engine.registry import CONVERT, TOOL, load_registry
from peel_engine.result import InvalidParams, UnsupportedInput

pytest.importorskip("PIL", reason="the image family needs pillow")

ENGINE_DIR = Path(__file__).resolve().parents[1]

#: The SPEC.md Images row, outputs column: JPG, PNG, WebP, HEIC, TIFF, AVIF,
#: BMP, PDF. Kept as data here so a dropped entry fails loudly.
CONVERSIONS = {
    "image.to_jpg": ("jpg", "JPEG"),
    "image.to_png": ("png", "PNG"),
    "image.to_webp": ("webp", "WEBP"),
    "image.to_heic": ("heic", "HEIF"),
    "image.to_tiff": ("tiff", "TIFF"),
    "image.to_avif": ("avif", "AVIF"),
    "image.to_bmp": ("bmp", "BMP"),
    "image.to_pdf": ("pdf", "PDF"),
}

#: The SPEC.md File tools, Image line.
TOOLS = {
    "image.compress": "Compress",
    "image.crop": "Crop",
    "image.edit": "Edit",
    "image.annotate": "Annotate",
    "image.redact": "Redact",
    "image.background": "Add background",
    "image.collage": "Collage",
    "image.create_pdf": "Create PDF",
    "image.metadata": "Metadata",
}

#: SPEC.md: "Tools that need user input (crop, redact, annotate, edit, ...,
#: metadata) open a small editor window; the rest run immediately."
EDITOR_TOOLS = {
    "image.crop",
    "image.edit",
    "image.annotate",
    "image.redact",
    "image.metadata",
}


# --- helpers ----------------------------------------------------------------


def _writable(ext: str) -> bool:
    """Can this machine encode that format at all? Decides the skips."""
    Image = image.pillow()
    try:
        Image.new("RGB", (8, 8)).save(io.BytesIO(), format=image.FORMATS[ext])
    except Exception:
        return False
    return True


def _readable(ext: str) -> bool:
    Image = image.pillow()
    buffer = io.BytesIO()
    try:
        Image.new("RGB", (8, 8)).save(buffer, format=image.FORMATS[ext])
        buffer.seek(0)
        with Image.open(buffer) as img:
            img.load()
    except Exception:
        return False
    return True


def needs(ext: str) -> None:
    """Skip when this machine has no codec for that format.

    PDF is checked on the write side only: pillow writes a PDF and never reads
    one, so PyMuPDF is what opens the result (see ``pdf_pages``).
    """
    if not _writable(ext):
        pytest.skip(f"this machine cannot write {ext.upper()}")
    if ext != "pdf" and not _readable(ext):
        pytest.skip(f"this machine cannot read {ext.upper()}")


def entries(options: dict, family: str) -> dict[str, dict]:
    for wheel in options["wheels"]:
        if wheel["family"] == family:
            return {e["id"]: e for group in wheel["groups"] for e in group["entries"]}
    return {}


def refused(result, code: str = "invalid_params") -> str:
    """A runner reports a bad request as a failed Result, not a traceback.

    Only the registry's own gates (unknown action, unsupported input, a param
    that fails its declared type or range) raise; everything a runner decides
    comes back through ``Result``. Both are asserted below, each the way the
    engine actually behaves.
    """
    assert not result.ok, f"expected a refusal, got {result.outputs}"
    assert result.code == code, result.message
    return result.message


def opened(path: Path):
    """Open a produced raster file with pillow, loaded and detached."""
    Image = image.pillow()
    with Image.open(path) as img:
        img.load()
        return img.copy(), img.format


def exif_of(path: Path) -> tuple[dict, dict]:
    """The top level EXIF and the GPS sub-IFD of a produced file.

    Read from the still open image on purpose. A TIFF keeps its tags in the
    file's own IFD rather than in an EXIF blob, so the ``Image.copy()`` that
    ``opened`` returns has lost them, and a test that asked the copy would
    quietly pass on JPEG and quietly fail on TIFF.
    """
    Image = image.pillow()
    with Image.open(path) as img:
        exif = img.getexif()
        return dict(exif), dict(exif.get_ifd(0x8825))


def pdf_pages(path: Path) -> list[tuple[float, float]]:
    """Page sizes of a produced PDF, read by PyMuPDF. Proves it parses."""
    fitz = pytest.importorskip("fitz", reason="reading the PDF back needs PyMuPDF")
    with fitz.open(str(path)) as doc:
        return [(page.rect.width, page.rect.height) for page in doc]


# --- fixtures ---------------------------------------------------------------


@pytest.fixture(scope="module")
def real():
    """The real registry, rebuilt strictly so a broken family cannot hide."""
    return load_registry(force=True, strict=True)


@pytest.fixture
def make_image(tmp_path):
    """Write a deterministic gradient image and return its path.

    A gradient rather than a flat fill: a flat fill compresses to the same few
    bytes at every quality, which would make the compress tests vacuous.
    """

    def _make(
        name: str,
        size: tuple[int, int] = (40, 30),
        mode: str = "RGB",
        exif=None,
        **save,
    ) -> Path:
        Image = image.pillow()
        width, height = size
        img = Image.new(mode, size)
        pixels = img.load()
        for y in range(height):
            for x in range(width):
                red = (x * 7 + y * 3) % 256
                green = (x * 3 + y * 11) % 256
                blue = (x * x + y * y) % 256
                if mode == "RGB":
                    pixels[x, y] = (red, green, blue)
                elif mode == "RGBA":
                    pixels[x, y] = (red, green, blue, 255 if x < width // 2 else 0)
                elif mode == "L":
                    pixels[x, y] = red
                else:  # P and anything else: a palette index is enough
                    pixels[x, y] = red % 8
        if mode == "P":
            # A real palette, so dropping saturation has something to drop.
            img.putpalette(
                [c for n in range(8) for c in ((n * 31) % 256, (n * 77) % 256, (n * 13) % 256)]
                + [0] * (256 - 8) * 3
            )
        path = tmp_path / name
        if exif is not None:
            save["exif"] = exif
        img.save(path, **save)
        return path

    return _make


@pytest.fixture
def make_svg(tmp_path):
    def _make(name: str = "logo.svg", width: int = 40, height: int = 20) -> Path:
        path = tmp_path / name
        path.write_text(
            '<svg xmlns="http://www.w3.org/2000/svg" '
            f'width="{width}" height="{height}" viewBox="0 0 {width} {height}">'
            f'<rect width="{width}" height="{height}" fill="#3366cc"/>'
            f'<circle cx="{width / 4}" cy="{height / 2}" r="{height / 3}" fill="#ffcc00"/>'
            "</svg>",
            encoding="utf-8",
        )
        return path

    return _make


# --- the registry entries ---------------------------------------------------


def test_every_spec_conversion_target_is_a_registry_entry(real):
    for action_id, (ext, _fmt) in CONVERSIONS.items():
        action = real.get(action_id)
        assert action.family == CONVERT
        assert action.group == "Images"
        assert action.output_ext == ext


def test_every_spec_image_tool_is_a_registry_entry(real):
    for action_id, label in TOOLS.items():
        action = real.get(action_id)
        assert action.family == TOOL
        assert action.group == "Images"
        assert action.label == label


def test_only_the_spec_tools_open_an_editor(real):
    opens_editor = {
        a.id for a in real.all() if a.id.startswith("image.") and a.needs_editor
    }
    assert opens_editor == EDITOR_TOOLS


def test_svg_is_an_input_format_only(real):
    assert "image.to_svg" not in real
    assert "svg" in real.get("image.to_png").inputs
    # A tool writes the source format back, so it must not take a vector input.
    assert "svg" not in real.get("image.crop").inputs
    # Collage and Create PDF choose their own output, so they may take one.
    assert "svg" in real.get("image.collage").inputs
    assert "svg" in real.get("image.create_pdf").inputs


def test_a_conversion_never_offers_the_format_the_file_already_is(real, make_image):
    needs("jpg")
    jpg = make_image("a.jpg")
    offered = entries(real.options([jpg]), CONVERT)
    assert "image.to_jpg" not in offered
    assert "image.to_png" in offered
    # The alias counts as the same format.
    assert "jpeg" not in real.get("image.to_jpg").inputs


def test_the_combining_tools_need_more_than_one_file(real):
    for action_id in ("image.collage", "image.create_pdf"):
        action = real.get(action_id)
        assert action.mode == "combine"
        assert action.min_files == 2


def test_the_geometry_tools_take_one_file_at_a_time(real):
    """Crop, Annotate and Redact carry pixel coordinates from one preview."""
    for action_id in ("image.crop", "image.annotate", "image.redact", "image.metadata"):
        assert real.get(action_id).max_files == 1


def test_every_image_runner_resolves(real):
    for action in real.all():
        if action.id.startswith("image."):
            assert callable(action.resolve_runner()), action.id


def test_listing_the_image_entries_imports_no_decoder():
    """The family module must keep pillow and PyMuPDF inside the runners."""
    script = (
        "import sys;"
        "from peel_engine.registry import load_registry;"
        "load_registry().options([__import__('pathlib').Path('a.png')]);"
        "import json; print(json.dumps(sorted(m for m in sys.modules "
        "if m in ('PIL', 'fitz', 'pymupdf', 'pillow_heif'))))"
    )
    proc = subprocess.run(
        [sys.executable, "-c", script],
        cwd=ENGINE_DIR,
        capture_output=True,
        text=True,
        timeout=60,
        check=True,
    )
    assert json.loads(proc.stdout) == []


# --- conversions ------------------------------------------------------------


def source_ext(target_ext: str) -> str:
    """A lossless source format that is not the target.

    A conversion never offers the format the file already is, so a PNG source
    cannot be the input for "to PNG".
    """
    return "bmp" if target_ext == "png" else "png"


@pytest.mark.parametrize("action_id", sorted(CONVERSIONS))
def test_each_conversion_writes_a_file_a_decoder_accepts(real, make_image, action_id):
    ext, fmt = CONVERSIONS[action_id]
    start = source_ext(ext)
    needs(start)
    if ext != "pdf":
        needs(ext)
    source = make_image(f"holiday.{start}", size=(40, 30))

    result = real.run(action_id, [source])

    assert result.ok, result.message
    out = result.outputs[0]
    assert out == source.parent / f"holiday.{ext}"
    assert out.stat().st_size > 0
    if ext == "pdf":
        assert len(pdf_pages(out)) == 1
        width, height = pdf_pages(out)[0]
        assert width > height  # the 40x30 source is landscape
    else:
        img, got = opened(out)
        assert got == fmt
        assert img.size == (40, 30)


@pytest.mark.parametrize("ext", ["jpg", "png", "webp", "heic", "tiff", "avif", "bmp"])
def test_every_spec_input_format_can_be_read(real, make_image, ext):
    needs(ext)
    target_ext = "bmp" if ext == "png" else "png"
    needs(target_ext)
    source = make_image(f"source.{ext}", size=(24, 16))

    result = real.run(f"image.to_{target_ext}", [source])

    assert result.ok, result.message
    img, fmt = opened(result.outputs[0])
    assert (fmt, img.size) == (FORMATS[target_ext], (24, 16))


def test_an_svg_is_rasterised_on_the_way_in(real, make_svg):
    needs("png")
    if image.svg_rasteriser() is None:
        pytest.skip("no SVG rasteriser (PyMuPDF or ImageMagick) on this machine")
    source = make_svg(width=40, height=20)

    result = real.run("image.to_png", [source])

    assert result.ok, result.message
    img, fmt = opened(result.outputs[0])
    assert fmt == "PNG"
    # Rasterised above its natural point size, and the aspect ratio is kept.
    assert img.width >= 40 and img.height >= 20
    assert abs(img.width / img.height - 2.0) < 0.05


def test_a_transparent_source_flattens_for_a_format_without_alpha(real, make_image):
    needs("png")
    needs("jpg")
    source = make_image("badge.png", size=(20, 20), mode="RGBA")

    result = real.run("image.to_jpg", [source])

    assert result.ok, result.message
    img, fmt = opened(result.outputs[0])
    assert fmt == "JPEG"
    assert img.mode == "RGB"


def test_a_batch_converts_every_file(real, make_image):
    needs("png")
    sources = [make_image(f"shot{n}.png", size=(16, 16)) for n in (1, 2, 3)]

    result = real.run("image.to_webp", sources)

    assert result.ok, result.message
    assert sorted(p.name for p in result.outputs) == ["shot1.webp", "shot2.webp", "shot3.webp"]
    for out in result.outputs:
        assert opened(out)[1] == "WEBP"


def test_a_second_run_never_overwrites_the_first(real, make_image):
    needs("png")
    source = make_image("note.png", size=(8, 8))

    first = real.run("image.to_webp", [source])
    second = real.run("image.to_webp", [source])

    assert first.outputs[0].name == "note.webp"
    assert second.outputs[0].name == "note 2.webp"
    assert opened(second.outputs[0])[1] == "WEBP"


def test_a_non_image_is_refused_before_anything_is_written(real, make_file):
    with pytest.raises(UnsupportedInput):
        real.run("image.to_png", [make_file("readme.txt")])


def test_the_exif_orientation_is_applied_and_then_dropped(real, make_image):
    """A phone photo is stored unrotated with an Orientation tag. The converted
    file must look upright without needing the tag, so the tag has to go."""
    needs("jpg")
    needs("png")
    Image = image.pillow()
    exif = Image.Exif()
    exif[0x0112] = 6  # rotate 90 clockwise for display
    source = make_image("tall.jpg", size=(40, 30), exif=exif)

    result = real.run("image.to_png", [source])

    assert result.ok, result.message
    img, _ = opened(result.outputs[0])
    assert img.size == (30, 40)
    assert exif_of(result.outputs[0])[0].get(0x0112) in (None, 1)


def test_a_conversion_carries_the_rest_of_the_metadata_over(real, make_image):
    needs("jpg")
    needs("webp")
    Image = image.pillow()
    exif = Image.Exif()
    exif[0x010E] = "Playa Enkanto"
    source = make_image("beach.jpg", exif=exif)

    result = real.run("image.to_webp", [source])

    assert result.ok, result.message
    assert exif_of(result.outputs[0])[0].get(0x010E) == "Playa Enkanto"


# --- tools ------------------------------------------------------------------


def test_compress_shrinks_the_file_and_keeps_the_format(real, make_image):
    needs("jpg")
    source = make_image("photo.jpg", size=(160, 120), quality=95)

    result = real.run("image.compress", [source], {"quality": 25})

    assert result.ok, result.message
    out = result.outputs[0]
    assert out.name == "photo (Compress).jpg"
    assert out.stat().st_size < source.stat().st_size
    img, fmt = opened(out)
    assert (fmt, img.size) == ("JPEG", (160, 120))
    assert result.data["bytes_after"] == out.stat().st_size


def test_compress_can_cap_the_long_edge(real, make_image):
    needs("jpg")
    source = make_image("photo.jpg", size=(160, 120))

    result = real.run("image.compress", [source], {"max_dimension": 40})

    assert result.ok, result.message
    img, _ = opened(result.outputs[0])
    assert img.size == (40, 30)


def test_compress_never_enlarges_a_small_image(real, make_image):
    needs("png")
    source = make_image("icon.png", size=(16, 16))

    result = real.run("image.compress", [source], {"max_dimension": 500})

    assert result.ok, result.message
    assert opened(result.outputs[0])[0].size == (16, 16)


def test_crop_returns_exactly_the_requested_box(real, make_image):
    needs("png")
    source = make_image("wide.png", size=(40, 30))

    result = real.run("image.crop", [source], {"x": 5, "y": 4, "w": 20, "h": 10})

    assert result.ok, result.message
    out = result.outputs[0]
    assert out.name == "wide (Crop).png"
    img, _ = opened(out)
    assert img.size == (20, 10)
    original, _ = opened(source)
    assert img.getpixel((0, 0)) == original.getpixel((5, 4))


def test_crop_refuses_a_box_outside_the_image(real, make_image):
    needs("png")
    source = make_image("small.png", size=(10, 10))

    message = refused(real.run("image.crop", [source], {"x": 5, "y": 5, "w": 20, "h": 20}))
    assert "does not fit" in message
    # Nothing was claimed, so no placeholder is left next to the source.
    assert sorted(p.name for p in source.parent.iterdir()) == ["small.png"]


def test_crop_refuses_an_empty_box(real, make_image):
    needs("png")
    source = make_image("small.png", size=(10, 10))

    # w has a declared minimum, so this one is refused by the registry gate.
    with pytest.raises(InvalidParams):
        real.run("image.crop", [source], {"x": 0, "y": 0, "w": 0, "h": 5})


def test_edit_rotates_clockwise(real, make_image):
    needs("png")
    source = make_image("page.png", size=(40, 30))

    result = real.run("image.edit", [source], {"rotate": 90})

    assert result.ok, result.message
    img, _ = opened(result.outputs[0])
    assert img.size == (30, 40)
    original, _ = opened(source)
    # The top left pixel of a clockwise rotation comes from the bottom left.
    assert img.getpixel((0, 0)) == original.getpixel((0, 29))


def test_edit_flips_horizontally(real, make_image):
    needs("png")
    source = make_image("page.png", size=(40, 30))

    result = real.run("image.edit", [source], {"flip": "horizontal"})

    assert result.ok, result.message
    img, _ = opened(result.outputs[0])
    original, _ = opened(source)
    assert img.getpixel((0, 0)) == original.getpixel((39, 0))


def test_edit_brightens(real, make_image):
    needs("png")
    source = make_image("dim.png", size=(20, 20))

    result = real.run("image.edit", [source], {"brightness": 2.0})

    assert result.ok, result.message
    img, _ = opened(result.outputs[0])
    original, _ = opened(source)
    assert sum(img.getpixel((3, 3))) > sum(original.getpixel((3, 3)))


def test_edit_adjusts_saturation_without_choking_on_a_palette(real, make_image):
    needs("png")
    source = make_image("flat.png", size=(20, 20), mode="P")

    result = real.run("image.edit", [source], {"saturation": 0.0})

    assert result.ok, result.message
    img, _ = opened(result.outputs[0])
    red, green, blue = img.convert("RGB").getpixel((10, 10))
    assert red == green == blue


def test_annotate_draws_the_shapes_it_is_given(real, make_image):
    needs("png")
    source = make_image("screenshot.png", size=(60, 40))
    shapes = [
        {"type": "rect", "x": 10, "y": 10, "w": 20, "h": 15, "color": "#ff0000", "fill": "#ff0000"},
        {"type": "arrow", "from": [2, 2], "to": [50, 30], "color": "#00ff00", "width": 3},
        {"type": "pen", "points": [[1, 38], [20, 36], [40, 39]], "color": "#0000ff", "width": 2},
        {"type": "text", "x": 32, "y": 2, "text": "aqui", "color": "#ffffff", "size": 12},
    ]

    result = real.run("image.annotate", [source], {"shapes": shapes})

    assert result.ok, result.message
    out = result.outputs[0]
    assert out.name == "screenshot (Annotate).png"
    img, _ = opened(out)
    assert img.convert("RGB").getpixel((20, 17)) == (255, 0, 0)
    assert img.size == (60, 40)
    assert result.data["shapes_drawn"] == 4


def test_annotate_rejects_a_shape_it_does_not_know(real, make_image):
    needs("png")
    source = make_image("screenshot.png")

    message = refused(real.run("image.annotate", [source], {"shapes": [{"type": "hologram"}]}))
    assert "shape" in message


def test_annotate_rejects_a_colour_that_is_not_a_colour(real, make_image):
    needs("png")
    source = make_image("screenshot.png")
    shapes = [{"type": "rect", "x": 1, "y": 1, "w": 4, "h": 4, "color": "octarine"}]

    message = refused(real.run("image.annotate", [source], {"shapes": shapes}))
    assert "colour" in message


def test_annotate_accepts_the_shapes_as_a_json_string(real, make_image):
    """The WPF editor passes --param shapes=@file, so a string must work."""
    needs("png")
    source = make_image("screenshot.png")
    shapes = json.dumps([{"type": "rect", "x": 1, "y": 1, "w": 4, "h": 4}])

    result = real.run("image.annotate", [source], {"shapes": shapes})

    assert result.ok, result.message


def test_redact_with_a_box_blacks_out_only_the_region(real, make_image):
    needs("png")
    source = make_image("id.png", size=(60, 40))
    original, _ = opened(source)

    result = real.run(
        "image.redact",
        [source],
        {"regions": [{"x": 10, "y": 10, "w": 20, "h": 10}], "mode": "box"},
    )

    assert result.ok, result.message
    img, _ = opened(result.outputs[0])
    assert img.convert("RGB").getpixel((20, 15)) == (0, 0, 0)
    assert img.convert("RGB").getpixel((50, 35)) == original.convert("RGB").getpixel((50, 35))


def test_redact_with_a_blur_destroys_the_detail_in_the_region(real, make_image):
    needs("png")
    source = make_image("id.png", size=(60, 40))
    original, _ = opened(source)

    result = real.run(
        "image.redact", [source], {"regions": [{"x": 10, "y": 10, "w": 30, "h": 20}]}
    )

    assert result.ok, result.message
    img, _ = opened(result.outputs[0])
    before = original.convert("RGB")
    after = img.convert("RGB")
    changed = sum(
        1
        for y in range(10, 30)
        for x in range(10, 40)
        if before.getpixel((x, y)) != after.getpixel((x, y))
    )
    assert changed > 300  # most of the 600 pixel region is gone
    assert after.getpixel((55, 35)) == before.getpixel((55, 35))


def test_redact_strips_the_metadata_too(real, make_image):
    """A redacted image that still carries the original EXIF is not redacted."""
    needs("jpg")
    Image = image.pillow()
    exif = Image.Exif()
    exif[0x010E] = "the secret place"
    source = make_image("id.jpg", size=(40, 40), exif=exif)

    result = real.run(
        "image.redact", [source], {"regions": [{"x": 0, "y": 0, "w": 10, "h": 10}]}
    )

    assert result.ok, result.message
    assert exif_of(result.outputs[0]) == ({}, {})


def test_redact_needs_at_least_one_region(real, make_image):
    needs("png")
    refused(real.run("image.redact", [make_image("id.png")], {"regions": []}))


def test_add_background_fills_the_transparency(real, make_image):
    needs("png")
    source = make_image("logo.png", size=(20, 20), mode="RGBA")

    result = real.run("image.background", [source], {"color": "#ff0000"})

    assert result.ok, result.message
    out = result.outputs[0]
    assert out.name == "logo (Add background).png"
    img, _ = opened(out)
    assert img.convert("RGB").getpixel((19, 0)) == (255, 0, 0)
    assert img.convert("RGBA").getpixel((19, 0))[3] == 255


def test_add_background_says_so_when_there_was_no_transparency(real, make_image):
    needs("png")
    source = make_image("solid.png", size=(10, 10), mode="RGB")

    result = real.run("image.background", [source])

    assert result.ok, result.message
    assert result.data["had_transparency"] is False


def test_collage_lays_the_images_out_in_a_grid(real, make_image):
    needs("png")
    first = make_image("a.png", size=(20, 10))
    second = make_image("b.png", size=(10, 20))

    result = real.run(
        "image.collage", [first, second], {"columns": 2, "gap": 4, "background": "#000000"}
    )

    assert result.ok, result.message
    out = result.outputs[0]
    assert out.name == "a (Collage).png"
    img, fmt = opened(out)
    # Cell is the largest tile, 20x20; one row of two cells, gap all round.
    assert (fmt, img.size) == ("PNG", (4 + 20 + 4 + 20 + 4, 4 + 20 + 4))
    assert result.data["columns"] == 2
    assert result.data["rows"] == 1


def test_collage_picks_its_own_grid_when_not_told(real, make_image):
    needs("png")
    sources = [make_image(f"{n}.png", size=(10, 10)) for n in range(4)]

    result = real.run("image.collage", sources)

    assert result.ok, result.message
    assert (result.data["columns"], result.data["rows"]) == (2, 2)


def test_create_pdf_makes_one_document_with_a_page_per_image(real, make_image):
    needs("png")
    sources = [make_image(f"page{n}.png", size=(30, 20)) for n in (1, 2, 3)]

    result = real.run("image.create_pdf", sources)

    assert result.ok, result.message
    out = result.outputs[0]
    assert out.name == "page1 (Create PDF).pdf"
    pages = pdf_pages(out)
    assert len(pages) == 3
    assert all(width > height for width, height in pages)


def test_create_pdf_accepts_mixed_formats(real, make_image):
    needs("png")
    needs("jpg")
    sources = [make_image("a.png", size=(20, 20)), make_image("b.jpg", size=(20, 20))]

    result = real.run("image.create_pdf", sources)

    assert result.ok, result.message
    assert len(pdf_pages(result.outputs[0])) == 2


def test_metadata_reads_without_writing_anything(real, make_image):
    needs("jpg")
    Image = image.pillow()
    exif = Image.Exif()
    exif[0x010E] = "Vendimia"
    exif[0x013B] = "Daniel"
    source = make_image("grapes.jpg", size=(20, 20), exif=exif)

    result = real.run("image.metadata", [source], {"action": "view"})

    assert result.ok, result.message
    assert result.outputs == []
    assert sorted(p.name for p in source.parent.iterdir()) == ["grapes.jpg"]
    assert result.data["metadata"]["title"] == "Vendimia"
    assert result.data["metadata"]["author"] == "Daniel"
    assert result.data["metadata"]["width"] == 20


def test_metadata_strips_every_tag(real, make_image):
    needs("jpg")
    Image = image.pillow()
    exif = Image.Exif()
    exif[0x010E] = "Vendimia"
    source = make_image("grapes.jpg", size=(20, 20), exif=exif)

    result = real.run("image.metadata", [source], {"action": "strip"})

    assert result.ok, result.message
    out = result.outputs[0]
    assert out.name == "grapes (Metadata).jpg"
    assert opened(out)[1] == "JPEG"
    assert exif_of(out) == ({}, {})


def test_metadata_sets_the_title_the_author_and_the_location(real, make_image):
    needs("jpg")
    source = make_image("grapes.jpg", size=(20, 20))

    result = real.run(
        "image.metadata",
        [source],
        {
            "action": "set",
            "title": "Vendimia 2026",
            "author": "Daniel",
            "latitude": 19.4326,
            "longitude": -99.1332,
        },
    )

    assert result.ok, result.message
    exif, gps = exif_of(result.outputs[0])
    assert exif.get(0x010E) == "Vendimia 2026"
    assert exif.get(0x013B) == "Daniel"
    assert (gps[1], gps[3]) == ("N", "W")
    degrees, minutes, seconds = gps[2]
    assert abs(float(degrees) + float(minutes) / 60 + float(seconds) / 3600 - 19.4326) < 1e-4


def test_metadata_set_with_nothing_to_set_is_refused(real, make_image):
    needs("jpg")
    refused(real.run("image.metadata", [make_image("grapes.jpg")], {"action": "set"}))


def test_metadata_needs_both_halves_of_a_location(real, make_image):
    needs("jpg")
    message = refused(
        real.run(
            "image.metadata",
            [make_image("grapes.jpg")],
            {"action": "set", "latitude": 19.4326},
        )
    )
    assert "longitude" in message


def test_a_failed_tool_leaves_no_placeholder_behind(real, make_image, monkeypatch):
    """The naming contract: a claim that never gets filled is removed."""
    needs("png")
    source = make_image("victim.png", size=(10, 10))

    def boom(*args, **kwargs):
        raise RuntimeError("encoder died")

    monkeypatch.setattr(image, "save", boom)
    result = real.run("image.compress", [source])

    assert not result.ok
    assert sorted(p.name for p in source.parent.iterdir()) == ["victim.png"]


def test_every_target_accepts_a_source_that_carries_metadata(real, make_image):
    """A phone photo is the normal input and it is never a bare bitmap: it
    arrives with EXIF, so every encoder has to cope with being handed one."""
    needs("jpg")
    Image = image.pillow()
    exif = Image.Exif()
    exif[0x010E] = "Vendimia"
    exif[0x013B] = "Daniel"
    gps = exif.get_ifd(0x8825)
    gps[1], gps[2] = "N", (19.0, 25.0, 57.36)
    gps[3], gps[4] = "W", (99.0, 7.0, 59.52)
    source = make_image("photo.jpg", size=(24, 18), exif=exif)

    for action_id, (ext, fmt) in sorted(CONVERSIONS.items()):
        if ext == "jpg":
            continue  # that is the source format, so it is not on the wheel
        needs(ext)
        result = real.run(action_id, [source])
        assert result.ok, f"{action_id}: {result.message}"
        if ext == "pdf":
            assert len(pdf_pages(result.outputs[0])) == 1
            continue
        img, got = opened(result.outputs[0])
        assert (got, img.size) == (fmt, (24, 18))
        if ext == "bmp":
            continue  # BMP has nowhere to put metadata
        carried, gps_out = exif_of(result.outputs[0])
        assert carried.get(0x010E) == "Vendimia", action_id
        # Where the photo was taken lives in a sub-IFD, which is the part that
        # breaks first: TIFF's compressed writer cannot place one at all.
        assert gps_out.get(1) == "N", action_id


# --- through the CLI, which is the only interface the shell has --------------


def test_the_cli_offers_the_image_wheel_for_a_dragged_photo(make_image, capsys):
    needs("jpg")
    source = make_image("playa.jpg")

    code = main(["options", str(source)])
    payload = json.loads(capsys.readouterr().out)

    assert code == 0
    assert payload["warnings"] == []
    conversions = entries(payload, CONVERT)
    tools = entries(payload, TOOL)
    assert {"image.to_png", "image.to_webp", "image.to_pdf"} <= set(conversions)
    assert "image.to_jpg" not in conversions
    assert set(TOOLS) - {"image.collage", "image.create_pdf"} <= set(tools)
    assert tools["image.crop"]["needs_editor"] is True
    assert [p["name"] for p in tools["image.crop"]["params"]] == ["x", "y", "w", "h"]


def test_the_cli_runs_an_image_tool_the_way_an_editor_calls_it(make_image, capsys, tmp_path):
    """The WPF editor passes a long shape list as --param shapes=@file."""
    needs("png")
    source = make_image("screenshot.png", size=(40, 40))
    shapes = tmp_path / "shapes.json"
    shapes.write_text(
        json.dumps([{"type": "rect", "x": 4, "y": 4, "w": 10, "h": 10, "fill": "#00ff00"}]),
        encoding="utf-8",
    )

    code = main(["run", "image.annotate", str(source), "--param", f"shapes=@{shapes}"])
    payload = json.loads(capsys.readouterr().out)

    assert code == 0, payload
    out = Path(payload["outputs"][0])
    assert out.name == "screenshot (Annotate).png"
    assert opened(out)[0].convert("RGB").getpixel((8, 8)) == (0, 255, 0)


def test_the_cli_reports_a_bad_shape_as_a_message_not_a_traceback(make_image, capsys):
    needs("png")
    source = make_image("screenshot.png")

    code = main(["run", "image.annotate", str(source), "--param", "shapes=[{}]"])
    payload = json.loads(capsys.readouterr().out)

    assert code == 1
    assert payload["code"] == "invalid_params"
    assert "shape" in payload["message"]
    assert "Traceback" not in payload["message"]


def test_metadata_writes_a_location_a_strict_reader_accepts(real, make_image):
    """A clean 116.6 degrees is 36 minutes exactly, never 35 and 60 seconds."""
    needs("jpg")
    source = make_image("vina.jpg", size=(16, 16))

    result = real.run(
        "image.metadata",
        [source],
        {"action": "set", "latitude": 32.0, "longitude": -116.6},
    )

    assert result.ok, result.message
    _exif, gps = exif_of(result.outputs[0])
    assert (gps[3], tuple(float(part) for part in gps[4])) == ("W", (116.0, 36.0, 0.0))
    minutes_and_seconds = tuple(gps[2])[1:] + tuple(gps[4])[1:]
    assert all(float(part) < 60 for part in minutes_and_seconds)


# --- review round 1 (hq-plct.2) ---------------------------------------------


def _icc() -> bytes:
    from PIL import ImageCms

    return ImageCms.ImageCmsProfile(ImageCms.createProfile("sRGB")).tobytes()


def _animated(path: Path, fmt: str, frames: int = 3) -> Path:
    Image = image.pillow()
    shots = [Image.new("RGBA", (24, 16), (60 * i, 255 - 60 * i, 40, 255)) for i in range(frames)]
    shots[0].save(path, format=fmt, save_all=True, append_images=shots[1:], duration=80, loop=0)
    return path


def test_metadata_strip_drops_comment_xmp_and_text_but_keeps_the_icc_profile(real, tmp_path):
    needs("jpg")
    Image = image.pillow()
    source = tmp_path / "grapes.jpg"
    exif = Image.Exif()
    exif[0x010E] = "Vendimia"
    Image.new("RGB", (20, 20), (90, 20, 60)).save(
        source,
        exif=exif,
        comment=b"shot at Jane Roe's house",
        xmp=b"<x:xmpmeta>Jane Roe</x:xmpmeta>",
        icc_profile=_icc(),
    )

    result = real.run("image.metadata", [source], {"action": "strip"})

    assert result.ok, result.message
    data = result.outputs[0].read_bytes()
    assert b"Jane Roe" not in data
    assert exif_of(result.outputs[0]) == ({}, {})
    with Image.open(result.outputs[0]) as out:
        assert out.info.get("icc_profile") == _icc()
        assert "comment" not in out.info


def test_metadata_strip_drops_png_text_chunks(real, tmp_path):
    from PIL import PngImagePlugin

    Image = image.pillow()
    source = tmp_path / "shot.png"
    text = PngImagePlugin.PngInfo()
    text.add_text("Author", "Jane Roe")
    Image.new("RGB", (10, 10)).save(source, pnginfo=text)

    result = real.run("image.metadata", [source], {"action": "strip"})

    assert result.ok, result.message
    assert b"Jane Roe" not in result.outputs[0].read_bytes()


def test_redact_keeps_the_icc_profile(real, tmp_path):
    needs("jpg")
    Image = image.pillow()
    source = tmp_path / "id.jpg"
    Image.new("RGB", (40, 40), (200, 100, 50)).save(source, icc_profile=_icc())

    result = real.run(
        "image.redact", [source], {"regions": [{"x": 0, "y": 0, "w": 10, "h": 10}], "mode": "box"}
    )

    assert result.ok, result.message
    with Image.open(result.outputs[0]) as out:
        assert out.info.get("icc_profile") == _icc()


@pytest.mark.parametrize(
    ("mode", "values", "middle"),
    [
        ("F", [i / 255 for i in range(256)], 128),
        ("I", [i * 256 for i in range(256)], 128),
        ("I;16B", [i * 256 for i in range(256)], 128),
    ],
)
def test_deep_images_are_scaled_not_clipped(real, tmp_path, mode, values, middle):
    needs("jpg")
    Image = image.pillow()
    source = tmp_path / "deep.tiff"
    ramp = Image.new(mode, (256, 1))
    ramp.putdata(values)
    ramp.save(source)

    result = real.run("image.to_jpg", [source], {})

    assert result.ok, result.message
    with Image.open(result.outputs[0]) as out:
        assert abs(out.convert("L").getpixel((128, 0)) - middle) <= 8


def test_a_16_bit_png_stays_16_bit_when_converted_to_tiff(real, tmp_path):
    needs("tiff")
    Image = image.pillow()
    source = tmp_path / "scan.png"
    ramp = Image.new("I;16", (256, 1))
    ramp.putdata([i * 256 for i in range(256)])
    ramp.save(source)

    result = real.run("image.to_tiff", [source], {})

    assert result.ok, result.message
    with Image.open(result.outputs[0]) as out:
        assert out.mode in image.DEEP_MODES
        assert out.getpixel((128, 0)) == 128 * 256


def test_an_svg_too_big_to_render_is_refused(real, tmp_path):
    pytest.importorskip("fitz", reason="the size check is on the PyMuPDF path")
    source = tmp_path / "huge.svg"
    source.write_text(
        '<svg xmlns="http://www.w3.org/2000/svg" width="60000" height="60000">'
        '<rect width="10" height="10"/></svg>'
    )

    result = real.run("image.to_png", [source], {})

    assert not result.ok
    assert "pixel limit" in result.message


def test_annotate_clamps_an_absurd_width_and_text_size(real, make_image):
    import time

    needs("png")
    source = make_image("shot.png", size=(60, 40))
    shapes = [
        {"type": "rect", "x": 5, "y": 5, "w": 20, "h": 20, "width": 10**9},
        {"type": "text", "x": 1, "y": 1, "text": "hi", "size": 10**9},
    ]

    started = time.monotonic()
    result = real.run("image.annotate", [source], {"shapes": shapes})

    assert result.ok, result.message
    assert time.monotonic() - started < 10
    assert opened(result.outputs[0])[0].size == (60, 40)


def test_a_transparent_png_flattens_onto_white_as_bmp(real, tmp_path):
    needs("bmp")
    Image = image.pillow()
    source = tmp_path / "logo.png"
    Image.new("RGBA", (4, 4), (255, 0, 0, 0)).save(source)

    result = real.run("image.to_bmp", [source], {})

    assert result.ok, result.message
    out, fmt = opened(result.outputs[0])
    assert fmt == "BMP"
    assert out.convert("RGB").getpixel((0, 0)) == (255, 255, 255)


def test_an_animated_png_keeps_every_frame_as_webp(real, tmp_path):
    needs("webp")
    source = _animated(tmp_path / "spin.png", "PNG", frames=3)

    result = real.run("image.to_webp", [source], {})

    assert result.ok, result.message
    assert "3 frames" in result.message
    Image = image.pillow()
    with Image.open(result.outputs[0]) as out:
        assert out.n_frames == 3


def test_an_animated_webp_to_jpg_says_it_kept_the_first_frame(real, tmp_path):
    needs("webp")
    source = _animated(tmp_path / "spin.webp", "WEBP", frames=4)

    result = real.run("image.to_jpg", [source], {})

    assert result.ok, result.message
    assert "4 frames" in result.message and "first frame" in result.message


def test_compress_keeps_an_animated_webp_animated(real, tmp_path):
    needs("webp")
    source = _animated(tmp_path / "spin.webp", "WEBP", frames=3)

    result = real.run("image.compress", [source], {"quality": 40})

    assert result.ok, result.message
    assert "All 3 frames kept" in result.message
    Image = image.pillow()
    with Image.open(result.outputs[0]) as out:
        assert out.n_frames == 3


def test_metadata_set_on_a_jpeg_keeps_its_quantisation_tables(real, make_image):
    needs("jpg")
    Image = image.pillow()
    source = make_image("grapes.jpg", size=(32, 32), quality=55)
    with Image.open(source) as before:
        tables = before.quantization

    result = real.run("image.metadata", [source], {"action": "set", "title": "Vendimia"})

    assert result.ok, result.message
    with Image.open(result.outputs[0]) as after:
        assert after.quantization == tables
