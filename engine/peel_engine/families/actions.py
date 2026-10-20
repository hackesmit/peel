"""File actions: the Alt wheel.

The spec's Alt wheel holds six actions. Copy Path is engine work (it returns
the text for the shell to put on the clipboard) and already lives in the text
family. The other five are pure Windows UI: a share sheet, a floating preview,
the clipboard as files, a move with a destination picker, and a terminal. The
engine cannot do any of those, so each entry is ``handled_by=SHELL`` and names
no runner: the registry still owns the entry, so the shell draws the Alt wheel
from ``peel options`` instead of hard coding it, and the tray app performs the
action itself when something is dropped on the segment.

Every action takes any file, folders included, and any number of them (a mixed
selection is normal), so they all share one input rule. ``peel run`` refuses a
shell action with a ``shell_action`` code, which is how the shell learns it has
to do the work rather than wait on the engine.
"""

from __future__ import annotations

from ..registry import ACTION, ANY, SHELL, Registry

#: Group every action sits in on the Alt wheel. Copy Path (text family) uses
#: the same group, so the six read as one set.
GROUP = "Files"


def register(registry: Registry) -> None:
    for id_, label in (
        ("files.share", "Share"),
        ("files.float", "Float"),
        ("files.copy", "Copy"),
        ("files.move", "Move"),
        ("files.terminal", "Terminal"),
    ):
        registry.add(
            id=id_,
            family=ACTION,
            label=label,
            group=GROUP,
            inputs=(ANY,),
            accepts_dirs=True,
            mode="combine",
            handled_by=SHELL,
        )
