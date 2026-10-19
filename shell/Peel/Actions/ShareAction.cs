// Share: raise the Windows share sheet (DataTransferManager) for the dropped
// files, the Windows stand in for AirDrop (Nearby Share is one of its targets).
//
// This is filled in once the project targets the Windows 10 SDK, which is what
// brings in Windows.ApplicationModel.DataTransfer. Until then it says so rather
// than failing silently.
using System.Collections.Generic;
using Peel.Jobs;

namespace Peel.Actions;

internal static class ShareAction
{
    internal static void Run(IReadOnlyList<string> files, JobRunner jobs)
    {
        Log.Write($"share: {files.Count} file(s), not yet wired to the share sheet");
        jobs.Report("Share is not available yet.");
    }
}
