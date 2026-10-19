# Peel: spec

Peel is a clean-room Windows recreation of the behavior of Tangerine for Mac
(https://tangerineformac.com/). It is built from Tangerine's public website and
demo only. No Tangerine binary, code or asset is copied or decompiled.

Target machine: George (Windows 11, Ryzen 7 9800X3D). Everything runs locally;
no file leaves the PC.

## Interaction model

- Drag any file (or several) in Explorer or on the desktop. While still holding
  the mouse button, tap a modifier key to open a radial wheel under the cursor:
  - Shift: conversions (target formats valid for the dragged files).
  - Ctrl: file tools (compress, crop, trim, ...).
  - Alt: file actions (share, float, copy, move, copy path, terminal).
  (Tangerine uses Shift / Command / Control; Windows maps Command to Ctrl and
  Control to Alt. All three keys are remappable in Settings.)
- The wheel stays open while the drag continues. Drop on a segment to run it;
  release elsewhere or press Esc to cancel. Tapping another modifier switches
  wheels.
- Keyboard path: with files selected in Explorer, a global hotkey (default
  Ctrl+Alt+Enter) opens the conversion wheel at the cursor. Arrow keys select,
  Enter applies, Esc cancels, Ctrl/Alt switch wheels.
- Output is written next to the source as `<name>.<ext>`, or
  `<name> (<tool>).<ext>` when a tool runs, never overwriting (append ` 2`, ` 3`).
- Batch: a multi-file drag applies the action to every file. Merge/join/collage
  combine them into one output.
- Tray icon: settings, recent jobs, open log, quit. Starts with Windows.
- No conversion allowance, no paywall.

## Conversions (Shift wheel)

| Family | Inputs | Outputs |
| --- | --- | --- |
| Images | JPG, PNG, WebP, HEIC, TIFF, AVIF, BMP, SVG (input only) | JPG, PNG, WebP, HEIC, TIFF, AVIF, BMP, PDF |
| Audio | MP3, M4A, WAV, FLAC, OGG, Opus, AIFF, WMA | same set |
| Video | MP4, MOV, MKV, WebM, AVI, WMV, GIF | same set, plus MP3 (audio extract) |
| PDF | PDF | DOCX, PPTX, XLSX, JPG, PNG (all pages, 300 DPI), EPUB, TXT, MD |
| Documents | DOCX, Markdown, TXT, EPUB, RTF, ODT | each other, plus PDF and HTML |
| Slides | PPTX, PPT (input only) | PDF, JPG, PNG (one image per slide) |
| Spreadsheets | XLSX, ODS, CSV, TSV, XLS (input only) | each other, plus PDF |
| Subtitles | SRT, VTT | each other, plus TXT |
| Archives | ZIP, TAR, GZIP, 7Z, RAR (input only) | ZIP, TAR, GZIP, 7Z |

Apple-only formats (Pages, RTFD, text clippings) are dropped: they do not exist
on Windows.

## File tools (Ctrl wheel)

Image: Compress, Crop, Edit (rotate, flip, brightness, contrast, saturation),
Annotate (pen, arrow, box, text), Redact (blur or black box regions), Add
background (fill transparency with a color), Collage (multi-file grid), Create
PDF (multi-file), Metadata (view, strip, edit title/author/location).

Video: Compress, Crop, Trim, Split (at timestamps or every N seconds), Join
(multi-file), Speed (0.25x to 4x), Remove audio, Redact (blur a region),
Snapshot (save frames at a time or every N seconds).

Audio: Compress, Trim, Normalize volume (EBU R128), Bleep (beep over time
ranges), Channels (mono / stereo), Visualize (waveform video), Metadata (title,
artist, album).

PDF: Compress, Merge (multi-file), Split (each page or ranges), Organize pages
(reorder, rotate, delete).

Documents/EPUB/subtitles: Extract text and Markdown, Extract subtitle text.

Archives: Extract.

Tools that need user input (crop, redact, annotate, edit, trim, split, organize
pages, bleep, metadata) open a small editor window; the rest run immediately
with sensible defaults.

## File actions (Alt wheel)

Share (Windows Nearby Share / share sheet, in place of AirDrop), Float (pin a
small always-on-top thumbnail that can be dragged out again), Copy (to
clipboard as files), Move (to a recent folder or a picker), Copy Path, Terminal
(open Windows Terminal in the file's folder). Actions work on folders and mixed
selections.

## Architecture

- `engine/`: Python 3 package `peel_engine`, cross-platform (tested on Linux
  hqbox and on George). Data-driven registry: given input paths it returns the
  valid conversions, tools and actions as JSON; given an action id and params it
  runs the backend and returns output paths. CLI: `peel options <files...>`,
  `peel run <action-id> <files...> [--param k=v]`, `peel doctor`.
  Backends: ffmpeg, libvips or ImageMagick, pillow + pillow-heif, PyMuPDF,
  pdf2docx, LibreOffice headless, pandoc, 7-Zip.
- `shell/`: C# .NET 8 WPF tray app for Windows. Low-level mouse and keyboard
  hooks detect a drag in progress plus a modifier tap, then show a topmost
  radial overlay that is an OLE drop target. It calls the engine CLI and shows
  progress toasts. Editors are WPF windows that collect params for the engine.
- `install/`: George setup script (winget packages, Python venv, startup entry).
