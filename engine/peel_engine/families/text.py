"""Files family: the Alt wheel action that needs no backend at all.

It is the smallest real family and the proof of the plugin path: an action that
writes nothing, accepts anything including folders, and is resolved through the
same dotted ``module:function`` runner path as everything else. Because it has
no backend it still works on a bare machine and on CI.

It used to also hold ``text.to_md`` (TXT to Markdown) as the conversion half of
that proof. The documents family (docs.py, bead hq-plct.4) owns every TXT
conversion now, so that entry is gone: the registry's duplicate wheel segment
guard refused convert/Documents/MD twice the moment docs.py landed, which is
exactly the handover signal it was put there to give.
"""

from __future__ import annotations

from ..registry import ACTION, ANY, Job, Registry
from ..result import Result, ok


def register(registry: Registry) -> None:
    registry.add(
        id="files.copy_path",
        family=ACTION,
        label="Copy Path",
        group="Files",
        inputs=(ANY,),
        accepts_dirs=True,
        mode="combine",
        runner="peel_engine.families.text:copy_path",
    )


def copy_path(job: Job) -> Result:
    """Put the full paths on the clipboard, one per line.

    Clipboard access belongs to the shell, so the engine returns the text and
    says so. The shell copies it and shows the toast.
    """
    text = "\n".join(str(p.resolve()) for p in job.inputs)
    count = len(job.inputs)
    noun = "path" if count == 1 else "paths"
    return ok([], f"Copied {count} {noun}", clipboard_text=text)
