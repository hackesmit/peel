"""Review round 2 for hq-plct.7: the --files-from list against names that
Windows allows but Python's str.splitlines() treats as line breaks.

NTFS forbids only U+0000 to U+001F (and a few ASCII punctuation marks) in a
file name, so U+0085 (NEL), U+2028 (LINE SEPARATOR) and U+2029 (PARAGRAPH
SEPARATOR) are legal. The shell writes one path per line separated by "\n"
only; the engine must split on "\n" (and drop a trailing "\r") and nothing
else, or such a path reaches the registry as two wrong paths.

Run explicitly (the name is outside the default discovery pattern):
    python3 -m pytest -q tests/review2_hq_plct_7.py
"""

from __future__ import annotations

import json

import pytest

from peel_engine.cli import main


@pytest.mark.parametrize("sep", [" ", " ", "\x85"], ids=["LS", "PS", "NEL"])
def test_a_legal_windows_name_with_a_unicode_line_separator_survives_the_list(
    sep, make_file, tmp_path, capsys
):
    odd = make_file(f"nota{sep}borrador.txt")
    plain = make_file("plain.txt")
    listing = tmp_path / "list.txt"
    # Exactly what EngineClient.Arrange writes: each path then "\n", UTF-8, no BOM.
    listing.write_bytes(f"{odd}\n{plain}\n".encode("utf-8"))

    code = main(["options", "--files-from", str(listing)])
    payload = json.loads(capsys.readouterr().out)

    assert code == 0, payload
    assert payload["files"] == [str(odd), str(plain)]


def test_run_through_the_list_converts_the_file_with_a_line_separator(make_file, tmp_path, capsys):
    odd = make_file("acta final.txt", "x")
    listing = tmp_path / "list.txt"
    listing.write_bytes(f"{odd}\n".encode("utf-8"))

    code = main(["run", "text.to_md", "--files-from", str(listing)])
    payload = json.loads(capsys.readouterr().out)

    assert code == 0, payload
    assert payload["outputs"] == [str(odd.with_suffix(".md"))]
