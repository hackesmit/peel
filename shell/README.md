# Peel shell (Windows tray app)

The part of Peel you touch: a tray app that watches for a drag, opens a radial
wheel when you tap a modifier, and hands what you drop to the engine.

C# .NET 8, WPF, Windows only. It is built and tested on George; nothing here
compiles on hqbox.

## How a drag becomes a conversion

1. `Hooks/InputHooks.cs` installs WH_MOUSE_LL and WH_KEYBOARD_LL on a thread of
   their own, so a slow repaint can never delay an input event. Measured worst
   callback on George: 0.5 ms, against a budget of 10 ms.
2. `Hooks/DragStateMachine.cs` turns those events into meaning: left button
   down plus movement past SM_CXDRAG or SM_CYDRAG is a drag, a modifier tap
   during one asks for a wheel, Escape cancels. It is pure state, so the unit
   tests drive the whole interaction with no mouse.
3. `Wheel/WheelWindow.xaml.cs` shows a topmost, never activated, per pixel
   transparent window centred on the pointer, which is also an OLE drop target.
4. The drag data names the files. `Wheel/WheelController.cs` asks
   `peel options <files>` what they allow and draws one segment for each entry.
5. Dropping on a segment runs `peel run <id> <files>` in the background
   (`Jobs/JobRunner.cs`) and reports it in a toast. Dropping in the hub, or off
   the wheel, or pressing Escape, leaves the drag to whatever was underneath.

The shell knows no formats. Every segment, its label, its group, whether it
needs an editor and which modifier opens which wheel all come from the engine's
registry at run time, so a new family in `engine/` appears on the wheel with no
change here.

## Three things that are load bearing

These were proven by the spike in `spikes/drag-overlay` before any of this was
written, and breaking one of them breaks the feature quietly.

- **The hub is a real hole**, cut with `SetWindowRgn`, not a transparent pixel.
  Windows picks a drop target with `WindowFromPoint`, which respects the window
  region, so a drop in the middle reaches the window below and the normal
  Explorer drop happens. That is what "cancel" has to mean.
- **The overlay is hidden after the drop, not when the button comes up.** OLE
  delivers `DragDrop` after the low level hook sees the button up; hiding at
  once turns the drop into a `DragLeave` and the user loses it. The wheel waits
  500 ms.
- **The hole is cut only once the drag data has arrived.** At the moment the
  wheel opens the pointer is at its centre, so a window that already had a
  hole would never be offered the data. Until then the window region is the
  ring's whole disc, never the square, so a quick drop in a corner outside the
  ring still falls through.

A consequence worth knowing: the wheel reads "..." until the pointer moves,
because that is when OLE offers the data. Moving towards a segment is
exactly the motion that fills it in.

Two safety nets sit behind them. `Wheel/StuckOverlayWatch.cs` asks Windows for
the physical mouse button twice a second rather than trusting the remembered
drag state, because a low level hook that answers too slowly is skipped
silently and the button up can simply go missing; a wheel left up with the
button released is taken down within a few seconds. And the engine is only ever
started as a real executable: the installer's `peel.cmd` shim is deliberately
ignored, because a .cmd goes through cmd.exe, which parses its own command line
after .NET has quoted the arguments, and a file named `a&calc&b.png` would then
run calc.

## Build and test from hqbox

```
rsync -a --delete --exclude .git --exclude 'bin/' --exclude 'obj/' \
    ~/hq/rigs/<worktree>/ /mnt/george/peel-dev/<branch>/
ssh george "powershell -NoProfile -Command \"cd C:\peel-dev\<branch>\shell; dotnet build -c Release\""
ssh george "powershell -NoProfile -Command \"cd C:\peel-dev\<branch>\shell; dotnet test -c Release\""
```

The GUI tests cannot run over ssh: a process started there lands in session 0,
which has no desktop. `tests/run-e2e.ps1` registers a scheduled task with an
Interactive principal so the driver runs in the logged on session, then reports.
Daniel has to be logged in at the console.

```
ssh george "powershell -NoProfile -ExecutionPolicy Bypass -File C:\peel-dev\<branch>\shell\tests\run-e2e.ps1"
ssh george "powershell -NoProfile -ExecutionPolicy Bypass -File C:\peel-dev\<branch>\shell\tests\run-e2e.ps1 -Test render"
```

- `-Test drag` (the default) is the real thing: a real Explorer drag driven with
  SendInput, a modifier tap, a drop on a segment, and the engine writes the
  file. It also checks that the other modifiers open the wheels the registry
  says they open, that Escape writes nothing, that a drop in the hub runs
  nothing, that killing Peel mid drag still lets Explorer take the drop, and
  that no hook callback went near 10 ms. It aims at a segment by reading the
  wheel's own log line, so it keeps working as the registry grows. It defaults
  to the TXT to MD conversion because that is what the registry holds today;
  once the image family lands, run it with
  `-Source sample.png -Segment WEBP -Expect sample.webp`, which is the outcome
  the bead describes.
- `-Test render` points Peel at `tests/FakeEngine`, a stand in CLI that serves a
  canned registry and refuses to run anything, and screenshots a nine segment
  wheel, a wheel with more entries than fit, and the page it turns to. A
  developer machine may hold a single conversion, which proves nothing about a
  full ring.
- `-Test editors` opens each image editor from the tools wheel with a real
  drag, drives it with UI Automation, applies, and checks the output pixel by
  pixel or tag by tag, with one screenshot per editor. Pass `-Engine
  <peel.exe>` when the installed engine is older than the branch (its registry
  decides what the tools wheel offers, so a stale install leaves it empty).

They leave their evidence in `C:\peel-dev\e2e`, `C:\peel-dev\e2e-render` and
`C:\peel-dev\e2e-editors`: `result.json`, `drive.log`, Peel's own `peel.log`
and the screenshots.

## The editor windows

`Editors/` holds the windows that collect parameters for an entry the registry
marks `needs_editor`. Five are built, all for images: Crop, Edit, Annotate,
Redact and Metadata (`Editors/Image/`).

Four things in there are load bearing.

- **The editor is opened after the drop, never during it.**
  `IEditorHost.TryCollectParams` runs inside OLE's drop callback, with the
  dragging application's drag loop waiting on the return, so a window shown
  there freezes Explorer until the user is done with it. `EditorHost` queues
  the window on the dispatcher, returns false so the wheel starts no job, and
  starts the job itself when the user applies. False is "not now", not
  "failed".
- **The schema comes from the engine.** Slider ranges, enum choices, defaults
  and field labels are read from the entry's own `params` (`ParamSchema`), so
  the registry stays the one statement of what an action takes. Only what the
  user actually changed is sent, so the engine's defaults stay the definition
  of neutral.
- **A click becomes an image pixel in one place.** `PreviewMap` does that
  arithmetic and the windows never do it themselves, because a preview is
  fitted, letterboxed and then scaled again by the monitor's DPI, and a crop
  box that is wrong by a factor does not look wrong enough to notice.
- **A rotated or exotic photo is rasterised by the engine.** `image.load`
  opens every image upright (it applies and drops the EXIF Orientation tag)
  and its docstring promises that a crop box refers to what the editor showed.
  WPF does not apply the tag and has no codec for HEIC or AVIF, so
  `PreviewSource` sends any file whose Orientation is not 1, or that WPF
  refuses, through `peel run image.to_png --out-dir <temp>`: the preview is
  then what the engine sees, by construction.

The Edit window's live preview is pillow's `ImageEnhance` pixel for pixel
(`Editors/Image/Adjust.cs`), in the engine's order, because a preview that is
only roughly the result is worse than none: the user sets a slider by eye and
the file gets something else.

**Peel.csproj does not set `InvariantGlobalization`, and must not.** WPF asks
for culture 1033 behind the user's back and gets `CultureNotFoundException` in
invariant mode: giving a `TextBox` the keyboard focus does it, and so does any
`{Binding}`. A `FontFamily` looked up by name throws a
`TypeInitializationException` from `MS.Internal.FontCache.MajorLanguages`
instead. The app's own handler catches all three, so the symptom is a window
that is subtly wrong and a line in the log rather than a crash, which is why
the csproj carries a comment and not a setting.

## Logging

`%LOCALAPPDATA%\Peel\logs\peel.log`, or wherever `PEEL_LOG` points. Writes are
queued and flushed by a background thread, because the drag state machine logs
from the hook thread and nothing on that thread may touch a file. The format is
stable (`HH:mm:ss.fff message`): the end to end test reads it.

`PEEL_EXIT_AFTER=<seconds>` makes the app quit by itself, so a failed test run
cannot leave a tray app behind.

## Not built yet

- The keyboard path (a hotkey on an Explorer selection, arrow keys): bead
  hq-plct.8.
- The six Alt wheel actions. Entries marked `handled_by: shell` say so in a
  toast instead of running: bead hq-plct.9.
- The editor windows for video, audio and PDF entries with `needs_editor`
  (trim, split, bleep, organize pages): bead hq-plct.11. `Editors/EditorHost.cs`
  knows which entries it has a window for and says so in a toast for the rest.
- Settings, including remapping the modifiers. The wheel to modifier mapping is
  already data (the engine's `default_modifier`), so this is a settings file and
  a window, not a change to the wheel.
- Toasts are placed from the primary monitor's work area, so on a multi monitor
  setup with mixed scaling they can land slightly off. The wheel itself is per
  monitor DPI aware.

## Which drags get a wheel (review round 1)

- **Only file drags.** When the modifier is tapped, the hook asks Windows for
  the physical primary button (GetAsyncKeyState); if it is up, a button up was
  missed (released over an elevated window, Win+L, a UAC prompt) and the drag
  is ended instead of opening a wheel. A window being moved or sized by its
  title bar or border is refused too: Windows flags that thread with
  GUI_INMOVESIZE, which `Hooks/WindowsDragProbe.cs` reads with
  GetGUIThreadInfo, a call that sends no message and so cannot hang on a dead
  window. WM_NCHITTEST was not used because it is a message to the other
  process.
- **The wheel steps aside for anything else.** If OLE offers data with no
  file paths (text, a browser image, an Outlook attachment that is only
  FileContents, which Peel does not support), the wheel is dismissed at once
  and the next pointer move gives the drag back to the window below. If the
  pointer moves over the wheel and no OLE drag ever enters it (text selection,
  a rubber band), it leaves 350 ms later (`Wheel/ArrivalWatch.cs`).
- **One drag, one session.** A new drag, or a fresh showing, ends the previous
  session: wheel, timers, paths and pending `peel options` call. A drop is
  accepted only if its own data names the paths the session read.
- **No filesystem work in OLE callbacks.** Telling folders from files runs on
  the thread pool with a 1.5 s deadline (`Wheel/FileClassifier.cs`); past it,
  names decide, and `peel run` checks for real. The engine call itself (list
  file and process creation included) also starts on the thread pool, so
  nothing synchronous runs inside a DragEnter.

## Engine processes

- Each engine call runs in its own kill on close job object
  (`Jobs/ProcessJob.cs`): a timeout, a cancel or Quit terminates the whole
  tree, and if Peel itself dies Windows does it. The engine is created
  suspended, put in the job and registered for Quit before its first thread
  runs (`Jobs/ContainedProcess.cs`), so nothing it starts can escape the job.
  If Windows refuses the job, the engine is not run and the toast says why.
  Quit also cancels pending `peel options` calls. The timeout covers reading
  the pipes as well as the exit, since a backend can hold them open. Only the
  three pipe handles are inherited.
- Past 24000 characters of command line the paths go to the engine in a
  temporary UTF-8 list file (`peel options|run --files-from`), one path per
  line ended by "\n" only, so U+0085, U+2028 and U+2029 (legal in a Windows
  name) stay inside the path. It is deleted after the call, also when writing
  it fails half way; lists a killed Peel left in `%TEMP%\Peel` are swept at
  start up once they are a day old.
- A result carrying `data.clipboard_text` (Copy Path) is put on the clipboard
  on the UI thread, one try at a time with an awaited pause between tries
  while another app holds it; the toast says so if it never gets through.
  There is no read back, because reading the clipboard blocks while it is busy.
- Each hook is checked every 5 s on its own evidence (`Hooks/HookLiveness.cs`):
  a cursor that moved while the mouse hook stayed quiet, or input that neither
  hook saw, reinstalls both. A dead keyboard hook leaves no readable trace, so
  both are also reinstalled once the user has been idle 15 s, at most every
  10 minutes. A reinstall that leaves a hook missing is retried every 30 s.
