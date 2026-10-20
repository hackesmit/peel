// Round 3 fixes for the Alt wheel actions (hq-plct.9), one regression test per
// cross-vendor round 2 finding: a same-length edit of a named stream, a
// destination deleted or changed before the source is deleted, a failed copy
// next to data Peel did not create, and a share sheet Peel does not own. The
// cross device cases go through the \\localhost\C$ alias (ERROR_NOT_SAME_DEVICE
// on the same disk), so they run the real CrossVolumeMove fallback.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Peel.Actions;
using Xunit;

namespace Peel.Tests;

public class ActionFixRound3Tests
{
    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "peel-fix3-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string UncAlias(string local)
    {
        var full = Path.GetFullPath(local);
        return $@"\\localhost\{full[0]}$" + full.Substring(2);
    }

    /// <summary>album/ with a.txt (and its stream a.txt:secret), b.txt and sub/c.txt.</summary>
    private static string MakeAlbum(string root)
    {
        var album = Path.Combine(root, "album");
        Directory.CreateDirectory(Path.Combine(album, "sub"));
        File.WriteAllText(Path.Combine(album, "a.txt"), "alpha");
        File.WriteAllText(Path.Combine(album, "a.txt:secret"), "old");
        File.WriteAllText(Path.Combine(album, "b.txt"), "bravo");
        File.WriteAllText(Path.Combine(album, "sub", "c.txt"), "charlie");
        return album;
    }

    private static void Wipe(string root)
    {
        try
        {
            foreach (var f in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
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

    private static string[] StagingLeft(string parent) =>
        Directory.GetDirectories(parent, CrossVolumeMove.StagingPrefix + "*");

    // --- 1. Named streams are verified by content -----------------------------

    [Fact]
    public void ACleanMoveCarriesANamedStreamAndEmptiesTheSource()
    {
        var root = NewRoot();
        try
        {
            var album = MakeAlbum(root);
            var target = Path.Combine(root, "copy");
            var result = CrossVolumeMove.Move(album, target);
            Assert.Empty(result.Remaining);
            Assert.False(Directory.Exists(album));
            Assert.Equal("old", File.ReadAllText(Path.Combine(target, "a.txt:secret")));
            Assert.Empty(StagingLeft(root));
        }
        finally
        {
            Wipe(root);
        }
    }

    [Fact]
    public void ASameLengthStreamEditAfterTheCopyKeepsTheSource()
    {
        var root = NewRoot();
        try
        {
            var album = MakeAlbum(root);
            var a = Path.Combine(album, "a.txt");
            var target = Path.Combine(root, "copy");
            var result = CrossVolumeMove.Move(album, target, afterCopy: _ =>
            {
                // Same stream name and length, same main stream, same last
                // write time: only the stream's own hash can tell.
                var stamp = File.GetLastWriteTimeUtc(a);
                File.WriteAllText(a + ":secret", "new");
                File.SetLastWriteTimeUtc(a, stamp);
            });
            Assert.Contains("a.txt", result.Remaining);
            Assert.Equal("new", File.ReadAllText(a + ":secret"));
            Assert.Equal("old", File.ReadAllText(Path.Combine(target, "a.txt:secret")));
            Assert.False(File.Exists(Path.Combine(album, "b.txt")));
        }
        finally
        {
            Wipe(root);
        }
    }

    [Fact]
    public void AStreamAddedToTheSourceAfterTheCopyKeepsIt()
    {
        var root = NewRoot();
        try
        {
            var album = MakeAlbum(root);
            var b = Path.Combine(album, "b.txt");
            var target = Path.Combine(root, "copy");
            var result = CrossVolumeMove.Move(album, target, afterCopy: _ =>
            {
                var stamp = File.GetLastWriteTimeUtc(b);
                File.WriteAllText(b + ":late", "added");
                File.SetLastWriteTimeUtc(b, stamp);
            });
            Assert.Contains("b.txt", result.Remaining);
            Assert.Equal("added", File.ReadAllText(b + ":late"));
        }
        finally
        {
            Wipe(root);
        }
    }

    // --- 2. The destination stays protected until the source is deleted -------

    [Fact]
    public void ADestinationFileDeletedAfterTheCopyKeepsItsSource()
    {
        var root = NewRoot();
        try
        {
            var album = MakeAlbum(root);
            var target = Path.Combine(root, "copy");
            var result = CrossVolumeMove.Move(album, target, afterCopy: t => File.Delete(Path.Combine(t, "a.txt")));
            Assert.Equal(new[] { "a.txt" }, result.Remaining.ToArray());
            Assert.Equal("alpha", File.ReadAllText(Path.Combine(album, "a.txt")));
            Assert.Equal("old", File.ReadAllText(Path.Combine(album, "a.txt:secret")));
            Assert.False(File.Exists(Path.Combine(album, "b.txt")));
            Assert.Equal("bravo", File.ReadAllText(Path.Combine(target, "b.txt")));
        }
        finally
        {
            Wipe(root);
        }
    }

    [Fact]
    public void ADestinationFileChangedAfterTheCopyKeepsItsSource()
    {
        var root = NewRoot();
        try
        {
            var album = MakeAlbum(root);
            var target = Path.Combine(root, "copy");
            var result = CrossVolumeMove.Move(album, target, afterCopy: t =>
            {
                File.WriteAllText(Path.Combine(t, "b.txt"), "BRAVO");
                File.WriteAllText(Path.Combine(t, "a.txt:secret"), "OLD");
            });
            Assert.Contains("a.txt", result.Remaining);
            Assert.Contains("b.txt", result.Remaining);
            Assert.Equal("bravo", File.ReadAllText(Path.Combine(album, "b.txt")));
            Assert.Equal("old", File.ReadAllText(Path.Combine(album, "a.txt:secret")));
            Assert.False(File.Exists(Path.Combine(album, "sub", "c.txt")));
        }
        finally
        {
            Wipe(root);
        }
    }

    [Fact]
    public void ADestinationFolderReplacedAfterTheCopyKeepsItsSource()
    {
        var root = NewRoot();
        try
        {
            var album = MakeAlbum(root);
            var target = Path.Combine(root, "copy");
            var result = CrossVolumeMove.Move(album, target, afterCopy: t =>
            {
                // The same names and bytes, but not the files this move copied.
                Directory.Move(Path.Combine(t, "sub"), Path.Combine(root, "elsewhere"));
                Directory.CreateDirectory(Path.Combine(t, "sub"));
                File.WriteAllText(Path.Combine(t, "sub", "c.txt"), "charlie");
            });
            Assert.Contains(Path.Combine("sub", "c.txt"), result.Remaining);
            Assert.Equal("charlie", File.ReadAllText(Path.Combine(album, "sub", "c.txt")));
        }
        finally
        {
            Wipe(root);
        }
    }

    [Fact]
    public void TheVerifiedCopyCannotBeDeletedOrChangedUntilTheSourceIsGone()
    {
        var root = NewRoot();
        try
        {
            var album = MakeAlbum(root);
            var target = Path.Combine(root, "copy");
            var attempts = new List<string>();
            void Try(string what, Action action)
            {
                try
                {
                    action();
                    attempts.Add(what);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    // refused, as it must be
                }
            }
            var result = CrossVolumeMove.Move(album, target, whileProtected: t =>
            {
                Try("delete a.txt", () => File.Delete(Path.Combine(t, "a.txt")));
                Try("write b.txt", () => File.WriteAllText(Path.Combine(t, "b.txt"), "BRAVO"));
                Try("write a.txt:secret", () => File.WriteAllText(Path.Combine(t, "a.txt:secret"), "OLD"));
                Try("rename sub", () => Directory.Move(Path.Combine(t, "sub"), Path.Combine(t, "sub2")));
                Try("rename the target", () => Directory.Move(t, Path.Combine(root, "renamed")));
            });
            Assert.Empty(attempts);
            Assert.Empty(result.Remaining);
            Assert.Equal("alpha", File.ReadAllText(Path.Combine(target, "a.txt")));
            Assert.Equal("old", File.ReadAllText(Path.Combine(target, "a.txt:secret")));
            Assert.Equal("bravo", File.ReadAllText(Path.Combine(target, "b.txt")));
            Assert.Equal("charlie", File.ReadAllText(Path.Combine(target, "sub", "c.txt")));
        }
        finally
        {
            Wipe(root);
        }
    }

    // --- 3. A failed copy removes only what Peel created ----------------------

    [Fact]
    public void AFailedCopyNeverDeletesATargetSomeoneElseCreated()
    {
        var root = NewRoot();
        try
        {
            var album = MakeAlbum(root);
            var target = Path.Combine(root, "copy");
            using (new FileStream(Path.Combine(album, "sub", "c.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.ThrowsAny<IOException>(() => CrossVolumeMove.Move(album, target, duringCopy: _ =>
                {
                    Directory.CreateDirectory(target);
                    File.WriteAllText(Path.Combine(target, "new.txt"), "not Peel's");
                }));
            }
            Assert.Equal("not Peel's", File.ReadAllText(Path.Combine(target, "new.txt")));
            Assert.Empty(StagingLeft(root));
            Assert.Equal("alpha", File.ReadAllText(Path.Combine(album, "a.txt")));
        }
        finally
        {
            Wipe(root);
        }
    }

    [Fact]
    public void ATargetThatAppearsDuringTheCopyIsNeverReplaced()
    {
        var root = NewRoot();
        try
        {
            var album = MakeAlbum(root);
            var target = Path.Combine(root, "copy");
            var error = Assert.ThrowsAny<IOException>(() => CrossVolumeMove.Move(album, target, duringCopy: _ =>
            {
                Directory.CreateDirectory(target);
                File.WriteAllText(Path.Combine(target, "other.txt"), "another move's output");
            }));
            Assert.Contains("already exists", error.Message);
            Assert.Equal(new[] { Path.Combine(target, "other.txt") }, Directory.GetFiles(target));
            Assert.Empty(StagingLeft(root));
            Assert.Equal("alpha", File.ReadAllText(Path.Combine(album, "a.txt")));
            Assert.Equal("old", File.ReadAllText(Path.Combine(album, "a.txt:secret")));
        }
        finally
        {
            Wipe(root);
        }
    }

    [Fact]
    public void AFailedCopyLeavesForeignFilesInItsStagingFolder()
    {
        var root = NewRoot();
        try
        {
            var album = MakeAlbum(root);
            var target = Path.Combine(root, "copy");
            string? staging = null;
            using (new FileStream(Path.Combine(album, "sub", "c.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.ThrowsAny<IOException>(() => CrossVolumeMove.Move(album, target, duringCopy: s =>
                {
                    staging = s;
                    File.WriteAllText(Path.Combine(s, "foreign.txt"), "someone else's");
                }));
            }
            Assert.NotNull(staging);
            Assert.Equal("someone else's", File.ReadAllText(Path.Combine(staging!, "foreign.txt")));
            Assert.Equal(new[] { Path.Combine(staging!, "foreign.txt") }, Directory.GetFileSystemEntries(staging!));
            Assert.False(Directory.Exists(target));
        }
        finally
        {
            Wipe(root);
        }
    }

    [Fact]
    public void AStagingFolderIsAlwaysNewlyCreated()
    {
        var root = NewRoot();
        try
        {
            var first = CrossVolumeMove.Staging.Reserve(root);
            var second = CrossVolumeMove.Staging.Reserve(root);
            Assert.NotEqual(first.Root, second.Root);
            Assert.StartsWith(CrossVolumeMove.StagingPrefix, Path.GetFileName(first.Root));
            first.Clean();
            second.Clean();
            Assert.Empty(StagingLeft(root));
        }
        finally
        {
            Wipe(root);
        }
    }

    // --- Round 2 note (a): read-only files ------------------------------------

    [Fact]
    public void AReadOnlyFileAndItsStreamMoveAcrossVolumes()
    {
        var root = NewRoot();
        try
        {
            var album = MakeAlbum(root);
            File.SetAttributes(Path.Combine(album, "a.txt"), FileAttributes.ReadOnly);
            var dst = Directory.CreateDirectory(Path.Combine(root, "dst")).FullName;
            MoveAction.MoveInto(album, UncAlias(dst), out var remaining);
            Assert.Empty(remaining);
            Assert.False(Directory.Exists(album));
            var moved = Path.Combine(dst, "album", "a.txt");
            Assert.True(File.GetAttributes(moved).HasFlag(FileAttributes.ReadOnly));
            Assert.Equal("old", File.ReadAllText(moved + ":secret"));
        }
        finally
        {
            Wipe(root);
        }
    }

    // --- 4. Share closes only Peel's own sheet ---------------------------------
    // The ownership checks moved to ActionFixRound4Tests when round 4 replaced
    // the claim by appearance with proof by window owner.

    [Fact]
    public void ShareNeverInjectsAGlobalKeystroke()
    {
        foreach (var type in new[] { typeof(ShareSheet), typeof(ShareOwner), typeof(ShareAction) })
        {
            var names = type.GetMethods(BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .Select(m => m.Name).ToList();
            Assert.DoesNotContain("keybd_event", names);
            Assert.DoesNotContain("SendInput", names);
        }
    }
}
