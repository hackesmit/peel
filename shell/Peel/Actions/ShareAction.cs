// Share: raise the Windows share sheet (DataTransferManager) for the dropped
// files, the Windows stand in for AirDrop. Nearby Share, Mail, Teams and
// whatever else the user has are the targets the system offers; Peel only
// hands the files over.
//
// The share sheet is anchored to a window, and Peel is a tray app with none,
// so a tiny transparent window is opened at the cursor to anchor it and taken
// down once the sheet is dismissed. The files are offered as StorageItems in
// the DataRequested handler, with a deferral because resolving a path to a
// StorageFile is asynchronous. If the sheet cannot be raised from the tray
// process the failure is a message, not a crash (the bead's stop-when).
//
// Closing the anchor does not take the sheet down, and a sheet left on screen
// outlives Peel and swallows the next drag beneath it. So a sheet Peel raised
// is dismissed (ShareSheet) when the anchor's timer ends and when Peel exits.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Peel.Jobs;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;
using WinRT;

namespace Peel.Actions;

internal static class ShareAction
{
    [ComImport]
    [Guid("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDataTransferManagerInterop
    {
        IntPtr GetForWindow([In] IntPtr appWindow, [In] ref Guid riid);
        void ShowShareUIForWindow([In] IntPtr appWindow);
    }

    private static readonly Guid DataTransferManagerId =
        new(0xa5caee9b, 0x8708, 0x49d1, 0x8d, 0x36, 0x67, 0xd2, 0x5a, 0x8d, 0xa0, 0x0c);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    internal static void Run(IReadOnlyList<string> files, JobRunner jobs)
    {
        if (!TryShow(files, out var error))
        {
            jobs.Report($"Share is not available: {error}");
        }
    }

    /// <summary>
    /// Raise the share sheet for the files. Returns false with the reason when
    /// it cannot be raised (the bead's stop-when), so the caller can report it
    /// and the scripted check can read it back.
    /// </summary>
    internal static bool TryShow(IReadOnlyList<string> files, out string error)
    {
        error = string.Empty;
        Window anchor;
        try
        {
            anchor = ShowAnchor();
        }
        catch (Exception ex)
        {
            Log.Write("share: no anchor window", ex);
            error = $"no anchor window: {ex.Message}";
            return false;
        }

        try
        {
            var hwnd = new WindowInteropHelper(anchor).Handle;
            var interop = DataTransferManager.As<IDataTransferManagerInterop>();
            var abi = interop.GetForWindow(hwnd, DataTransferManagerId);
            var manager = MarshalInterface<DataTransferManager>.FromAbi(abi);

            TypedEventHandler<DataTransferManager, DataRequestedEventArgs>? handler = null;
            handler = async (_, args) =>
            {
                manager.DataRequested -= handler;
                var request = args.Request;
                var deferral = request.GetDeferral();
                try
                {
                    var items = new List<IStorageItem>();
                    foreach (var path in files)
                    {
                        if (Directory.Exists(path))
                        {
                            items.Add(await StorageFolder.GetFolderFromPathAsync(path));
                        }
                        else
                        {
                            items.Add(await StorageFile.GetFileFromPathAsync(path));
                        }
                    }
                    request.Data.Properties.Title = Title(files);
                    request.Data.SetStorageItems(items);
                    Log.Write($"share: offered {items.Count} item(s)");
                }
                catch (Exception error)
                {
                    request.FailWithDisplayText($"Peel could not read the files to share: {error.Message}");
                    Log.Write("share: offering the files failed", error);
                }
                finally
                {
                    deferral.Complete();
                }
            };
            manager.DataRequested += handler;

            // The share sheet is a separate system flyout anchored to this
            // window; closing the window dismisses it, so the anchor must
            // outlive the user's choice. It is not closed on deactivation (that
            // fires the instant the sheet takes focus). A timer takes the 1x1
            // invisible window down well after the sheet is done, and app exit
            // closes it in any case.
            var generation = ++_generation;
            var cleanup = new DispatcherTimer { Interval = AnchorLifetime };
            cleanup.Tick += (_, _) =>
            {
                cleanup.Stop();
                // Only the newest share's timer may take a sheet down, so an
                // older timer never closes a sheet the user just raised again.
                if (generation == _generation)
                {
                    DismissRaised("the anchor timer ended");
                }
                anchor.Close();
            };
            cleanup.Start();
            DismissOnExit();
            interop.ShowShareUIForWindow(hwnd);
            _raised = true;
            Log.Write($"share: share sheet raised for {files.Count} file(s)");
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("share: the share sheet could not be raised from the tray process", ex);
            error = ex.Message;
            anchor.Close();
            return false;
        }
    }

    /// <summary>How long a raised sheet may stay before Peel takes it down.</summary>
    internal static readonly TimeSpan AnchorLifetime = TimeSpan.FromMinutes(2);

    // Set once Peel has raised a sheet, so Peel only ever dismisses a sheet
    // when it raised one (never one the user opened from another app while
    // Peel happened to be running and idle).
    private static bool _raised;
    private static int _generation;
    private static bool _exitHooked;

    private static void DismissOnExit()
    {
        if (_exitHooked || Application.Current is null)
        {
            return;
        }
        _exitHooked = true;
        Application.Current.Exit += (_, _) => DismissRaised("Peel is exiting");
    }

    /// <summary>Take down any share sheet on screen if Peel raised one.</summary>
    internal static void DismissRaised(string why)
    {
        if (!_raised)
        {
            return;
        }
        try
        {
            var left = ShareSheet.CloseAll();
            Log.Write($"share: closing sheets because {why}, {left} left on screen");
            if (left == 0)
            {
                _raised = false;
            }
        }
        catch (Exception error)
        {
            Log.Write("share: dismissing the share sheet failed", error);
        }
    }

    private static string Title(IReadOnlyList<string> files) =>
        files.Count == 1 ? Path.GetFileName(files[0]) : $"{files.Count} files";

    private static Window ShowAnchor()
    {
        var cursor = System.Windows.Forms.Cursor.Position;
        var anchor = new Window
        {
            Width = 1,
            Height = 1,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            Topmost = true,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = cursor.X,
            Top = cursor.Y,
        };
        anchor.Show();
        // The share sheet only appears for a foreground window, so take the
        // foreground before raising it.
        anchor.Activate();
        SetForegroundWindow(new WindowInteropHelper(anchor).Handle);
        return anchor;
    }
}
