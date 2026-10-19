// The decisions behind reading an Explorer selection, with no COM and no
// Windows calls in them so the unit tests can pin each one.
//
// Two questions live here. First, which Shell.Application window is the tab the
// user is looking at: on Windows 11 every tab of one Explorer frame reports the
// same HWND, so the frame alone is ambiguous and the tab has to be matched by
// its own ShellTabWindowClass handle. Second, which selected items the engine
// can take: real files and folders yes, a drive root or a virtual namespace
// item (This PC, a library, Recycle Bin) no, and the user is told what was
// skipped instead of seeing it vanish.
using System;
using System.Collections.Generic;
using System.IO;

namespace Peel.Keyboard;

/// <summary>What one selected item is, as far as the engine is concerned.</summary>
internal enum SelectedKind
{
    File,
    Folder,
    DriveRoot,
    Virtual,
}

/// <summary>One Shell.Application window that belongs to the foreground frame.</summary>
internal readonly record struct TabCandidate(int Index, IntPtr TabHandle);

internal static class SelectionRules
{
    /// <summary>
    /// The candidate that is the active tab, or -1 with a reason when it cannot
    /// be told apart. The rule: a candidate whose tab handle is the visible
    /// ShellTabWindowClass child wins. With no tabs at all (Windows 10, or a
    /// frame that has not built its tab windows) a single candidate is
    /// unambiguous. Several candidates and no match means guessing, and a guess
    /// converts the wrong files, so it refuses.
    /// </summary>
    internal static int PickActiveTab(IReadOnlyList<TabCandidate> candidates, IntPtr activeTab, out string reason)
    {
        reason = string.Empty;
        if (candidates.Count == 0)
        {
            reason = "none";
            return -1;
        }
        if (activeTab != IntPtr.Zero)
        {
            foreach (var candidate in candidates)
            {
                if (candidate.TabHandle == activeTab)
                {
                    return candidate.Index;
                }
            }
        }
        if (candidates.Count == 1)
        {
            return candidates[0].Index;
        }
        reason = "Peel could not tell which Explorer tab is active. Click the file again, then press the hotkey.";
        return -1;
    }

    /// <summary>
    /// The visible tab among the frame's ShellTabWindowClass children, in the
    /// order Windows lists them (top of the z-order first). Inactive tabs are
    /// hidden, so the first visible one is the active tab.
    /// </summary>
    internal static IntPtr PickVisibleTab(IReadOnlyList<(IntPtr Handle, bool Visible)> tabs)
    {
        foreach (var (handle, visible) in tabs)
        {
            if (visible)
            {
                return handle;
            }
        }
        return IntPtr.Zero;
    }

    /// <summary>What a selected item's Path names. The existence checks are passed in so a test needs no disk.</summary>
    internal static SelectedKind Classify(string? path, Func<string, bool> fileExists, Func<string, bool> directoryExists)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return SelectedKind.Virtual;
        }
        // A namespace item (This PC is "::{20D04FE0-...}") is not a path.
        if (path.StartsWith("::", StringComparison.Ordinal) || !Path.IsPathFullyQualified(path))
        {
            return SelectedKind.Virtual;
        }
        if (IsRoot(path))
        {
            return directoryExists(path) ? SelectedKind.DriveRoot : SelectedKind.Virtual;
        }
        if (fileExists(path))
        {
            return SelectedKind.File;
        }
        if (directoryExists(path))
        {
            return SelectedKind.Folder;
        }
        return SelectedKind.Virtual;
    }

    /// <summary>A drive (C:\) or a share root (\\server\share), with or without the trailing separator.</summary>
    internal static bool IsRoot(string path)
    {
        string? root;
        try
        {
            root = Path.GetPathRoot(path);
        }
        catch (ArgumentException)
        {
            return false;
        }
        if (string.IsNullOrEmpty(root))
        {
            return false;
        }
        var trimmed = path.TrimEnd('\\', '/');
        return string.Equals(trimmed, root.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The message for the items that were left out, or empty when none were.
    /// <paramref name="kept"/> says whether anything is left to convert.
    /// </summary>
    internal static string SkippedMessage(int drives, int virtuals, int kept)
    {
        if (drives == 0 && virtuals == 0)
        {
            return string.Empty;
        }
        var what = (drives, virtuals) switch
        {
            ( > 0, 0) => drives == 1 ? "a drive" : $"{drives} drives",
            (0, > 0) => virtuals == 1 ? "an item that is not a file or folder" : $"{virtuals} items that are not files or folders",
            _ => $"{drives + virtuals} drives or items that are not files or folders",
        };
        return kept > 0
            ? $"Peel skipped {what}. It converts the other selected files."
            : $"Peel cannot convert {what}. Select files or folders, then press the hotkey.";
    }
}
