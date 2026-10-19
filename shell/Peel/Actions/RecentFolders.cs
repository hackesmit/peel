// The last few folders Move sent files to, newest first. Move offers them so
// the common case (the same few destinations) is one click, with a picker for
// anywhere else.
//
// The list is plain text, one path per line, under %LOCALAPPDATA%\Peel, next
// to the log. Promotion is pure and tested: a folder used again moves to the
// front without a duplicate (Windows paths compare case-insensitively), and
// the list is capped so it stays a short menu.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Peel.Actions;

internal static class RecentFolders
{
    internal const int Max = 5;

    /// <summary>
    /// Put <paramref name="folder"/> at the front, drop any case-insensitive
    /// duplicate of it, and keep at most <see cref="Max"/> entries.
    /// </summary>
    internal static IReadOnlyList<string> Promote(IReadOnlyList<string> current, string folder)
    {
        var result = new List<string> { folder };
        foreach (var existing in current)
        {
            if (!string.Equals(existing, folder, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(existing);
            }
        }
        return result.Count > Max ? result.GetRange(0, Max) : result;
    }

    /// <summary>
    /// The folders that still exist, in order. A local path whose folder is
    /// gone is dropped, so Move never offers a dead destination. A network
    /// (UNC) path is kept without a check: probing an unreachable server can
    /// block for many seconds, and Move reports a missing folder anyway.
    /// </summary>
    internal static IReadOnlyList<string> Prune(IReadOnlyList<string> folders, Func<string, bool> exists) =>
        folders.Where(f => f.StartsWith(@"\\", StringComparison.Ordinal) || exists(f)).ToList();

    /// <summary>The saved list with missing local folders removed (and saved so).</summary>
    internal static IReadOnlyList<string> LoadExisting()
    {
        var saved = Load();
        var kept = Prune(saved, Directory.Exists);
        if (kept.Count != saved.Count)
        {
            Save(kept);
        }
        return kept;
    }

    /// <summary>The text of a menu item for <paramref name="folder"/>: '&amp;'
    /// is doubled so WinForms shows it instead of reading it as a mnemonic.</summary>
    internal static string MenuText(string folder) => folder.Replace("&", "&&");

    private static string StorePath()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(local, "Peel", "recent-folders.txt");
    }

    internal static IReadOnlyList<string> Load()
    {
        try
        {
            var path = StorePath();
            if (!File.Exists(path))
            {
                return Array.Empty<string>();
            }
            return File.ReadAllLines(path)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .Take(Max)
                .ToList();
        }
        catch (Exception error)
        {
            Log.Write("cannot read the recent folders", error);
            return Array.Empty<string>();
        }
    }

    internal static void Save(IReadOnlyList<string> folders)
    {
        try
        {
            var path = StorePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllLines(path, folders.Take(Max));
        }
        catch (Exception error)
        {
            Log.Write("cannot save the recent folders", error);
        }
    }

    /// <summary>Record a destination just used, folding it into the saved list.</summary>
    internal static void Remember(string folder) => Save(Promote(Prune(Load(), Directory.Exists), folder));
}
