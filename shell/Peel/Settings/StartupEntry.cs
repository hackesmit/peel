// Start with Windows: one value under HKCU\...\Run, so it needs no admin and
// belongs to this user only. install\publish.ps1 writes the same value. The
// value always names the installed Peel.exe (%LOCALAPPDATA%\Peel\app), never
// whatever Peel.exe happens to be running, so saving settings from a dev build
// cannot point log on at a build folder.
using System;
using System.IO;
using Microsoft.Win32;

namespace Peel.Settings;

internal static class StartupEntry
{
    internal const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string ValueName = "Peel";

    /// <summary>Where install\publish.ps1 puts the app.</summary>
    internal static string InstalledExe => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Peel", "app", "Peel.exe");

    /// <summary>The command line Windows runs at log on, or null when there is none.</summary>
    internal static string? Read(string runKey = RunKey)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(runKey);
            return key?.GetValue(ValueName) as string;
        }
        catch (Exception error)
        {
            Log.Write("cannot read the start with Windows entry", error);
            return null;
        }
    }

    internal static string CommandFor(string exe) => $"\"{exe}\"";

    /// <summary>
    /// Add or remove the Run value. Turning it on names <paramref name="exe"/>
    /// (the installed Peel.exe by default); when that file is not there Peel
    /// is not installed, so the value is left as it is and publish.ps1 adds it
    /// at install time. Returns null on success, else a message.
    /// </summary>
    internal static string? Set(bool on, string? exe = null, string runKey = RunKey)
    {
        exe ??= InstalledExe;
        try
        {
            if (on && !File.Exists(exe))
            {
                Log.Write($"start with Windows left as it is: Peel is not installed at {exe}");
                return null;
            }
            using var key = Registry.CurrentUser.CreateSubKey(runKey, writable: true);
            if (on)
            {
                var command = CommandFor(exe);
                if (!string.Equals(key.GetValue(ValueName) as string, command, StringComparison.OrdinalIgnoreCase))
                {
                    key.SetValue(ValueName, command, RegistryValueKind.String);
                    Log.Write($"start with Windows on: {command}");
                }
            }
            else if (key.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                Log.Write("start with Windows off");
            }
            return null;
        }
        catch (Exception error)
        {
            Log.Write("cannot change the start with Windows entry", error);
            return $"Peel could not change the start with Windows setting: {error.Message}";
        }
    }

    /// <summary>
    /// Put the Run value back to what Read returned before a change (null
    /// removes it). Returns null on success, else a message.
    /// </summary>
    internal static string? Restore(string? previous, string runKey = RunKey)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(runKey, writable: true);
            if (previous is null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            else
            {
                key.SetValue(ValueName, previous, RegistryValueKind.String);
            }
            Log.Write("start with Windows entry restored after a failed save");
            return null;
        }
        catch (Exception error)
        {
            Log.Write("cannot restore the start with Windows entry", error);
            return $"Peel could not undo its start with Windows change either: {error.Message}";
        }
    }
}
