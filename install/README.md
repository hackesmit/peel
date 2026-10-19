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

Python module backends (`pillow`, `pillow-heif`, `pymupdf`, `pdf2docx`) and
`pytest` are installed into the venv with pip, not winget.

libvips ships as a winget portable package and is not placed on PATH by its
installer; the script finds its `vips.exe` under the WinGet Packages tree and
adds that folder to the user PATH itself.

## Layout it creates

- `%LOCALAPPDATA%\Peel\venv`: the Python environment (engine + backends).
- `%LOCALAPPDATA%\Peel\bin\peel.cmd`: shim that forwards to the venv's `peel`.
- `%LOCALAPPDATA%\Peel\bin` is added to the user PATH, so `peel` works in any
  new shell.

The installed app (`C:\Program Files\Peel` or `%LOCALAPPDATA%\Peel`) is owned by
the shell-install bead, not this script.

## Python version

George runs CPython 3.14, and every module backend has a working 3.14 wheel
(verified 2026-10-19). If a future wheel has no 3.14 build, install 3.12
(`winget install Python.Python.3.12`) and re-run: the venv step picks the
newest installed CPython via the `py -3` launcher.

## Verify

```
ssh george "peel doctor"
ssh george "cd C:\peel-dev\main\engine && python -m pytest -q tests\test_core.py"
```

`peel doctor` reports every backend as found; the test suite exits 0.
