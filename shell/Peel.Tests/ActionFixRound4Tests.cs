// Round 4 fixes for the Alt wheel actions (hq-plct.9), one regression test per
// finding of the round 3 reviews: a named stream added to a source folder just
// before it is deleted, a copied folder that is missing or replaced, and a
// share sheet Peel cannot prove its own. The FAT32/exFAT fix is checked by
// Review3ActionsTests with PEEL_REVIEW_FAT set. Section 5 (hq-plct.21): the
// delete handles never ask for FILE_WRITE_ATTRIBUTES.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using Peel.Actions;
using Xunit;

namespace Peel.Tests;

public class ActionFixRound4Tests
{
    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "peel-fix4-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Wipe(string root)
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch
        {
            // best effort
        }
    }

    /// <summary>album/ with a.txt and an empty folder empty/.</summary>
    private static string MakeAlbum(string root)
    {
        var album = Path.Combine(root, "album");
        Directory.CreateDirectory(Path.Combine(album, "empty"));
        File.WriteAllText(Path.Combine(album, "a.txt"), "alpha");
        return album;
    }

    // --- 1. A folder's stream added just before its delete --------------------

    /// <summary>Add a named stream the way another program could while Peel
    /// holds the folder with DELETE access: sharing delete, so the open is not
    /// refused with a sharing violation.</summary>
    private static void AddStream(string path, string text)
    {
        using var stream = new FileStream(path + ":secret", FileMode.Create, FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete);
        using var writer = new StreamWriter(stream);
        writer.Write(text);
    }

    [Fact]
    public void AStreamAddedToTheSourceRootBeforeItsDeleteKeepsTheRoot()
    {
        var root = NewRoot();
        try
        {
            var album = MakeAlbum(root);
            var target = Path.Combine(root, "copy");
            var result = CrossVolumeMove.Move(album, target, beforeFolderMark: path =>
            {
                // The race lucy described: the stream check has passed, the
                // folder is not marked yet, another program adds a stream.
                if (string.Equals(path, album, StringComparison.OrdinalIgnoreCase))
                {
                    AddStream(path, "only copy");
                }
            });
            Assert.True(Directory.Exists(album));
            Assert.Equal("only copy", File.ReadAllText(album + ":secret"));
            Assert.Equal(new[] { "." }, result.Remaining);
            Assert.False(File.Exists(Path.Combine(album, "a.txt")));
            Assert.False(Directory.Exists(Path.Combine(album, "empty")));
        }
        finally
        {
            Wipe(root);
        }
    }

    [Fact]
    public void AStreamAddedToASourceSubfolderBeforeItsDeleteKeepsIt()
    {
        var root = NewRoot();
        try
        {
            var album = MakeAlbum(root);
            var empty = Path.Combine(album, "empty");
            var target = Path.Combine(root, "copy");
            var result = CrossVolumeMove.Move(album, target, beforeFolderMark: path =>
            {
                if (string.Equals(path, empty, StringComparison.OrdinalIgnoreCase))
                {
                    AddStream(path, "only copy");
                }
            });
            Assert.Equal("only copy", File.ReadAllText(empty + ":secret"));
            Assert.Equal(new[] { "empty" }, result.Remaining);
        }
        finally
        {
            Wipe(root);
        }
    }

    [Fact]
    public void AFolderWithAStreamIsNeverDeleted()
    {
        var root = NewRoot();
        try
        {
            var folder = Path.Combine(root, "f");
            Directory.CreateDirectory(folder);
            File.WriteAllText(folder + ":secret", "kept");
            Assert.False(CrossVolumeMove.TryDeleteFolder(folder, CrossVolumeMove.Stat(folder).Id, out var why));
            Assert.Contains("stream", why);
            Assert.Equal("kept", File.ReadAllText(folder + ":secret"));
        }
        finally
        {
            Wipe(root);
        }
    }

    [Fact]
    public void AnEmptyFolderWithoutStreamsIsDeletedThroughItsHandle()
    {
        var root = NewRoot();
        try
        {
            var folder = Path.Combine(root, "f");
            Directory.CreateDirectory(folder);
            Assert.True(CrossVolumeMove.TryDeleteFolder(folder, CrossVolumeMove.Stat(folder).Id, out var why), why);
            Assert.False(Directory.Exists(folder));
        }
        finally
        {
            Wipe(root);
        }
    }

    [Fact]
    public void AFolderReplacedBeforeItsDeleteIsKept()
    {
        var root = NewRoot();
        try
        {
            var folder = Path.Combine(root, "f");
            Directory.CreateDirectory(folder);
            var id = CrossVolumeMove.Stat(folder).Id;
            Directory.Delete(folder);
            Directory.CreateDirectory(folder);
            Assert.False(CrossVolumeMove.TryDeleteFolder(folder, id, out _));
            Assert.True(Directory.Exists(folder));
        }
        finally
        {
            Wipe(root);
        }
    }

    // --- 2. A source folder goes only when its copy is held -------------------

    [Fact]
    public void AMissingEmptyCopiedChildKeepsItsSourceFolder()
    {
        var root = NewRoot();
        try
        {
            var album = MakeAlbum(root);
            var target = Path.Combine(root, "copy");
            var result = CrossVolumeMove.Move(album, target,
                afterCopy: t => Directory.Delete(Path.Combine(t, "empty")));
            Assert.True(Directory.Exists(Path.Combine(album, "empty")));
            Assert.Equal(new[] { "empty" }, result.Remaining);
            Assert.False(File.Exists(Path.Combine(album, "a.txt")));
            Assert.Equal("alpha", File.ReadAllText(Path.Combine(target, "a.txt")));
        }
        finally
        {
            Wipe(root);
        }
    }

    [Fact]
    public void AMissingEmptyCopiedRootKeepsTheSourceRoot()
    {
        var root = NewRoot();
        try
        {
            var album = Path.Combine(root, "album");
            Directory.CreateDirectory(album);
            var target = Path.Combine(root, "copy");
            var result = CrossVolumeMove.Move(album, target, afterCopy: t => Directory.Delete(t));
            Assert.True(Directory.Exists(album));
            Assert.Equal(new[] { "." }, result.Remaining);
        }
        finally
        {
            Wipe(root);
        }
    }

    [Fact]
    public void AReplacedEmptyCopiedChildKeepsItsSourceFolder()
    {
        var root = NewRoot();
        try
        {
            var album = MakeAlbum(root);
            var target = Path.Combine(root, "copy");
            var result = CrossVolumeMove.Move(album, target, afterCopy: t =>
            {
                Directory.Delete(Path.Combine(t, "empty"));
                Directory.CreateDirectory(Path.Combine(t, "empty"));
            });
            Assert.True(Directory.Exists(Path.Combine(album, "empty")));
            Assert.Equal(new[] { "empty" }, result.Remaining);
        }
        finally
        {
            Wipe(root);
        }
    }

    [Fact]
    public void ACleanMoveWithAnEmptyFolderEmptiesTheSource()
    {
        var root = NewRoot();
        try
        {
            var album = MakeAlbum(root);
            var target = Path.Combine(root, "copy");
            var result = CrossVolumeMove.Move(album, target);
            Assert.Empty(result.Remaining);
            Assert.False(Directory.Exists(album));
            Assert.True(Directory.Exists(Path.Combine(target, "empty")));
        }
        finally
        {
            Wipe(root);
        }
    }

    // --- 4. Read-only entries on FAT32 and exFAT ----------------------------
    // Those lack the extended delete mark, so Peel clears the read-only
    // attribute through the handle for the plain mark and puts it back when the
    // mark fails. They need PEEL_REVIEW_FAT (a folder on a FAT32 or exFAT
    // volume); without it the NTFS temp folder is used.

    private static string FatOrNtfsRoot()
    {
        var fat = Environment.GetEnvironmentVariable("PEEL_REVIEW_FAT");
        if (string.IsNullOrEmpty(fat) || !Directory.Exists(fat))
        {
            return NewRoot();
        }
        var root = Path.Combine(fat, "peel-fix4-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    [Fact]
    public void AReadOnlyEmptyFolderIsDeletedThroughItsHandle()
    {
        var root = FatOrNtfsRoot();
        try
        {
            var dir = Path.Combine(root, "ro");
            Directory.CreateDirectory(dir);
            File.SetAttributes(dir, FileAttributes.Directory | FileAttributes.ReadOnly);
            Assert.True(CrossVolumeMove.TryDeleteFolder(dir, CrossVolumeMove.Stat(dir).Id, out var why), why);
            Assert.False(Directory.Exists(dir));
        }
        finally
        {
            Wipe(root);
        }
    }

    [Fact]
    public void AReadOnlyFolderThatCannotBeMarkedKeepsItsAttributes()
    {
        var root = FatOrNtfsRoot();
        var dir = Path.Combine(root, "ro");
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "keep.txt"), "keep");
            File.SetAttributes(dir, FileAttributes.Directory | FileAttributes.ReadOnly | FileAttributes.Hidden);
            // Not empty, so the delete mark itself fails after the attribute
            // was cleared for it.
            Assert.False(CrossVolumeMove.TryDeleteFolder(dir, CrossVolumeMove.Stat(dir).Id, out _));
            var attributes = File.GetAttributes(dir);
            Assert.True(attributes.HasFlag(FileAttributes.ReadOnly), attributes.ToString());
            Assert.True(attributes.HasFlag(FileAttributes.Hidden), attributes.ToString());
            Assert.Equal("keep", File.ReadAllText(Path.Combine(dir, "keep.txt")));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                File.SetAttributes(dir, FileAttributes.Directory);
            }
            Wipe(root);
        }
    }

    // --- 5. An ACL denying FILE_WRITE_ATTRIBUTES (hq-plct.21) -----------------
    // The delete handles ask only for DELETE and read access, so a source the
    // user may delete but whose attributes they may not change still moves
    // cleanly. A FAT volume has no ACL, so this runs on NTFS only; the FAT
    // read-only path is checked below and by the tests in section 4.

    private static readonly SecurityIdentifier Me = WindowsIdentity.GetCurrent().User!;

    /// <summary>
    /// Run <paramref name="body"/> as this user without the backup and restore
    /// privileges. An elevated ssh session on George holds both enabled, and
    /// with them SetFileAttributes and every backup-semantics open (all folder
    /// handles) pass a deny ACE; Daniel's desktop session has neither.
    /// </summary>
    private static void WithoutBackupPrivileges(Action body)
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_DUPLICATE | TOKEN_QUERY, out var process))
        {
            throw new Win32Exception();
        }
        using (process)
        {
            if (!DuplicateTokenEx(process, TOKEN_ALL_ACCESS, IntPtr.Zero, SecurityImpersonation, TokenImpersonation, out var token))
            {
                throw new Win32Exception();
            }
            using (token)
            {
                foreach (var name in new[] { "SeBackupPrivilege", "SeRestorePrivilege" })
                {
                    if (!LookupPrivilegeValueW(null, name, out var luid))
                    {
                        throw new Win32Exception();
                    }
                    var removed = new TOKEN_PRIVILEGES { PrivilegeCount = 1, Luid = luid, Attributes = SE_PRIVILEGE_REMOVED };
                    // ERROR_NOT_ALL_ASSIGNED (a token without it) is fine.
                    if (!AdjustTokenPrivileges(token, false, ref removed, 0, IntPtr.Zero, IntPtr.Zero))
                    {
                        throw new Win32Exception();
                    }
                }
                WindowsIdentity.RunImpersonated(token, body);
            }
        }
    }

    private const uint TOKEN_DUPLICATE = 0x0002;
    private const uint TOKEN_QUERY = 0x0008;
    private const uint TOKEN_ALL_ACCESS = 0xF01FF;
    private const int SecurityImpersonation = 2;
    private const int TokenImpersonation = 2;
    private const uint SE_PRIVILEGE_REMOVED = 0x4;

    // One LUID_AND_ATTRIBUTES; a LUID is two DWORDs, so 4-byte packing.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public long Luid;
        public uint Attributes;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(SafeAccessTokenHandle token, uint access, IntPtr attributes,
        int level, int type, out SafeAccessTokenHandle duplicate);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValueW(string? system, string name, out long luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(SafeAccessTokenHandle token, bool disableAll,
        ref TOKEN_PRIVILEGES state, int length, IntPtr previous, IntPtr returned);

    private static FileSystemAccessRule DenyWriteAttributes() => new(
        Me, FileSystemRights.WriteAttributes, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Deny);

    private static void Deny(string path, bool isDirectory)
    {
        if (isDirectory)
        {
            var dir = new DirectoryInfo(path);
            var acl = dir.GetAccessControl();
            acl.AddAccessRule(DenyWriteAttributes());
            dir.SetAccessControl(acl);
        }
        else
        {
            var file = new FileInfo(path);
            var acl = file.GetAccessControl();
            acl.AddAccessRule(DenyWriteAttributes());
            file.SetAccessControl(acl);
        }
    }

    private static void Allow(string path)
    {
        if (Directory.Exists(path))
        {
            var dir = new DirectoryInfo(path);
            var acl = dir.GetAccessControl();
            acl.RemoveAccessRuleSpecific(DenyWriteAttributes());
            dir.SetAccessControl(acl);
            File.SetAttributes(path, FileAttributes.Directory);
        }
        else if (File.Exists(path))
        {
            var file = new FileInfo(path);
            var acl = file.GetAccessControl();
            acl.RemoveAccessRuleSpecific(DenyWriteAttributes());
            file.SetAccessControl(acl);
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    /// <summary>Clear read-only flags under <paramref name="root"/> so Wipe
    /// can remove a copy that kept them.</summary>
    private static void Unlock(string root)
    {
        if (!Directory.Exists(root))
        {
            return;
        }
        foreach (var entry in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(entry, Directory.Exists(entry) ? FileAttributes.Directory : FileAttributes.Normal);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ASourceThatDeniesWriteAttributesMovesCleanly(bool readOnly)
    {
        var root = NewRoot();
        var album = Path.Combine(root, "album");
        var sub = Path.Combine(album, "sub");
        var file = Path.Combine(album, "a.txt");
        try
        {
            Directory.CreateDirectory(sub);
            File.WriteAllText(file, "alpha");
            File.WriteAllText(file + ":note", "stream");
            if (readOnly)
            {
                File.SetAttributes(file, FileAttributes.ReadOnly);
                File.SetAttributes(sub, FileAttributes.Directory | FileAttributes.ReadOnly);
            }
            Deny(file, isDirectory: false);
            Deny(sub, isDirectory: true);
            Deny(album, isDirectory: true);
            var target = Path.Combine(root, "copy");
            CrossVolumeResult? result = null;
            WithoutBackupPrivileges(() =>
            {
                // The ACL really bites: these attributes cannot be changed now.
                Assert.Throws<UnauthorizedAccessException>(() => File.SetAttributes(file, FileAttributes.Hidden));
                Assert.Throws<UnauthorizedAccessException>(() => File.SetAttributes(sub, FileAttributes.Hidden));
                result = CrossVolumeMove.Move(album, target);
            });

            Assert.Empty(result!.Remaining);
            Assert.False(Directory.Exists(album));
            Assert.Equal("alpha", File.ReadAllText(Path.Combine(target, "a.txt")));
            Assert.Equal("stream", File.ReadAllText(Path.Combine(target, "a.txt") + ":note"));
            Assert.True(Directory.Exists(Path.Combine(target, "sub")));
        }
        finally
        {
            Allow(file);
            Allow(sub);
            Allow(album);
            Unlock(root);
            Wipe(root);
        }
    }

    [Fact]
    public void AReadOnlyFileIsDeletedThroughItsHandle()
    {
        // On FAT the read-only flag is cleared through a second, attributes
        // only handle while the delete handle keeps writers out.
        var root = FatOrNtfsRoot();
        var target = NewRoot();
        try
        {
            var album = Path.Combine(root, "album");
            Directory.CreateDirectory(album);
            var file = Path.Combine(album, "a.txt");
            File.WriteAllText(file, "alpha");
            File.SetAttributes(file, FileAttributes.ReadOnly | FileAttributes.Hidden);
            File.SetAttributes(album, FileAttributes.Directory | FileAttributes.ReadOnly);
            var result = CrossVolumeMove.Move(album, Path.Combine(target, "album"));
            Assert.Empty(result.Remaining);
            Assert.False(Directory.Exists(album));
            Assert.Equal("alpha", File.ReadAllText(Path.Combine(target, "album", "a.txt")));
        }
        finally
        {
            Unlock(root);
            Unlock(target);
            Wipe(root);
            Wipe(target);
        }
    }

    [Fact]
    public void AReadOnlyFileThatChangedKeepsItsAttributes()
    {
        // The delete check fails after the copy, so nothing is cleared.
        var root = FatOrNtfsRoot();
        var target = NewRoot();
        var file = Path.Combine(root, "album", "a.txt");
        try
        {
            var album = Path.Combine(root, "album");
            Directory.CreateDirectory(album);
            File.WriteAllText(file, "alpha");
            File.SetAttributes(file, FileAttributes.ReadOnly | FileAttributes.Hidden);
            var result = CrossVolumeMove.Move(album, Path.Combine(target, "album"), whileProtected: _ =>
            {
                File.SetAttributes(file, FileAttributes.Normal);
                File.WriteAllText(file, "changed!");
                File.SetAttributes(file, FileAttributes.ReadOnly | FileAttributes.Hidden);
            });
            Assert.Equal(new[] { "a.txt" }, result.Remaining);
            Assert.Equal("changed!", File.ReadAllText(file));
            var attributes = File.GetAttributes(file);
            Assert.True(attributes.HasFlag(FileAttributes.ReadOnly), attributes.ToString());
            Assert.True(attributes.HasFlag(FileAttributes.Hidden), attributes.ToString());
        }
        finally
        {
            Unlock(root);
            Unlock(target);
            Wipe(root);
            Wipe(target);
        }
    }

    // --- 3. Peel never closes a share sheet -----------------------------------
    // Round 4 leaves the sheet to the user: no method in Peel calls the sheet
    // closer. Only the scripted probe (another assembly) uses CloseAll.

    [Fact]
    public void NothingInPeelClosesAShareSheet()
    {
        var closers = typeof(ShareSheet).GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            .Where(m => m.Name is "Close" or "CloseAll").ToList();
        Assert.Equal(2, closers.Count);
        var callers = new List<string>();
        foreach (var type in typeof(ShareSheet).Assembly.GetTypes().Where(t => t != typeof(ShareSheet) && t.DeclaringType != typeof(ShareSheet)))
        {
            const BindingFlags all = BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic
                | BindingFlags.Public | BindingFlags.DeclaredOnly;
            foreach (var method in type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all)))
            {
                if (CallsAny(method, closers))
                {
                    callers.Add($"{type.FullName}.{method.Name}");
                }
            }
        }
        Assert.Empty(callers);
    }

    [Fact]
    public void ShareKeepsNoSheetOwnershipLogic()
    {
        Assert.Null(typeof(ShareSheet).Assembly.GetType("Peel.Actions.ShareOwner"));
        Assert.Null(typeof(ShareAction).GetMethod("Dismiss", BindingFlags.Static | BindingFlags.NonPublic));
    }

    /// <summary>True when the IL of <paramref name="method"/> has a call to one
    /// of <paramref name="targets"/> (call, callvirt or ldftn).</summary>
    private static bool CallsAny(MethodBase method, IReadOnlyCollection<MethodInfo> targets)
    {
        byte[]? il;
        try
        {
            il = method.GetMethodBody()?.GetILAsByteArray();
        }
        catch (Exception)
        {
            return false;
        }
        if (il is null)
        {
            return false;
        }
        for (var i = 0; i + 4 < il.Length; i++)
        {
            var isCall = il[i] is 0x28 or 0x6F || (il[i] == 0xFE && il[i + 1] == 0x06);
            if (!isCall)
            {
                continue;
            }
            var at = il[i] == 0xFE ? i + 2 : i + 1;
            if (at + 4 > il.Length)
            {
                continue;
            }
            try
            {
                var called = method.Module.ResolveMethod(BitConverter.ToInt32(il, at),
                    method.DeclaringType?.GetGenericArguments(), method.IsGenericMethod ? method.GetGenericArguments() : null);
                if (called is MethodInfo info && targets.Contains(info))
                {
                    return true;
                }
            }
            catch (Exception)
            {
                // Not a method token at this offset.
            }
        }
        return false;
    }
}
