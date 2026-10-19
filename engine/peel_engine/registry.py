"""The registry: every conversion, tool and action is one data entry.

The registry is data, not branches. A family module adds entries; nothing else
in the engine and nothing at all in the Windows shell knows which formats
exist. The shell renders exactly what ``peel options`` returns.

An entry names its runner as a dotted ``module:function`` string, resolved only
when the action actually runs, so listing options never imports a heavy
dependency.

Output contract for runners (every family bead relies on this):

* ``job.output_path()`` (and ``job.reserve()``) CLAIM the name on disk before
  returning it: the file is created empty with an exclusive create, so two peel
  processes running at the same time can never be handed the same path. A
  directory output is claimed with ``directory=True`` (an exclusive mkdir).
* The runner then fills the claimed path in place. Python writes just open it
  (``"wb"``). External tools must be told to overwrite the empty placeholder:
  ``ffmpeg -y``, ``7z -y``; ImageMagick and vips overwrite by default. Tools
  that choose their own output name (``soffice --convert-to``, ``pandoc`` with
  extracted media) write into a temporary folder and the runner moves the
  result onto the claim with ``os.replace``. Never delete and recreate a
  claim, that reopens the race.
* After the runner, any claim that is not in ``result.outputs`` and is still
  empty is removed, and on a failed run every claim still empty is removed, so
  a failure never leaves a zero byte file next to the source.
"""

from __future__ import annotations

import json
import math
import time
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Callable, Sequence

from . import backends, naming
from .result import (
    InvalidParams,
    MissingFile,
    PeelError,
    Result,
    RunnerFailed,
    UnknownAction,
    UnsupportedInput,
)

#: The three wheels. ``family`` on an entry is one of these.
CONVERT = "convert"
TOOL = "tool"
ACTION = "action"
FAMILIES = (CONVERT, TOOL, ACTION)

#: Wheel labels and the modifier key the spec opens them with by default. The
#: shell may remap the keys in Settings; these are only the defaults it starts
#: from, so the engine never hard codes a keyboard layout.
WHEELS: dict[str, dict[str, str]] = {
    CONVERT: {"label": "Conversions", "default_modifier": "Shift"},
    TOOL: {"label": "File tools", "default_modifier": "Ctrl"},
    ACTION: {"label": "File actions", "default_modifier": "Alt"},
}

#: Accept any extension. Used by the Alt wheel actions, which work on anything.
ANY = "*"

#: Who performs an entry. Almost everything is ENGINE. A few spec actions are
#: pure Windows UI (Share, Float, Terminal): the engine still owns the entry so
#: the shell keeps rendering whatever ``peel options`` returns, and the shell
#: performs it instead of calling ``peel run``.
ENGINE = "engine"
SHELL = "shell"
HANDLERS = (ENGINE, SHELL)

#: Output rule: the output keeps the source extension (compress, crop, trim).
SAME = "same"

PARAM_TYPES = ("string", "int", "float", "bool", "enum", "path", "timecode", "json", "list")


@dataclass(frozen=True)
class Param:
    """One input the shell editor collects before a run. JSON-schema-lite."""

    name: str
    type: str
    label: str
    default: Any = None
    required: bool = False
    choices: tuple[Any, ...] = ()
    minimum: float | None = None
    maximum: float | None = None
    help: str = ""

    def __post_init__(self) -> None:
        if self.type not in PARAM_TYPES:
            raise ValueError(f"param {self.name}: unknown type {self.type!r}")
        if self.type == "enum" and not self.choices:
            raise ValueError(f"param {self.name}: enum needs choices")
        object.__setattr__(self, "choices", tuple(self.choices))

    def to_dict(self) -> dict[str, Any]:
        return {
            "name": self.name,
            "type": self.type,
            "label": self.label,
            "default": self.default,
            "required": self.required,
            "choices": list(self.choices),
            "minimum": self.minimum,
            "maximum": self.maximum,
            "help": self.help,
        }

    def coerce(self, value: Any) -> Any:
        """Turn a CLI string (or an already typed value) into the param type."""
        try:
            return self._coerce(value)
        except InvalidParams:
            raise
        except (TypeError, ValueError) as exc:
            raise InvalidParams(f"{self.label} ({self.name}): {exc}") from None

    def _coerce(self, value: Any) -> Any:
        if self.type == "string" or self.type == "path":
            return str(value)
        if self.type == "int":
            result: Any = int(str(value).strip()) if not isinstance(value, bool) else int(value)
            return self._check_range(result)
        if self.type == "float":
            number = float(value)
            if not math.isfinite(number):
                raise ValueError(f"expected a finite number, got {value!r}")
            return self._check_range(number)
        if self.type == "bool":
            if isinstance(value, bool):
                return value
            text = str(value).strip().lower()
            if text in ("1", "true", "yes", "on"):
                return True
            if text in ("0", "false", "no", "off"):
                return False
            raise ValueError(f"expected true or false, got {value!r}")
        if self.type == "enum":
            if value in self.choices:
                return value
            text = str(value)
            for choice in self.choices:
                if str(choice) == text:
                    return choice
            allowed = ", ".join(str(c) for c in self.choices)
            raise InvalidParams(f"{self.label} ({self.name}): expected one of {allowed}, got {value!r}")
        if self.type == "timecode":
            return _parse_timecode(value)
        if self.type == "json":
            return value if not isinstance(value, str) else json.loads(value)
        if self.type == "list":
            if isinstance(value, (list, tuple)):
                return list(value)
            text = str(value).strip()
            if text.startswith("["):
                return list(json.loads(text))
            return [part.strip() for part in text.split(",") if part.strip()]
        raise ValueError(f"unknown type {self.type}")

    def _check_range(self, value: float) -> float:
        if self.minimum is not None and value < self.minimum:
            raise InvalidParams(f"{self.label} ({self.name}): {value} is below the minimum {self.minimum}")
        if self.maximum is not None and value > self.maximum:
            raise InvalidParams(f"{self.label} ({self.name}): {value} is above the maximum {self.maximum}")
        return value


def _parse_timecode(value: Any) -> float:
    """Seconds from a number or an ``HH:MM:SS.mmm`` / ``MM:SS`` string."""
    if isinstance(value, (int, float)) and not isinstance(value, bool):
        if not math.isfinite(value):
            raise ValueError(f"not a timecode: {value!r}")
        return float(value)
    text = str(value).strip()
    if not text:
        raise ValueError("empty timecode")
    parts = text.split(":")
    if len(parts) > 3:
        raise ValueError(f"not a timecode: {text!r}")
    total = 0.0
    for part in parts:
        total = total * 60 + float(part)
    if not math.isfinite(total):
        raise ValueError(f"not a timecode: {text!r}")
    return total


@dataclass(frozen=True)
class Action:
    """One registry entry: a conversion, a file tool or a file action."""

    id: str
    family: str
    label: str
    group: str
    runner: str = ""
    #: Accepted input extensions, bare and lowercase, or ``(ANY,)``.
    inputs: tuple[str, ...] = (ANY,)
    #: Fixed extension, ``SAME`` to keep the source extension, or None when the
    #: runner decides (page splits, archive extraction, actions that write
    #: nothing).
    output_ext: str | None = None
    params: tuple[Param, ...] = ()
    #: ``each`` runs once per input file; ``combine`` runs once for all of them.
    mode: str = "each"
    needs_editor: bool = False
    min_files: int = 1
    max_files: int | None = None
    accepts_dirs: bool = False
    #: Backends that must be installed before this entry can run.
    backends: tuple[str, ...] = ()
    #: Tool name put in parentheses in the output file name. Tools default to
    #: their label; conversions write the bare source name.
    name_tag: str | None = None
    #: ENGINE (``peel run`` does it) or SHELL (the tray app does it itself).
    handled_by: str = ENGINE

    def __post_init__(self) -> None:
        if self.family not in FAMILIES:
            raise ValueError(f"{self.id}: family must be one of {FAMILIES}, got {self.family!r}")
        if self.mode not in ("each", "combine"):
            raise ValueError(f"{self.id}: mode must be each or combine, got {self.mode!r}")
        if self.handled_by not in HANDLERS:
            raise ValueError(f"{self.id}: handled_by must be one of {HANDLERS}, got {self.handled_by!r}")
        if self.handled_by == ENGINE and ":" not in self.runner:
            raise ValueError(f"{self.id}: runner must be 'module:function', got {self.runner!r}")
        if self.handled_by == SHELL and self.runner:
            raise ValueError(f"{self.id}: a shell handled entry has no runner")
        if self.min_files < 1:
            raise ValueError(f"{self.id}: min_files must be at least 1")
        if self.max_files is not None and self.max_files < self.min_files:
            raise ValueError(f"{self.id}: max_files is below min_files")
        object.__setattr__(
            self, "inputs", tuple(e if e == ANY else naming.normalise_ext(e) for e in self.inputs)
        )
        object.__setattr__(self, "params", tuple(self.params))
        object.__setattr__(self, "backends", tuple(self.backends))
        for name in self.backends:
            if name not in backends.BY_NAME:
                raise ValueError(f"{self.id}: unknown backend {name!r}")

    @property
    def accepts_any(self) -> bool:
        return ANY in self.inputs

    @property
    def tag(self) -> str | None:
        """The ``(Tool)`` part of an output name, or None for a conversion."""
        if self.name_tag is not None:
            return self.name_tag or None
        return self.label if self.family == TOOL else None

    def accepts(self, path: Path) -> bool:
        if path.is_dir():
            return self.accepts_dirs
        if self.accepts_any:
            return True
        return naming.normalise_ext(path.suffix) in self.inputs

    def accepts_count(self, count: int) -> bool:
        if count < self.min_files:
            return False
        return self.max_files is None or count <= self.max_files

    def why_not(self, paths: Sequence[Path]) -> str | None:
        """Human readable reason this entry rejects these paths, or None."""
        if not self.accepts_count(len(paths)):
            if len(paths) < self.min_files:
                return f"{self.label} needs at least {self.min_files} files, got {len(paths)}"
            return f"{self.label} takes at most {self.max_files} files, got {len(paths)}"
        for path in paths:
            if not self.accepts(path):
                if path.is_dir():
                    return f"{self.label} does not work on folders"
                kind = naming.normalise_ext(path.suffix) or "no extension"
                return f"{self.label} does not accept {kind} files"
        return None

    def ext_for(self, source: Path) -> str | None:
        """The output extension for one source, resolving the SAME rule."""
        if self.output_ext is None:
            return None
        if self.output_ext == SAME:
            return naming.normalise_ext(source.suffix)
        return naming.normalise_ext(self.output_ext)

    def resolve_runner(self) -> Callable[[Job], Result]:
        from importlib import import_module

        if self.handled_by == SHELL:
            raise RunnerFailed(
                f"{self.label} is performed by the Peel app itself, not by the engine."
            )
        module_name, _, func_name = self.runner.partition(":")
        module = import_module(module_name)
        try:
            return getattr(module, func_name)
        except AttributeError:
            raise RunnerFailed(f"{self.id}: runner {self.runner} does not exist") from None

    def to_dict(self, *, missing_backends: Sequence[str] | None = None) -> dict[str, Any]:
        missing = list(missing_backends or [])
        return {
            "id": self.id,
            "family": self.family,
            "label": self.label,
            "group": self.group,
            "inputs": list(self.inputs),
            "output_ext": self.output_ext,
            "mode": self.mode,
            "needs_editor": self.needs_editor,
            "min_files": self.min_files,
            "max_files": self.max_files,
            "accepts_dirs": self.accepts_dirs,
            "handled_by": self.handled_by,
            "params": [p.to_dict() for p in self.params],
            "backends": list(self.backends),
            "missing_backends": missing,
            "available": not missing,
        }


@dataclass
class Job:
    """Everything a runner is handed: the entry, the inputs and the params."""

    action: Action
    inputs: list[Path]
    params: dict[str, Any] = field(default_factory=dict)
    out_dir: Path | None = None
    #: Names promised within this batch (shared across the jobs of one run).
    _reserved: list[Path] = field(default_factory=list, repr=False)
    #: Paths this job claimed on disk, for cleanup after a failed run.
    _claimed: list[Path] = field(default_factory=list, repr=False)
    #: Scratch folders from scratch_dir(), removed after the run.
    _scratch: list[Path] = field(default_factory=list, repr=False)

    @property
    def source(self) -> Path:
        """The first input. For ``each`` mode it is the only one."""
        return self.inputs[0]

    def output_path(
        self,
        source: Path | None = None,
        ext: str | None = None,
        tag: str | None = None,
        suffix: str = "",
        directory: bool = False,
    ) -> Path:
        """Claim and return the next free output path for this job.

        The path exists (an empty file, or an empty folder with ``directory``)
        when this returns; see the output contract at the top of the module.
        ``suffix`` is appended to the stem before the extension, for runners
        that write several files from one source (page splits, snapshots).
        """
        source = Path(source) if source is not None else self.source
        extension = ext if ext is not None else self.action.ext_for(source)
        if extension is None:
            raise RunnerFailed(f"{self.action.id}: the runner must choose the output extension")
        name_tag = self.action.tag if tag is None else (tag or None)
        if suffix:
            name_tag = f"{name_tag} {suffix}" if name_tag else suffix
        path = naming.output_path(
            source,
            extension,
            tag=name_tag,
            out_dir=self.out_dir,
            reserved=self._reserved,
            directory=directory,
        )
        self._remember(path)
        return path

    def scratch_dir(self, near: Path) -> Path:
        """A fresh temporary folder beside ``near`` (normally a claimed output).

        Tools that cannot write straight onto the claim (soffice, pandoc) work
        here, then ``os.replace`` their result onto the claim. Being on the same
        drive keeps that replace atomic. The folder is removed after the run.
        """
        import tempfile

        folder = Path(tempfile.mkdtemp(prefix=".peel-", dir=Path(near).parent))
        self._scratch.append(folder)
        return folder

    def reserve(self, path: Path, *, directory: bool = False) -> Path:
        """Claim a path the runner built itself (or the next free variant)."""
        unique = naming.unique_path(Path(path), reserved=self._reserved, directory=directory)
        self._remember(unique)
        return unique

    def _remember(self, path: Path) -> None:
        self._reserved.append(path)
        self._claimed.append(path)

    def release_unused(self, keep: Sequence[Path] = (), *, failed: bool = False) -> None:
        """Remove claims not in ``keep``.

        After a good run only claims that are still empty go. After a failed
        run every claimed file not in ``keep`` goes, half written ones too: the
        exclusive create means nobody else can have written them. A claimed
        folder is only removed while empty.
        """
        import shutil

        for folder in self._scratch:
            shutil.rmtree(folder, ignore_errors=True)
        self._scratch.clear()
        kept = {naming.path_key(p) for p in keep}
        for path in self._claimed:
            if naming.path_key(path) in kept:
                continue
            naming.release(path, only_if_empty=not failed)


class Registry:
    """A collection of actions. One instance per process, built by discovery."""

    def __init__(self) -> None:
        self._actions: dict[str, Action] = {}
        self._labels: set[tuple[str, str, str]] = set()
        #: Families that failed to load, as ``{code, module, message}``.
        self.warnings: list[dict[str, str]] = []

    def snapshot(self) -> tuple[dict[str, Action], set[tuple[str, str, str]]]:
        """State to roll back to if a family fails half way through."""
        return dict(self._actions), set(self._labels)

    def restore(self, state: tuple[dict[str, Action], set[tuple[str, str, str]]]) -> None:
        actions, labels = state
        self._actions = dict(actions)
        self._labels = set(labels)

    def register(self, action: Action) -> Action:
        if action.id in self._actions:
            raise ValueError(f"duplicate action id: {action.id}")
        key = (action.family, action.group, action.label.lower())
        if key in self._labels:
            raise ValueError(
                f"duplicate wheel segment: {action.family}/{action.group}/{action.label} "
                f"(from {action.id}). Two segments with the same label in one group "
                f"cannot be told apart on the wheel."
            )
        self._actions[action.id] = action
        self._labels.add(key)
        return action

    def add(self, **kwargs: Any) -> Action:
        """Shorthand for ``register(Action(...))``, used by family modules."""
        return self.register(Action(**kwargs))

    def get(self, action_id: str) -> Action:
        try:
            return self._actions[action_id]
        except KeyError:
            raise UnknownAction(f"no such action: {action_id}") from None

    def all(self) -> list[Action]:
        return list(self._actions.values())

    def __len__(self) -> int:
        return len(self._actions)

    def __contains__(self, action_id: object) -> bool:
        return action_id in self._actions

    def options(
        self, paths: Sequence[Path] = (), *, include_unavailable: bool = False
    ) -> dict[str, Any]:
        """The wheels for these paths: only entries valid for every path.

        With no paths this returns the whole registry, which is what the shell
        preloads at start up.
        """
        paths = [Path(p) for p in paths]
        wheels: list[dict[str, Any]] = []
        for family in FAMILIES:
            groups: dict[str, list[dict[str, Any]]] = {}
            for action in self._actions.values():
                if action.family != family:
                    continue
                if paths and action.why_not(paths) is not None:
                    continue
                missing = backends.missing(action.backends)
                if missing and not include_unavailable:
                    continue
                groups.setdefault(action.group, []).append(action.to_dict(missing_backends=missing))
            if not groups:
                continue
            wheels.append(
                {
                    "family": family,
                    "label": WHEELS[family]["label"],
                    "default_modifier": WHEELS[family]["default_modifier"],
                    "groups": [
                        {"group": group, "entries": sorted(entries, key=lambda e: e["label"])}
                        for group, entries in sorted(groups.items())
                    ],
                }
            )
        return {
            "ok": True,
            "files": [str(p) for p in paths],
            "wheels": wheels,
            "warnings": list(self.warnings),
        }

    def run(
        self,
        action_id: str,
        paths: Sequence[Path],
        params: dict[str, Any] | None = None,
        out_dir: Path | None = None,
    ) -> Result:
        """Validate, then run. Batching for ``each`` mode happens here, so a
        runner only ever deals with one job."""
        action = self.get(action_id)
        paths = [Path(p) for p in paths]
        if not paths:
            raise UnsupportedInput(f"{action.label} needs at least one file")
        for path in paths:
            if not path.exists():
                raise MissingFile(f"file not found: {path}")
        reason = action.why_not(paths)
        if reason is not None:
            raise UnsupportedInput(reason)
        for name in action.backends:
            backends.require(name)
        if out_dir is not None:
            out_dir = Path(out_dir)
            if out_dir.exists() and not out_dir.is_dir():
                raise UnsupportedInput(
                    f"output folder {out_dir} is a file, not a folder", code="bad_out_dir"
                )
            try:
                out_dir.mkdir(parents=True, exist_ok=True)
            except OSError as exc:
                raise UnsupportedInput(
                    f"cannot create output folder {out_dir}: {exc.strerror or exc}",
                    code="bad_out_dir",
                ) from None

        if action.handled_by == SHELL:
            raise UnsupportedInput(
                f"{action.label} is performed by the Peel app itself, not by the engine.",
                code="shell_action",
            )

        resolved = validate_params(action, params or {})
        runner = action.resolve_runner()

        if action.mode == "combine":
            return _call(runner, Job(action=action, inputs=paths, params=resolved, out_dir=out_dir))
        return _run_each(runner, action, paths, resolved, out_dir)


def _call(runner: Callable[[Job], Result], job: Job) -> Result:
    result = _invoke(runner, job)
    # A failed run keeps nothing it claimed that is still empty; a good run
    # drops only the empty claims it did not report as outputs.
    job.release_unused(keep=result.outputs, failed=not result.ok)
    return result


def _invoke(runner: Callable[[Job], Result], job: Job) -> Result:
    try:
        result = runner(job)
    except PeelError as exc:
        return exc.to_result()
    except Exception as exc:  # a runner bug or a backend blowing up
        return RunnerFailed(f"{job.action.label} failed: {exc}").to_result()
    if not isinstance(result, Result):
        return RunnerFailed(f"{job.action.id}: runner returned {type(result).__name__}, not a Result").to_result()
    return result


def _run_each(
    runner: Callable[[Job], Result],
    action: Action,
    paths: list[Path],
    params: dict[str, Any],
    out_dir: Path | None,
) -> Result:
    if len(paths) == 1:
        return _call(runner, Job(action=action, inputs=paths, params=params, out_dir=out_dir))

    outputs: list[Path] = []
    failures: list[dict[str, str]] = []
    reserved: list[Path] = []
    for path in paths:
        job = Job(action=action, inputs=[path], params=dict(params), out_dir=out_dir, _reserved=reserved)
        result = _call(runner, job)
        if result.ok:
            outputs.extend(result.outputs)
        else:
            failures.append({"file": str(path), "message": result.message, "code": result.code or "error"})
    done = len(paths) - len(failures)
    if failures:
        first = failures[0]["message"]
        extra = f" (and {len(failures) - 1} more)" if len(failures) > 1 else ""
        return Result(
            ok=False,
            outputs=outputs,
            message=f"{done} of {len(paths)} files done. {first}{extra}",
            code="partial" if outputs else failures[0]["code"],
            data={"failures": failures},
        )
    return Result(ok=True, outputs=outputs, message=f"{done} files done")


def validate_params(action: Action, given: dict[str, Any]) -> dict[str, Any]:
    """Coerce every declared param, fill defaults, reject unknown names."""
    declared = {p.name: p for p in action.params}
    unknown = sorted(set(given) - set(declared))
    if unknown:
        allowed = ", ".join(sorted(declared)) or "none"
        raise InvalidParams(
            f"{action.id}: unknown param {unknown[0]!r}. Accepted params: {allowed}"
        )
    resolved: dict[str, Any] = {}
    for name, param in declared.items():
        if name in given:
            resolved[name] = param.coerce(given[name])
        elif param.required:
            raise InvalidParams(f"{action.id}: {param.label} ({name}) is required")
        else:
            resolved[name] = param.default
    return resolved


_registry: Registry | None = None


def load_registry(*, force: bool = False, strict: bool = False) -> Registry:
    """Build the registry by discovering every family module. Cached.

    A family that fails to load is skipped and listed in ``warnings``; with
    ``strict`` the failure is raised instead.
    """
    global _registry
    if _registry is None or force or strict:
        from .families import discover

        registry = Registry()
        discover(registry, strict=strict)
        _registry = registry
    return _registry


def build_time_ms() -> float:
    """Milliseconds to build a fresh registry. Used by the speed test."""
    start = time.perf_counter()
    load_registry(force=True)
    return (time.perf_counter() - start) * 1000


__all__ = [
    "ACTION",
    "ANY",
    "Action",
    "CONVERT",
    "ENGINE",
    "FAMILIES",
    "Job",
    "Param",
    "Registry",
    "SAME",
    "SHELL",
    "TOOL",
    "WHEELS",
    "load_registry",
    "validate_params",
]
