# Spike: can a Windows overlay catch an Explorer drag? (2026-10-19)

Result: yes. Tested on George (Windows 11 26200) with a real Explorer window,
in Daniel's desktop session.

Evidence from log.txt of the final run:

```
13:03.819 drag start at 198,192
13:04.347 shift down, dragging=True
13:04.369 overlay shown at 377,281
13:05.718 drag end
13:05.723 DROP segment=WEBP files=C:\peel-dev\spike\testdir\sample.png
13:05.724 worst hook 417us
```

Screenshot mid-drag: wheel-mid-drag.png (WEBP segment highlighted under the cursor).

## What works
- WH_MOUSE_LL and WH_KEYBOARD_LL hooks keep firing during Explorer's
  DoDragDrop loop. Drag = button down plus movement past SM_CXDRAG/SM_CYDRAG.
- A topmost, no-activate window shown mid-drag becomes the OLE drop target as
  soon as the cursor moves over it. Explorer offers FileDrop, Shell IDList
  Array, FileContents and more.
- A color-keyed transparent hole in the middle passes the drag through to the
  window below, so "drop in the centre" can mean "cancel, do the normal drop".
- Hook cost: worst 0.4 ms per call, far under the 10 ms budget.

## Gotchas for the shell bead
- Hide the overlay a few hundred ms AFTER the hook sees button-up. OLE delivers
  DragDrop after the low-level button-up; hiding at once turns the drop into a
  DragLeave.
- SetCursorPos does not reach low-level hooks. Test drivers must use
  mouse_event / SendInput with MOUSEEVENTF_MOVE | ABSOLUTE (see drive.ps1).
- Processes started over ssh run in session 0 and cannot show UI. Launch GUI
  tests in session 1 with a one-shot `schtasks /create ... /it` plus `/run`.
- Explorer hides extensions on George: find items by name with and without
  the extension (UI Automation, ControlType ListItem).
- The color-key edge shows a magenta fringe. The real shell should use a WPF
  window with AllowsTransparency and per-pixel alpha.
- Segment angle mapping in this spike labels top/bottom swapped (screen y
  grows downward). Cosmetic, fix in the real wheel.
- Not yet tested: drags out of elevated windows (UIPI blocks those into a
  non-elevated overlay), drags from the desktop, multi-file drags, browser
  drags (FileContents without FileDrop).
