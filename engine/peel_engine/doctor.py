"""``peel doctor``: which backends are installed, and what is missing.

The install script and the tray app both read this, so it reports rather than
fails. A missing backend is not an error here; it only disables the entries
that need it.
"""

from __future__ import annotations

import platform
import sys
from typing import Any

from . import backends
from .registry import load_registry


def check() -> dict[str, Any]:
    backends.clear_cache()
    registry = load_registry()

    users: dict[str, int] = {}
    for action in registry.all():
        for name in action.backends:
            users[name] = users.get(name, 0) + 1

    reports: list[dict[str, Any]] = []
    for backend in backends.BACKENDS:
        found = backends.find(backend.name)
        reports.append(
            {
                "name": backend.name,
                "label": backend.label,
                "kind": backend.kind,
                "found": found is not None,
                "path": str(found) if found else None,
                "version": backends.version(backend.name) if found else "",
                "used_for": backend.used_for,
                "used_by": users.get(backend.name, 0),
                "install": backend.install,
            }
        )

    missing = [r["name"] for r in reports if not r["found"]]
    blocked = [
        action.id
        for action in registry.all()
        if any(name in missing for name in action.backends)
    ]
    return {
        "ok": True,
        "platform": f"{platform.system()} {platform.release()}",
        "python": sys.version.split()[0],
        "backends": reports,
        "missing": missing,
        "actions_total": len(registry),
        "actions_blocked": sorted(blocked),
        "message": _summary(reports, blocked),
    }


def _summary(reports: list[dict[str, Any]], blocked: list[str]) -> str:
    found = sum(1 for r in reports if r["found"])
    total = len(reports)
    if found == total:
        return f"All {total} backends found."
    return f"{found} of {total} backends found, {len(blocked)} actions unavailable."
