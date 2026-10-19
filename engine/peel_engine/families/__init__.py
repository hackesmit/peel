"""Family modules: each one adds its entries to the registry.

A family module is any module in this package that does not start with an
underscore and exposes ``register(registry)``. Discovery imports it, so a
family module must stay cheap to import: declare entries at module level and
import heavy dependencies (pillow, PyMuPDF, ffmpeg wrappers) inside the runner
functions. ``peel options`` imports every family module, and the spec budget
for it is well under a second.
"""

from __future__ import annotations

import importlib
import pkgutil
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


def discover(registry: "Registry") -> "Registry":
    """Import every family module and let it register its entries."""
    for dotted in family_modules():
        module = importlib.import_module(dotted)
        register = getattr(module, "register", None)
        if register is None:
            raise RuntimeError(f"family module {dotted} has no register(registry)")
        register(registry)
    return registry
