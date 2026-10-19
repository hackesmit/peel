// Entries flagged needs_editor (crop, trim, redact, metadata and the rest)
// need values from the user before the engine can run them. The editor windows
// are their own bead; this is the seam they plug into.
using System.Collections.Generic;
using Peel.Engine;

namespace Peel.Jobs;

internal interface IEditorHost
{
    /// <summary>
    /// Collect the parameters an entry declares. Returns false when the user
    /// cancelled, or when no editor exists yet, in which case the
    /// implementation has already told the user why.
    /// </summary>
    bool TryCollectParams(ActionEntry entry, IReadOnlyList<string> files, out Dictionary<string, string> parameters);
}

/// <summary>Stand in until the editor windows exist.</summary>
internal sealed class MissingEditorHost : IEditorHost
{
    private readonly JobRunner _jobs;

    internal MissingEditorHost(JobRunner jobs) => _jobs = jobs;

    public bool TryCollectParams(ActionEntry entry, IReadOnlyList<string> files, out Dictionary<string, string> parameters)
    {
        parameters = new Dictionary<string, string>();
        Log.Write($"{entry.Id} needs an editor, which is not built yet");
        _jobs.Report($"{entry.Label} needs a window to set it up. That editor is not yet built.");
        return false;
    }
}
