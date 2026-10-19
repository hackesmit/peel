"""``--files-from``: the path list the shell uses when a drag is too long for
one Windows command line (hq-plct.7 review round 1)."""

from __future__ import annotations

import json

from peel_engine.cli import main


def run_cli(argv, capsys) -> tuple[int, dict]:
    code = main(argv)
    return code, json.loads(capsys.readouterr().out)


def write_list(path, paths, *, bom=False, newline="\n"):
    text = newline.join(str(p) for p in paths) + newline
    path.write_bytes((b"\xef\xbb\xbf" if bom else b"") + text.encode("utf-8"))
    return path


def test_options_reads_paths_from_a_list(make_file, tmp_path, capsys):
    files = [make_file(f"n{i}.txt") for i in range(3)]
    listing = write_list(tmp_path / "list.txt", files)
    code, payload = run_cli(["options", "--files-from", str(listing)], capsys)
    assert code == 0
    assert payload["files"] == [str(f) for f in files]
    assert any(e["id"] == "docs.doc_to_md" for w in payload["wheels"] for g in w["groups"] for e in g["entries"])


def test_list_paths_come_before_command_line_paths(make_file, tmp_path, capsys):
    a, b, c = make_file("a.txt"), make_file("b.txt"), make_file("c.txt")
    listing = write_list(tmp_path / "list.txt", [a, b])
    code, payload = run_cli(["options", str(c), "--files-from", str(listing)], capsys)
    assert code == 0
    assert payload["files"] == [str(a), str(b), str(c)]


def test_bom_crlf_blank_lines_and_unicode_names(make_file, tmp_path, capsys):
    odd = make_file("ñandú café 日本語 🍊 trailing .txt")
    plain = make_file("plain.txt")
    listing = tmp_path / "list.txt"
    body = f"{odd}\r\n\r\n{plain}\r\n"
    listing.write_bytes(b"\xef\xbb\xbf" + body.encode("utf-8"))
    code, payload = run_cli(["options", "--files-from", str(listing)], capsys)
    assert code == 0
    assert payload["files"] == [str(odd), str(plain)]


def test_run_takes_its_files_from_a_list(make_file, tmp_path, capsys):
    sources = [make_file(f"s{i}.txt", "x") for i in range(4)]
    listing = write_list(tmp_path / "list.txt", sources)
    code, payload = run_cli(["run", "docs.doc_to_md", "--files-from", str(listing)], capsys)
    assert code == 0, payload
    assert payload["outputs"] == [str(s.with_suffix(".md")) for s in sources]
    for s in sources:
        assert s.with_suffix(".md").exists()


def test_a_list_longer_than_a_windows_command_line_works(tmp_path, capsys):
    folder = tmp_path / ("Vacaciones 2026 Ensenada " * 2).strip()
    folder.mkdir()
    sources = []
    for i in range(700):
        src = folder / f"IMG_{i:05d}.txt"
        src.write_text("x", encoding="utf-8")
        sources.append(src)
    listing = write_list(tmp_path / "list.txt", sources)
    assert listing.stat().st_size > 32767
    code, payload = run_cli(["options", "--files-from", str(listing)], capsys)
    assert code == 0
    assert len(payload["files"]) == 700


def test_run_with_an_empty_list_and_no_files_is_a_usage_error(tmp_path, capsys):
    listing = tmp_path / "empty.txt"
    listing.write_text("\n\n", encoding="utf-8")
    code, payload = run_cli(["run", "docs.doc_to_md", "--files-from", str(listing)], capsys)
    assert code == 1
    assert payload["code"] == "usage"


def test_a_missing_list_is_a_usage_error(tmp_path, capsys):
    code, payload = run_cli(["options", "--files-from", str(tmp_path / "nope.txt")], capsys)
    assert code == 1
    assert payload["code"] == "usage"
    assert "nope.txt" in payload["message"]


def test_a_list_that_is_not_utf8_is_a_usage_error(tmp_path, capsys):
    listing = tmp_path / "bad.txt"
    listing.write_bytes(b"\xff\xfe\x00bad")
    code, payload = run_cli(["options", "--files-from", str(listing)], capsys)
    assert code == 1
    assert payload["code"] == "usage"


def test_only_lf_and_crlf_end_a_line_in_the_list(make_file, tmp_path, capsys):
    # U+0085, U+2028 and U+2029 are legal in a Windows name; str.splitlines()
    # would break the path at each of them (hq-plct.7 review round 2).
    odd = [make_file(f"acta{sep}final {i}.txt") for i, sep in enumerate(["\x85", "\u2028", "\u2029"])]
    plain = make_file("plain.txt")
    listing = write_list(tmp_path / "list.txt", [*odd, plain], newline="\r\n")
    code, payload = run_cli(["options", "--files-from", str(listing)], capsys)
    assert code == 0, payload
    assert payload["files"] == [str(p) for p in [*odd, plain]]
