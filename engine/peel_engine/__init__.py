"""Peel engine: the cross platform half of Peel.

Public surface:

* ``load_registry()`` gives the Registry built from the family modules,
* ``Registry.options(paths)`` returns the wheels valid for those paths,
* ``Registry.run(action_id, paths, params)`` returns a Result.

The ``peel`` CLI in :mod:`peel_engine.cli` is a thin JSON wrapper over those
three calls, and it is all the Windows shell talks to.
"""

from __future__ import annotations

__version__ = "0.1.0"

from .registry import (
    ACTION,
    ANY,
    CONVERT,
    ENGINE,
    SAME,
    SHELL,
    TOOL,
    Action,
    Job,
    Param,
    Registry,
    load_registry,
)
from .result import PeelError, Result, failure, ok

__all__ = [
    "ACTION",
    "ANY",
    "Action",
    "CONVERT",
    "ENGINE",
    "Job",
    "Param",
    "PeelError",
    "Registry",
    "Result",
    "SAME",
    "SHELL",
    "TOOL",
    "__version__",
    "failure",
    "load_registry",
    "ok",
]
