"""Adversarial review checks for hq-plct.1 (round 1). Renamed from
review_hq_plct_1.py after the fixes so they run with the normal suite; the
assertions are the reviewer's, unchanged.
"""

from __future__ import annotations

import importlib
import json
import subprocess
import sys
from pathlib import Path

import pytest

from peel_engine.registry import Action, Job, load_registry, TOOL

ENGINE = Path(__file__).resolve().parents[1]
FAMILIES_DIR = ENGINE / "peel_engine" / "families"


def _cli(*args: str, cwd: Path) -> subprocess.CompletedProcess:
    return subprocess.run(
        [sys.executable, "-m", "peel_engine.cli", *args],
        cwd=cwd,
        capture_output=True,
        text=True,
        env={"PYTHONPATH": str(ENGINE), "PATH": "/usr/bin:/bin"},
    )


# B1: one broken family module must not take every wheel down with it.
def test_review_a_family_that_raises_on_import_does_not_kill_options(tmp_path):
    probe = FAMILIES_DIR / "zz_review_raises.py"
    probe.write_text("raise ImportError('pillow not installed')\n", encoding="utf-8")
    try:
        importlib.invalidate_caches()
        src = tmp_path / "a.srt"
        src.write_text("1\n00:00:01,000 --> 00:00:02,000\nhello\n", encoding="utf-8")
        proc = _cli("options", str(src), cwd=tmp_path)
        payload = json.loads(proc.stdout)
        ids = [e["id"] for w in payload.get("wheels", []) for g in w["groups"] for e in g["entries"]]
        assert payload["ok"] is True, payload
        assert "docs.sub_to_txt" in ids and "files.copy_path" in ids
    finally:
        probe.unlink()
        sys.modules.pop("peel_engine.families.zz_review_raises", None)
        load_registry(force=True)


# B2: "never overwrite" must hold across two runs that overlap in time
# (Daniel drops the same file twice, or two wheel actions fire together).
def test_review_two_overlapping_runs_do_not_overwrite_each_other(tmp_path):
    src = tmp_path / "photo.txt"
    src.write_text("x", encoding="utf-8")
    action = Action(
        id="r.tool", family=TOOL, label="Compress", group="G",
        inputs=("txt",), output_ext="txt", runner="m:f",
    )
    # Two separate peel processes each build their own Job; both pick a name
    # before either has written its file, exactly as a slow ffmpeg run would.
    first = Job(action=action, inputs=[src]).output_path()
    second = Job(action=action, inputs=[src]).output_path()
    first.write_text("run one", encoding="utf-8")
    second.write_text("run two", encoding="utf-8")
    assert first != second, f"both runs were handed {first}; run two overwrote run one"


# B3: the CLI promises one JSON object on stdout for every call; the shell
# parses stdout. Bad or missing arguments currently print argparse usage to
# stderr, nothing on stdout, exit 2.
@pytest.mark.parametrize(
    "argv",
    [
        ("run",),
        ("run", "docs.sub_to_txt"),
        ("bogus",),
        ("run", "docs.sub_to_txt", "a.srt", "--param"),
    ],
)
def test_review_bad_arguments_still_answer_in_json(tmp_path, argv):
    proc = _cli(*argv, cwd=tmp_path)
    payload = json.loads(proc.stdout)  # JSONDecodeError today: stdout is empty
    assert payload["ok"] is False
    assert proc.returncode == 1


# Non-blocking: an --out-dir that is an existing file comes back as code
# "internal" with a Python exception name instead of a structured code.
def test_review_out_dir_that_is_a_file_is_a_structured_error(tmp_path):
    src = tmp_path / "a.srt"
    src.write_text("1\n00:00:01,000 --> 00:00:02,000\nhello\n", encoding="utf-8")
    blocker = tmp_path / "out"
    blocker.write_text("", encoding="utf-8")
    proc = _cli("run", "docs.sub_to_txt", str(src), "--out-dir", str(blocker), cwd=tmp_path)
    payload = json.loads(proc.stdout)
    assert payload["ok"] is False
    assert payload.get("code") != "internal", payload
