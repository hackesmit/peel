# CLAUDE.md: peel

Peel recreates the behavior of Tangerine for Mac on Daniel's Windows PC
(George). Scope and architecture: docs/SPEC.md (authoritative).

## Clean room
Build from SPEC.md and the public website only. Never download, decompile or
copy Tangerine's binary, code, icons or other assets. Own names and own icons.

## Layout
- `engine/`: Python package `peel_engine` plus `peel` CLI. Must run on Linux
  (hqbox) and Windows (George). Tests: `cd engine && python3 -m pytest -q`.
  Tests generate their fixture files (ffmpeg testsrc, pillow images, etc.) at
  run time; do not commit binary fixtures larger than 50 KB.
- `shell/`: C# .NET 8 WPF tray app, Windows only. Built on George.
- `install/`: George setup (winget, venv, startup entry).

## Working on George from hqbox
- George's C: is at `/mnt/george` (empty means George is asleep: say so).
- Dev copy: rsync the worktree to `/mnt/george/peel-dev/<branch>/` (C:\peel-dev\<branch>), then
  `ssh george "powershell -NoProfile -Command \"cd C:\peel-dev\<branch>\shell; dotnet build -c Release\""`.
- The installed app lives in `C:\Program Files\Peel` or `%LOCALAPPDATA%\Peel`;
  only the install bead touches it.

## Conventions
- Engine registry is data, not branches: each action is one entry (id, family,
  label, inputs, output ext, params schema, runner). The shell never hard-codes
  formats; it renders what `peel options` returns.
- Outputs go next to the source; never overwrite (append " 2", " 3").
- Every runner returns a structured result; errors are messages, not tracebacks.
- Ledger: the hq ledger with label `peel` (run bd from ~/hq).
