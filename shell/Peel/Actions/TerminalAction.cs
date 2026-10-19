// Terminal: open a shell in the folder of what was dropped. Windows Terminal
// (wt.exe) is the modern one; a machine without it falls back to PowerShell.
//
// The folder is the dropped folder itself, or the parent of a dropped file. A
// mixed selection opens one terminal, at the first item's folder, the same way
// Tangerine opens one Finder window. Arguments go through ProcessStartInfo's
// ArgumentList, never a command string, so a folder named with a quote or an
// ampersand can never be parsed as part of a command (the same reason the
// engine is never started through the .cmd shim).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Peel.Jobs;

namespace Peel.Actions;

internal static class TerminalAction
{
    /// <summary>The folder to open: a dropped folder, or a dropped file's parent.</summary>
    internal static string FolderFor(IReadOnlyList<string> files)
    {
        var first = files[0];
        if (Directory.Exists(first))
        {
            return first;
        }
        var parent = Path.GetDirectoryName(first);
        return string.IsNullOrEmpty(parent) ? first : parent;
    }

    /// <summary>Windows Terminal opened at the folder.</summary>
    internal static ProcessStartInfo Primary(string folder)
    {
        var info = new ProcessStartInfo { FileName = "wt.exe", UseShellExecute = false };
        info.ArgumentList.Add("-d");
        info.ArgumentList.Add(folder);
        return info;
    }

    /// <summary>PowerShell left open at the folder, for a machine without wt.exe.</summary>
    internal static ProcessStartInfo Fallback(string folder)
    {
        var info = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            WorkingDirectory = folder,
        };
        info.ArgumentList.Add("-NoExit");
        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-Command");
        info.ArgumentList.Add("Set-Location -LiteralPath $PWD");
        return info;
    }

    internal static void Run(IReadOnlyList<string> files, JobRunner jobs)
    {
        var folder = FolderFor(files);
        try
        {
            Process.Start(Primary(folder));
            Log.Write($"terminal: wt.exe at {folder}");
            jobs.Report($"Opened a terminal in {Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar))}.");
            return;
        }
        catch (Exception error)
        {
            Log.Write($"terminal: wt.exe did not start, trying PowerShell: {error.Message}");
        }
        try
        {
            Process.Start(Fallback(folder));
            Log.Write($"terminal: powershell at {folder}");
            jobs.Report($"Opened PowerShell in {Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar))}.");
        }
        catch (Exception error)
        {
            Log.Write("terminal: no shell could be started", error);
            jobs.Report($"No terminal could be opened: {error.Message}");
        }
    }
}
