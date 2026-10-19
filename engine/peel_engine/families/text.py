"""Plain text family: the smallest real family, and the proof of the plugin path.

It needs no backend, so it works on a bare machine and on CI. The two entries
exercise both halves of the mechanism end to end: a conversion that writes a
file next to the source, and an Alt wheel action that writes nothing and
accepts anything, folders included.

When the documents family lands (bead hq-plct.4) it owns TXT conversions and
must drop ``text.to_md``. The registry refuses two segments with the same label
in the same wheel group, so that clash fails loudly at discovery rather than
showing twice on the wheel.
"""

from __future__ import annotations

from ..registry import ACTION, ANY, CONVERT, Job, Registry
from ..result import Result, ok


def register(registry: Registry) -> None:
    registry.add(
        id="text.to_md",
        family=CONVERT,
        label="MD",
        group="Documents",
        inputs=("txt",),
        output_ext="md",
        runner="peel_engine.families.text:to_md",
    )
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


def to_md(job: Job) -> Result:
    """Copy a text file to Markdown. Plain text is already valid Markdown."""
    source = job.source
    target = job.output_path()
    target.write_bytes(source.read_bytes())
    return ok([target], f"Wrote {target.name}")


def copy_path(job: Job) -> Result:
    """Put the full paths on the clipboard, one per line.

    Clipboard access belongs to the shell, so the engine returns the text and
    says so. The shell copies it and shows the toast.
    """
    text = "\n".join(str(p.resolve()) for p in job.inputs)
    count = len(job.inputs)
    noun = "path" if count == 1 else "paths"
    return ok([], f"Copied {count} {noun}", clipboard_text=text)
