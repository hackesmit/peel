// The physical identity of a file or folder: the volume serial number plus the
// file index the filesystem gives it. Two paths that name the same folder by
// different spellings (a UNC alias such as \\localhost\C$\..., a subst drive, a
// junction, an 8.3 short name, a different case) open to the same identity,
// which path strings cannot show. Move uses it to refuse a folder moved into
// itself or its own descendant, and to tell a real cross-volume move from a
// same-volume rename.
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Peel.Actions;

internal readonly record struct FileId(uint VolumeSerial, ulong Index);

internal static class FileIdentity
{
    private const uint FILE_READ_ATTRIBUTES = 0x0080;
    private const uint FILE_SHARE_ALL = 0x1 | 0x2 | 0x4;
    private const uint OPEN_EXISTING = 3;
    // Needed to open a directory at all. Without FILE_FLAG_OPEN_REPARSE_POINT a
    // junction or symlink is followed, so the identity is the real target's.
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    private const uint FILE_NAME_NORMALIZED = 0x0;
    private const uint VOLUME_NAME_DOS = 0x0;

    [StructLayout(LayoutKind.Sequential)]
    private struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out BY_HANDLE_FILE_INFORMATION info);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint size, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileExW(string existing, string target, uint flags);

    private const int ERROR_NOT_SAME_DEVICE = 17;

    /// <summary>
    /// Rename <paramref name="source"/> to <paramref name="target"/> in one step,
    /// never replacing anything and never copying. Returns false only when
    /// Windows says the two are on different devices (ERROR_NOT_SAME_DEVICE),
    /// which includes the same disk reached once locally and once through a
    /// UNC alias, and a volume mounted in a folder. Any other failure throws.
    /// </summary>
    internal static bool TryRename(string source, string target)
    {
        if (MoveFileExW(source, target, 0))
        {
            return true;
        }
        var error = Marshal.GetLastWin32Error();
        if (error == ERROR_NOT_SAME_DEVICE)
        {
            return false;
        }
        throw new IOException($"cannot move \"{source}\": {new Win32Exception(error).Message}", error);
    }

    private static SafeFileHandle Open(string path)
    {
        var handle = CreateFileW(path, FILE_READ_ATTRIBUTES, FILE_SHARE_ALL, IntPtr.Zero,
            OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException($"cannot open \"{path}\": {new Win32Exception(error).Message}", error);
        }
        return handle;
    }

    private static FileId Read(SafeFileHandle handle, string path)
    {
        if (!GetFileInformationByHandle(handle, out var info))
        {
            var error = Marshal.GetLastWin32Error();
            throw new IOException($"cannot identify \"{path}\": {new Win32Exception(error).Message}", error);
        }
        return new FileId(info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow);
    }

    /// <summary>The identity of an existing file or folder. Throws when it
    /// cannot be read, so a caller deciding safety fails closed.</summary>
    internal static FileId Of(string path)
    {
        using var handle = Open(path);
        return Read(handle, path);
    }

    /// <summary>The path Windows itself resolves <paramref name="path"/> to, with
    /// every junction, symlink, subst drive, short name and case difference
    /// already followed. A UNC path stays a UNC path.</summary>
    internal static string FinalPath(string path)
    {
        using var handle = Open(path);
        var buffer = new StringBuilder(512);
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, FILE_NAME_NORMALIZED | VOLUME_NAME_DOS);
        if (length >= buffer.Capacity)
        {
            buffer = new StringBuilder((int)length + 1);
            length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, FILE_NAME_NORMALIZED | VOLUME_NAME_DOS);
        }
        if (length == 0 || length >= buffer.Capacity)
        {
            var error = Marshal.GetLastWin32Error();
            throw new IOException($"cannot resolve \"{path}\": {new Win32Exception(error).Message}", error);
        }
        return buffer.ToString();
    }

    /// <summary>
    /// True when the folder <paramref name="inner"/> is the folder
    /// <paramref name="outer"/> itself or lies anywhere beneath it on disk,
    /// however either path is spelled. It resolves <paramref name="inner"/> to
    /// its final path and compares the physical identity of it and of every
    /// ancestor against <paramref name="outer"/>. Any failure to read an
    /// identity throws, so the caller refuses rather than guesses.
    /// </summary>
    internal static bool IsSameOrBeneath(string inner, string outer)
    {
        var target = Of(outer);
        // Both spellings are checked: the path as given (it may cross a
        // junction whose final path leaves the tree lexically) and the final
        // path (every alias resolved). Either reaching the outer folder means
        // the inner folder lives inside it.
        foreach (var start in new[] { Path.GetFullPath(inner), FinalPath(inner) })
        {
            for (var current = start; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            {
                if (Of(current) == target)
                {
                    return true;
                }
            }
        }
        return false;
    }
}
