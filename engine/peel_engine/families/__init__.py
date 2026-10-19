"""Family modules: each one adds its entries to the registry.

A family module is any module in this package that does not start with an
underscore and exposes ``register(registry)``. Discovery imports it, so a
family module must stay cheap to import: declare entries at module level and
import heavy dependencies (pillow, PyMuPDF, ffmpeg wrappers) inside the runner
functions. ``peel options`` imports every family module, and the spec budget
for it is well under a second.

One broken family must never take the others down with it. On George there is
no terminal to read a traceback, so ``discover`` isolates each module: if the
import fails, ``register`` is missing, or ``register`` raises (a duplicate id
or wheel label included), that family is rolled back, the rest still load, and
the failure is recorded in ``registry.warnings``. ``peel options`` and ``peel
doctor`` both report those warnings. Tests call ``discover(strict=True)`` (or
assert the warnings list is empty) so a clash still fails loudly in CI.
"""

from __future__ import annotations

import importlib
import pkgutil
import sys
from typing import TYPE_CHECKING

if TYPE_CHECKING:
    from ..registry import Registry


def family_modules() -> list[str]:
    """Dotted names of the family modules, in a stable order."""
    return sorted(
        f"{__name__}.{info.name}"
        for info in pkgutil.iter_modules(__path__)
        if not info.name.startswith("_")
    )


def discover(registry: "Registry", *, strict: bool = False) -> "Registry":
    """Import every family module and let it register its entries.

    A family that fails is skipped whole (nothing it registered stays) and
    reported in ``registry.warnings``. With ``strict`` the first failure is
    raised instead, for tests.
    """
    for dotted in family_modules():
        snapshot = registry.snapshot()
        try:
            module = importlib.import_module(dotted)
            register = getattr(module, "register", None)
            if register is None:
                raise RuntimeError(f"family module {dotted} has no register(registry)")
            register(registry)
        except Exception as exc:
            if strict:
                raise
            registry.restore(snapshot)
            sys.modules.pop(dotted, None)
            registry.warnings.append(
                {
                    "code": "family_failed",
                    "module": dotted,
                    "message": f"{dotted.rsplit('.', 1)[-1]} family skipped: {type(exc).__name__}: {exc}",
                }
            )
    return registry
