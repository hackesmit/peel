"""hq-73mx: doctor reads the LibreOffice version without running soffice."""
from peel_engine import backends


def test_soffice_version_read_from_bootstrap_ini_never_runs(tmp_path, monkeypatch):
    prog = tmp_path / "program"
    prog.mkdir()
    exe = prog / "soffice.exe"
    exe.write_text("")
    (prog / "bootstrap.ini").write_text(
        "[Bootstrap]\nInstallMode=<installmode>\nProductKey=LibreOffice 26.8\n")
    monkeypatch.setattr(backends, "find", lambda name: exe)

    def boom(*a, **k):
        raise AssertionError("soffice must not be started")

    monkeypatch.setattr(backends.subprocess, "run", boom)
    assert backends.version("soffice") == "LibreOffice 26.8"


def test_soffice_version_empty_without_bootstrap_ini(tmp_path, monkeypatch):
    exe = tmp_path / "soffice"
    exe.write_text("")
    monkeypatch.setattr(backends, "find", lambda name: exe)
    assert backends.version("soffice") == ""
