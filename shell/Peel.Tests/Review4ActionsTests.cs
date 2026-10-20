// Review round 4 checks for the Alt wheel actions (hq-plct.9).
//
// FAT checks need volumes: PEEL_REVIEW_FAT (and PEEL_REVIEW_FAT2 on another FAT
// volume for FAT to FAT) point at folders on them. Without them those checks
// return early and pass. The rest run on the temp folder (NTFS).
using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Peel.Actions;
using Xunit;

namespace Peel.Tests;

public class Review4ActionsTests
{
    private static string? Root(string variable)
    {
        var fat = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrEmpty(fat) || !Directory.Exists(fat))
        {
            return null;
        }
        var root = Path.Combine(fat, "peel-rv4-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string Ntfs()
    {
        var root = Path.Combine(Path.GetTempPath(), "peel-rv4-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Wipe(string? root)
    {
        if (root is null)
        {
            return;
        }
        try
        {
            foreach (var f in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(f, FileAttributes.Normal);
            }
            Directory.Delete(root, recursive: true);
        }
        catch
        {
            // best effort
        }
    }

    private const string LongName = "a folder with quite a long name, accents ñandú and spaces";

    /// <summary>A read-only file, a hidden folder, a long name, an empty folder.</summary>
    private static string MakeAlbum(string parent)
    {
        var album = Path.Combine(parent, "album");
        var hidden = Path.Combine(album, "hidden");
        Directory.CreateDirectory(hidden);
        new DirectoryInfo(hidden).Attributes |= FileAttributes.Hidden;
        Directory.CreateDirectory(Path.Combine(album, LongName, "empty"));
        File.WriteAllText(Path.Combine(album, "ro.txt"), "readonly");
        File.SetAttributes(Path.Combine(album, "ro.txt"), FileAttributes.ReadOnly);
        File.WriteAllText(Path.Combine(hidden, "h.txt"), "hidden");
        File.WriteAllText(Path.Combine(album, LongName, LongName + ".txt"), "long");
        return album;
    }

    private static void AssertMovedWhole(string album, string target, CrossVolumeResult result)
    {
        Assert.Empty(result.Remaining);
        Assert.False(Directory.Exists(album));
        Assert.Equal("readonly", File.ReadAllText(Path.Combine(target, "ro.txt")));
        Assert.Equal("hidden", File.ReadAllText(Path.Combine(target, "hidden", "h.txt")));
        Assert.Equal("long", File.ReadAllText(Path.Combine(target, LongName, LongName + ".txt")));
        Assert.True(Directory.Exists(Path.Combine(target, LongName, "empty")));
    }

    private static void MoveWhole(string? from, string? to)
    {
        if (from is null || to is null)
        {
            Wipe(from);
            Wipe(to);
            return;
        }
        try
        {
            var album = MakeAlbum(from);
            var target = Path.Combine(to, "album");
            AssertMovedWhole(album, target, CrossVolumeMove.Move(album, target));
        }
        finally
        {
            Wipe(from);
            Wipe(to);
        }
    }

    [Fact]
    public void FatToNtfsWithReadOnlyHiddenAndLongNamesMovesWhole() => MoveWhole(Root("PEEL_REVIEW_FAT"), Ntfs());

    [Fact]
    public void NtfsToFatWithReadOnlyHiddenAndLongNamesMovesWhole() => MoveWhole(Ntfs(), Root("PEEL_REVIEW_FAT"));

    [Fact]
    public void FatToFatAcrossTwoVolumesMovesWhole() => MoveWhole(Root("PEEL_REVIEW_FAT"), Root("PEEL_REVIEW_FAT2"));

    /// <summary>The narrow cause of the two FAT failures above: a read-only
    /// file on a FAT or exFAT source stays after a clean move (the extended
    /// disposition that ignores read-only is not supported there and the plain
    /// one refuses a read-only file), though the move says read-only files go.</summary>
    [Fact]
    public void AReadOnlyFileOnAFatSourceIsMoved()
    {
        var fat = Root("PEEL_REVIEW_FAT");
        if (fat is null)
        {
            return;
        }
        var ntfs = Ntfs();
        try
        {
            var album = Path.Combine(fat, "album");
            Directory.CreateDirectory(album);
            File.WriteAllText(Path.Combine(album, "ro.txt"), "readonly");
            File.SetAttributes(Path.Combine(album, "ro.txt"), FileAttributes.ReadOnly);
            var result = CrossVolumeMove.Move(album, Path.Combine(ntfs, "album"));
            Assert.Empty(result.Remaining);
            Assert.False(Directory.Exists(album));
        }
        finally
        {
            Wipe(fat);
            Wipe(ntfs);
        }
    }

    // --- Destinations removed after the copy, on NTFS and FAT ------------------

    private static void DestinationRemoved(string? from, string? to, Action<string> afterCopy, Action<string, CrossVolumeResult?> check)
    {
        if (from is null || to is null)
        {
            Wipe(from);
            Wipe(to);
            return;
        }
        try
        {
            var album = MakeAlbum(from);
            var target = Path.Combine(to, "album");
            CrossVolumeResult? result = null;
            try
            {
                result = CrossVolumeMove.Move(album, target, afterCopy: afterCopy);
            }
            catch (IOException)
            {
                // Refusing is fine; the source must be whole either way.
            }
            check(album, result);
        }
        finally
        {
            Wipe(from);
            Wipe(to);
        }
    }

    private static void EmptyChild(string? from, string? to) => DestinationRemoved(from, to,
        t => Directory.Delete(Path.Combine(t, LongName, "empty")),
        (album, result) =>
        {
            Assert.True(Directory.Exists(Path.Combine(album, LongName, "empty")));
            Assert.NotNull(result);
            Assert.Contains(Path.Combine(LongName, "empty"), result!.Remaining);
        });

    private static void NonEmptyFolder(string? from, string? to) => DestinationRemoved(from, to,
        t =>
        {
            File.SetAttributes(Path.Combine(t, "hidden", "h.txt"), FileAttributes.Normal);
            Directory.Delete(Path.Combine(t, "hidden"), recursive: true);
        },
        (album, _) => Assert.Equal("hidden", File.ReadAllText(Path.Combine(album, "hidden", "h.txt"))));

    private static void WholeRoot(string? from, string? to) => DestinationRemoved(from, to,
        t =>
        {
            foreach (var f in Directory.EnumerateFiles(t, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(f, FileAttributes.Normal);
            }
            Directory.Delete(t, recursive: true);
        },
        (album, _) =>
        {
            Assert.Equal("readonly", File.ReadAllText(Path.Combine(album, "ro.txt")));
            Assert.Equal("hidden", File.ReadAllText(Path.Combine(album, "hidden", "h.txt")));
            Assert.Equal("long", File.ReadAllText(Path.Combine(album, LongName, LongName + ".txt")));
        });

    [Fact] public void NtfsEmptyChildRemovedKeepsSource() => EmptyChild(Ntfs(), Ntfs());
    [Fact] public void FatEmptyChildRemovedKeepsSource() => EmptyChild(Ntfs(), Root("PEEL_REVIEW_FAT"));
    [Fact] public void FatSourceEmptyChildRemovedKeepsSource() => EmptyChild(Root("PEEL_REVIEW_FAT"), Ntfs());
    [Fact] public void NtfsNonEmptyFolderRemovedKeepsSource() => NonEmptyFolder(Ntfs(), Ntfs());
    [Fact] public void FatNonEmptyFolderRemovedKeepsSource() => NonEmptyFolder(Ntfs(), Root("PEEL_REVIEW_FAT"));
    [Fact] public void NtfsRootRemovedKeepsSource() => WholeRoot(Ntfs(), Ntfs());
    [Fact] public void FatRootRemovedKeepsSource() => WholeRoot(Root("PEEL_REVIEW_FAT"), Root("PEEL_REVIEW_FAT2") ?? Ntfs());

    // --- A source file locked during the move ---------------------------------

    private static void Locked(string? from, string? to)
    {
        if (from is null || to is null)
        {
            Wipe(from);
            Wipe(to);
            return;
        }
        FileStream? lockStream = null;
        try
        {
            var album = MakeAlbum(from);
            var target = Path.Combine(to, "album");
            var result = CrossVolumeMove.Move(album, target, whileProtected: _ =>
            {
                lockStream = new FileStream(Path.Combine(album, "hidden", "h.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            });
            lockStream!.Dispose();
            Assert.Equal("hidden", File.ReadAllText(Path.Combine(album, "hidden", "h.txt")));
            Assert.Contains(Path.Combine("hidden", "h.txt"), result.Remaining);
            Assert.False(File.Exists(Path.Combine(album, LongName, LongName + ".txt")));
        }
        finally
        {
            lockStream?.Dispose();
            Wipe(from);
            Wipe(to);
        }
    }

    [Fact] public void NtfsLockedFileStays() => Locked(Ntfs(), Ntfs());
    [Fact] public void FatLockedFileStays() => Locked(Root("PEEL_REVIEW_FAT"), Ntfs());

    // --- The premise of the folder delete: no stream after the mark -----------

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);

    [StructLayout(LayoutKind.Sequential)]
    private struct Disposition
    {
        [MarshalAs(UnmanagedType.U1)] public bool DeleteFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle h, int infoClass, ref Disposition info, int size);

    /// <summary>TryDeleteFolder relies on a delete-pending folder refusing a new
    /// named stream (the second listing is final). Check it on the box.</summary>
    [Fact]
    public void ADeletePendingFolderRefusesANewStream()
    {
        var root = Ntfs();
        try
        {
            var dir = Path.Combine(root, "d");
            Directory.CreateDirectory(dir);
            using var h = CreateFileW(dir, 0x00010000 | 0x80, 1, IntPtr.Zero, 3, 0x02000000 | 0x00200000, IntPtr.Zero);
            Assert.False(h.IsInvalid, new Win32Exception().Message);
            var d = new Disposition { DeleteFile = true };
            Assert.True(SetFileInformationByHandle(h, 4, ref d, Marshal.SizeOf<Disposition>()));
            var added = false;
            try
            {
                using var s = new FileStream(dir + ":late", FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                s.WriteByte(1);
                added = true;
            }
            catch (Exception)
            {
                // refused: the premise holds
            }
            Assert.False(added, "a stream was added to a delete-pending folder; it would be lost on close");
        }
        finally
        {
            Wipe(root);
        }
    }

    /// <summary>A stream added to a FAT folder cannot exist; the folder delete
    /// on FAT must still work through its handle.</summary>
    [Fact]
    public void AnEmptyFatFolderIsDeletedThroughItsHandle()
    {
        var fat = Root("PEEL_REVIEW_FAT");
        if (fat is null)
        {
            return;
        }
        try
        {
            var dir = Path.Combine(fat, "e");
            Directory.CreateDirectory(dir);
            var id = CrossVolumeMove.Stat(dir).Id;
            Assert.True(CrossVolumeMove.TryDeleteFolder(dir, id, out var why), why);
            Assert.False(Directory.Exists(dir));
        }
        finally
        {
            Wipe(fat);
        }
    }

    /// <summary>A file dropped into a source folder right before its mark keeps
    /// it (and the file).</summary>
    [Fact]
    public void AFileAddedBeforeTheFolderMarkStays()
    {
        var from = Root("PEEL_REVIEW_FAT") ?? Ntfs();
        var to = Ntfs();
        try
        {
            var album = MakeAlbum(from);
            var target = Path.Combine(to, "album");
            var result = CrossVolumeMove.Move(album, target, beforeFolderMark: p =>
            {
                if (p.EndsWith("empty"))
                {
                    File.WriteAllText(Path.Combine(p, "late.txt"), "late");
                }
            });
            Assert.Equal("late", File.ReadAllText(Path.Combine(album, LongName, "empty", "late.txt")));
            Assert.Contains(Path.Combine(LongName, "empty", "late.txt"), result.Remaining);
        }
        finally
        {
            Wipe(from);
            Wipe(to);
        }
    }
}
