// The fallback for moving a folder to another volume, where one rename is not
// possible. It is built so that no data is ever lost, even while other programs
// keep writing into the folder or into the destination (lucy rounds 1 and 2):
//
// 1. Preflight, before anything is touched: refuse a tree holding a junction or
//    symbolic link (a copy would turn it into a plain folder), and refuse a
//    tree where any folder carries a named alternate data stream (no copy API
//    keeps a directory's streams, so deleting the source would destroy them).
// 2. Manifest: every file and folder with its relative path, identity (volume
//    serial plus file index), size, last write time and named file streams.
// 3. Copy from the manifest into a private staging folder next to the target.
//    The staging folder is reserved with a fresh random name that CreateDirectory
//    must create itself (an existing folder is never taken over), and every
//    folder and file inside it is created exclusively and recorded with its
//    identity. A failure removes only those recorded entries, one at a time and
//    only while they still have the recorded identity, so nothing another
//    program put there is ever deleted. The finished copy is published with a
//    rename that never replaces: a target that appeared meanwhile fails the
//    move, and the target is never touched.
// 4. Protect and verify the destination: every copied folder and file, and
//    every named stream of every file, is opened with a handle that denies
//    writers and deleters, its identity is checked against the staging record,
//    and the main stream and each named stream are hashed through those
//    handles. The handles stay open until the source deletion is over, so the
//    verified copy cannot change or disappear in between. A copy that is
//    missing or was replaced keeps its source.
// 5. Delete only source entries that still match. Each source file is opened
//    with DELETE access and no write or delete sharing, and each of its named
//    streams with a handle that denies writers; through those handles its
//    identity, size, last write time, stream names and every stream's SHA-256
//    are checked against the manifest and the protected copy, and only then is
//    it marked delete-on-close through the same handle (read-only files
//    included). The stream list is read once more after the mark, and the mark
//    is withdrawn if a stream appeared (or cannot be listed). Folders are
//    removed deepest first, only when empty and only when their copy is held
//    by step 4, each through one handle the same way: identity and an empty
//    stream list, the mark, the stream list again. Nothing is ever deleted
//    recursively, so a file created during the move is never seen, never
//    copied and never deleted: it stays.
// 6. Whatever is still in the source (files and empty folders) is reported by
//    name as a partial move.
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
/// were left in place: new, changed, in use, not removable or without a
/// verified copy.</summary>
internal sealed record CrossVolumeResult(string Target, IReadOnlyList<string> Remaining);

internal static class CrossVolumeMove
{
    internal sealed record Entry(
        string Relative, bool IsDirectory, FileId Id, long Size, DateTime LastWriteUtc,
        IReadOnlyList<(string Name, long Size)> Streams);

    /// <summary>The hashes of one verified copy: the main stream and every
    /// named stream by name.</summary>
    internal sealed record Verified(byte[] Main, IReadOnlyDictionary<string, byte[]> Streams);

    /// <summary>How many remaining names a report lists before "and N more".</summary>
    internal const int ReportNames = 5;

    /// <summary>The name prefix of a staging folder.</summary>
    internal const string StagingPrefix = ".peel-move-";

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
    /// untouched, the target untouched and Peel's own staging entries removed)
    /// when the preflight, the copy or the publish fails. The hooks are for
    /// tests: <paramref name="duringCopy"/> gets the staging folder before the
    /// first copy, <paramref name="afterCopy"/> the published target before it
    /// is protected, <paramref name="whileProtected"/> the target while the
    /// verified copy is held and before any source is deleted,
    /// <paramref name="beforeFolderMark"/> each source folder after its stream
    /// check and before it is marked for deletion.
    /// </summary>
    internal static CrossVolumeResult Move(
        string source, string target, Action<string>? afterCopy = null,
        Action<string>? duringCopy = null, Action<string>? whileProtected = null,
        Action<string>? beforeFolderMark = null)
    {
        var manifest = Manifest(source);
        if (Directory.Exists(target) || File.Exists(target))
        {
            throw new IOException($"\"{target}\" already exists");
        }
        var staging = Staging.Reserve(Path.GetDirectoryName(Path.GetFullPath(target))!);
        try
        {
            duringCopy?.Invoke(staging.Root);
            CopyInto(source, staging, manifest);
            staging.Publish(target);
        }
        catch
        {
            staging.Clean();
            throw;
        }
        afterCopy?.Invoke(target);
        using var guard = DestinationGuard.Protect(target, manifest, staging.Ids);
        whileProtected?.Invoke(target);
        var remaining = DeleteMatching(source, manifest, guard, beforeFolderMark);
        return new CrossVolumeResult(target, remaining);
    }

    private static void CopyInto(string source, Staging staging, IReadOnlyList<Entry> manifest)
    {
        foreach (var entry in manifest)
        {
            if (entry.IsDirectory)
            {
                if (entry.Relative != ".")
                {
                    staging.CreateFolder(entry.Relative);
                }
                continue;
            }
            var copy = staging.CopyFile(Path.Combine(source, entry.Relative), entry.Relative);
            // A FAT or exFAT destination drops named streams; that fails the
            // move here, before anything is deleted. Sizes and contents are
            // compared later, under protection, against the source as it is
            // when it is deleted.
            var copied = NamedStreams(copy).Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!entry.Streams.All(s => copied.Contains(s.Name)))
            {
                throw new IOException(
                    $"the copy of \"{entry.Relative}\" lost its alternate data streams (is the destination NTFS?); nothing was moved");
            }
        }
    }

    // --- Staging ---------------------------------------------------------------

    /// <summary>
    /// A private folder next to the target that only this move created, plus
    /// every entry the move created inside it. Cleaning removes those entries
    /// and nothing else.
    /// </summary>
    internal sealed class Staging
    {
        private readonly List<(string Relative, FileId Id, bool IsDirectory)> _owned = new();

        private Staging(string root, FileId id)
        {
            Root = root;
            _owned.Add((".", id, true));
        }

        internal string Root { get; private set; }

        /// <summary>The identity of every owned entry by relative path ("." is the root).</summary>
        internal IReadOnlyDictionary<string, FileId> Ids =>
            _owned.ToDictionary(o => o.Relative, o => o.Id, StringComparer.OrdinalIgnoreCase);

        /// <summary>Create a new folder with a random name in <paramref name="parent"/>.
        /// CreateDirectoryW fails on an existing name, so success means this
        /// call made it; an existing folder is never adopted.</summary>
        internal static Staging Reserve(string parent)
        {
            for (var attempt = 0; ; attempt++)
            {
                var root = Path.Combine(parent, StagingPrefix + Guid.NewGuid().ToString("N"));
                if (CreateDirectoryW(root, IntPtr.Zero))
                {
                    return new Staging(root, Stat(root).Id);
                }
                var error = Marshal.GetLastWin32Error();
                if (error != ERROR_ALREADY_EXISTS || attempt >= 3)
                {
                    throw new IOException($"cannot create a staging folder in \"{parent}\": {new Win32Exception(error).Message}", error);
                }
            }
        }

        internal void CreateFolder(string relative)
        {
            var path = Path.Combine(Root, relative);
            if (!CreateDirectoryW(path, IntPtr.Zero))
            {
                var error = Marshal.GetLastWin32Error();
                throw new IOException($"cannot create \"{path}\": {new Win32Exception(error).Message}", error);
            }
            _owned.Add((relative, Stat(path).Id, true));
        }

        /// <summary>Copy one file in; File.Copy without overwrite fails on an
        /// existing name, so the copy is always a new file of this move's.</summary>
        internal string CopyFile(string from, string relative)
        {
            var path = Path.Combine(Root, relative);
            File.Copy(from, path, overwrite: false);
            _owned.Add((relative, Stat(path).Id, false));
            return path;
        }

        /// <summary>Rename the staging folder to <paramref name="target"/>,
        /// failing (never replacing) when the target exists.</summary>
        internal void Publish(string target)
        {
            // FAT and exFAT number a folder by where its entry sits, so the
            // rename gives the root a new identity. A handle held across the
            // rename follows the folder itself, and its identity afterwards is
            // the one the destination check expects.
            using var root = CreateFileW(Root, FILE_READ_ATTRIBUTES, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
            if (root.IsInvalid || !GetFileInformationByHandle(root, out var before) || Unpack(before).Id != _owned[0].Id)
            {
                throw new IOException($"cannot publish the copy as \"{target}\": the staging folder is not the one this move made");
            }
            if (!MoveFileExW(Root, target, 0))
            {
                var error = Marshal.GetLastWin32Error();
                var why = error is ERROR_ALREADY_EXISTS or ERROR_FILE_EXISTS
                    ? "already exists"
                    : new Win32Exception(error).Message;
                throw new IOException($"cannot publish the copy as \"{target}\": {why}", error);
            }
            Root = target;
            if (!GetFileInformationByHandle(root, out var after))
            {
                var error = Marshal.GetLastWin32Error();
                throw new IOException($"cannot read the published copy \"{target}\": {new Win32Exception(error).Message}", error);
            }
            _owned[0] = (".", Unpack(after).Id, true);
        }

        /// <summary>
        /// Remove what this move created: files first, then folders deepest
        /// first, each only while it has the recorded identity, and a folder
        /// only when empty. Anything else in the folder stays, and so does the
        /// folder holding it.
        /// </summary>
        internal void Clean()
        {
            var order = _owned.Where(o => !o.IsDirectory)
                .Concat(_owned.Where(o => o.IsDirectory).OrderByDescending(o => o.Relative == "." ? -1 : o.Relative.Length));
            foreach (var (relative, id, isDirectory) in order)
            {
                var path = relative == "." ? Root : Path.Combine(Root, relative);
                if (!TryDeleteOwned(path, id, isDirectory, out var why))
                {
                    Log.Write($"move: left {path} in place while cleaning up: {why}");
                }
            }
        }
    }

    /// <summary>Delete one file or empty folder through a handle, only if it
    /// still has identity <paramref name="id"/>.</summary>
    private static bool TryDeleteOwned(string path, FileId id, bool isDirectory, out string why)
    {
        using var handle = CreateFileW(path, DELETE | FILE_READ_ATTRIBUTES, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT | (isDirectory ? FILE_FLAG_BACKUP_SEMANTICS : 0), IntPtr.Zero);
        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info))
        {
            why = new Win32Exception(Marshal.GetLastWin32Error()).Message;
            return false;
        }
        if (Unpack(info).Id != id)
        {
            why = "it is not the entry this move created";
            return false;
        }
        return MarkForDelete(handle, out why);
    }

    // --- Destination protection ------------------------------------------------

    /// <summary>
    /// Handles on every folder, file and named stream of the published copy,
    /// opened so nothing else can write to or delete them, plus the hashes read
    /// through them. Disposing releases the copy.
    /// </summary>
    private sealed class DestinationGuard : IDisposable
    {
        private readonly List<SafeFileHandle> _handles = new();
        private readonly Dictionary<string, Verified> _verified = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _folders = new(StringComparer.OrdinalIgnoreCase);

        internal IReadOnlyDictionary<string, Verified> Verified => _verified;

        /// <summary>The copied folders (relative paths, "." the root) that
        /// passed the identity check and are held until disposal.</summary>
        internal IReadOnlySet<string> Folders => _folders;

        internal static DestinationGuard Protect(string target, IReadOnlyList<Entry> manifest, IReadOnlyDictionary<string, FileId> ids)
        {
            var guard = new DestinationGuard();
            try
            {
                // Folders first, the root first: a folder held without delete
                // sharing cannot be renamed or removed, so the paths opened
                // below stay the copy's. Folders above the target need no
                // hold: every check is by identity through these handles, so a
                // renamed ancestor moves the copy, it never loses it.
                foreach (var entry in manifest.Where(e => e.IsDirectory).OrderBy(e => e.Relative == "." ? -1 : e.Relative.Length))
                {
                    var path = entry.Relative == "." ? target : Path.Combine(target, entry.Relative);
                    var handle = CreateFileW(path, FILE_LIST_DIRECTORY | FILE_READ_ATTRIBUTES, FILE_SHARE_READ | FILE_SHARE_WRITE,
                        IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
                    if (guard.Keep(handle, path, ids.GetValueOrDefault(entry.Relative), out var why))
                    {
                        guard._folders.Add(entry.Relative);
                    }
                    else
                    {
                        Log.Write($"move: cannot hold the copied folder {path}, its source stays: {why}");
                    }
                }
                foreach (var entry in manifest.Where(e => !e.IsDirectory))
                {
                    var path = Path.Combine(target, entry.Relative);
                    if (guard.TryVerify(path, ids.GetValueOrDefault(entry.Relative), out var verified, out var why))
                    {
                        guard._verified[entry.Relative] = verified;
                    }
                    else
                    {
                        Log.Write($"move: the copy {path} cannot be verified, its source stays: {why}");
                    }
                }
                return guard;
            }
            catch
            {
                guard.Dispose();
                throw;
            }
        }

        private bool Keep(SafeFileHandle handle, string path, FileId expected, out string why)
        {
            if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info))
            {
                why = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                handle.Dispose();
                return false;
            }
            if (Unpack(info).Id != expected)
            {
                why = "it is not the copy this move made";
                handle.Dispose();
                return false;
            }
            _handles.Add(handle);
            why = string.Empty;
            return true;
        }

        private bool TryVerify(string path, FileId expected, out Verified verified, out string why)
        {
            verified = null!;
            try
            {
                var main = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ, IntPtr.Zero,
                    OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_SEQUENTIAL_SCAN, IntPtr.Zero);
                if (!Keep(main, path, expected, out why))
                {
                    return false;
                }
                var streams = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
                foreach (var name in StreamNames(main))
                {
                    var stream = CreateFileW(path + name, GENERIC_READ, FILE_SHARE_READ, IntPtr.Zero,
                        OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_SEQUENTIAL_SCAN, IntPtr.Zero);
                    if (stream.IsInvalid)
                    {
                        why = $"cannot hold its stream {name}: {new Win32Exception(Marshal.GetLastWin32Error()).Message}";
                        stream.Dispose();
                        return false;
                    }
                    _handles.Add(stream);
                    streams[name] = Hash(stream);
                }
                verified = new Verified(Hash(main), streams);
                why = string.Empty;
                return true;
            }
            catch (Exception error)
            {
                why = error.Message;
                return false;
            }
        }

        public void Dispose()
        {
            foreach (var handle in _handles)
            {
                handle.Dispose();
            }
            _handles.Clear();
        }
    }

    // --- Source deletion -------------------------------------------------------

    private static IReadOnlyList<string> DeleteMatching(
        string source, IReadOnlyList<Entry> manifest, DestinationGuard guard, Action<string>? beforeFolderMark)
    {
        var copies = guard.Verified;
        var remaining = new List<string>();
        foreach (var entry in manifest.Where(e => !e.IsDirectory))
        {
            var path = Path.Combine(source, entry.Relative);
            string why;
            if (!copies.TryGetValue(entry.Relative, out var copy))
            {
                why = "its copy could not be verified";
            }
            else if (TryDeleteIfUnchanged(path, entry, copy, out why))
            {
                continue;
            }
            Log.Write($"move: kept {path}: {why}");
            remaining.Add(entry.Relative);
        }
        // Deepest folders first, the root last; a folder is only removed when
        // it is empty, so a folder holding a kept or new file stays. A folder
        // whose copy is missing or not held stays too, even when empty.
        foreach (var entry in manifest.Where(e => e.IsDirectory).OrderByDescending(e => e.Relative == "." ? -1 : e.Relative.Length))
        {
            var path = entry.Relative == "." ? source : Path.Combine(source, entry.Relative);
            string why;
            if (!guard.Folders.Contains(entry.Relative))
            {
                why = "its copy could not be verified";
            }
            else if (TryDeleteFolder(path, entry.Id, out why, beforeFolderMark))
            {
                continue;
            }
            Log.Write($"move: kept folder {path}: {why}");
        }
        // Report every file and every empty folder still in the source, whether
        // kept above or created during the move and never in the manifest. A
        // folder that stays only because it holds them is not listed itself.
        if (Directory.Exists(source))
        {
            try
            {
                foreach (var left in Directory.EnumerateFileSystemEntries(source, "*", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(source, left);
                    var report = File.Exists(left) || !Directory.EnumerateFileSystemEntries(left).Any();
                    if (report && !remaining.Contains(relative, StringComparer.OrdinalIgnoreCase))
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
                // Only the empty root is left; say so rather than claim the
                // move was whole.
                remaining.Add(".");
            }
        }
        return remaining;
    }

    /// <summary>
    /// Delete one source file only if it is still exactly the file that was
    /// copied, every named stream included. The handles deny writers for the
    /// whole check, and the delete goes through the main handle, so nothing can
    /// slip in between the check and the delete.
    /// </summary>
    internal static bool TryDeleteIfUnchanged(string path, Entry entry, Verified copy, out string why)
    {
        try
        {
            return DeleteIfUnchanged(path, entry, copy, out why);
        }
        catch (Exception error)
        {
            // Any doubt keeps the file: it is reported as left in the source.
            why = error.Message;
            return false;
        }
    }

    private static bool DeleteIfUnchanged(string path, Entry entry, Verified copy, out string why)
    {
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
        var names = StreamNames(handle);
        if (!SameNames(names, copy.Streams.Keys))
        {
            why = "its alternate data streams differ from the copy";
            return false;
        }
        if (!Hash(handle).AsSpan().SequenceEqual(copy.Main))
        {
            why = "its content differs from the copy";
            return false;
        }
        // Each named stream is held without write sharing while it is hashed
        // and until the delete, so no stream can change after its check. Delete
        // sharing is allowed, since a file whose stream is open without it
        // cannot be deleted at all.
        var held = new List<SafeFileHandle>();
        try
        {
            foreach (var name in names)
            {
                var stream = CreateFileW(path + name, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_DELETE, IntPtr.Zero,
                    OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_SEQUENTIAL_SCAN, IntPtr.Zero);
                if (stream.IsInvalid)
                {
                    why = $"cannot hold its stream {name}: {new Win32Exception(Marshal.GetLastWin32Error()).Message}";
                    stream.Dispose();
                    return false;
                }
                held.Add(stream);
                if (!Hash(stream).AsSpan().SequenceEqual(copy.Streams[name]))
                {
                    why = $"its stream {name} differs from the copy";
                    return false;
                }
            }
            if (!MarkForDelete(handle, out why))
            {
                return false;
            }
            // A stream created between the listing and the mark would be lost
            // with the file; a delete-pending file accepts no new opens, so this
            // last listing is final.
            return StreamsStillAre(handle, copy.Streams.Keys, out why);
        }
        finally
        {
            foreach (var stream in held)
            {
                stream.Dispose();
            }
        }
    }

    /// <summary>
    /// Delete one empty source folder through a single handle, only if it
    /// still has identity <paramref name="id"/> and no named stream. Its
    /// streams are listed through that handle before and after it is marked
    /// delete-on-close; a delete-pending folder accepts no new opens, so no
    /// stream can be added after the second listing, and one added before it
    /// withdraws the mark. Any doubt keeps the folder.
    /// </summary>
    internal static bool TryDeleteFolder(string path, FileId id, out string why, Action<string>? beforeMark = null)
    {
        try
        {
            using var handle = CreateFileW(path, DELETE | FILE_READ_ATTRIBUTES, FILE_SHARE_READ, IntPtr.Zero,
                OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
            if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info))
            {
                why = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                return false;
            }
            if (Unpack(info).Id != id)
            {
                why = "it was replaced during the move";
                return false;
            }
            if (StreamNames(handle).Count > 0)
            {
                why = "it has an alternate data stream that was not copied";
                return false;
            }
            beforeMark?.Invoke(path);
            if (!MarkForDelete(handle, out why))
            {
                return false;
            }
            return StreamsStillAre(handle, Array.Empty<string>(), out why);
        }
        catch (Exception error)
        {
            why = error.Message;
            return false;
        }
    }

    /// <summary>
    /// After a delete mark: true when the streams of <paramref name="handle"/>
    /// are exactly <paramref name="expected"/>. Otherwise, or when they cannot
    /// be listed, the mark is withdrawn and false returned.
    /// </summary>
    private static bool StreamsStillAre(SafeFileHandle handle, IEnumerable<string> expected, out string why)
    {
        try
        {
            if (SameNames(StreamNames(handle), expected))
            {
                why = string.Empty;
                return true;
            }
            why = "a stream was added during the move";
        }
        catch (Exception error)
        {
            why = $"its streams cannot be listed: {error.Message}";
        }
        Unmark(handle);
        return false;
    }

    /// <summary>Mark a file or folder delete-on-close through its handle,
    /// read-only ones included.</summary>
    private static bool MarkForDelete(SafeFileHandle handle, out string why)
    {
        var ex = new FILE_DISPOSITION_INFO_EX { Flags = FILE_DISPOSITION_FLAG_DELETE | FILE_DISPOSITION_FLAG_IGNORE_READONLY_ATTRIBUTE };
        if (SetFileInformationByHandle(handle, FileDispositionInfoEx, ref ex, Marshal.SizeOf<FILE_DISPOSITION_INFO_EX>()))
        {
            why = string.Empty;
            return true;
        }
        var error = Marshal.GetLastWin32Error();
        if (error is ERROR_INVALID_PARAMETER or ERROR_NOT_SUPPORTED or ERROR_INVALID_FUNCTION)
        {
            // A filesystem without the extended call (FAT, older Windows).
            var plain = new FILE_DISPOSITION_INFO { DeleteFile = true };
            if (SetFileInformationByHandle(handle, FileDispositionInfo, ref plain, Marshal.SizeOf<FILE_DISPOSITION_INFO>()))
            {
                why = string.Empty;
                return true;
            }
            error = Marshal.GetLastWin32Error();
        }
        why = new Win32Exception(error).Message;
        return false;
    }

    private static void Unmark(SafeFileHandle handle)
    {
        var ex = new FILE_DISPOSITION_INFO_EX { Flags = 0 };
        var plain = new FILE_DISPOSITION_INFO { DeleteFile = false };
        if (!SetFileInformationByHandle(handle, FileDispositionInfoEx, ref ex, Marshal.SizeOf<FILE_DISPOSITION_INFO_EX>())
            && !SetFileInformationByHandle(handle, FileDispositionInfo, ref plain, Marshal.SizeOf<FILE_DISPOSITION_INFO>()))
        {
            throw new IOException($"cannot withdraw a delete: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
        }
    }

    private static byte[] Hash(SafeFileHandle handle)
    {
        using var stream = new FileStream(new SafeFileHandle(handle.DangerousGetHandle(), ownsHandle: false), FileAccess.Read);
        stream.Position = 0;
        return SHA256.HashData(stream);
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

    private static bool SameNames(IEnumerable<string> a, IEnumerable<string> b) =>
        a.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(b);

    /// <summary>
    /// The named streams (":name:$DATA") of an open file, read through the
    /// handle itself so no second open is needed. The unnamed main stream is
    /// left out. Throws when they cannot be read, so a caller fails closed.
    /// </summary>
    internal static IReadOnlyList<string> StreamNames(SafeFileHandle handle)
    {
        var size = 4096;
        while (true)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (!GetFileInformationByHandleEx(handle, FileStreamInfo, buffer, (uint)size))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == ERROR_MORE_DATA && size < 1 << 22)
                    {
                        size *= 4;
                        continue;
                    }
                    if (error == ERROR_HANDLE_EOF || (NoStreamsError(error) && !HasNamedStreams(handle)))
                    {
                        // No named streams, or a volume (FAT, exFAT) that
                        // cannot hold any. On a volume that can, every other
                        // error throws so the caller fails closed.
                        return Array.Empty<string>();
                    }
                    throw new IOException($"cannot list streams: {new Win32Exception(error).Message}", error);
                }
                var names = new List<string>();
                var offset = 0;
                while (true)
                {
                    var next = Marshal.ReadInt32(buffer, offset);
                    var length = Marshal.ReadInt32(buffer, offset + 4);
                    var name = Marshal.PtrToStringUni(buffer + offset + 24, length / 2);
                    if (!string.Equals(name, "::$DATA", StringComparison.OrdinalIgnoreCase))
                    {
                        names.Add(name);
                    }
                    if (next == 0)
                    {
                        return names;
                    }
                    offset += next;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    /// <summary>The errors a stream listing returns on a filesystem without
    /// named streams.</summary>
    private static bool NoStreamsError(int error) =>
        error is ERROR_INVALID_PARAMETER or ERROR_INVALID_FUNCTION or ERROR_NOT_SUPPORTED;

    /// <summary>True when the volume holding <paramref name="handle"/> can
    /// hold named streams (NTFS, ReFS). Unknown counts as true, so a listing
    /// error there still throws.</summary>
    private static bool HasNamedStreams(SafeFileHandle handle) =>
        !GetVolumeInformationByHandleW(handle, IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero, out var flags, IntPtr.Zero, 0)
        || (flags & FILE_NAMED_STREAMS) != 0;

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
            if (error == ERROR_HANDLE_EOF || NoStreamsError(error))
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
    private const uint FILE_LIST_DIRECTORY = 0x0001;
    private const uint FILE_SHARE_READ = 0x1;
    private const uint FILE_SHARE_WRITE = 0x2;
    private const uint FILE_SHARE_DELETE = 0x4;
    private const uint FILE_READ_ATTRIBUTES = 0x0080;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
    private const uint FILE_FLAG_SEQUENTIAL_SCAN = 0x08000000;
    private const int FileDispositionInfo = 4;
    private const int FileStreamInfo = 7;
    private const int FileDispositionInfoEx = 21;
    private const uint FILE_DISPOSITION_FLAG_DELETE = 0x1;
    private const uint FILE_DISPOSITION_FLAG_IGNORE_READONLY_ATTRIBUTE = 0x10;
    private const uint FILE_NAMED_STREAMS = 0x00040000;
    private const int ERROR_INVALID_FUNCTION = 1;
    private const int ERROR_HANDLE_EOF = 38;
    private const int ERROR_NOT_SUPPORTED = 50;
    private const int ERROR_FILE_EXISTS = 80;
    private const int ERROR_INVALID_PARAMETER = 87;
    private const int ERROR_ALREADY_EXISTS = 183;
    private const int ERROR_MORE_DATA = 234;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct FILE_DISPOSITION_INFO_EX
    {
        public uint Flags;
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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryW(string path, IntPtr security);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileExW(string existing, string target, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out BY_HANDLE_FILE_INFORMATION info);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationByHandleW(
        SafeFileHandle handle, IntPtr name, uint nameSize, IntPtr serial, IntPtr maxComponent,
        out uint flags, IntPtr fsName, uint fsNameSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, IntPtr buffer, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int infoClass, ref FILE_DISPOSITION_INFO info, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int infoClass, ref FILE_DISPOSITION_INFO_EX info, int size);
}
