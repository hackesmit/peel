// Terminal: open a shell in the folder of what was dropped. Windows Terminal
// (wt.exe) is the modern one; a machine without it falls back to PowerShell.
//
// The folder is the dropped folder itself, or the parent of a dropped file. A
// mixed selection opens one terminal, at the first item's folder, the same way
// Tangerine opens one Finder window.
//
// The folder never appears in any command line. Windows Terminal has its own
// command grammar on top of argv: a ';' in any argument starts a new
// subcommand, so passing "-d <folder>" would let a folder named
// "x; new-tab powershell -Command calc" start a second command (lucy round 1).
// Argv quoting does not switch that parser off. So wt.exe always gets the fixed
// arguments "-d ." and the folder is handed over only as the new process's
// working directory (ProcessStartInfo.WorkingDirectory, the lpCurrentDirectory
// of CreateProcess), which no parser reads. The PowerShell fallback does the
// same with a constant command text.
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

    /// <summary>The only arguments wt.exe is ever given. "." is the working directory.</summary>
    internal static readonly IReadOnlyList<string> PrimaryArguments = new[] { "-d", "." };

    /// <summary>Windows Terminal opened at the folder, through its working directory only.</summary>
    internal static ProcessStartInfo Primary(string folder)
    {
        var info = new ProcessStartInfo
        {
            FileName = "wt.exe",
            UseShellExecute = false,
            WorkingDirectory = folder,
        };
        foreach (var arg in PrimaryArguments)
        {
            info.ArgumentList.Add(arg);
        }
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
        // Constant text: the folder arrives only as the working directory.
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
