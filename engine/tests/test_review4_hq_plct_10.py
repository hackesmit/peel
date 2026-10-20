"""Review round 4 checks for the image editors (hq-plct.10).

A UTF-16 or UTF-32 XMP packet with no byte order mark whose first character
is not "<" (a leading newline or space) is not recognised as wide text. It
is decoded as UTF-8, which succeeds because NUL bytes are valid UTF-8, so the
scrub finds no "GPS" (the text is "G\\x00P\\x00S") and keeps the packet, and
the post-write check reads it the same way and reports success. The place
stays in the written TIFF. Required behaviour: scrub or drop (fail closed).

Fixtures are generated at run time (CLAUDE.md: no committed binaries).
"""

from __future__ import annotations

from pathlib import Path

import pytest

from peel_engine.families import image
from peel_engine.registry import load_registry

pytest.importorskip("PIL", reason="the image family needs pillow")

TAG_XMP = 700

# ASCII only on purpose: no U+FEFF inside begin="", so a wide encoding of it
# is also valid UTF-8 byte for byte.
XMP = (
    '<?xpacket begin="" id="W5M0MpCehiHzreSzNTczkc9d"?>'
    '<x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF '
    'xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">'
    '<rdf:Description rdf:about="" xmlns:exif="http://ns.adobe.com/exif/1.0/">'
    "<exif:GPSLatitude>32,27.4N</exif:GPSLatitude>"
    "<exif:GPSLongitude>116,36.7W</exif:GPSLongitude>"
    "</rdf:Description></rdf:RDF></x:xmpmeta>"
    '<?xpacket end="w"?>'
)

WIDE = ["utf-16-le", "utf-16-be", "utf-32-le", "utf-32-be"]


@pytest.fixture(scope="module")
def real():
    return load_registry(force=True, strict=True)


@pytest.mark.parametrize("codec", WIDE)
def test_scrub_does_not_keep_a_wide_packet_led_by_whitespace(codec):
    packet = ("\n" + XMP).encode(codec)
    kept = image._xmp_without_location(packet)
    assert kept is None or "GPS".encode(codec) not in kept, (
        f"{codec} packet kept with its GPS properties"
    )


@pytest.mark.parametrize("codec", WIDE)
def test_clear_location_removes_a_wide_packet_led_by_whitespace(real, tmp_path, codec):
    Image = image.pillow()
    source = tmp_path / "photo.tif"
    Image.new("RGB", (40, 30), (120, 140, 160)).save(
        source, "TIFF", tiffinfo={TAG_XMP: ("\n" + XMP).encode(codec)}
    )
    try:
        result = real.run(
            "image.metadata", [source],
            {"action": "set", "title": "t", "clear_location": True},
        )
    except Exception:
        result = None  # a refusal is a safe answer
    for out in tmp_path.iterdir():
        if out == source:
            continue
        assert "GPSLatitude".encode(codec) not in out.read_bytes(), (
            f"{codec}: GPS survived in {out.name}; result was {result}"
        )
