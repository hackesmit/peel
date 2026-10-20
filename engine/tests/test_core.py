"""Engine core: naming, params, registry matching, running, CLI and doctor."""

from __future__ import annotations

import json
import os
import subprocess
import sys
import textwrap
from pathlib import Path

import pytest

from peel_engine import backends, naming
from peel_engine.cli import main, parse_params
from peel_engine.doctor import check
from peel_engine.families import family_modules
from peel_engine.registry import (
    ACTION,
    ANY,
    CONVERT,
    SAME,
    SHELL,
    TOOL,
    Action,
    Param,
    Registry,
    load_registry,
    validate_params,
)
from peel_engine.result import (
    BackendMissing,
    InvalidParams,
    MissingFile,
    Result,
    UnknownAction,
    UnsupportedInput,
)

ENGINE_DIR = Path(__file__).resolve().parents[1]
FAMILIES_DIR = ENGINE_DIR / "peel_engine" / "families"


def entries(options: dict, family: str) -> dict[str, dict]:
    """Flatten one wheel of an options payload into {id: entry}."""
    for wheel in options["wheels"]:
        if wheel["family"] == family:
            return {e["id"]: e for group in wheel["groups"] for e in group["entries"]}
    return {}


def ids(options: dict) -> set[str]:
    return {
        e["id"]
        for wheel in options["wheels"]
        for group in wheel["groups"]
        for e in group["entries"]
    }


# --- naming -----------------------------------------------------------------


def test_output_lands_next_to_the_source(make_file):
    src = make_file("holiday.txt")
    out = naming.output_path(src, "md")
    assert out == src.parent / "holiday.md"


def test_conversion_keeps_the_bare_name_and_a_tool_adds_its_name(make_file):
    src = make_file("holiday.txt")
    assert naming.output_path(src, "md").name == "holiday.md"
    assert naming.output_path(src, "txt", tag="Compress").name == "holiday (Compress).txt"


def test_an_existing_name_is_never_overwritten(make_file):
    src = make_file("note.txt")
    first = naming.output_path(src, "md")
    first.write_text("one", encoding="utf-8")
    second = naming.output_path(src, "md")
    assert second.name == "note 2.md"
    second.write_text("two", encoding="utf-8")
    assert naming.output_path(src, "md").name == "note 3.md"
    assert first.read_text(encoding="utf-8") == "one"


def test_the_suffix_goes_after_the_tool_name(make_file):
    src = make_file("clip.txt")
    first = naming.output_path(src, "txt", tag="Trim")
    first.write_text("x", encoding="utf-8")
    assert naming.output_path(src, "txt", tag="Trim").name == "clip (Trim) 2.txt"


def test_converting_to_the_same_extension_does_not_clobber_the_source(make_file):
    src = make_file("same.txt")
    assert naming.output_path(src, "txt") == src.parent / "same 2.txt"


def test_reserved_paths_stop_one_batch_colliding_with_itself(make_file):
    src = make_file("batch.txt")
    first = naming.output_path(src, "md")
    second = naming.output_path(src, "md", reserved=[first])
    assert (first.name, second.name) == ("batch.md", "batch 2.md")


def test_out_dir_overrides_the_source_folder(make_file, tmp_path):
    src = make_file("a.txt")
    elsewhere = tmp_path / "out"
    elsewhere.mkdir()
    assert naming.output_path(src, "md", out_dir=elsewhere) == elsewhere / "a.md"


def test_extensions_are_normalised():
    assert naming.normalise_ext(".JPG") == "jpg"
    assert naming.normalise_ext("Png") == "png"
    assert naming.normalise_ext(" .WebP ") == "webp"


def test_a_file_without_an_extension_keeps_its_whole_name(make_file):
    src = make_file("README")
    assert naming.output_path(src, "md").name == "README.md"


def test_a_double_extension_only_loses_the_last_part(make_file):
    src = make_file("site.tar.gz")
    assert naming.output_path(src, "zip").name == "site.tar.zip"


def test_a_name_is_claimed_on_disk_before_it_is_returned(make_file):
    src = make_file("claim.txt")
    first = naming.output_path(src, "md")
    assert first.exists() and first.stat().st_size == 0
    assert naming.output_path(src, "md").name == "claim 2.md"


def test_a_folder_output_is_claimed_with_mkdir(make_file):
    src = make_file("pack.txt")
    first = naming.output_path(src, "", directory=True)
    assert first.is_dir() and first.name == "pack"
    assert naming.output_path(src, "", directory=True).name == "pack 2"


def test_reserved_names_compare_case_insensitively(make_file):
    src = make_file("Case.txt")
    promised = src.parent / "CASE.MD"
    assert naming.output_path(src, "md", reserved=[promised]).name == "Case 2.md"


def test_release_only_removes_an_empty_claim(make_file):
    src = make_file("rel.txt")
    empty = naming.output_path(src, "md")
    full = naming.output_path(src, "md")
    full.write_text("kept", encoding="utf-8")
    naming.release(empty)
    naming.release(full)
    assert not empty.exists() and full.read_text(encoding="utf-8") == "kept"


def test_concurrent_claims_never_share_a_name(make_file):
    """Many threads racing for one name each get a different path."""
    from concurrent.futures import ThreadPoolExecutor

    src = make_file("race.txt")
    with ThreadPoolExecutor(max_workers=16) as pool:
        paths = list(pool.map(lambda _: naming.output_path(src, "md"), range(64)))
    assert len({str(p) for p in paths}) == 64


# --- params -----------------------------------------------------------------


@pytest.mark.parametrize(
    "ptype,given,expected",
    [
        ("string", "hi", "hi"),
        ("int", "7", 7),
        ("float", "0.5", 0.5),
        ("bool", "true", True),
        ("bool", "no", False),
        ("bool", "1", True),
        ("list", "a, b ,c", ["a", "b", "c"]),
        ("list", '["a","b"]', ["a", "b"]),
        ("json", '{"k": 1}', {"k": 1}),
        ("timecode", "12", 12.0),
        ("timecode", "1:30", 90.0),
        ("timecode", "01:00:01.5", 3601.5),
    ],
)
def test_params_coerce_cli_strings(ptype, given, expected):
    assert Param("p", ptype, "P").coerce(given) == expected


@pytest.mark.parametrize("given", ["nan", "inf", "-inf", "NaN", float("nan")])
def test_a_float_param_must_be_finite(given):
    param = Param(name="level", type="float", label="Level", minimum=0, maximum=1)
    with pytest.raises(InvalidParams, match="finite"):
        param.coerce(given)


@pytest.mark.parametrize("given", ["inf", "1:inf", float("nan")])
def test_a_timecode_must_be_finite(given):
    with pytest.raises(InvalidParams):
        Param(name="at", type="timecode", label="At").coerce(given)


def test_an_enum_only_takes_its_choices():
    param = Param("mode", "enum", "Mode", choices=("blur", "box"))
    assert param.coerce("blur") == "blur"
    with pytest.raises(InvalidParams) as err:
        param.coerce("pixelate")
    assert "blur, box" in str(err.value)


def test_a_number_outside_its_range_is_rejected():
    param = Param("quality", "int", "Quality", minimum=1, maximum=100)
    assert param.coerce("50") == 50
    with pytest.raises(InvalidParams) as err:
        param.coerce("101")
    assert "maximum" in str(err.value)
    with pytest.raises(InvalidParams):
        param.coerce("0")


def test_a_bad_number_reports_the_param_not_a_traceback():
    with pytest.raises(InvalidParams) as err:
        Param("quality", "int", "Quality").coerce("lots")
    assert "Quality (quality)" in str(err.value)


def test_an_enum_needs_choices():
    with pytest.raises(ValueError):
        Param("mode", "enum", "Mode")


def test_an_unknown_param_type_is_rejected():
    with pytest.raises(ValueError):
        Param("p", "colour", "P")


def test_defaults_fill_in_and_required_params_are_enforced(registry):
    compress = registry.get("demo.compress")
    assert validate_params(compress, {}) == {"quality": 80}
    assert validate_params(compress, {"quality": "30"}) == {"quality": 30}

    with pytest.raises(InvalidParams) as err:
        validate_params(registry.get("demo.crop"), {"x": 1, "y": 2, "w": 3})
    assert "required" in str(err.value)


def test_an_unknown_param_name_is_rejected_with_the_accepted_list(registry):
    with pytest.raises(InvalidParams) as err:
        validate_params(registry.get("demo.compress"), {"qualty": 10})
    assert "qualty" in str(err.value)
    assert "quality" in str(err.value)


# --- registry shape ---------------------------------------------------------


def test_a_duplicate_id_is_refused(registry):
    with pytest.raises(ValueError, match="duplicate action id"):
        registry.add(
            id="demo.to_up",
            family=CONVERT,
            label="Other",
            group="Demo",
            inputs=("txt",),
            output_ext="x",
            runner="conftest:write_upper",
        )


def test_two_segments_cannot_share_a_label_in_one_group(registry):
    """The wheel would show two identical segments, so discovery must fail."""
    with pytest.raises(ValueError, match="duplicate wheel segment"):
        registry.add(
            id="demo.to_up_again",
            family=CONVERT,
            label="up",
            group="Demo",
            inputs=("md",),
            output_ext="up",
            runner="conftest:write_upper",
        )


def test_the_same_label_in_another_group_is_fine(registry):
    registry.add(
        id="other.to_up",
        family=CONVERT,
        label="UP",
        group="Other",
        inputs=("md",),
        output_ext="up",
        runner="conftest:write_upper",
    )
    assert "other.to_up" in registry


@pytest.mark.parametrize(
    "kwargs,message",
    [
        ({"family": "wheel"}, "family must be one of"),
        ({"mode": "both"}, "mode must be each or combine"),
        ({"runner": "conftest.write_upper"}, "module:function"),
        ({"backends": ("imagemagik",)}, "unknown backend"),
        ({"min_files": 3, "max_files": 2}, "max_files is below min_files"),
        ({"min_files": 0}, "min_files must be at least 1"),
    ],
)
def test_a_malformed_entry_is_refused_at_registration(kwargs, message):
    base = dict(
        id="demo.x",
        family=CONVERT,
        label="X",
        group="Demo",
        inputs=("txt",),
        output_ext="x",
        runner="conftest:write_upper",
    )
    with pytest.raises(ValueError, match=message):
        Action(**{**base, **kwargs})


def test_the_same_rule_keeps_the_source_extension(registry):
    compress = registry.get("demo.compress")
    assert compress.output_ext == SAME
    assert compress.ext_for(Path("a.LOG")) == "log"
    assert registry.get("demo.to_up").ext_for(Path("a.txt")) == "up"
    assert registry.get("demo.explode").ext_for(Path("a.txt")) is None


def test_a_tool_tags_its_output_and_a_conversion_does_not(registry):
    assert registry.get("demo.compress").tag == "Compress"
    assert registry.get("demo.to_up").tag is None
    assert registry.get("demo.describe").tag is None


# --- options ----------------------------------------------------------------


def test_options_only_lists_entries_valid_for_every_file(registry, make_file):
    txt, log = make_file("a.txt"), make_file("b.log")
    assert "demo.compress" in ids(registry.options([txt]))
    assert "demo.to_up" in ids(registry.options([txt]))
    # UP only accepts txt, so a mixed selection drops it but keeps Compress.
    mixed = ids(registry.options([txt, log]))
    assert "demo.compress" in mixed
    assert "demo.to_up" not in mixed


def test_options_for_an_unknown_extension_still_lists_the_any_actions(registry, make_file):
    odd = make_file("thing.qqq")
    assert ids(registry.options([odd])) == {"demo.describe", "demo.float"}


def test_a_combine_entry_needs_its_minimum_number_of_files(registry, make_file):
    one, two = make_file("a.txt"), make_file("b.txt")
    assert "demo.merge" not in ids(registry.options([one]))
    assert "demo.merge" in ids(registry.options([one, two]))


def test_a_single_file_entry_drops_out_of_a_multi_selection(registry, make_file):
    one, two = make_file("a.txt"), make_file("b.txt")
    assert "demo.crop" in ids(registry.options([one]))
    assert "demo.crop" not in ids(registry.options([one, two]))


def test_only_folder_aware_entries_accept_a_folder(registry, tmp_path):
    folder = tmp_path / "pics"
    folder.mkdir()
    assert ids(registry.options([folder])) == {"demo.describe", "demo.float"}


def test_an_entry_whose_backend_is_missing_is_hidden_unless_asked_for(
    registry, make_file, fake_backends
):
    txt = make_file("a.txt")
    fake_backends(magick=False)
    assert "demo.needs_magick" not in ids(registry.options([txt]))

    shown = entries(registry.options([txt], include_unavailable=True), CONVERT)
    assert shown["demo.needs_magick"]["available"] is False
    assert shown["demo.needs_magick"]["missing_backends"] == ["magick"]

    fake_backends(magick=True)
    assert "demo.needs_magick" in ids(registry.options([txt]))


def test_options_groups_the_entries_by_wheel(registry, make_file):
    payload = registry.options([make_file("a.txt")])
    wheels = {w["family"]: w for w in payload["wheels"]}
    assert set(wheels) <= {CONVERT, TOOL, ACTION}
    assert wheels[CONVERT]["label"] == "Conversions"
    assert wheels[CONVERT]["default_modifier"] == "Shift"
    assert wheels[TOOL]["default_modifier"] == "Ctrl"
    assert wheels[ACTION]["default_modifier"] == "Alt"
    assert [g["group"] for g in wheels[TOOL]["groups"]] == ["Demo"]


def test_an_entry_carries_everything_the_shell_needs_to_render_it(registry, make_file):
    entry = entries(registry.options([make_file("a.txt")]), TOOL)["demo.crop"]
    assert entry["needs_editor"] is True
    assert entry["mode"] == "each"
    assert entry["max_files"] == 1
    assert [p["name"] for p in entry["params"]] == ["x", "y", "w", "h"]
    assert entry["params"][0]["required"] is True
    assert entry["params"][0]["type"] == "int"


def test_options_with_no_files_returns_the_whole_registry(registry):
    """What the tray app preloads at start up, before anything is dragged."""
    payload = registry.options(include_unavailable=True)
    assert payload["files"] == []
    assert len(ids(payload)) == len(registry)


def test_a_rejected_selection_says_why(registry, make_file):
    one = make_file("a.txt")
    assert "at least 2" in registry.get("demo.merge").why_not([one])
    assert "does not accept" in registry.get("demo.to_up").why_not([make_file("b.log")])
    assert registry.get("demo.to_up").why_not([one]) is None


# --- running ----------------------------------------------------------------


def test_run_converts_one_file(registry, make_file):
    src = make_file("a.txt", "hello")
    result = registry.run("demo.to_up", [src])
    assert result.ok
    assert [p.name for p in result.outputs] == ["a.up"]
    assert result.outputs[0].read_text(encoding="utf-8") == "HELLO"


def test_run_applies_defaults_and_params(registry, make_file):
    src = make_file("a.txt", "body")
    assert registry.run("demo.compress", [src]).outputs[0].read_text(
        encoding="utf-8"
    ).startswith("q=80")
    out = registry.run("demo.compress", [src], {"quality": "30"}).outputs[0]
    assert out.read_text(encoding="utf-8").startswith("q=30")
    assert out.name == "a (Compress) 2.txt"


def test_run_in_each_mode_handles_a_whole_batch(registry, make_file):
    files = [make_file(f"f{n}.txt", f"v{n}") for n in range(3)]
    result = registry.run("demo.to_up", files)
    assert result.ok
    assert sorted(p.name for p in result.outputs) == ["f0.up", "f1.up", "f2.up"]
    assert "3 files done" in result.message


def test_a_batch_reports_the_files_that_failed_and_keeps_the_rest(registry, make_file):
    files = [make_file("good.txt"), make_file("bad.txt"), make_file("fine.txt")]
    result = registry.run("demo.fail_on_bad", files)
    assert result.ok is False
    assert [p.name for p in result.outputs] == ["good (Picky).out", "fine (Picky).out"]
    assert result.code == "partial"
    assert result.data["failures"][0]["file"].endswith("bad.txt")
    assert "2 of 3 files done" in result.message


def test_run_in_combine_mode_gets_every_input_at_once(registry, make_file):
    a, b = make_file("a.txt", "A"), make_file("b.txt", "B")
    result = registry.run("demo.merge", [a, b])
    assert result.ok
    assert result.outputs[0].name == "a (Merge).txt"
    assert result.outputs[0].read_text(encoding="utf-8") == "AB"


def test_a_runner_may_write_several_files_and_pick_the_extension(registry, make_file):
    result = registry.run("demo.explode", [make_file("a.txt")])
    assert [p.name for p in result.outputs] == ["a (Explode 1).part", "a (Explode 2).part"]


def test_an_action_can_write_nothing_and_still_report(registry, make_file, tmp_path):
    folder = tmp_path / "stuff"
    folder.mkdir()
    result = registry.run("demo.describe", [make_file("a.txt"), folder])
    assert result.ok
    assert result.outputs == []
    assert result.data["names"] == ["a.txt", "stuff"]


def test_out_dir_is_created_and_used(registry, make_file, tmp_path):
    out = tmp_path / "deep" / "out"
    result = registry.run("demo.to_up", [make_file("a.txt")], out_dir=out)
    assert result.outputs[0] == out / "a.up"
    assert result.outputs[0].exists()


def test_an_unknown_action_is_an_error_not_a_crash(registry, make_file):
    with pytest.raises(UnknownAction, match="no such action"):
        registry.run("demo.nope", [make_file("a.txt")])


def test_a_missing_file_is_reported_by_name(registry, tmp_path):
    with pytest.raises(MissingFile, match="file not found"):
        registry.run("demo.to_up", [tmp_path / "ghost.txt"])


def test_running_an_action_on_the_wrong_type_says_so(registry, make_file):
    with pytest.raises(UnsupportedInput, match="does not accept log files"):
        registry.run("demo.to_up", [make_file("a.log")])


def test_running_with_no_files_is_rejected(registry):
    with pytest.raises(UnsupportedInput):
        registry.run("demo.to_up", [])


def test_a_missing_backend_blocks_the_run_with_an_install_hint(
    registry, make_file, fake_backends
):
    fake_backends(magick=False)
    with pytest.raises(BackendMissing) as err:
        registry.run("demo.needs_magick", [make_file("a.txt")])
    assert "ImageMagick is not installed" in str(err.value)
    assert "winget install" in str(err.value)
    assert err.value.code == "backend_missing"


def test_a_runner_blowing_up_becomes_a_message_not_a_traceback(registry, make_file):
    result = registry.run("demo.boom", [make_file("a.txt")])
    assert result.ok is False
    assert result.code == "runner_failed"
    assert "backend exploded" in result.message
    assert "Traceback" not in result.message


def test_a_failed_run_leaves_no_claimed_file_behind(registry, make_file):
    src = make_file("crash.txt")
    result = registry.run("demo.half_write", [src])
    assert result.ok is False and "encoder crashed" in result.message
    assert sorted(p.name for p in src.parent.iterdir()) == ["crash.txt"]


def test_a_good_run_drops_the_empty_claims_it_did_not_report(registry, make_file):
    src = make_file("two.txt")
    result = registry.run("demo.claim_two", [src])
    assert result.ok is True
    assert sorted(p.name for p in src.parent.iterdir()) == ["two (Two claims).out", "two.txt"]


def test_a_tool_that_names_its_own_output_is_moved_onto_the_claim(registry, make_file):
    src = make_file("doc.txt")
    (src.parent / "doc.ext").write_text("older run", encoding="utf-8")
    result = registry.run("demo.external", [src])
    assert result.ok is True
    assert result.outputs == [src.parent / "doc 2.ext"]
    assert result.outputs[0].read_text(encoding="utf-8") == "from the tool"
    assert (src.parent / "doc.ext").read_text(encoding="utf-8") == "older run"


def test_a_folder_output_is_claimed_and_kept(registry, make_file):
    src = make_file("bundle.txt")
    result = registry.run("demo.extract", [src])
    assert result.ok is True
    assert result.outputs == [src.parent / "bundle"]
    assert (src.parent / "bundle" / "inside.txt").exists()


def test_an_out_dir_that_is_a_file_has_its_own_code(registry, make_file, tmp_path):
    blocker = tmp_path / "blocker"
    blocker.write_text("", encoding="utf-8")
    with pytest.raises(UnsupportedInput) as info:
        registry.run("demo.to_up", [make_file("a.txt")], out_dir=blocker)
    assert info.value.code == "bad_out_dir"


def test_a_runner_that_forgets_to_return_a_result_is_caught(registry, make_file):
    result = registry.run("demo.bad_return", [make_file("a.txt")])
    assert result.ok is False
    assert "not a Result" in result.message


def test_a_runner_is_only_imported_when_it_runs(registry):
    assert registry.get("demo.to_up").runner == "conftest:write_upper"
    assert callable(registry.get("demo.to_up").resolve_runner())


def test_a_shell_handled_action_is_on_the_wheel_but_not_run_by_the_engine(
    registry, make_file
):
    """Share, Float and Terminal are Windows UI. The entry still lives in the
    registry so the shell renders the Alt wheel from ``peel options`` instead
    of hard coding it, and the shell performs it itself."""
    entry = entries(registry.options([make_file("a.txt")]), ACTION)["demo.float"]
    assert entry["handled_by"] == SHELL
    assert entry["available"] is True

    with pytest.raises(UnsupportedInput) as err:
        registry.run("demo.float", [make_file("b.txt")])
    assert err.value.code == "shell_action"
    assert "Peel app itself" in str(err.value)


def test_a_shell_handled_entry_must_not_name_a_runner():
    with pytest.raises(ValueError, match="no runner"):
        Action(
            id="demo.y",
            family=ACTION,
            label="Y",
            group="Files",
            handled_by=SHELL,
            runner="conftest:describe",
        )


def test_an_engine_entry_still_has_to_name_a_runner():
    with pytest.raises(ValueError, match="module:function"):
        Action(id="demo.z", family=ACTION, label="Z", group="Files")


# --- the built in family and the plugin path --------------------------------


def test_the_real_registry_loads_the_shipped_families():
    real = load_registry(force=True)
    assert "docs.sub_to_txt" in real
    assert "files.copy_path" in real


def test_a_real_conversion_writes_a_sibling_file(make_file):
    """Through a shipped entry that needs no backend, so this holds on a bare
    machine: subtitles to text is pure Python."""
    real = load_registry()
    src = make_file("notes.srt", "1\n00:00:01,000 --> 00:00:02,000\nhello\n")
    result = real.run("docs.sub_to_txt", [src])
    assert result.ok
    assert result.outputs[0] == src.parent / "notes.txt"
    assert result.outputs[0].read_text(encoding="utf-8") == "hello\n"


def test_copy_path_works_on_a_mixed_selection(make_file, tmp_path):
    real = load_registry()
    folder = tmp_path / "album"
    folder.mkdir()
    result = real.run("files.copy_path", [make_file("a.txt"), folder])
    assert result.ok
    assert result.outputs == []
    assert result.data["clipboard_text"].split("\r\n") == [
        '"' + os.path.abspath(tmp_path / "a.txt") + '"',
        '"' + os.path.abspath(folder) + '"',
    ]


def test_every_registered_runner_resolves():
    """Guards the later family beads: a typo in a dotted runner fails here."""
    for action in load_registry(force=True).all():
        if action.handled_by == SHELL:
            continue
        assert callable(action.resolve_runner()), action.id


def test_a_new_family_module_is_picked_up_without_touching_the_engine():
    """Drop a module into families/ and it is on the wheel. That is the plugin path."""
    probe = FAMILIES_DIR / "zz_probe.py"
    probe.write_text(
        textwrap.dedent(
            '''
            from peel_engine.registry import CONVERT, Registry
            from peel_engine.result import Result, ok


            def register(registry: Registry) -> None:
                registry.add(
                    id="zz.probe",
                    family=CONVERT,
                    label="Probe",
                    group="Probe",
                    inputs=("txt",),
                    output_ext="probe",
                    runner="peel_engine.families.zz_probe:run",
                )


            def run(job) -> Result:
                target = job.output_path()
                target.write_text("probe", encoding="utf-8")
                return ok([target], "probed")
            '''
        ).lstrip(),
        encoding="utf-8",
    )
    try:
        import importlib

        importlib.invalidate_caches()
        assert "peel_engine.families.zz_probe" in family_modules()
        real = load_registry(force=True)
        assert "zz.probe" in real
        assert callable(real.get("zz.probe").resolve_runner())
    finally:
        probe.unlink()
        sys.modules.pop("peel_engine.families.zz_probe", None)
        load_registry(force=True)


def test_a_family_module_without_register_is_skipped_and_reported():
    probe = FAMILIES_DIR / "zz_broken.py"
    probe.write_text("VALUE = 1\n", encoding="utf-8")
    try:
        import importlib

        importlib.invalidate_caches()
        with pytest.raises(RuntimeError, match="has no register"):
            load_registry(strict=True)
        real = load_registry(force=True)
        assert "docs.sub_to_txt" in real
        assert [w["module"] for w in real.warnings] == ["peel_engine.families.zz_broken"]
        assert "has no register" in real.warnings[0]["message"]
    finally:
        probe.unlink()
        sys.modules.pop("peel_engine.families.zz_broken", None)
        load_registry(force=True)


def test_the_shipped_families_load_with_no_warnings():
    """The loud guard: a wheel label clash between real families fails CI."""
    real = load_registry(strict=True)
    assert real.warnings == []
    assert load_registry(force=True).warnings == []


def test_a_family_whose_register_raises_is_rolled_back_whole(make_file):
    """A duplicate wheel label is fatal for that family only, and nothing it
    registered before the clash is left on the wheel."""
    probe = FAMILIES_DIR / "zz_clash.py"
    probe.write_text(
        textwrap.dedent(
            '''
            from peel_engine.registry import CONVERT


            def register(registry):
                registry.add(
                    id="zz.first", family=CONVERT, label="Clash first", group="Clash",
                    inputs=("txt",), output_ext="x", runner="peel_engine.families.zz_clash:run",
                )
                registry.add(
                    id="zz.dup", family=CONVERT, label="TXT", group="Subtitles",
                    inputs=("srt",), output_ext="txt", runner="peel_engine.families.zz_clash:run",
                )
            '''
        ).lstrip(),
        encoding="utf-8",
    )
    try:
        import importlib

        importlib.invalidate_caches()
        with pytest.raises(ValueError, match="duplicate wheel segment"):
            load_registry(strict=True)
        real = load_registry(force=True)
        assert "zz.first" not in real and "zz.dup" not in real
        assert "docs.sub_to_txt" in real
        options = real.options([make_file("a.txt")])
        assert options["ok"] is True
        assert "duplicate wheel segment" in options["warnings"][0]["message"]
        report = check()
        assert report["warnings"] and "failed to load" in report["message"]
    finally:
        probe.unlink()
        sys.modules.pop("peel_engine.families.zz_clash", None)
        load_registry(force=True)


def test_options_always_carries_a_warnings_list(registry, make_file):
    assert registry.options([make_file("a.txt")])["warnings"] == []


# --- doctor -----------------------------------------------------------------


def test_doctor_reports_every_known_backend():
    report = check()
    assert report["ok"] is True
    assert {r["name"] for r in report["backends"]} == set(backends.BY_NAME)
    for entry in report["backends"]:
        assert entry["kind"] in ("exe", "module")
        assert isinstance(entry["found"], bool)
        if not entry["found"]:
            assert entry["install"]


def test_doctor_names_the_missing_backends_and_the_actions_they_block():
    report = check()
    found = {r["name"] for r in report["backends"] if r["found"]}
    assert set(report["missing"]) == set(backends.BY_NAME) - found
    assert report["actions_total"] == len(load_registry())
    blocked = {
        a.id
        for a in load_registry().all()
        if any(name in report["missing"] for name in a.backends)
    }
    assert set(report["actions_blocked"]) == blocked
    assert "backends found" in report["message"]


def test_doctor_probes_again_rather_than_trusting_a_stale_cache(fake_backends):
    """An install script runs doctor straight after installing a backend."""
    fake_backends(**{name: False for name in backends.BY_NAME})
    report = check()
    real_ffmpeg = backends.find("ffmpeg")
    by_name = {r["name"]: r for r in report["backends"]}
    assert by_name["ffmpeg"]["found"] is (real_ffmpeg is not None)


def test_doctor_finds_a_backend_through_the_env_override(tmp_path, fake_backends):
    fake_backends()
    fake = tmp_path / "ffmpeg"
    fake.write_text("#!/bin/sh\n", encoding="utf-8")
    import os

    os.environ["PEEL_BACKEND_FFMPEG"] = str(fake)
    try:
        backends.clear_cache()
        assert backends.find("ffmpeg") == fake
    finally:
        del os.environ["PEEL_BACKEND_FFMPEG"]
        backends.clear_cache()


# --- CLI --------------------------------------------------------------------


def run_cli(argv, capsys) -> tuple[int, dict]:
    code = main(argv)
    return code, json.loads(capsys.readouterr().out)


def test_cli_options_prints_the_wheels(make_file, capsys):
    code, payload = run_cli(["options", str(make_file("a.srt"))], capsys)
    assert code == 0
    assert payload["ok"] is True
    assert "docs.sub_to_txt" in ids(payload)


def test_cli_run_prints_outputs_and_exits_zero(make_file, capsys):
    src = make_file("a.srt", "1\n00:00:01,000 --> 00:00:02,000\nhello\n")
    code, payload = run_cli(["run", "docs.sub_to_txt", str(src)], capsys)
    assert code == 0
    assert payload == {
        "ok": True,
        "outputs": [str(src.parent / "a.txt")],
        "message": "Wrote 1 subtitle to a.txt",
    }


def test_cli_run_failure_exits_one_with_a_code(tmp_path, capsys):
    code, payload = run_cli(["run", "docs.sub_to_txt", str(tmp_path / "ghost.srt")], capsys)
    assert code == 1
    assert payload["ok"] is False
    assert payload["code"] == "missing_file"
    assert "ghost.srt" in payload["message"]


def test_cli_rejects_an_unknown_action(make_file, capsys):
    code, payload = run_cli(["run", "nope.nope", str(make_file("a.txt"))], capsys)
    assert code == 1
    assert payload["code"] == "unknown_action"


def test_cli_doctor_prints_a_report(capsys):
    code, payload = run_cli(["doctor"], capsys)
    assert code == 0
    assert payload["backends"]
    assert payload["python"].startswith("3.")


def test_cli_param_pairs_are_parsed():
    assert parse_params(["quality=30", "mode=blur"]) == {"quality": "30", "mode": "blur"}


def test_cli_param_value_can_come_from_a_file(tmp_path):
    shapes = tmp_path / "shapes.json"
    shapes.write_text('[{"x": 1}]', encoding="utf-8")
    assert parse_params([f"shapes=@{shapes}"]) == {"shapes": '[{"x": 1}]'}


def test_cli_params_file_is_merged_and_pairs_win(tmp_path):
    params = tmp_path / "p.json"
    params.write_text('{"quality": 10, "mode": "box"}', encoding="utf-8")
    assert parse_params(["quality=90"], params) == {"quality": "90", "mode": "box"}


@pytest.mark.parametrize(
    "argv",
    [
        [],
        ["bogus"],
        ["run"],
        ["run", "docs.sub_to_txt"],
        ["run", "docs.sub_to_txt", "a.srt", "--param"],
        ["run", "docs.sub_to_txt", "a.srt", "--param", "noequals"],
        ["--indent", "x", "options"],
        ["options", "--nope"],
    ],
)
def test_cli_misuse_answers_one_json_object_with_code_usage(argv, capsys):
    code = main(argv)
    captured = capsys.readouterr()
    payload = json.loads(captured.out)
    assert code == 1
    assert payload["ok"] is False and payload["code"] == "usage"
    assert payload["message"]
    assert captured.err == ""


def test_windows_folder_variables_expand_with_fallbacks(monkeypatch):
    for name in ("ProgramFiles", "PROGRAMFILES", "LOCALAPPDATA", "USERPROFILE"):
        monkeypatch.delenv(name, raising=False)
    assert backends.expand_windows(r"%ProgramFiles%\x") == [r"C:\Program Files\x"]
    assert backends.expand_windows(r"%LOCALAPPDATA%\x") == []
    monkeypatch.setenv("ProgramFiles", r"D:\Apps")
    monkeypatch.setenv("USERPROFILE", r"C:\Users\d")
    assert backends.expand_windows(r"%ProgramFiles%\x") == [r"D:\Apps\x", r"C:\Program Files\x"]
    assert backends.expand_windows(r"%LOCALAPPDATA%\x") == [r"C:\Users\d\AppData\Local\x"]


@pytest.mark.parametrize("bad", ["quality", "=30", ""])
def test_cli_rejects_a_malformed_param(bad):
    with pytest.raises(InvalidParams, match="expected KEY=VALUE"):
        parse_params([bad])


def test_cli_rejects_a_params_file_that_is_not_an_object(tmp_path):
    params = tmp_path / "p.json"
    params.write_text("[1, 2]", encoding="utf-8")
    with pytest.raises(InvalidParams, match="JSON object"):
        parse_params([], params)


def test_cli_options_can_include_unavailable_entries(make_file, capsys, fake_backends):
    fake_backends()
    code, payload = run_cli(
        ["options", "--include-unavailable", str(make_file("a.txt"))], capsys
    )
    assert code == 0
    assert payload["ok"] is True


# --- the speed budget -------------------------------------------------------

HEAVY = ("PIL", "fitz", "pymupdf", "pdf2docx", "numpy", "pandas", "openpyxl", "docx", "pptx")


def test_listing_options_imports_no_heavy_backend():
    """The spec budgets ``peel options`` under 300 ms, which only holds while
    the family modules keep their heavy imports inside the runners."""
    script = (
        "import json, sys;"
        "from peel_engine.registry import load_registry;"
        "load_registry().options([__import__('pathlib').Path('a.txt')]);"
        "print(json.dumps(sorted(sys.modules)))"
    )
    proc = subprocess.run(
        [sys.executable, "-c", script],
        cwd=ENGINE_DIR,
        capture_output=True,
        text=True,
        timeout=60,
        check=True,
    )
    loaded = set(json.loads(proc.stdout))
    assert loaded.isdisjoint(HEAVY), sorted(loaded & set(HEAVY))


def test_building_the_registry_stays_well_inside_the_budget():
    from peel_engine.registry import build_time_ms

    assert build_time_ms() < 300
