"""``peel``: the command line the Windows shell drives.

Three commands, one JSON object on stdout each, exit 0 when ``ok`` is true and
1 when it is false:

    peel options <files...>                 the wheels valid for those files
    peel run <action-id> <files...>         run it, report the outputs
    peel doctor                             which backends are installed

Both ``options`` and ``run`` also take ``--files-from <list>``: a UTF-8 text
file with one path per line, read before any path given on the command line
is appended. The shell switches to it when a drag carries more paths than fit
in one Windows command line (32767 characters), so a folder of photos still
reaches the engine as one call. Blank lines are skipped; nothing else in a
line is trimmed, because a file name may end in a space.

Errors come back as ``{"ok": false, "message": ..., "code": ...}``. A traceback
is printed only with ``PEEL_DEBUG=1`` set, because the shell shows ``message``
to the user in a toast.

That holds for misuse too: a missing or unknown subcommand, a missing action
id or file list, a flag without its value or a ``--param`` that is not
``KEY=VALUE`` all answer ``{"ok": false, "code": "usage", ...}`` on stdout with
exit 1. argparse never prints its own usage text or exits 2. Only ``--help``
and ``--version``, which a person types and the shell never sends, print plain
text (exit 0).

``peel options`` does not check that the files exist; the shell only ever
passes paths it was just handed by a drag. A missing file is reported by
``peel run`` (code ``missing_file``).
"""

from __future__ import annotations

import argparse
import json
import os
import sys
from pathlib import Path
from typing import Any, Sequence

from . import __version__
from .result import InvalidParams, PeelError

PROG = "peel"


class UsageError(PeelError):
    """The command line itself is wrong. Stable code for the shell: usage."""

    code = "usage"


class _Parser(argparse.ArgumentParser):
    """argparse that raises instead of printing usage and exiting 2.

    Sub parsers inherit this class, so every level reports through it.
    """

    def error(self, message: str) -> None:  # type: ignore[override]
        usage = " ".join(self.format_usage().split())
        if usage.lower().startswith("usage: "):
            usage = usage[len("usage: "):]
        raise UsageError(f"{self.prog}: {message}", usage=usage)


_FILES_FROM_HELP = "read more paths from this UTF-8 file, one per line"


def read_file_list(path: Path) -> list[Path]:
    """The paths in a ``--files-from`` list, in order.

    UTF-8 with or without a byte order mark, LF or CRLF line ends. Blank lines
    are skipped. A line is otherwise taken exactly as written: the list is split
    on "\n" only, never with str.splitlines(), because that also breaks at
    U+0085, U+2028 and U+2029, which are legal in a Windows file name.
    """
    try:
        text = Path(path).read_text(encoding="utf-8-sig")
    except OSError as exc:
        raise UsageError(f"cannot read the file list {path}: {exc}") from None
    except UnicodeDecodeError as exc:
        raise UsageError(f"the file list {path} is not UTF-8: {exc}") from None
    lines = (line[:-1] if line.endswith("\r") else line for line in text.split("\n"))
    return [Path(line) for line in lines if line.strip()]


def collect_files(args: argparse.Namespace) -> list[Path]:
    """Paths from ``--files-from`` first, then those on the command line."""
    files: list[Path] = []
    if getattr(args, "files_from", None) is not None:
        files.extend(read_file_list(args.files_from))
    files.extend(args.files)
    return files


def build_parser() -> argparse.ArgumentParser:
    parser = _Parser(prog=PROG, description="Peel engine")
    parser.add_argument("--version", action="version", version=f"{PROG} {__version__}")
    parser.add_argument("--indent", type=int, default=None, help="pretty print the JSON")
    sub = parser.add_subparsers(dest="command", required=True)

    options = sub.add_parser(
        "options",
        help="list the conversions, tools and actions valid for these files",
        description="With no files, the whole registry is returned.",
    )
    options.add_argument("files", nargs="*", type=Path)
    options.add_argument("--files-from", type=Path, metavar="LIST", help=_FILES_FROM_HELP)
    options.add_argument(
        "--include-unavailable",
        action="store_true",
        help="also list entries whose backend is not installed",
    )

    run = sub.add_parser("run", help="run one action on one or more files")
    run.add_argument("action")
    run.add_argument("files", nargs="*", type=Path)
    run.add_argument("--files-from", type=Path, metavar="LIST", help=_FILES_FROM_HELP)
    run.add_argument(
        "--param",
        action="append",
        default=[],
        metavar="KEY=VALUE",
        help="an action parameter. Use @path to read the value from a file.",
    )
    run.add_argument(
        "--params-file",
        type=Path,
        help="JSON object of parameters, for editors that pass structured data",
    )
    run.add_argument("--out-dir", type=Path, help="write outputs here instead of next to the source")

    sub.add_parser("doctor", help="report which backends are installed")
    return parser


def parse_params(pairs: Sequence[str], params_file: Path | None = None) -> dict[str, Any]:
    """Turn ``--param k=v`` pairs and ``--params-file`` into one dict.

    A value of ``@path`` is read from that file, which is how the WPF editors
    pass a long list of shapes or page ranges without fighting the shell
    quoting rules.
    """
    params: dict[str, Any] = {}
    if params_file is not None:
        try:
            loaded = json.loads(Path(params_file).read_text(encoding="utf-8"))
        except OSError as exc:
            raise InvalidParams(f"cannot read params file {params_file}: {exc}") from None
        except json.JSONDecodeError as exc:
            raise InvalidParams(f"params file {params_file} is not valid JSON: {exc}") from None
        if not isinstance(loaded, dict):
            raise InvalidParams(f"params file {params_file} must hold a JSON object")
        params.update(loaded)

    for pair in pairs:
        key, sep, value = pair.partition("=")
        if not sep or not key:
            raise InvalidParams(f"bad --param {pair!r}, expected KEY=VALUE", code="usage")
        if value.startswith("@"):
            path = Path(value[1:])
            try:
                value = path.read_text(encoding="utf-8")
            except OSError as exc:
                raise InvalidParams(f"cannot read param value from {path}: {exc}") from None
        params[key] = value
    return params


def _dispatch(args: argparse.Namespace) -> dict[str, Any]:
    from .registry import load_registry

    if args.command == "doctor":
        from .doctor import check

        return check()

    files = collect_files(args)
    if args.command == "run" and not files:
        raise UsageError(
            "peel run: the following arguments are required: files (or --files-from)",
            usage="peel run action [files ...] [--files-from LIST]",
        )
    registry = load_registry()
    if args.command == "options":
        return registry.options(files, include_unavailable=args.include_unavailable)

    params = parse_params(args.param, args.params_file)
    result = registry.run(args.action, files, params, out_dir=args.out_dir)
    return result.to_dict()


def main(argv: Sequence[str] | None = None) -> int:
    parser = build_parser()
    indent: int | None = None
    try:
        args = parser.parse_args(argv)
        indent = args.indent
        payload = _dispatch(args)
    except PeelError as exc:
        payload = exc.to_result().to_dict()
    except KeyboardInterrupt:
        payload = {"ok": False, "outputs": [], "message": "cancelled", "code": "cancelled"}
    except Exception as exc:
        if os.environ.get("PEEL_DEBUG"):
            raise
        payload = {
            "ok": False,
            "outputs": [],
            "message": f"{type(exc).__name__}: {exc}",
            "code": "internal",
        }
    print(json.dumps(payload, indent=indent, ensure_ascii=False))
    return 0 if payload.get("ok") else 1


if __name__ == "__main__":
    sys.exit(main())
