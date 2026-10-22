# Peel

Peel converts, edits and moves files on Windows from a wheel that opens while
you drag. Start dragging a file, tap Shift, and drop it on "MP3" or "PDF".
The result appears next to the original. Everything runs on your own PC;
nothing is uploaded.

Peel is an independent, clean-room recreation of the behaviour of
[Tangerine for Mac](https://tangerineformac.com/), built only from its public
website and demo. It shares no code, icons or other assets with Tangerine and
is not affiliated with it.

## What it does

- **Shift wheel, conversions:** images (JPG, PNG, WebP, HEIC, TIFF, AVIF, BMP,
  SVG in), audio (MP3, M4A, WAV, FLAC, OGG, Opus, AIFF, WMA), video (MP4, MOV,
  MKV, WebM, AVI, WMV, GIF, plus MP3 audio extract), PDF to Word, PowerPoint,
  Excel, images, EPUB, text or Markdown, documents, slides, spreadsheets,
  subtitles and archives.
- **Ctrl wheel, file tools:** compress, crop, edit, annotate and redact images;
  trim, split, join, speed up, crop, redact and snapshot video; trim,
  normalize, bleep and tag audio; merge, split and reorder PDF pages; extract
  text; extract archives. Tools that need a choice open a small editor.
- **Alt wheel, file actions:** Share, Float (a pinned thumbnail you can drag
  out again), Copy, Move, Copy Path and Terminal.
- **No drag needed:** select files in Explorer and press Ctrl+Alt+Enter.

Outputs never overwrite anything: a second run writes `photo 2.jpg`.

How to use it: [docs/USAGE.md](docs/USAGE.md). Full scope:
[docs/SPEC.md](docs/SPEC.md).

## Install (Windows 10 or 11)

You need [Python 3.11 or newer](https://www.python.org/downloads/windows/)
with the `py` launcher, and [winget](https://learn.microsoft.com/windows/package-manager/winget/)
(built in on Windows 11).

1. Clone or download this repository, for example to `C:\peel`.
2. In an **administrator** PowerShell, install the backends (FFmpeg,
   ImageMagick, libvips, LibreOffice, Pandoc, 7-Zip, the .NET 8 SDK) and the
   engine:

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File C:\peel\install\setup.ps1 -EnginePath C:\peel\engine
   ```

3. In a normal PowerShell, build and install the app for your user:

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File C:\peel\install\publish.ps1 -Source C:\peel
   ```

Peel starts, shows an icon in the tray, and starts again when you sign in. To
upgrade, pull the new version and run step 3 again: if the new build does not
start, Peel goes back to the one you had. Details: [install/README.md](install/README.md).

## Layout

| Folder | What |
| --- | --- |
| `engine/` | Python package `peel_engine` and the `peel` command: every conversion, tool and action is a registry entry. Runs on Windows and Linux. Tests: `cd engine && python -m pytest -q` |
| `shell/` | The Windows tray app (C# .NET 8, WPF): drag detection, the wheels, the editors. Tests: `cd shell && dotnet test -c Release`. See [shell/README.md](shell/README.md) |
| `install/` | Setup, install, upgrade with rollback, and a smoke test |
| `docs/` | Usage guide and spec |

## Licence

Peel is released under the [MIT licence](LICENSE).

Peel installs and calls other programs and Python packages that keep their
own licences, for example FFmpeg (LGPL/GPL), 7-Zip (LGPL), LibreOffice (MPL),
Pandoc (GPL) and PyMuPDF (AGPL). They are downloaded by the install scripts,
not included in this repository.
