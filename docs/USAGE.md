# Using Peel

Peel converts, edits and moves files from a wheel that opens while you drag.
Everything runs on this PC. It lives in the tray (the orange peel icon near the
clock) and starts when you sign in to Windows.

## Drag a file

1. Start dragging any file (or several) in Explorer or on the desktop.
2. Keep holding the mouse button and tap a key:
   - **Shift**: conversions (PNG to JPG, DOCX to PDF, MP4 to MP3, ...).
   - **Ctrl**: file tools (Compress, Zip, Trim, Crop, ...).
   - **Alt**: file actions (Copy Path; Share, Float, Copy, Move and Terminal
     are coming).
3. Move onto a segment and let go. The result appears next to the original
   file, and a small notice says when it is done.

To cancel, let go in the middle of the wheel (the normal drop happens), let go
outside it, or press **Esc**. Tap a different key to switch wheels. When a
wheel has more entries than fit, the last segment says "More".

## Without a drag

Select files in Explorer and press **Ctrl+Alt+Enter**. The conversion wheel
opens at the pointer. Use the **arrow keys** to choose, **Enter** to run and
**Esc** to close. Press **Ctrl** or **Alt** to switch to the tools or the
actions wheel.

## Where the output goes

Peel writes `photo.jpg` next to `photo.png`, or `photo (Compress).png` for a
tool. It never overwrites a file: a second run makes `photo 2.jpg`.

## Settings

Right-click (or click) the tray icon and choose **Settings...**. Starting Peel
again from its folder also opens the settings.

- **Wheels**: choose which key opens each wheel. Each wheel needs its own key.
- **Keyboard shortcut**: click the box and press the new shortcut. It needs
  Ctrl, Alt or Win plus one key. **Reset** goes back to Ctrl+Alt+Enter.
- **Output**: next to the source file, or always into one folder. If that
  folder is missing (for example an unplugged drive), Peel writes next to the
  source instead.
- **Compress quality**: Smaller files, Balanced or Better quality. It changes
  what Compress does to images and audio when you do not choose a value.
- **Start up**: start Peel when you sign in.

Changes apply at once, no restart. They are saved in
`%APPDATA%\Peel\settings.json`.

## The tray menu

- **Recent jobs**: click one to show its output in Explorer.
- **Settings...**: see above.
- **Open log**: what Peel did, for when something looks wrong.
- **Quit Peel**: stops it until the next sign in, or until you start it again.

## If something is wrong

- **The wheel does not open**: check that Peel is in the tray. If it is not,
  start `%LOCALAPPDATA%\Peel\app\Peel.exe`.
- **"another app uses" the shortcut**: pick a different shortcut in Settings.
- **A format is missing from the wheel**: the engine hides what this PC cannot
  do. In PowerShell, `peel doctor` lists what is missing.
- **Anything else**: tray, **Open log**, and send the last lines to the HQ.

## Where it is installed

| What | Where |
| --- | --- |
| The app | `%LOCALAPPDATA%\Peel\app` |
| The engine and its Python | `%LOCALAPPDATA%\Peel\engine`, `%LOCALAPPDATA%\Peel\venv` |
| Settings | `%APPDATA%\Peel\settings.json` |
| Log | `%LOCALAPPDATA%\Peel\logs\peel.log` |
| Start at sign in | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, value `Peel` |

Nothing needs administrator rights. To upgrade, the HQ runs
`install\publish.ps1` again: it stops Peel, replaces the engine and the app
and starts it again; if the upgrade fails it starts the build it had. The
start at sign in entry always names the installed app, never a test build. To remove Peel, quit it, turn off **Start up** in Settings, and delete
the `%LOCALAPPDATA%\Peel` folder.
