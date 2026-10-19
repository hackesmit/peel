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
- **The hole is cut only once the drag data has arrived** (or after 450 ms if
  it never does). At the moment the wheel opens the pointer is at its centre,
  so a window that already had a hole would never be offered the data.

A consequence worth knowing: the wheel reads "..." until the pointer reaches
the ring, because that is when OLE offers the data. Moving towards a segment is
exactly the motion that fills it in.

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

Both leave their evidence in `C:\peel-dev\e2e` and `C:\peel-dev\e2e-render`:
`result.json`, `drive.log`, Peel's own `peel.log` and the screenshots.

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
- The editor windows for entries with `needs_editor`. `Jobs/IEditorHost.cs` is
  the seam they plug into; the stand in says the editor is not built: bead
  hq-plct.10.
- Settings, including remapping the modifiers. The wheel to modifier mapping is
  already data (the engine's `default_modifier`), so this is a settings file and
  a window, not a change to the wheel.
- Toasts are placed from the primary monitor's work area, so on a multi monitor
  setup with mixed scaling they can land slightly off. The wheel itself is per
  monitor DPI aware.
