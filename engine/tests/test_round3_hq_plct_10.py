"""Engine fix from review round 3 of the image editor windows (hq-plct.10).

clear_location also takes the place out of the XMP packet. A TIFF keeps XMP
in IFD0 tag 700 and pillow's TIFF writer copies it from the source, so the
EXIF GPS went while exif:GPSLatitude stayed. The runner now scrubs every XMP
GPS property (dropping the packet when it cannot), and refuses to report
success when the written file still names a place.

Fixtures are generated at run time (CLAUDE.md: no committed binaries).
"""

from __future__ import annotations

from pathlib import Path

import pytest

from peel_engine.families import image
from peel_engine.registry import load_registry

pytest.importorskip("PIL", reason="the image family needs pillow")

TAG_GPS_IFD = 0x8825
TAG_XMP = 700

XMP_ELEMENTS = (
    b'<x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF '
    b'xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">'
    b'<rdf:Description rdf:about="" xmlns:exif="http://ns.adobe.com/exif/1.0/" '
    b'xmlns:tiff="http://ns.adobe.com/tiff/1.0/" tiff:Orientation="1">'
    b"<exif:GPSLatitude>32,27.4N</exif:GPSLatitude>"
    b"<exif:GPSLongitude>116,36.7W</exif:GPSLongitude>"
    b'<exif:GPSVersionID/>'
    b"<tiff:Make>Peel</tiff:Make>"
    b"</rdf:Description></rdf:RDF></x:xmpmeta>"
)


@pytest.fixture(scope="module")
def real():
    return load_registry(force=True, strict=True)


def _tiff(tmp_path: Path, xmp: bytes) -> Path:
    Image = image.pillow()
    exif = Image.Exif()
    gps = exif.get_ifd(TAG_GPS_IFD)
    gps[1] = "N"
    gps[2] = (32.0, 27.0, 24.12)
    gps[3] = "W"
    gps[4] = (116.0, 36.0, 44.28)
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


def test_xmp_gps_elements_go_and_the_rest_of_the_packet_stays(real, tmp_path):
    result = _clear(real, _tiff(tmp_path, XMP_ELEMENTS))
    assert result.ok, result.message
    data = Path(result.outputs[0]).read_bytes()
    assert b"GPS" not in data
    assert b"<tiff:Make>Peel</tiff:Make>" in data
    assert b'tiff:Orientation="1"' in data


def test_the_scrub_drops_a_packet_it_cannot_clean():
    assert image._xmp_without_location(b"<x>GPS in some form we do not parse</x>") is None
    assert image._xmp_without_location((1, 2, 3)) is None
    assert image._xmp_without_location(b'<d exif:GPSLatitude="1" a:b="c"/>') == b'<d a:b="c"/>'


def test_success_is_never_reported_while_a_location_remains(real, tmp_path, monkeypatch):
    # Simulate a writer that puts the place back whatever the runner does.
    monkeypatch.setattr(image, "_xmp_without_location", lambda value: value)
    source = _tiff(tmp_path, XMP_ELEMENTS)
    result = _clear(real, source)
    assert not result.ok
    assert "location could not be removed" in result.message
    assert sorted(p.name for p in tmp_path.iterdir()) == ["photo.tif"]
