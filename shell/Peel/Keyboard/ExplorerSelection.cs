// Reads the files selected in the Explorer tab that is in front, through the
// Shell.Application COM automation object.
//
// The keyboard path has no drag to carry the paths, so it asks the shell what
// the foreground Explorer window has selected. Shell.Application.Windows()
// enumerates every open Explorer tab (and Internet Explorer window). On Windows
// 11 every tab of one frame reports the frame's HWND, so matching the HWND is
// not enough: the active tab is the frame's visible ShellTabWindowClass child,
// and each enumerated window names its own tab through
// IServiceProvider::QueryService(SID_STopLevelBrowser, IShellBrowser) and
// IShellBrowser::GetWindow. SelectionRules makes the pick.
//
// COM is reached by late binding (dynamic over the ProgID), so the project
// needs no generated interop assembly; only the two vtable interfaces for the
// tab handle are declared here. The read runs on its own STA thread, never the
// UI thread: the UI thread also hosts the low level mouse and keyboard hooks,
// and a hung Explorer or an offline network share in File.Exists would stall
// them past LowLevelHooksTimeout. A read that takes too long is abandoned and
// reported. Every RCW is released with FinalReleaseComObject.
//
// Known limit: the live selection on the wallpaper desktop (Progman/WorkerW) is
// not read here; that needs cross-process listview reads the drag path never
// required. The result then says so and the controller tells the user to select
// in an Explorer window. Filed as friction on the bead.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Peel.Hooks;

namespace Peel.Keyboard;

internal readonly record struct SelectionResult(IReadOnlyList<string> Paths, string Reason, string Notice = "")
{
    internal bool HasFiles => Paths.Count > 0;

    internal static SelectionResult Empty(string reason) => new(Array.Empty<string>(), reason);
}

internal static class ExplorerSelection
{
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(6);

    private static readonly Guid SidTopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    private static readonly Guid IidShellBrowser = new("000214E2-0000-0000-C000-000000000046");

    [ComImport]
    [Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IComServiceProvider
    {
        [PreserveSig]
        int QueryService(ref Guid service, ref Guid riid, out IntPtr result);
    }

    // IShellBrowser derives from IOleWindow, so GetWindow is vtable slot 3
    // (after QueryInterface, AddRef, Release). It is called through the raw
    // pointer rather than an RCW: an RCW could share identity with the window's
    // own RCW, and releasing it would then break the window still in use.
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetWindowFn(IntPtr self, out IntPtr handle);

    /// <summary>
    /// Read the selection of <paramref name="foreground"/> (captured on the UI
    /// thread when the hotkey fired) on a fresh STA thread. Never throws; a read
    /// that outlives <paramref name="timeout"/> is reported as such and its
    /// thread is left to finish on its own (it is a background thread).
    /// </summary>
    internal static Task<SelectionResult> ReadAsync(IntPtr foreground, TimeSpan timeout)
    {
        var done = new TaskCompletionSource<SelectionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => done.TrySetResult(Read(foreground)))
        {
            IsBackground = true,
            Name = "Peel Explorer selection",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return WithTimeout(done.Task, timeout);
    }

    private static async Task<SelectionResult> WithTimeout(Task<SelectionResult> read, TimeSpan timeout)
    {
        var winner = await Task.WhenAny(read, Task.Delay(timeout)).ConfigureAwait(false);
        if (winner == read)
        {
            return await read.ConfigureAwait(false);
        }
        Log.Write($"reading the Explorer selection took longer than {timeout.TotalSeconds:0} s, abandoned");
        return SelectionResult.Empty("Explorer did not answer in time. Try the hotkey again.");
    }

    /// <summary>
    /// The filesystem paths selected in the active tab of <paramref name="foreground"/>,
    /// or an empty result whose Reason says why there are none. Never throws.
    /// Call on an STA thread.
    /// </summary>
    internal static SelectionResult Read(IntPtr foreground)
    {
        if (foreground == IntPtr.Zero)
        {
            return SelectionResult.Empty("No window is in front.");
        }

        var activeTab = ActiveTabOf(foreground);
        object? shell = null;
        object? windows = null;
        var candidates = new List<TabCandidate>();
        var held = new Dictionary<int, object>();
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type is null)
            {
                return SelectionResult.Empty("Shell automation is not available on this system.");
            }
            shell = Activator.CreateInstance(type);
            windows = ((dynamic)shell!).Windows();
            int count = ((dynamic)windows!).Count;
            for (var i = 0; i < count; i++)
            {
                object? window = null;
                try
                {
                    window = ((dynamic)windows).Item(i);
                    if (window is null)
                    {
                        continue;
                    }
                    long handle = (long)((dynamic)window).HWND;
                    if (handle != foreground.ToInt64())
                    {
                        continue;
                    }
                    candidates.Add(new TabCandidate(i, TabHandleOf(window)));
                    held[i] = window;
                    window = null; // kept for the pick below, released in finally
                }
                catch (Exception error)
                {
                    // A window that is not a file-system folder (Internet
                    // Explorer, a control panel page) may throw: skip it.
                    Log.Write($"skipping an Explorer window while reading the selection: {error.Message}");
                }
                finally
                {
                    Release(window);
                }
            }

            if (candidates.Count == 0)
            {
                var className = Native.ClassOf(foreground);
                if (className is "Progman" or "WorkerW")
                {
                    return SelectionResult.Empty(
                        "Reading the desktop selection is not supported yet. Select the files in an Explorer window.");
                }
                return SelectionResult.Empty(
                    "The window in front is not Explorer. Select one or more files in Explorer, then press the hotkey.");
            }

            var picked = SelectionRules.PickActiveTab(candidates, activeTab, out var reason);
            Log.Write($"explorer frame {foreground.ToInt64()}: {candidates.Count} tab(s), active tab {activeTab.ToInt64()}, picked {picked}");
            if (picked < 0)
            {
                return SelectionResult.Empty(reason);
            }
            return FromWindow(held[picked]);
        }
        catch (Exception error)
        {
            Log.Write("cannot read the Explorer selection", error);
            return SelectionResult.Empty($"Peel could not read the Explorer selection: {error.Message}");
        }
        finally
        {
            foreach (var window in held.Values)
            {
                Release(window);
            }
            Release(windows);
            Release(shell);
        }
    }

    /// <summary>The visible ShellTabWindowClass child of an Explorer frame, or zero (no tabs, or not Explorer).</summary>
    private static IntPtr ActiveTabOf(IntPtr frame)
    {
        var tabs = new List<(IntPtr, bool)>();
        var child = IntPtr.Zero;
        for (var guard = 0; guard < 256; guard++)
        {
            child = KeyboardNative.FindWindowEx(frame, child, "ShellTabWindowClass", null);
            if (child == IntPtr.Zero)
            {
                break;
            }
            tabs.Add((child, KeyboardNative.IsWindowVisible(child)));
        }
        return SelectionRules.PickVisibleTab(tabs);
    }

    /// <summary>The ShellTabWindowClass handle of one enumerated window, or zero when it does not say.</summary>
    private static IntPtr TabHandleOf(object window)
    {
        if (window is not IComServiceProvider provider)
        {
            return IntPtr.Zero;
        }
        var service = SidTopLevelBrowser;
        var riid = IidShellBrowser;
        IntPtr pointer = IntPtr.Zero;
        try
        {
            if (provider.QueryService(ref service, ref riid, out pointer) != 0 || pointer == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }
            var table = Marshal.ReadIntPtr(pointer);
            var slot = Marshal.ReadIntPtr(table, 3 * IntPtr.Size);
            var getWindow = Marshal.GetDelegateForFunctionPointer<GetWindowFn>(slot);
            return getWindow(pointer, out var handle) == 0 ? handle : IntPtr.Zero;
        }
        catch (Exception error)
        {
            Log.Write($"cannot read an Explorer tab handle: {error.Message}");
            return IntPtr.Zero;
        }
        finally
        {
            if (pointer != IntPtr.Zero)
            {
                Marshal.Release(pointer);
            }
        }
    }

    private static SelectionResult FromWindow(object window)
    {
        var paths = new List<string>();
        int drives = 0, virtuals = 0, seen = 0;
        object? document = null;
        object? items = null;
        try
        {
            document = ((dynamic)window).Document;
            items = ((dynamic)document!).SelectedItems();
            int count = ((dynamic)items!).Count;
            for (var i = 0; i < count; i++)
            {
                object? item = null;
                try
                {
                    item = ((dynamic)items).Item(i);
                    if (item is null)
                    {
                        continue;
                    }
                    seen++;
                    string? path = ((dynamic)item).Path as string;
                    switch (SelectionRules.Classify(path, File.Exists, Directory.Exists))
                    {
                        case SelectedKind.File:
                        case SelectedKind.Folder:
                            paths.Add(path!);
                            break;
                        case SelectedKind.DriveRoot:
                            drives++;
                            Log.Write($"skipping a selected drive root: {path}");
                            break;
                        default:
                            virtuals++;
                            Log.Write($"skipping a selected virtual item: {path}");
                            break;
                    }
                }
                catch (Exception error)
                {
                    virtuals++;
                    Log.Write($"skipping a selected item: {error.Message}");
                }
                finally
                {
                    Release(item);
                }
            }
        }
        catch (Exception error)
        {
            Log.Write($"the active Explorer tab has no readable selection: {error.Message}");
            return SelectionResult.Empty("This Explorer window shows no files Peel can read.");
        }
        finally
        {
            Release(items);
            Release(document);
        }

        var message = SelectionRules.SkippedMessage(drives, virtuals, paths.Count);
        if (paths.Count > 0)
        {
            return new SelectionResult(paths, string.Empty, message);
        }
        return SelectionResult.Empty(seen == 0 ? "Nothing is selected in this Explorer window." : message);
    }

    private static void Release(object? comObject)
    {
        if (comObject is not null && Marshal.IsComObject(comObject))
        {
            try
            {
                Marshal.FinalReleaseComObject(comObject);
            }
            catch (Exception error)
            {
                Log.Write($"cannot release a COM object: {error.Message}");
            }
        }
    }
}
