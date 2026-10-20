// Start with Windows: one value under HKCU\...\Run, so it needs no admin and
// belongs to this user only. install\publish.ps1 writes the same value; the
// settings window turns it on or off for whatever Peel.exe is running.
using System;
using Microsoft.Win32;

namespace Peel.Settings;

internal static class StartupEntry
{
    internal const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string ValueName = "Peel";

    /// <summary>The command line Windows runs at log on, or null when there is none.</summary>
    internal static string? Read()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) as string;
        }
        catch (Exception error)
        {
            Log.Write("cannot read the start with Windows entry", error);
            return null;
        }
    }

    internal static string CommandFor(string exe) => $"\"{exe}\"";

    /// <summary>Add or remove the Run value. Returns null on success, else a message.</summary>
    internal static string? Set(bool on, string? exe)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (on)
            {
                if (string.IsNullOrEmpty(exe))
                {
                    return "Peel cannot tell where it is installed, so it cannot start with Windows.";
                }
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
}
