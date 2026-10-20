"""The Alt wheel file actions.

Five of the six actions are performed by the Windows shell, not the engine, so
there is no runner to exercise here: the engine's whole job is to put them on
the Alt wheel with the right shape and to refuse to run them, so the shell
knows it owns the work. Copy Path is the sixth and is engine work; it lives in
the text family and is covered by test_core, so this file only checks that the
six read as one set on the wheel.
"""

from __future__ import annotations

import os
from pathlib import Path

import pytest

from peel_engine.families.actions import GROUP
from peel_engine.registry import ACTION, SHELL, load_registry
from peel_engine.result import UnsupportedInput

#: The five actions this family owns, id to label.
SHELL_ACTIONS = {
    "files.share": "Share",
    "files.float": "Float",
    "files.copy": "Copy",
    "files.move": "Move",
    "files.terminal": "Terminal",
}

#: The sixth action, performed by the engine (text family).
COPY_PATH = "files.copy_path"


def alt_wheel(options: dict) -> dict:
    for wheel in options["wheels"]:
        if wheel["family"] == ACTION:
            return wheel
    raise AssertionError("no Alt wheel in the options payload")


def alt_entries(options: dict) -> dict[str, dict]:
    return {
        entry["id"]: entry
        for group in alt_wheel(options)["groups"]
        for entry in group["entries"]
    }


@pytest.fixture
def real():
    return load_registry(force=True, strict=True)


def test_the_five_shell_actions_load_with_no_warnings(real):
    assert real.warnings == []
    for action_id in SHELL_ACTIONS:
        assert action_id in real, action_id


def test_each_shell_action_is_alt_wheel_windows_ui(real):
    for action_id, label in SHELL_ACTIONS.items():
        action = real.get(action_id)
        assert action.family == ACTION
        assert action.group == GROUP
        assert action.label == label
        assert action.handled_by == SHELL
        assert action.runner == ""
        assert action.accepts_any
        assert action.accepts_dirs


def test_the_six_actions_share_one_alt_wheel_group(real):
    options = real.options([])
    wheel = alt_wheel(options)
    assert wheel["default_modifier"] == "Alt"
    groups = {g["group"] for g in wheel["groups"]}
    assert GROUP in groups
    entries = alt_entries(options)
    for action_id in SHELL_ACTIONS:
        assert action_id in entries
    assert COPY_PATH in entries, "Copy Path belongs on the Alt wheel beside the five"


@pytest.mark.parametrize("action_id", sorted(SHELL_ACTIONS))
def test_a_shell_action_is_offered_for_file_folder_and_mix(action_id, make_file, tmp_path, real):
    one_file = make_file("photo.png", "x")
    folder = tmp_path / "album"
    folder.mkdir()
    for paths in ([one_file], [folder], [one_file, folder]):
        entries = alt_entries(real.options(paths))
        assert action_id in entries, (action_id, paths)
        assert entries[action_id]["available"] is True


@pytest.mark.parametrize("action_id", sorted(SHELL_ACTIONS))
def test_the_engine_refuses_to_run_a_shell_action(action_id, make_file, real):
    with pytest.raises(UnsupportedInput) as err:
        real.run(action_id, [make_file("a.txt")])
    assert err.value.code == "shell_action"
    assert "Peel app itself" in str(err.value)


def test_a_shell_action_accepts_a_mixed_selection_of_many(make_file, tmp_path, real):
    files = [make_file(f"f{n}.bin", "x") for n in range(3)]
    folder = tmp_path / "dir"
    folder.mkdir()
    paths: list[Path] = [*files, folder]
    entries = alt_entries(real.options(paths))
    for action_id in SHELL_ACTIONS:
        assert action_id in entries


def _copy_path_lines(real, paths) -> list[str]:
    result = real.run(COPY_PATH, paths)
    assert result.ok
    assert result.outputs == []
    text = result.data["clipboard_text"]
    # CRLF, the Windows clipboard convention, and no bare LF anywhere.
    assert "\n" not in text.replace("\r\n", "")
    return text.split("\r\n")


def test_copy_path_quotes_a_file_a_folder_and_a_mix(make_file, tmp_path, real):
    spaced = make_file("my file.txt", "x")
    folder = tmp_path / "an album"
    folder.mkdir()
    q = lambda p: '"' + os.path.abspath(p) + '"'  # noqa: E731
    assert _copy_path_lines(real, [spaced]) == [q(spaced)]
    assert _copy_path_lines(real, [folder]) == [q(folder)]
    assert _copy_path_lines(real, [spaced, folder]) == [q(spaced), q(folder)]


def test_copy_path_joins_many_paths_with_crlf_and_no_trailing_newline(make_file, real):
    files = [make_file(f"f {n}.bin", "x") for n in range(4)]
    text = real.run(COPY_PATH, files).data["clipboard_text"]
    assert text.count("\r\n") == 3
    assert not text.endswith("\r\n")
    for line in text.split("\r\n"):
        assert line.startswith('"') and line.endswith('"') and line.count('"') == 2


def test_copy_path_keeps_the_path_as_given_not_its_symlink_target(tmp_path, real):
    target = tmp_path / "real"
    target.mkdir()
    link = tmp_path / "alias"
    try:
        link.symlink_to(target, target_is_directory=True)
    except OSError:
        pytest.skip("symlinks not permitted here")
    assert _copy_path_lines(real, [link]) == ['"' + os.path.abspath(link) + '"']
