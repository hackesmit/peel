// The fallback for moving a folder to another volume, where one rename is not
// possible. It is built so that no data is ever lost, even while other programs
// keep writing into the folder (lucy round 1):
//
// 1. Preflight, before anything is touched: refuse a tree holding a junction or
//    symbolic link (a copy would turn it into a plain folder), and refuse a
//    tree where any folder carries a named alternate data stream (no copy API
//    keeps a directory's streams, so deleting the source would destroy them).
// 2. Manifest: every file and folder with its relative path, identity (volume
//    serial plus file index), size, last write time and named file streams.
// 3. Copy from the manifest only. Any copy failure removes the destination
//    folder this move created and leaves the source untouched.
// 4. Verify every copy: same size as the manifest, the same named streams
//    (File.Copy keeps a file's streams on NTFS; a FAT or exFAT destination
//    would drop them, and that also fails the move before any delete), and a
//    SHA-256 of the copy is recorded.
// 5. Delete only source entries that still match. Each source file is opened
//    with DELETE access and no write or delete sharing, so nothing can change
//    or replace it in the interval; through that handle its identity, size,
//    last write time and SHA-256 are checked against the manifest and the
//    copy, and only then is it marked delete-on-close through the same handle.
//    Folders are removed deepest first and only when empty. Nothing is ever
//    deleted recursively, so a file created during the move is never seen,
//    never copied and never deleted: it stays in the source.
// 6. Whatever is still in the source is reported by name as a partial move.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace Peel.Actions;

/// <summary>A cross-volume folder move whose copy is complete and verified.
/// <paramref name="Remaining"/> lists the source entries (relative paths) that
/// were left in place: new, changed, in use or not removable.</summary>
internal sealed record CrossVolumeResult(string Target, IReadOnlyList<string> Remaining);

internal static class CrossVolumeMove
{
    internal sealed record Entry(
        string Relative, bool IsDirectory, FileId Id, long Size, DateTime LastWriteUtc,
        IReadOnlyList<(string Name, long Size)> Streams);

    /// <summary>How many remaining names a report lists before "and N more".</summary>
    internal const int ReportNames = 5;

    // --- Preflight and manifest -------------------------------------------

    /// <summary>
    /// The manifest of <paramref name="source"/>, or an IOException naming why
    /// the fallback is refused. Nothing is created or changed.
    /// </summary>
    internal static IReadOnlyList<Entry> Manifest(string source)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(source));
        var root = new DirectoryInfo(source);
        if (root.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException($"\"{name}\" is a link; move it in Explorer instead");
        }
        var infos = root.GetFileSystemInfos("*", SearchOption.AllDirectories);
        foreach (var info in infos)
        {
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new IOException($"\"{name}\" contains a link ({info.Name}); move it in Explorer instead");
            }
        }
        var entries = new List<Entry>();
        foreach (var info in new FileSystemInfo[] { root }.Concat(infos))
        {
            var relative = Path.GetRelativePath(source, info.FullName);
            var streams = NamedStreams(info.FullName);
            var isDir = info is DirectoryInfo;
            if (isDir && streams.Count > 0)
            {
                var where = relative == "." ? $"\"{name}\"" : $"\"{relative}\" in \"{name}\"";
                throw new IOException(
                    $"the folder {where} has alternate data stream {streams[0].Name}, which a copy to another drive "
                    + "would lose; nothing was moved. Move it in Explorer or remove the stream first");
            }
            // Size and last write come from an open handle, the same source the
            // delete check reads, so a directory entry's lazily updated
            // timestamps can never make an unchanged file look changed.
            var (id, size, lastWrite) = Stat(info.FullName);
            entries.Add(new Entry(relative, isDir, id, isDir ? 0 : size, lastWrite, streams));
        }
        return entries;
    }

    // --- The move ------------------------------------------------------------

    /// <summary>
    /// Copy, verify, then delete matching sources. Throws (with the source
    /// untouched and the partial destination removed) when the preflight, the
    /// copy or the verification fails.
    /// </summary>
    internal static CrossVolumeResult Move(string source, string target, Action<string>? afterCopy = null)
    {
        var manifest = Manifest(source);
        if (Directory.Exists(target) || File.Exists(target))
        {
            throw new IOException($"\"{target}\" already exists");
        }
        Directory.CreateDirectory(target);
        Dictionary<string, byte[]> hashes;
        try
        {
            hashes = CopyAndVerify(source, target, manifest);
        }
        catch
        {
            RemovePartialCopy(target);
            throw;
        }
        afterCopy?.Invoke(target);
        var remaining = DeleteMatching(source, manifest, hashes);
        return new CrossVolumeResult(target, remaining);
    }

    private static Dictionary<string, byte[]> CopyAndVerify(string source, string target, IReadOnlyList<Entry> manifest)
    {
        var hashes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in manifest)
        {
            var copy = Path.GetFullPath(Path.Combine(target, entry.Relative));
            if (entry.IsDirectory)
            {
                Directory.CreateDirectory(copy);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Copy(Path.Combine(source, entry.Relative), copy, overwrite: false);
            var copiedStreams = NamedStreams(copy);
            if (!SameStreams(entry.Streams, copiedStreams))
            {
                throw new IOException(
                    $"the copy of \"{entry.Relative}\" lost its alternate data streams (is the destination NTFS?); nothing was moved");
            }
            // A size that differs from the manifest means the file changed
            // while it was copied; the copy is kept but the source will not
            // match below, so the source stays. Its hash is still recorded.
            using var stream = new FileStream(copy, FileMode.Open, FileAccess.Read, FileShare.Read);
            hashes[entry.Relative] = SHA256.HashData(stream);
        }
        return hashes;
    }

    /// <summary>Remove the destination folder this move created. Only called
    /// for a folder that did not exist before the move.</summary>
    private static void RemovePartialCopy(string target)
    {
        try
        {
            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }
        }
        catch (Exception error)
        {
            Log.Write($"move: could not remove the partial copy {target}", error);
        }
    }

    private static IReadOnlyList<string> DeleteMatching(
        string source, IReadOnlyList<Entry> manifest, IReadOnlyDictionary<string, byte[]> hashes)
    {
        var remaining = new List<string>();
        foreach (var entry in manifest.Where(e => !e.IsDirectory))
        {
            var path = Path.Combine(source, entry.Relative);
            if (!TryDeleteIfUnchanged(path, entry, hashes[entry.Relative], out var why))
            {
                Log.Write($"move: kept {path}: {why}");
                remaining.Add(entry.Relative);
            }
        }
        // Deepest folders first, the root last; RemoveDirectory only removes an
        // empty folder, so a folder holding a kept or new file stays.
        foreach (var entry in manifest.Where(e => e.IsDirectory).OrderByDescending(e => e.Relative == "." ? -1 : e.Relative.Length))
        {
            var path = entry.Relative == "." ? source : Path.Combine(source, entry.Relative);
            try
            {
                if (Stat(path).Id == entry.Id && NamedStreams(path).Count == 0)
                {
                    Directory.Delete(path, recursive: false);
                }
            }
            catch (Exception error)
            {
                Log.Write($"move: kept folder {path}: {error.Message}");
            }
        }
        // Report every file still in the source, whether it was kept above or
        // appeared during the move and was never in the manifest.
        if (Directory.Exists(source))
        {
            try
            {
                foreach (var left in Directory.EnumerateFileSystemEntries(source, "*", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(source, left);
                    if (File.Exists(left) && !remaining.Contains(relative, StringComparer.OrdinalIgnoreCase))
                    {
                        remaining.Add(relative);
                    }
                }
            }
            catch (Exception error)
            {
                Log.Write($"move: could not list what stayed in {source}", error);
            }
            if (remaining.Count == 0)
            {
                // Only empty folders are left (for example one recreated during
                // the move); say so rather than claim the move was whole.
                remaining.Add(".");
            }
        }
        return remaining;
    }

    /// <summary>
    /// Delete one source file only if it is still exactly the file that was
    /// copied. The handle denies writers and other deleters for the whole
    /// check, and the delete goes through that same handle, so nothing can
    /// slip in between the check and the delete.
    /// </summary>
    internal static bool TryDeleteIfUnchanged(string path, Entry entry, byte[] copyHash, out string why)
    {
        try
        {
            return DeleteIfUnchanged(path, entry, copyHash, out why);
        }
        catch (Exception error)
        {
            // Any doubt keeps the file: it is reported as left in the source.
            why = error.Message;
            return false;
        }
    }

    private static bool DeleteIfUnchanged(string path, Entry entry, byte[] copyHash, out string why)
    {
        // Streams are listed before the locking handle is opened, since the
        // listing opens the file itself; the handle's identity and hash checks
        // below still catch a file replaced after this point.
        if (!SameStreams(entry.Streams, NamedStreams(path)))
        {
            why = "its alternate data streams changed during the move";
            return false;
        }
        using var handle = CreateFileW(path, GENERIC_READ | DELETE, FILE_SHARE_READ, IntPtr.Zero,
            OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_SEQUENTIAL_SCAN, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            why = new Win32Exception(Marshal.GetLastWin32Error()).Message;
            return false;
        }
        if (!GetFileInformationByHandle(handle, out var info))
        {
            why = new Win32Exception(Marshal.GetLastWin32Error()).Message;
            return false;
        }
        var (id, size, lastWrite) = Unpack(info);
        if (id != entry.Id)
        {
            why = "it was replaced during the move";
            return false;
        }
        if (size != entry.Size || lastWrite != entry.LastWriteUtc)
        {
            why = "it changed during the move";
            return false;
        }
        byte[] hash;
        using (var stream = new FileStream(new SafeFileHandle(handle.DangerousGetHandle(), ownsHandle: false), FileAccess.Read))
        {
            hash = SHA256.HashData(stream);
        }
        if (!hash.AsSpan().SequenceEqual(copyHash))
        {
            why = "its content differs from the copy";
            return false;
        }
        var dispose = new FILE_DISPOSITION_INFO { DeleteFile = true };
        if (!SetFileInformationByHandle(handle, FileDispositionInfo, ref dispose, Marshal.SizeOf<FILE_DISPOSITION_INFO>()))
        {
            why = new Win32Exception(Marshal.GetLastWin32Error()).Message;
            return false;
        }
        why = string.Empty;
        return true;
    }

    private static (FileId Id, long Size, DateTime LastWriteUtc) Unpack(BY_HANDLE_FILE_INFORMATION info) => (
        new FileId(info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow),
        ((long)info.FileSizeHigh << 32) | info.FileSizeLow,
        DateTime.FromFileTimeUtc(((long)(uint)info.LastWriteTime.dwHighDateTime << 32) | (uint)info.LastWriteTime.dwLowDateTime));

    /// <summary>Identity, size and last write of a file or folder, from a
    /// handle that does not follow links. Throws when unreadable (fail closed).</summary>
    internal static (FileId Id, long Size, DateTime LastWriteUtc) Stat(string path)
    {
        using var handle = CreateFileW(path, FILE_READ_ATTRIBUTES, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info))
        {
            var error = Marshal.GetLastWin32Error();
            throw new IOException($"cannot read \"{path}\": {new Win32Exception(error).Message}", error);
        }
        return Unpack(info);
    }

    /// <summary>A short report of what stayed: up to five names, then a count.</summary>
    internal static string Describe(IReadOnlyList<string> remaining)
    {
        var names = remaining.Take(ReportNames).ToList();
        var more = remaining.Count - names.Count;
        return string.Join(", ", names) + (more > 0 ? $" and {more} more" : string.Empty);
    }

    // --- Alternate data streams ----------------------------------------------

    private static bool SameStreams(IReadOnlyList<(string Name, long Size)> a, IReadOnlyList<(string Name, long Size)> b) =>
        a.Count == b.Count
        && a.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Zip(b.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
            .All(p => string.Equals(p.First.Name, p.Second.Name, StringComparison.OrdinalIgnoreCase) && p.First.Size == p.Second.Size);

    /// <summary>
    /// The named alternate data streams of a file or folder (the unnamed main
    /// stream "::$DATA" is left out). Empty on a filesystem without streams.
    /// Throws if they cannot be listed, so a caller deciding safety fails closed.
    /// </summary>
    internal static IReadOnlyList<(string Name, long Size)> NamedStreams(string path)
    {
        var found = new List<(string, long)>();
        var handle = FindFirstStreamW(path, 0, out var data, 0);
        if (handle == INVALID_HANDLE_VALUE)
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ERROR_HANDLE_EOF || error == ERROR_INVALID_PARAMETER)
            {
                // No streams at all (a folder), or a filesystem that has none.
                return found;
            }
            throw new IOException($"cannot list the streams of \"{path}\": {new Win32Exception(error).Message}", error);
        }
        try
        {
            do
            {
                if (!string.Equals(data.cStreamName, "::$DATA", StringComparison.OrdinalIgnoreCase))
                {
                    found.Add((data.cStreamName, data.StreamSize));
                }
            }
            while (FindNextStreamW(handle, out data));
            var last = Marshal.GetLastWin32Error();
            if (last != ERROR_HANDLE_EOF)
            {
                throw new IOException($"cannot list the streams of \"{path}\": {new Win32Exception(last).Message}", last);
            }
        }
        finally
        {
            FindClose(handle);
        }
        return found;
    }

    // --- Win32 -----------------------------------------------------------------

    private const uint GENERIC_READ = 0x80000000;
    private const uint DELETE = 0x00010000;
    private const uint FILE_SHARE_READ = 0x1;
    private const uint FILE_SHARE_WRITE = 0x2;
    private const uint FILE_SHARE_DELETE = 0x4;
    private const uint FILE_READ_ATTRIBUTES = 0x0080;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
    private const uint FILE_FLAG_SEQUENTIAL_SCAN = 0x08000000;
    private const int FileDispositionInfo = 4;
    private const int ERROR_HANDLE_EOF = 38;
    private const int ERROR_INVALID_PARAMETER = 87;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WIN32_FIND_STREAM_DATA
    {
        public long StreamSize;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260 + 36)]
        public string cStreamName;
    }

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

    [StructLayout(LayoutKind.Sequential)]
    private struct FILE_DISPOSITION_INFO
    {
        [MarshalAs(UnmanagedType.U1)]
        public bool DeleteFile;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindFirstStreamW(string fileName, int infoLevel, out WIN32_FIND_STREAM_DATA data, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindNextStreamW(IntPtr handle, out WIN32_FIND_STREAM_DATA data);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out BY_HANDLE_FILE_INFORMATION info);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int infoClass, ref FILE_DISPOSITION_INFO info, int size);
}
