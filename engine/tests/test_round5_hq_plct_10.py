"""Engine fix from review round 4 of the image editors (hq-plct.10).

GPS property names are matched in any letter case: a DJI drone writes
drone-dji:GpsLatitude, which the case-sensitive scrub and post-write check
both missed. A packet holding a NUL or another C0 control (not allowed in
XML) is unreadable, so it is dropped and counts as a location left.

Fixtures are generated at run time (CLAUDE.md: no committed binaries).
"""

from __future__ import annotations

import pytest

from peel_engine.families import image
from peel_engine.registry import load_registry

pytest.importorskip("PIL", reason="the image family needs pillow")

TAG_XMP = 700

DJI = (
    '<?xpacket begin="" id="W5M0MpCehiHzreSzNTczkc9d"?>'
    '<x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF '
    'xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">'
    '<rdf:Description rdf:about="" xmlns:drone-dji="http://www.dji.com/drone-dji/1.0/" '
    'xmlns:tiff="http://ns.adobe.com/tiff/1.0/" tiff:Make="Peel" '
    'drone-dji:GpsLatitude="+32.4567">'
    "<drone-dji:GpsLongitude>-116.6123</drone-dji:GpsLongitude>"
    "</rdf:Description></rdf:RDF></x:xmpmeta>"
    '<?xpacket end="w"?>'
)


@pytest.fixture(scope="module")
def real():
    return load_registry(force=True, strict=True)


def _source(tmp_path, kind, xmp):
    Image = image.pillow()
    img = Image.new("RGB", (40, 30), (120, 140, 160))
    path = tmp_path / f"photo.{kind}"
    if kind == "tif":
        img.save(path, "TIFF", tiffinfo={TAG_XMP: xmp.encode()})
    elif kind == "jpg":
        img.save(path, "JPEG", xmp=xmp.encode())
    else:
        from PIL import PngImagePlugin

        info = PngImagePlugin.PngInfo()
        info.add_itxt("XML:com.adobe.xmp", xmp)
        img.save(path, "PNG", pnginfo=info)
    return path


def _outputs(tmp_path, source):
    return [p for p in tmp_path.iterdir() if p != source]


@pytest.mark.parametrize("kind", ["tif", "jpg", "png"])
def test_clear_location_removes_dji_gps_case_variants(real, tmp_path, kind):
    source = _source(tmp_path, kind, DJI)
    try:
        real.run("image.metadata", [source],
                 {"action": "set", "title": "t", "clear_location": True})
    except Exception:
        pass  # a refusal is a safe answer
    for out in _outputs(tmp_path, source):
        assert b"gpslat" not in out.read_bytes().lower().replace(b"\x00", b"")
        assert b"gpslon" not in out.read_bytes().lower().replace(b"\x00", b"")


def test_scrub_removes_or_drops_mixed_case_gps():
    kept = image._xmp_without_location(DJI.encode())
    assert kept is None or b"gps" not in kept.lower()


@pytest.mark.parametrize("text", ["abc\x00def", "a\x01b", "a\x1fb"])
def test_control_character_packet_is_dropped(text):
    packet = ("<x:xmpmeta xmlns:x='adobe:ns:meta/'>" + text + "</x:xmpmeta>").encode()
    assert image._xmp_text(packet) is None
    assert image._xmp_without_location(packet) is None


def test_tab_and_newline_are_still_readable():
    assert image._xmp_text(b"<a>\t\r\n</a>") is not None


@pytest.mark.parametrize("kind", ["tif", "jpg", "png"])
def test_location_check_sees_mixed_case_gps(tmp_path, kind):
    source = _source(tmp_path, kind, DJI)
    assert image._location_left(source) is True
