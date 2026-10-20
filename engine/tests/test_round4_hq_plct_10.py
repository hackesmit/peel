"""Engine fix from review round 4 of the image editor windows (hq-plct.10).

XMP may be UTF-16 or UTF-32 (with or without a byte order mark). The round 3
scrub and the post-write check both looked for the bytes b"GPS", which a
UTF-16 packet never contains, so clear_location kept the place and reported
success. The scrub now decodes the packet first and drops any it cannot read,
and it runs in linear time (the regex took seconds on unclosed GPS elements).

Fixtures are generated at run time (CLAUDE.md: no committed binaries).
"""

from __future__ import annotations

import time
from pathlib import Path

import pytest

from peel_engine.families import image
from peel_engine.registry import load_registry

pytest.importorskip("PIL", reason="the image family needs pillow")

TAG_GPS_IFD = 0x8825
TAG_XMP = 700

XMP = (
    '<?xpacket begin="﻿" id="W5M0MpCehiHzreSzNTczkc9d"?>'
    '<x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF '
    'xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">'
    '<rdf:Description rdf:about="" xmlns:exif="http://ns.adobe.com/exif/1.0/" '
    'xmlns:tiff="http://ns.adobe.com/tiff/1.0/" tiff:Orientation="1" '
    'exif:GPSAltitude="12/1">'
    "<exif:GPSLatitude>32,27.4N</exif:GPSLatitude>"
    "<exif:GPSLongitude>116,36.7W</exif:GPSLongitude>"
    "<tiff:Make>Peel</tiff:Make>"
    "</rdf:Description></rdf:RDF></x:xmpmeta>"
    '<?xpacket end="w"?>'
)

ENCODINGS = {
    "utf-16-le-bom": (b"\xff\xfe", "utf-16-le"),
    "utf-16-be-bom": (b"\xfe\xff", "utf-16-be"),
    "utf-16-le": (b"", "utf-16-le"),
    "utf-16-be": (b"", "utf-16-be"),
    "utf-32-le-bom": (b"\xff\xfe\x00\x00", "utf-32-le"),
    "utf-32-be-bom": (b"\x00\x00\xfe\xff", "utf-32-be"),
    "utf-32-le": (b"", "utf-32-le"),
}


@pytest.fixture(scope="module")
def real():
    return load_registry(force=True, strict=True)


def _tiff(tmp_path: Path, xmp: bytes) -> Path:
    Image = image.pillow()
    exif = Image.Exif()
    gps = exif.get_ifd(TAG_GPS_IFD)
    gps[1] = "N"
    gps[2] = (32.0, 27.0, 24.12)
    path = tmp_path / "photo.tif"
    Image.new("RGB", (40, 30), (120, 140, 160)).save(
        path, "TIFF", exif=exif, tiffinfo={TAG_XMP: xmp}
    )
    return path


def _clear(real, source: Path):
    return real.run(
        "image.metadata", [source],
        {"action": "set", "title": "t", "clear_location": True},
    )


def _xmp_of(path: Path):
    Image = image.pillow()
    with Image.open(path) as out:
        value = out.tag_v2.get(TAG_XMP)
    return value[0] if isinstance(value, tuple) and len(value) == 1 else value


@pytest.mark.parametrize("name", ENCODINGS)
def test_a_wide_xmp_packet_loses_its_place_and_keeps_the_rest(real, tmp_path, name):
    bom, codec = ENCODINGS[name]
    source = _tiff(tmp_path, bom + XMP.encode(codec))
    assert "GPSLatitude".encode(codec) in source.read_bytes(), "fixture must carry the place"
    result = _clear(real, source)
    assert result.ok, result.message
    packet = _xmp_of(Path(result.outputs[0]))
    assert isinstance(packet, bytes) and packet.startswith(bom)
    text = packet[len(bom):].decode(codec)
    assert "GPS" not in text
    assert "<tiff:Make>Peel</tiff:Make>" in text
    assert 'tiff:Orientation="1"' in text


def test_a_packet_that_does_not_decode_is_dropped(real, tmp_path):
    # A UTF-16LE mark with an odd byte count is not UTF-16 at all.
    broken = b"\xff\xfe" + XMP.encode("utf-16-le") + b"\x00"
    assert image._xmp_without_location(broken) is None
    result = _clear(real, _tiff(tmp_path, broken))
    assert result.ok, result.message
    assert _xmp_of(Path(result.outputs[0])) is None


def test_a_packet_declaring_another_encoding_is_dropped():
    latin = b'<?xml version="1.0" encoding="ISO-8859-1"?><x:xmpmeta/>'
    assert image._xmp_without_location(latin) is None


def test_the_check_reads_a_wide_packet_in_its_own_encoding(real, tmp_path, monkeypatch):
    # Simulate a scrub that misses: the check must still see the UTF-16 place.
    monkeypatch.setattr(image, "_xmp_without_location", lambda value: value)
    source = _tiff(tmp_path, b"\xff\xfe" + XMP.encode("utf-16-le"))
    result = _clear(real, source)
    assert not result.ok
    assert "location could not be removed" in result.message
    assert sorted(p.name for p in tmp_path.iterdir()) == ["photo.tif"]


def test_the_check_counts_an_unreadable_packet_as_a_place(real, tmp_path, monkeypatch):
    monkeypatch.setattr(image, "_xmp_without_location", lambda value: value)
    source = _tiff(tmp_path, b"\xff\xfe<\x00x\x00\x00")
    result = _clear(real, source)
    assert not result.ok


def test_unclosed_gps_elements_are_dropped_in_linear_time():
    # Round 3's regex took about 8 s on 112 KB of these.
    packet = b"<x:xmpmeta>" + b"<exif:GPSLatitude>1" * 6000
    start = time.perf_counter()
    assert image._xmp_without_location(packet) is None
    assert time.perf_counter() - start < 1.0
    wide = packet.replace(b"<x:xmpmeta>", b"", 1) * 10
    start = time.perf_counter()
    assert image._xmp_without_location(wide) is None
    assert time.perf_counter() - start < 1.0


def test_nested_gps_elements_and_markup_go_whole():
    packet = (
        b"<d><exif:GPSDestLatitude><rdf:Alt><rdf:li>1</rdf:li></rdf:Alt>"
        b"</exif:GPSDestLatitude><!-- note --><a:b c='>'/></d>"
    )
    assert image._xmp_without_location(packet) == b"<d><!-- note --><a:b c='>'/></d>"
    assert image._xmp_without_location(b"<d><exif:GPSLatitude>1</d>") is None
    assert image._xmp_without_location(b'<d a="unclosed>') is None
