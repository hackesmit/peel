"""Adversarial review checks for hq-plct.1, round 2.

Not collected by the normal suite on purpose (no test_ prefix). Run:
    cd engine && python3 -m pytest -q tests/review2_hq_plct_1.py
"""

from __future__ import annotations

import importlib
import json
import multiprocessing as mp
import os
import subprocess
import sys
from pathlib import Path

import pytest

from peel_engine import registry as reg
from peel_engine.registry import Action, Job, Registry, TOOL, load_registry
from peel_engine.result import Result

ENGINE = Path(__file__).resolve().parents[1]
FAMILIES_DIR = ENGINE / "peel_engine" / "families"


def _cli(*args: str, cwd: Path) -> subprocess.CompletedProcess:
    return subprocess.run(
        [sys.executable, "-m", "peel_engine.cli", *args],
        cwd=cwd, capture_output=True, text=True,
        env={"PYTHONPATH": str(ENGINE), "PATH": "/usr/bin:/bin"},
    )


def _action(runner: str = "m:f", mode: str = "each") -> Action:
    return Action(id="r2.tool", family=TOOL, label="Compress", group="G",
                  inputs=("txt",), output_ext="txt", runner=runner, mode=mode)


def _claim_in_child(src: str, q) -> None:
    path = Job(action=_action(), inputs=[Path(src)]).output_path()
    path.write_text(str(os.getpid()), encoding="utf-8")
    q.put(str(path))


# B2 variant: real separate processes, not threads.
def test_r2_real_processes_never_share_a_name(tmp_path):
    src = tmp_path / "photo.txt"
    src.write_text("x", encoding="utf-8")
    ctx = mp.get_context("fork")
    q = ctx.Queue()
    procs = [ctx.Process(target=_claim_in_child, args=(str(src), q)) for _ in range(16)]
    for p in procs:
        p.start()
    for p in procs:
        p.join()
    names = [q.get() for _ in procs]
    assert len(set(names)) == 16
    for n in names:
        assert Path(n).read_text(encoding="utf-8").isdigit()


# Cleanup must never touch a file the user already had, empty ones included.
def test_r2_failed_run_keeps_preexisting_user_files(tmp_path):
    src = tmp_path / "photo.txt"
    src.write_text("x", encoding="utf-8")
    user_full = tmp_path / "photo (Compress).txt"
    user_full.write_text("mine", encoding="utf-8")
    user_empty = tmp_path / "photo (Compress) 2.txt"
    user_empty.write_text("", encoding="utf-8")

    def runner(job: Job) -> Result:
        a = job.output_path()
        b = job.reserve(user_empty)  # runner asks for the user's own name
        a.write_text("half", encoding="utf-8")
        b.write_text("half", encoding="utf-8")
        raise RuntimeError("backend crashed")

    res = reg._call(runner, Job(action=_action(), inputs=[src]))
    assert res.ok is False
    assert user_full.read_text(encoding="utf-8") == "mine"
    assert user_empty.exists() and user_empty.read_text(encoding="utf-8") == ""
    left = sorted(p.name for p in tmp_path.iterdir())
    assert left == ["photo (Compress) 2.txt", "photo (Compress).txt", "photo.txt"], left


# A good run that reports an empty output by its absolute path must keep it.
# path_key uses normpath, not abspath, so a relative claim and an absolute
# report do not match and the reported output is deleted. (Non-blocking.)
def test_r2_reported_output_in_absolute_form_is_kept(tmp_path, monkeypatch):
    monkeypatch.chdir(tmp_path)
    Path("empty.txt").write_text("", encoding="utf-8")

    def runner(job: Job) -> Result:
        out = job.output_path()
        return Result(ok=True, outputs=[out.resolve()], message="done")

    res = reg._call(runner, Job(action=_action(), inputs=[Path("empty.txt")]))
    assert res.ok
    assert res.outputs[0].exists(), f"{res.outputs[0]} reported but deleted"


# B1 variant: a family that calls sys.exit at import (SystemExit is not an
# Exception subclass). (Non-blocking: unusual in a family module.)
def test_r2_family_exiting_on_import_does_not_kill_options(tmp_path):
    probe = FAMILIES_DIR / "zz_review2_exits.py"
    probe.write_text("import sys\nsys.exit('no backend')\n", encoding="utf-8")
    try:
        importlib.invalidate_caches()
        src = tmp_path / "a.txt"
        src.write_text("hi", encoding="utf-8")
        proc = _cli("options", str(src), cwd=tmp_path)
        payload = json.loads(proc.stdout or "{}")
        assert payload.get("ok") is True, (proc.stdout, proc.stderr)
    finally:
        probe.unlink()
        sys.modules.pop("peel_engine.families.zz_review2_exits", None)
        load_registry(force=True)


# B1 variant: with one family broken, a healthy family still runs and doctor
# names the broken one.
def test_r2_broken_family_leaves_run_and_doctor_working(tmp_path):
    probe = FAMILIES_DIR / "zz_review2_raises.py"
    probe.write_text("raise ImportError('fitz missing')\n", encoding="utf-8")
    try:
        importlib.invalidate_caches()
        src = tmp_path / "a.srt"
        src.write_text("1\n00:00:01,000 --> 00:00:02,000\nhello\n", encoding="utf-8")
        run = json.loads(_cli("run", "docs.sub_to_txt", str(src), cwd=tmp_path).stdout)
        assert run["ok"] is True and (tmp_path / "a.txt").read_text() == "hello\n"
        opts = json.loads(_cli("options", str(src), cwd=tmp_path).stdout)
        assert any(w["module"].endswith("zz_review2_raises") for w in opts["warnings"])
        doc = _cli("doctor", cwd=tmp_path)
        assert "zz_review2_raises" in doc.stdout
    finally:
        probe.unlink()
        sys.modules.pop("peel_engine.families.zz_review2_raises", None)
        load_registry(force=True)


# B3 variants: every misuse answers one JSON object on stdout, exit 1.
@pytest.mark.parametrize("argv", [
    (), ("doctor", "extra"), ("--indent",), ("--indent", "x", "options"),
    ("run", "docs.sub_to_txt", "a.srt", "--par", "k=v"),
    ("run", "docs.sub_to_txt", "a.srt", "--out-dir"),
    ("run", "docs.sub_to_txt", "a.srt", "--param", "=x"),
])
def test_r2_misuse_is_json(tmp_path, argv):
    proc = _cli(*argv, cwd=tmp_path)
    payload = json.loads(proc.stdout)
    assert proc.returncode == 1 and payload["ok"] is False
    assert payload["code"] == "usage" and proc.stderr == ""


def test_scratch_dir_sits_beside_the_claim_and_is_removed(tmp_path):
    from peel_engine.registry import load_registry, Job

    reg = load_registry()
    action = reg.all()[0]
    src = tmp_path / "a.txt"
    src.write_text("x")
    job = Job(action=action, inputs=[src])
    scratch = job.scratch_dir(src)
    assert scratch.parent == tmp_path and scratch.is_dir()
    job.release_unused(failed=False)
    assert not scratch.exists()
