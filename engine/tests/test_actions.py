"""The Alt wheel file actions.

Five of the six actions are performed by the Windows shell, not the engine, so
there is no runner to exercise here: the engine's whole job is to put them on
the Alt wheel with the right shape and to refuse to run them, so the shell
knows it owns the work. Copy Path is the sixth and is engine work; it lives in
the text family and is covered by test_core, so this file only checks that the
six read as one set on the wheel.
"""

from __future__ import annotations

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
