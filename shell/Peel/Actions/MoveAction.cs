// Move: send the dropped files to a folder. The wheel offers the last few
// destinations (RecentFolders) plus a picker for anywhere else, the same two
// ways Tangerine's Move works.
//
// Nothing is ever overwritten: a name already taken in the destination grows a
// " 2", " 3", ... suffix, the same rule the engine uses for its outputs, so a
// move never silently replaces a file. A move to another drive, which
// Directory.Move cannot do in one step, falls back to a copy then a delete.
using System;
using System.Collections.Generic;
using System.IO;
using Peel.Jobs;

namespace Peel.Actions;

internal readonly record struct MoveOutcome(int Moved, IReadOnlyList<string> Destinations, IReadOnlyList<string> Failures);

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
    internal static string MoveInto(string source, string destDir)
    {
        var name = Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var target = UniqueDestination(destDir, name, TakenOnDisk);
        if (Directory.Exists(source))
        {
            try
            {
                Directory.Move(source, target);
            }
            catch (IOException)
            {
                // A different drive: Directory.Move cannot cross volumes, so
                // copy the tree and remove the original.
                CopyTree(source, target);
                Directory.Delete(source, recursive: true);
            }
        }
        else
        {
            File.Move(source, target);
        }
        return target;
    }

    private static void CopyTree(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(dir.Replace(source, target));
        }
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, file.Replace(source, target), overwrite: false);
        }
    }

    /// <summary>Move every dropped item into one folder and record it as recent.</summary>
    internal static MoveOutcome MoveAll(IReadOnlyList<string> files, string destDir)
    {
        var destinations = new List<string>();
        var failures = new List<string>();
        foreach (var source in files)
        {
            try
            {
                destinations.Add(MoveInto(source, destDir));
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
        return new MoveOutcome(destinations.Count, destinations, failures);
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
            var outcome = MoveAll(files, destination);
            var where = Path.GetFileName(destination.TrimEnd(Path.DirectorySeparatorChar));
            if (outcome.Failures.Count == 0)
            {
                var noun = outcome.Moved == 1 ? "item" : "items";
                jobs.Report($"Moved {outcome.Moved} {noun} to {where}.");
            }
            else
            {
                jobs.Report($"Moved {outcome.Moved} of {files.Count} to {where}. {outcome.Failures[0]}");
            }
        }

        foreach (var folder in RecentFolders.Load())
        {
            var captured = folder;
            var item = menu.Items.Add(folder);
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
