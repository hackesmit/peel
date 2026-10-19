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
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
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

    internal static void Run(IReadOnlyList<string> files, JobRunner jobs)
    {
        Window anchor;
        try
        {
            anchor = ShowAnchor();
        }
        catch (Exception error)
        {
            Log.Write("share: no anchor window", error);
            jobs.Report($"Share could not open: {error.Message}");
            return;
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

            anchor.Deactivated += (_, _) => anchor.Dispatcher.BeginInvoke(() => anchor.Close());
            interop.ShowShareUIForWindow(hwnd);
            Log.Write($"share: share sheet raised for {files.Count} file(s)");
        }
        catch (Exception error)
        {
            Log.Write("share: the share sheet could not be raised from the tray process", error);
            jobs.Report($"Share is not available: {error.Message}");
            anchor.Close();
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
        return anchor;
    }
}
