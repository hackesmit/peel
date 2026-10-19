"""``peel``: the command line the Windows shell drives.

Three commands, one JSON object on stdout each, exit 0 when ``ok`` is true and
1 when it is false:

    peel options <files...>                 the wheels valid for those files
    peel run <action-id> <files...>         run it, report the outputs
    peel doctor                             which backends are installed

Errors come back as ``{"ok": false, "message": ..., "code": ...}``. A traceback
is printed only with ``PEEL_DEBUG=1`` set, because the shell shows ``message``
to the user in a toast.
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


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(prog=PROG, description="Peel engine")
    parser.add_argument("--version", action="version", version=f"{PROG} {__version__}")
    parser.add_argument("--indent", type=int, default=None, help="pretty print the JSON")
    sub = parser.add_subparsers(dest="command", required=True)

    options = sub.add_parser(
        "options",
        help="list the conversions, tools and actions valid for these files",
        description="With no files, the whole registry is returned.",
    )
    options.add_argument("files", nargs="*", type=Path)
    options.add_argument(
        "--include-unavailable",
        action="store_true",
        help="also list entries whose backend is not installed",
    )

    run = sub.add_parser("run", help="run one action on one or more files")
    run.add_argument("action")
    run.add_argument("files", nargs="+", type=Path)
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
            raise InvalidParams(f"bad --param {pair!r}, expected KEY=VALUE")
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

    registry = load_registry()
    if args.command == "options":
        return registry.options(args.files, include_unavailable=args.include_unavailable)

    params = parse_params(args.param, args.params_file)
    result = registry.run(args.action, args.files, params, out_dir=args.out_dir)
    return result.to_dict()


def main(argv: Sequence[str] | None = None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)
    try:
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
    print(json.dumps(payload, indent=args.indent, ensure_ascii=False))
    return 0 if payload.get("ok") else 1


if __name__ == "__main__":
    sys.exit(main())
