// Move: send the dropped files to a folder. The wheel offers the last few
// destinations (RecentFolders) plus a picker for anywhere else, the same two
// ways Tangerine's Move works.
//
// Nothing is ever overwritten: a name already taken in the destination grows a
// " 2", " 3", ... suffix, the same rule the engine uses for its outputs, so a
// move never silently replaces a file. A folder move to another volume, which
// cannot be one rename, falls back to CrossVolumeMove: copy from a manifest,
// verify, then delete only the source files that still match, never a
// recursive delete; anything left behind is reported by name. Which volume a
// folder is on, and whether a destination is inside the moved folder, are
// decided from physical file identities (FileIdentity), never path strings.
using System;
using System.Collections.Generic;
using System.IO;
using Peel.Jobs;

namespace Peel.Actions;

/// <summary>What a Move did. <paramref name="Partials"/> names the items whose
/// copy is complete but some source entries stayed behind (new, changed or not
/// removable); they count as moved, since nothing is missing at the destination.</summary>
internal readonly record struct MoveOutcome(
    int Moved, IReadOnlyList<string> Destinations, IReadOnlyList<string> Failures, IReadOnlyList<string> Partials)
{
    internal MoveOutcome(int moved, IReadOnlyList<string> destinations, IReadOnlyList<string> failures)
        : this(moved, destinations, failures, Array.Empty<string>())
    {
    }
}

internal static class MoveAction
{
    /// <summary>Where the suffix search gives up, matching the engine's cap.</summary>
    internal const int MaxSuffix = 1000;

    /// <summary>
    /// A free path for <paramref name="sourceName"/> inside
    /// <paramref name="destDir"/>: the name itself if free, else the stem with
    /// " 2", " 3", ... before the extension. <paramref name="exists"/> reports
    /// whether a candidate is taken (File or Directory on the real disk).
    /// </summary>
    internal static string UniqueDestination(string destDir, string sourceName, Func<string, bool> exists)
    {
        var first = Path.Combine(destDir, sourceName);
        if (!exists(first))
        {
            return first;
        }
        var stem = Path.GetFileNameWithoutExtension(sourceName);
        var extension = Path.GetExtension(sourceName);
        for (var n = 2; n < MaxSuffix; n++)
        {
            var candidate = Path.Combine(destDir, $"{stem} {n}{extension}");
            if (!exists(candidate))
            {
                return candidate;
            }
        }
        throw new IOException($"no free name for {sourceName} in {destDir} after {MaxSuffix} tries");
    }

    private static bool TakenOnDisk(string path) => File.Exists(path) || Directory.Exists(path);

    /// <summary>Move one file or folder into the destination, never overwriting.</summary>
    internal static string MoveInto(string source, string destDir) => MoveInto(source, destDir, out _);

    /// <summary>
    /// Move one file or folder into the destination, never overwriting.
    /// <paramref name="remaining"/> lists the source entries a cross-volume
    /// folder move left in place (relative to the source), empty otherwise.
    /// </summary>
    internal static string MoveInto(string source, string destDir, out IReadOnlyList<string> remaining)
    {
        remaining = Array.Empty<string>();
        var name = Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var target = UniqueDestination(destDir, name, TakenOnDisk);
        if (Directory.Exists(source))
        {
            // Moving a folder into itself or one of its own descendants is
            // never valid: the cross-volume fallback below would copy the tree
            // into a subfolder of the source and then delete the source, taking
            // the copy with it. The test is physical (volume serial plus file
            // index of the destination and each of its ancestors), so a UNC
            // alias, subst drive, junction, short name or other case cannot
            // slip past it. If the identities cannot be read it throws and
            // nothing moves.
            if (FileIdentity.IsSameOrBeneath(destDir, source))
            {
                throw new IOException($"cannot move \"{name}\" into itself");
            }
            // Same volume serial: try the one step rename. Windows still says
            // ERROR_NOT_SAME_DEVICE for the same disk reached locally and by a
            // UNC alias, and that is the only case that falls through to the
            // copy; every other rename failure is reported, never retried as a
            // destructive copy. A different serial (another drive, a volume
            // mounted in a folder, a network share) goes straight to the copy.
            var sameVolume = FileIdentity.Of(source).VolumeSerial == FileIdentity.Of(destDir).VolumeSerial;
            // CrossVolumeMove refuses before touching anything when the copy
            // could not be faithful (a link, a folder's alternate data stream).
            if (!sameVolume || !FileIdentity.TryRename(source, target))
            {
                remaining = CrossVolumeMove.Move(source, target).Remaining;
            }
        }
        else
        {
            File.Move(source, target);
        }
        return target;
    }

    /// <summary>Move every dropped item into one folder and record it as recent.</summary>
    internal static MoveOutcome MoveAll(IReadOnlyList<string> files, string destDir)
    {
        var destinations = new List<string>();
        var failures = new List<string>();
        var partials = new List<string>();
        foreach (var source in files)
        {
            try
            {
                destinations.Add(MoveInto(source, destDir, out var remaining));
                if (remaining.Count > 0)
                {
                    var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(source));
                    partials.Add(remaining.Count == 1 && remaining[0] == "."
                        ? $"{name}: copied, but an empty folder stayed in the source"
                        : $"{name}: copied, but {remaining.Count} item(s) stayed in the source because they are new, changed, in use or their copy could not be verified: {CrossVolumeMove.Describe(remaining)}");
                }
            }
            catch (Exception error)
            {
                Log.Write($"move: {source} failed", error);
                failures.Add($"{Path.GetFileName(source)}: {error.Message}");
            }
        }
        if (destinations.Count > 0)
        {
            RecentFolders.Remember(destDir);
        }
        return new MoveOutcome(destinations.Count, destinations, failures, partials);
    }

    /// <summary>
    /// Offer the recent folders and a picker at the cursor, then move into
    /// whatever is chosen. The menu is not modal, so the move runs later on the
    /// UI thread in the chosen item's handler; dismissing the menu moves
    /// nothing.
    /// </summary>
    internal static void Run(IReadOnlyList<string> files, JobRunner jobs, System.Windows.Threading.Dispatcher ui)
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        void MoveTo(string destination)
        {
            // The move can copy gigabytes across drives, so it runs off the UI
            // thread: doing it in the menu click handler would freeze the wheel
            // and the toasts. The menu is not modal, so ActionHost.Run has
            // already returned; failures are caught here, never thrown into the
            // click handler, and the one report is marshalled back to the UI.
            System.Threading.Tasks.Task.Run(() =>
            {
                string message;
                try
                {
                    var outcome = MoveAll(files, destination);
                    var where = Path.GetFileName(destination.TrimEnd(Path.DirectorySeparatorChar));
                    if (outcome.Failures.Count == 0 && outcome.Partials.Count == 0)
                    {
                        var noun = outcome.Moved == 1 ? "item" : "items";
                        message = $"Moved {outcome.Moved} {noun} to {where}.";
                    }
                    else if (outcome.Failures.Count == 0)
                    {
                        message = $"Partly moved to {where}. {outcome.Partials[0]}";
                    }
                    else
                    {
                        message = $"Moved {outcome.Moved} of {files.Count} to {where}. {outcome.Failures[0]}";
                    }
                }
                catch (Exception error)
                {
                    Log.Write("move: the move failed", error);
                    message = $"The move failed: {error.Message}";
                }
                ui.InvokeAsync(() => jobs.Report(message));
            });
        }

        foreach (var folder in RecentFolders.LoadExisting())
        {
            var captured = folder;
            var item = menu.Items.Add(RecentFolders.MenuText(folder));
            item.Click += (_, _) => MoveTo(captured);
        }
        if (menu.Items.Count > 0)
        {
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        }
        var pick = menu.Items.Add("Choose folder...");
        pick.Click += (_, _) =>
        {
            if (PickFolder() is { } destination)
            {
                MoveTo(destination);
            }
            else
            {
                Log.Write("move: cancelled, no destination chosen");
            }
        };
        menu.Closed += (_, _) => menu.Dispose();
        menu.Show(System.Windows.Forms.Cursor.Position);
    }

    private static string? PickFolder()
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Move the dropped files here",
            UseDescriptionForTitle = true,
        };
        return dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK ? dialog.SelectedPath : null;
    }
}
