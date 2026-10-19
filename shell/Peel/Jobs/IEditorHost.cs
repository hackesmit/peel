// Entries flagged needs_editor (crop, trim, redact, metadata and the rest)
// need values from the user before the engine can run them. This is the seam
// the editor windows plug into; Peel.Editors.EditorHost implements it, and an
// entry it has no window for yet is reported to the user there.
using System.Collections.Generic;
using Peel.Engine;

namespace Peel.Jobs;

internal interface IEditorHost
{
    /// <summary>
    /// Collect the parameters an entry declares. Returns false when the wheel
    /// must not start a job now: the user cancelled, no editor exists yet, or
    /// (the normal case) the editor has been queued and will start the job
    /// itself once the user applies. Whichever it was, the implementation has
    /// already told the user.
    ///
    /// This runs inside OLE's drop callback, with the dragging application's
    /// drag loop waiting on the return, so an implementation must not show a
    /// window or do anything slow before it returns. Peel.Editors.EditorHost
    /// is the one that does the real work; it queues and returns at once.
    /// </summary>
    bool TryCollectParams(ActionEntry entry, IReadOnlyList<string> files, out Dictionary<string, string> parameters);
}
