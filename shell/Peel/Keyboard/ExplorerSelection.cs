// Reads the files selected in the Explorer window that is in front, through the
// Shell.Application COM automation object.
//
// The keyboard path has no drag to carry the paths, so it asks the shell what
// the foreground Explorer window has selected. Shell.Application.Windows()
// enumerates every open Explorer (and Internet Explorer) window; the one whose
// HWND matches the foreground window is the one the user is looking at, and its
// Document.SelectedItems() names the selection. Only real filesystem paths are
// kept, so a selected virtual item (This PC, a library root) is quietly
// dropped rather than handed to the engine, which could not open it anyway.
//
// COM is reached by late binding (dynamic over the ProgID), so the project
// needs no COM reference and no generated interop assembly. The call is cheap
// and a hotkey press is deliberate and infrequent, so it runs on the UI thread.
//
// Known limit: the live selection on the wallpaper desktop (Progman/WorkerW) is
// not read here; that needs cross-process listview reads the drag path never
// required. The result then says so and the controller tells the user to select
// in an Explorer window. Filed as friction on the bead.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Peel.Hooks;

namespace Peel.Keyboard;

internal readonly record struct SelectionResult(IReadOnlyList<string> Paths, string Reason)
{
    internal bool HasFiles => Paths.Count > 0;

    internal static SelectionResult Empty(string reason) => new(Array.Empty<string>(), reason);
}

internal static class ExplorerSelection
{
    /// <summary>
    /// The filesystem paths selected in the foreground Explorer window, or an
    /// empty result whose Reason says why there are none. Never throws.
    /// </summary>
    internal static SelectionResult Read()
    {
        var foreground = KeyboardNative.GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return SelectionResult.Empty("No window is in front.");
        }

        object? shell = null;
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type is null)
            {
                return SelectionResult.Empty("Shell automation is not available on this system.");
            }
            shell = Activator.CreateInstance(type);
            dynamic application = shell!;
            dynamic windows = application.Windows();
            int count = windows.Count;
            for (var i = 0; i < count; i++)
            {
                dynamic? window = null;
                try
                {
                    window = windows.Item(i);
                    if (window is null)
                    {
                        continue;
                    }
                    long handle = (long)window.HWND;
                    if (handle != foreground.ToInt64())
                    {
                        continue;
                    }
                    var paths = SelectedPaths(window);
                    return paths.Count > 0
                        ? new SelectionResult(paths, string.Empty)
                        : SelectionResult.Empty("Nothing is selected in this Explorer window.");
                }
                catch (Exception error)
                {
                    // A window that is not a file-system folder (Internet
                    // Explorer, a control panel page) has no SelectedItems:
                    // skip it and keep looking for the foreground one.
                    Log.Write($"skipping an Explorer window while reading the selection: {error.Message}");
                }
                finally
                {
                    Release(window);
                }
            }

            var className = Native.ClassOf(foreground);
            if (className is "Progman" or "WorkerW")
            {
                return SelectionResult.Empty(
                    "Reading the desktop selection is not supported yet. Select the files in an Explorer window.");
            }
            return SelectionResult.Empty(
                "The window in front is not Explorer. Select one or more files in Explorer, then press the hotkey.");
        }
        catch (Exception error)
        {
            Log.Write("cannot read the Explorer selection", error);
            return SelectionResult.Empty($"Peel could not read the Explorer selection: {error.Message}");
        }
        finally
        {
            Release(shell);
        }
    }

    private static List<string> SelectedPaths(dynamic window)
    {
        var paths = new List<string>();
        dynamic? document = null;
        dynamic? items = null;
        try
        {
            document = window.Document;
            items = document.SelectedItems();
            int count = items.Count;
            for (var i = 0; i < count; i++)
            {
                dynamic? item = null;
                try
                {
                    item = items.Item(i);
                    string? path = item?.Path as string;
                    // Only real files and folders: a virtual item's Path is a
                    // namespace string the engine cannot open.
                    if (!string.IsNullOrEmpty(path) && (File.Exists(path) || Directory.Exists(path)))
                    {
                        paths.Add(path);
                    }
                }
                catch (Exception error)
                {
                    Log.Write($"skipping a selected item: {error.Message}");
                }
                finally
                {
                    Release(item);
                }
            }
        }
        finally
        {
            Release(items);
            Release(document);
        }
        return paths;
    }

    private static void Release(object? comObject)
    {
        if (comObject is not null && Marshal.IsComObject(comObject))
        {
            try
            {
                Marshal.ReleaseComObject(comObject);
            }
            catch (Exception error)
            {
                Log.Write($"cannot release a COM object: {error.Message}");
            }
        }
    }
}
