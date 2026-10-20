// The Alt wheel file actions, performed by the tray app itself.
//
// Five of the Alt wheel's six entries are pure Windows UI (a share sheet, a
// floating preview, the clipboard as files, a move with a destination picker,
// a terminal), so the engine marks them handled_by=shell and this is where the
// drop is turned into the action. Copy Path is the sixth and is engine work;
// it comes back as data.clipboard_text and JobRunner puts it on the clipboard,
// so it never reaches here.
//
// This is the seam the wheel hands a dropped shell action to, the same shape
// as IEditorHost for the editor beads: ActionHost.Run(entry, files, jobs, ui).
// Nothing here reads the registry or names a wheel; it only maps the entry id
// the engine already chose to the Windows call that performs it, so a new
// action is an engine entry plus a case below, never a change to the wheel.
using System;
using System.Collections.Generic;
using System.Windows.Threading;
using Peel.Engine;
using Peel.Jobs;

namespace Peel.Actions;

internal static class ActionHost
{
    /// <summary>The ids the engine's actions family registers as handled_by=shell.</summary>
    internal static class Ids
    {
        internal const string Share = "files.share";
        internal const string Float = "files.float";
        internal const string Copy = "files.copy";
        internal const string Move = "files.move";
        internal const string Terminal = "files.terminal";
    }

    /// <summary>True when this host knows how to perform the given entry id.</summary>
    internal static bool Handles(string id) =>
        id is Ids.Share or Ids.Float or Ids.Copy or Ids.Move or Ids.Terminal;

    /// <summary>
    /// Perform a dropped shell action. Called on the UI thread with the files
    /// the drop named. Every action reports its own outcome through the toast
    /// host, so a failure is a message, never a thrown exception reaching the
    /// wheel.
    /// </summary>
    internal static void Run(
        ActionEntry entry,
        IReadOnlyList<string> files,
        JobRunner jobs,
        Dispatcher ui)
    {
        if (files.Count == 0)
        {
            Log.Write($"{entry.Id} dropped with no files");
            jobs.Report($"{entry.Label} had no files to work on.");
            return;
        }
        try
        {
            switch (entry.Id)
            {
                case Ids.Copy:
                    CopyAction.Run(files, jobs, ui);
                    break;
                case Ids.Move:
                    MoveAction.Run(files, jobs, ui);
                    break;
                case Ids.Terminal:
                    TerminalAction.Run(files, jobs);
                    break;
                case Ids.Float:
                    FloatAction.Run(files, jobs);
                    break;
                case Ids.Share:
                    ShareAction.Run(files, jobs);
                    break;
                default:
                    Log.Write($"no shell action is wired for {entry.Id}");
                    jobs.Report($"{entry.Label} is not available.");
                    break;
            }
        }
        catch (Exception error)
        {
            Log.Write($"{entry.Id} failed", error);
            jobs.Report($"{entry.Label} failed: {error.Message}");
        }
    }
}
