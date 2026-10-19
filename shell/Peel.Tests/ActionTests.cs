// The pure cores of the Alt wheel actions: which ids the host performs, the
// clipboard file list, the terminal's folder and command, the never
// overwriting move destination, and the recent folders list. The Windows UI
// around them (the clipboard, the picker, the float window, the share sheet)
// is exercised by the scripted check on George; everything decidable without a
// desktop is decided here.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Peel.Actions;
using Xunit;

namespace Peel.Tests;

public class ActionTests
{
    // --- ActionHost routing -------------------------------------------------

    [Theory]
    [InlineData("files.share")]
    [InlineData("files.float")]
    [InlineData("files.copy")]
    [InlineData("files.move")]
    [InlineData("files.terminal")]
    public void TheHostPerformsEveryShellAction(string id)
    {
        Assert.True(ActionHost.Handles(id));
    }

    [Theory]
    [InlineData("files.copy_path")] // engine work, never reaches the host
    [InlineData("img.to_webp")]
    [InlineData("")]
    public void TheHostIgnoresEverythingElse(string id)
    {
        Assert.False(ActionHost.Handles(id));
    }

    // --- Copy ---------------------------------------------------------------

    [Fact]
    public void TheCopyListKeepsEveryPathInOrder()
    {
        var files = new[] { @"C:\a.png", @"C:\b.txt", @"C:\dir" };
        var list = CopyAction.FileList(files).Cast<string>().ToArray();
        Assert.Equal(files, list);
    }

    // --- Terminal -----------------------------------------------------------

    [Fact]
    public void TheTerminalOpensAFilesParentFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "peel-term");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "clip.mp4");
        File.WriteAllText(file, "x");
        try
        {
            Assert.Equal(folder, TerminalAction.FolderFor(new[] { file }));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void TheTerminalOpensADroppedFolderItself()
    {
        var folder = Path.Combine(Path.GetTempPath(), "peel-term-dir");
        Directory.CreateDirectory(folder);
        try
        {
            Assert.Equal(folder, TerminalAction.FolderFor(new[] { folder }));
        }
        finally
        {
            Directory.Delete(folder);
        }
    }

    [Fact]
    public void WindowsTerminalIsOpenedAtTheFolderWithoutAShell()
    {
        var info = TerminalAction.Primary(@"C:\some folder & more");
        Assert.Equal("wt.exe", info.FileName);
        Assert.False(info.UseShellExecute);
        Assert.Equal(new[] { "-d", @"C:\some folder & more" }, info.ArgumentList.ToArray());
    }

    [Fact]
    public void ThePowerShellFallbackStartsInTheFolder()
    {
        var info = TerminalAction.Fallback(@"C:\work");
        Assert.Equal("powershell.exe", info.FileName);
        Assert.Equal(@"C:\work", info.WorkingDirectory);
        Assert.Contains("-NoExit", info.ArgumentList);
    }

    // --- Move: never overwriting --------------------------------------------

    [Fact]
    public void AFreeNameIsUsedAsItIs()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dest = MoveAction.UniqueDestination(@"C:\dst", "photo.png", taken.Contains);
        Assert.Equal(@"C:\dst\photo.png", dest);
    }

    [Fact]
    public void ATakenNameGrowsASpaceTwoBeforeTheExtension()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\dst\photo.png" };
        var dest = MoveAction.UniqueDestination(@"C:\dst", "photo.png", taken.Contains);
        Assert.Equal(@"C:\dst\photo 2.png", dest);
    }

    [Fact]
    public void TheSuffixWalksUntilItFindsAFreeName()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\dst\photo.png", @"C:\dst\photo 2.png", @"C:\dst\photo 3.png",
        };
        var dest = MoveAction.UniqueDestination(@"C:\dst", "photo.png", taken.Contains);
        Assert.Equal(@"C:\dst\photo 4.png", dest);
    }

    [Fact]
    public void AFolderWithNoExtensionGrowsASpaceTwoToo()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\dst\album" };
        var dest = MoveAction.UniqueDestination(@"C:\dst", "album", taken.Contains);
        Assert.Equal(@"C:\dst\album 2", dest);
    }

    [Fact]
    public void AMovedFileLandsInTheDestinationAndTheSourceIsGone()
    {
        var root = Path.Combine(Path.GetTempPath(), "peel-move-" + Guid.NewGuid().ToString("N"));
        var src = Path.Combine(root, "src");
        var dst = Path.Combine(root, "dst");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dst);
        var file = Path.Combine(src, "note.txt");
        File.WriteAllText(file, "hello");
        try
        {
            var landed = MoveAction.MoveInto(file, dst);
            Assert.Equal(Path.Combine(dst, "note.txt"), landed);
            Assert.True(File.Exists(landed));
            Assert.False(File.Exists(file));
            Assert.Equal("hello", File.ReadAllText(landed));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MovingOntoATakenNameKeepsBothFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "peel-move-" + Guid.NewGuid().ToString("N"));
        var src = Path.Combine(root, "src");
        var dst = Path.Combine(root, "dst");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dst);
        File.WriteAllText(Path.Combine(dst, "note.txt"), "first");
        var file = Path.Combine(src, "note.txt");
        File.WriteAllText(file, "second");
        try
        {
            var landed = MoveAction.MoveInto(file, dst);
            Assert.Equal(Path.Combine(dst, "note 2.txt"), landed);
            Assert.Equal("first", File.ReadAllText(Path.Combine(dst, "note.txt")));
            Assert.Equal("second", File.ReadAllText(landed));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MovingAFolderIntoItsOwnDescendantIsRefusedAndNothingIsLost()
    {
        var root = Path.Combine(Path.GetTempPath(), "peel-move-" + Guid.NewGuid().ToString("N"));
        var album = Path.Combine(root, "album");
        var into = Path.Combine(album, "out");
        Directory.CreateDirectory(into);
        var keep = Path.Combine(album, "photo.txt");
        File.WriteAllText(keep, "precious");
        try
        {
            // Without the guard this used to copy album into album\out\album
            // and then delete album, losing everything. It must refuse instead.
            Assert.Throws<IOException>(() => MoveAction.MoveInto(album, into));
            Assert.True(Directory.Exists(album));
            Assert.True(File.Exists(keep));
            Assert.Equal("precious", File.ReadAllText(keep));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MovingAFolderIntoItselfIsRefused()
    {
        var root = Path.Combine(Path.GetTempPath(), "peel-move-" + Guid.NewGuid().ToString("N"));
        var album = Path.Combine(root, "album");
        Directory.CreateDirectory(album);
        File.WriteAllText(Path.Combine(album, "a.txt"), "x");
        try
        {
            Assert.Throws<IOException>(() => MoveAction.MoveInto(album, album));
            Assert.True(File.Exists(Path.Combine(album, "a.txt")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // --- RecentFolders ------------------------------------------------------

    [Fact]
    public void AUsedFolderGoesToTheFront()
    {
        var after = RecentFolders.Promote(new[] { @"C:\a", @"C:\b" }, @"C:\c");
        Assert.Equal(new[] { @"C:\c", @"C:\a", @"C:\b" }, after.ToArray());
    }

    [Fact]
    public void AFolderUsedAgainMovesUpWithoutADuplicate()
    {
        var after = RecentFolders.Promote(new[] { @"C:\a", @"C:\b", @"C:\c" }, @"C:\B");
        Assert.Equal(new[] { @"C:\B", @"C:\a", @"C:\c" }, after.ToArray());
    }

    [Fact]
    public void TheListIsCappedSoItStaysAShortMenu()
    {
        var current = Enumerable.Range(1, RecentFolders.Max).Select(n => $@"C:\f{n}").ToArray();
        var after = RecentFolders.Promote(current, @"C:\new");
        Assert.Equal(RecentFolders.Max, after.Count);
        Assert.Equal(@"C:\new", after[0]);
        Assert.DoesNotContain($@"C:\f{RecentFolders.Max}", after);
    }

    // --- Float --------------------------------------------------------------

    [Theory]
    [InlineData("holiday.JPG", true)]
    [InlineData("chart.png", true)]
    [InlineData("report.pdf", false)]
    [InlineData("archive", false)]
    public void TheFloatThumbnailIsOnlyDecodedForImages(string name, bool expected)
    {
        Assert.Equal(expected, FloatAction.IsImage(name));
    }

    [Fact]
    public void AFolderNamedLikeAnImageIsNotTreatedAsOne()
    {
        var root = Path.Combine(Path.GetTempPath(), "peel-float-" + Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(root, "holiday.jpg");
        Directory.CreateDirectory(folder);
        try
        {
            // A real folder called "holiday.jpg" must float as an icon tile, not
            // be handed to the image decoder, which would fail and show nothing.
            Assert.False(FloatAction.IsImage(folder));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
