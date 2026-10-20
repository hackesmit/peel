// Round 1 review fixes for the Alt wheel actions (hq-plct.9): the cross-volume
// Move that copies from a manifest, verifies, and deletes only unchanged
// sources; folder alternate data streams refused before anything is touched;
// the Float cap; recent folders pruned and '&' shown literally. The cross
// device cases go through the \\localhost\C$ alias, which makes Windows report
// ERROR_NOT_SAME_DEVICE for the same NTFS disk, so they run the real fallback.
using System;
using System.IO;
using System.Linq;
using Peel.Actions;
using Xunit;

namespace Peel.Tests;

public class ActionFixRound1Tests
{
    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "peel-fix1-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string UncAlias(string local)
    {
        var full = Path.GetFullPath(local);
        return $@"\\localhost\{full[0]}$" + full.Substring(2);
    }

    /// <summary>album/ with a.txt, b.txt and sub/c.txt.</summary>
    private static string MakeAlbum(string root)
    {
        var album = Path.Combine(root, "album");
        Directory.CreateDirectory(Path.Combine(album, "sub"));
        File.WriteAllText(Path.Combine(album, "a.txt"), "alpha");
        File.WriteAllText(Path.Combine(album, "b.txt"), "bravo");
        File.WriteAllText(Path.Combine(album, "sub", "c.txt"), "charlie");
        return album;
    }

    private static void Wipe(string root)
    {
        foreach (var f in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(f, FileAttributes.Normal);
        }
        Directory.Delete(root, recursive: true);
    }

    // --- Cross-volume move: manifest, verify, delete only what matches ------

    [Fact]
    public void ACleanCrossVolumeMoveCopiesEverythingAndEmptiesTheSource()
    {
        var root = NewRoot();
        try
        {
            var album = MakeAlbum(root);
            var dst = Directory.CreateDirectory(Path.Combine(root, "dst")).FullName;
            var landed = MoveAction.MoveInto(album, UncAlias(dst), out var remaining);
            Assert.Empty(remaining);
            Assert.False(Directory.Exists(album));
            Assert.Equal("alpha", File.ReadAllText(Path.Combine(dst, "album", "a.txt")));
            Assert.Equal("charlie", File.ReadAllText(Path.Combine(dst, "album", "sub", "c.txt")));
            Assert.EndsWith(@"dst\album", landed);
        }
        finally
        {
            Wipe(root);
        }
    }

    [Fact]
    public void AFileCreatedDuringTheMoveStaysInTheSourceAndIsReported()
    {
        var root = NewRoot();
        try
        {
            var album = MakeAlbum(root);
            var target = Path.Combine(root, "copy");
            var result = CrossVolumeMove.Move(album, target,
                afterCopy: _ => File.WriteAllText(Path.Combine(album, "sub", "new.txt"), "fresh"));
            Assert.Equal(new[] { Path.Combine("sub", "new.txt") }, result.Remaining.ToArray());
            Assert.Equal("fresh", File.ReadAllText(Path.Combine(album, "sub", "new.txt")));
            Assert.False(File.Exists(Path.Combine(album, "a.txt")));
            Assert.Equal("alpha", File.ReadAllText(Path.Combine(target, "a.txt")));
        }
        finally
        {
            Wipe(root);
        }
    }

    [Fact]
    public void AFileEditedAfterItWasCopiedIsKeptEvenWithTheSameSizeAndTime()
    {
        var root = NewRoot();
        try
        {
            var album = MakeAlbum(root);
            var target = Path.Combine(root, "copy");
            var a = Path.Combine(album, "a.txt");
            var result = CrossVolumeMove.Move(album, target, afterCopy: _ =>
            {
                // Same length, same last write time: only the content hash
                // can tell this edit apart.
                var stamp = File.GetLastWriteTimeUtc(a);
                File.WriteAllText(a, "ALPHA");
                File.SetLastWriteTimeUtc(a, stamp);
            });
            Assert.Equal(new[] { "a.txt" }, result.Remaining.ToArray());
            Assert.Equal("ALPHA", File.ReadAllText(a));
            Assert.Equal("alpha", File.ReadAllText(Path.Combine(target, "a.txt")));
            Assert.False(File.Exists(Path.Combine(album, "b.txt")));
        }
        finally
        {
            Wipe(root);
        }
    }

    [Fact]
    public void AFileReplacedDuringTheMoveIsKept()
    {
        var root = NewRoot();
        try
        {
            var album = MakeAlbum(root);
            var target = Path.Combine(root, "copy");
            var b = Path.Combine(album, "b.txt");
            var result = CrossVolumeMove.Move(album, target, afterCopy: _ =>
            {
                // Same name and the very same bytes, but a different file.
                File.Delete(b);
                File.WriteAllText(b, "bravo");
            });
            Assert.Contains("b.txt", result.Remaining);
            Assert.Equal("bravo", File.ReadAllText(b));
        }
        finally
        {
            Wipe(root);
        }
    }

    [Fact]
    public void ACopyFailureRemovesThePartialDestinationAndKeepsTheSource()
    {
        var root = NewRoot();
        try
        {
            var album = MakeAlbum(root);
            var dst = Directory.CreateDirectory(Path.Combine(root, "dst")).FullName;
            using (new FileStream(Path.Combine(album, "sub", "c.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.ThrowsAny<IOException>(() => MoveAction.MoveInto(album, UncAlias(dst)));
            }
            Assert.False(Directory.Exists(Path.Combine(dst, "album")), "the partial copy was left behind");
            Assert.Equal("alpha", File.ReadAllText(Path.Combine(album, "a.txt")));
            Assert.Equal("charlie", File.ReadAllText(Path.Combine(album, "sub", "c.txt")));
        }
        finally
        {
            Wipe(root);
        }
    }

    [Fact]
    public void ADeleteFailureReportsACompleteCopyAndTheItemsThatStayed()
    {
        var root = NewRoot();
        try
        {
            var album = MakeAlbum(root);
            var dst = Directory.CreateDirectory(Path.Combine(root, "dst")).FullName;
            MoveOutcome outcome;
            using (new FileStream(Path.Combine(album, "b.txt"), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                outcome = MoveAction.MoveAll(new[] { album }, UncAlias(dst));
            }
            Assert.Equal(1, outcome.Moved);
            Assert.Empty(outcome.Failures);
            var partial = Assert.Single(outcome.Partials);
            Assert.Contains("b.txt", partial);
            Assert.Contains("copied", partial);
            Assert.Equal("bravo", File.ReadAllText(Path.Combine(dst, "album", "b.txt")));
            Assert.Equal("bravo", File.ReadAllText(Path.Combine(album, "b.txt")));
            Assert.False(File.Exists(Path.Combine(album, "a.txt")));
        }
        finally
        {
            Wipe(root);
        }
    }

    [Fact]
    public void ManyRemainingNamesAreShortenedInTheReport()
    {
        var text = CrossVolumeMove.Describe(Enumerable.Range(1, 8).Select(n => $"f{n}.txt").ToArray());
        Assert.Equal("f1.txt, f2.txt, f3.txt, f4.txt, f5.txt and 3 more", text);
    }

    // --- Alternate data streams ------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("sub")]
    public void AFolderStreamRefusesTheCrossVolumeMoveBeforeTouchingAnything(string where)
    {
        var root = NewRoot();
        try
        {
            var album = MakeAlbum(root);
            var holder = Path.Combine(album, where);
            File.WriteAllText(Path.TrimEndingDirectorySeparator(holder) + ":secret", "hidden");
            Assert.NotEmpty(CrossVolumeMove.NamedStreams(holder));
            var dst = Directory.CreateDirectory(Path.Combine(root, "dst")).FullName;
            var error = Assert.Throws<IOException>(() => MoveAction.MoveInto(album, UncAlias(dst)));
            Assert.Contains("alternate data stream", error.Message);
            Assert.False(Directory.Exists(Path.Combine(dst, "album")));
            Assert.Equal("hidden", File.ReadAllText(Path.TrimEndingDirectorySeparator(holder) + ":secret"));
            Assert.Equal("alpha", File.ReadAllText(Path.Combine(album, "a.txt")));
        }
        finally
        {
            Wipe(root);
        }
    }

    [Fact]
    public void AFolderStreamDoesNotBlockASameVolumeRename()
    {
        var root = NewRoot();
        try
        {
            var album = MakeAlbum(root);
            File.WriteAllText(album + ":secret", "hidden");
            var dst = Directory.CreateDirectory(Path.Combine(root, "dst")).FullName;
            var landed = MoveAction.MoveInto(album, dst);
            Assert.Equal("hidden", File.ReadAllText(landed + ":secret"));
        }
        finally
        {
            Wipe(root);
        }
    }

    [Fact]
    public void AFileStreamSurvivesTheCrossVolumeCopy()
    {
        // File.Copy (CopyFileEx) keeps a file's named streams; the move checks
        // that it did before deleting the source.
        var root = NewRoot();
        try
        {
            var album = MakeAlbum(root);
            File.WriteAllText(Path.Combine(album, "a.txt") + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3");
            var dst = Directory.CreateDirectory(Path.Combine(root, "dst")).FullName;
            MoveAction.MoveInto(album, UncAlias(dst), out var remaining);
            Assert.Empty(remaining);
            Assert.Equal("[ZoneTransfer]\r\nZoneId=3", File.ReadAllText(Path.Combine(dst, "album", "a.txt") + ":Zone.Identifier"));
        }
        finally
        {
            Wipe(root);
        }
    }

    // --- Float, recent folders ----------------------------------------------------

    [Theory]
    [InlineData(1, 1)]
    [InlineData(20, 20)]
    [InlineData(21, 20)]
    [InlineData(200, 20)]
    public void FloatOpensAtMostTwentyWindowsPerDrop(int dropped, int windows)
    {
        Assert.Equal(windows, FloatAction.WindowsFor(dropped));
    }

    [Fact]
    public void MissingLocalRecentFoldersArePrunedAndNetworkOnesKept()
    {
        var saved = new[] { @"C:\here", @"C:\gone", @"\\server\share\x", @"D:\also here" };
        var kept = RecentFolders.Prune(saved, f => f != @"C:\gone");
        Assert.Equal(new[] { @"C:\here", @"\\server\share\x", @"D:\also here" }, kept.ToArray());
        var probed = 0;
        RecentFolders.Prune(new[] { @"\\server\share" }, _ => { probed++; return false; });
        Assert.Equal(0, probed);
    }

    [Fact]
    public void AnAmpersandInAFolderNameIsShownNotUsedAsAMnemonic()
    {
        Assert.Equal(@"C:\Tom && Jerry\R&&D", RecentFolders.MenuText(@"C:\Tom & Jerry\R&D"));
    }
}
