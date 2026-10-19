"""Structured results and errors.

Every runner returns a Result. Failures are messages with a machine readable
code, never tracebacks: the CLI serialises whatever comes back as JSON and the
Windows shell shows the message in a toast.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Iterable


@dataclass
class Result:
    """Outcome of one run. ``outputs`` are the files that were written."""

    ok: bool
    outputs: list[Path] = field(default_factory=list)
    message: str = ""
    code: str | None = None
    data: dict[str, Any] = field(default_factory=dict)

    def __post_init__(self) -> None:
        self.outputs = [Path(p) for p in self.outputs]

    def to_dict(self) -> dict[str, Any]:
        out: dict[str, Any] = {
            "ok": self.ok,
            "outputs": [str(p) for p in self.outputs],
            "message": self.message,
        }
        if self.code:
            out["code"] = self.code
        if self.data:
            out["data"] = self.data
        return out


def ok(outputs: Iterable[Path] = (), message: str = "", **data: Any) -> Result:
    return Result(ok=True, outputs=list(outputs), message=message, data=data)


def failure(message: str, code: str = "error", **data: Any) -> Result:
    return Result(ok=False, outputs=[], message=message, code=code, data=data)


class PeelError(Exception):
    """Base class for every expected failure. Carries a stable code."""

    code = "error"

    def __init__(self, message: str, *, code: str | None = None, **data: Any) -> None:
        super().__init__(message)
        self.message = message
        if code:
            self.code = code
        self.data = data

    def to_result(self) -> Result:
        return Result(ok=False, outputs=[], message=self.message, code=self.code, data=self.data)


class UnknownAction(PeelError):
    code = "unknown_action"


class UnsupportedInput(PeelError):
    code = "unsupported_input"


class MissingFile(PeelError):
    code = "missing_file"


class InvalidParams(PeelError):
    code = "invalid_params"


class BackendMissing(PeelError):
    code = "backend_missing"


class RunnerFailed(PeelError):
    code = "runner_failed"
