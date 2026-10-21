# Peel install (George)

`setup.ps1` prepares George (Windows 11) to run the Peel engine: it installs
the external backends, builds a Python venv, installs the engine into it, and
puts a `peel` command on your PATH. It is idempotent, so re-run it any time.

## Run it

From hqbox, with the engine source already synced to `C:\peel-dev\main`:

```
ssh george "powershell -NoProfile -ExecutionPolicy Bypass -File C:\peel-dev\main\install\setup.ps1"
```

On George directly, open PowerShell and run the same `.\setup.ps1`.

winget machine-scope installs (LibreOffice, ImageMagick, the .NET SDK) need an
elevated session; an admin ssh session or an elevated PowerShell satisfies that
without a prompt.

### Options

- `-EnginePath <path>`: engine source to install editable
  (default `C:\peel-dev\main\engine`).
- `-SkipWinget`: venv and shim only; skip the backend installs. Use when the
  backends are already present and you only changed the engine.

## What it installs

| Backend | winget id | Used for |
| --- | --- | --- |
| FFmpeg (+ ffprobe) | `Gyan.FFmpeg` | audio and video |
| ImageMagick | `ImageMagick.ImageMagick` | image conversion fallback |
| libvips | `libvips.libvips` | fast image conversion |
| LibreOffice | `TheDocumentFoundation.LibreOffice` | documents, slides, spreadsheets |
| Pandoc | `JohnMacFarlane.Pandoc` | document conversion |
| 7-Zip | `7zip.7zip` | archives |
| .NET 8 SDK | `Microsoft.DotNet.SDK.8` | builds the WPF shell (shell bead) |

Python module backends (`pillow`, `pillow-heif`, `pymupdf` 1.26.x, `pdf2docx`,
`openpyxl`, `python-pptx`, `python-docx`) and `pytest` are installed into
the venv with pip, not winget. They are pinned in `modules.txt`, which
setup.ps1 and publish.ps1 both read.

libvips ships as a winget portable package. winget normally exposes `vips` via
`%LOCALAPPDATA%\Microsoft\WinGet\Links`; only when that link is missing does
the script search the WinGet Packages tree for `vips.exe` and add its folder to
the user PATH.

## Layout it creates

- `%LOCALAPPDATA%\Peel\venv`: the Python environment (engine + backends).
- `%LOCALAPPDATA%\Peel\bin\peel.cmd`: shim that forwards to the venv's `peel`.
- `%LOCALAPPDATA%\Peel\bin` is added to the user PATH, so `peel` works in any
  new shell.

The installed app (`C:\Program Files\Peel` or `%LOCALAPPDATA%\Peel`) is owned by
the shell-install bead, not this script.

Once `publish.ps1` has run, `app` and `venv` are junctions to
`%LOCALAPPDATA%\Peel\versions\<stamp>\app` and `...\venv`, the pair in
use, and `publish.ps1` owns that venv. Re-running `setup.ps1` then leaves it
exactly as it is: no pip upgrade, no editable engine, no module installs. It
still installs the winget backends, writes the shim and runs `peel doctor`.
`-RecreateVenv` on a junction refuses (exit 1); run `publish.ps1` instead,
which builds a fresh pair. A Python module backend missing from the published
venv comes back with the next `publish.ps1`: it installs the modules of the
current venv and then everything in `modules.txt` (a pin wins over the
frozen version).

`test-setup.ps1` checks this against a scratch root (`-InstallRoot`) whose
venv is a junction to a stand in venv: a plain `-SkipWinget` run and a
`-RecreateVenv` run leave every file in the target as it was, and the user
PATH is never touched:

```
ssh george "powershell -NoProfile -ExecutionPolicy Bypass -File C:\peel-dev\<branch>\install\test-setup.ps1"
```

## publish.ps1 and its tests

`publish.ps1` installs or upgrades the app and the engine as a pair. The
journal `%LOCALAPPDATA%\Peel\install.json` names the last verified pair (it
stayed up for 10 s) and the candidate; the verified pair is never deleted
until a newer one is verified. A new build that quits, or whose start fails,
puts the verified pair and settings.json back (exit 6) by moving the two
junctions, so a locked file in the failed build cannot block it. A run killed
in the middle is put back together by the next run. Two runs at once: the
second exits 5. See its header for the steps and exit codes.
`test-publish.ps1` runs it against a scratch root in %TEMP% with a stand in
Peel.exe and checks each case, plus the loose JSON reader in `LooseJson.ps1`.
It needs someone signed in at George, and it never touches the installed
Peel. `-RealEngine` adds one run of the real pip build (`-NoStart`) from a
scratch venv without openpyxl, and checks that the new venv has every pin in
`modules.txt` and a green `peel doctor`; it takes a few minutes:

```
ssh george "powershell -NoProfile -ExecutionPolicy Bypass -File C:\peel-dev\<branch>\install\test-publish.ps1 -RealEngine"
```

## Python version

George runs CPython 3.14, and every module backend has a working 3.14 wheel
(verified 2026-10-19). If a future wheel has no 3.14 build, install 3.12
(`winget install Python.Python.3.12`) and re-run with
`setup.ps1 -PythonVersion 3.12 -RecreateVenv`. Without `-RecreateVenv` an
existing venv is reused as is.

The engine is installed editable from `-EnginePath` (default
`C:\peel-dev\main\engine`), so changing or deleting that folder changes or
breaks the installed `peel`. Sync only merged main there.

## Verify

```
ssh george "peel doctor"
ssh george "cd /d C:\peel-dev\main\engine && %LOCALAPPDATA%\Peel\venv\Scripts\python.exe -m pytest -q tests\test_core.py"
```

`peel doctor` reports every backend as found; the test suite exits 0.
