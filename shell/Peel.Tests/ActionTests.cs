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
        Assert.Equal(@"C:\some folder & more", info.WorkingDirectory);
        Assert.Equal(new[] { "-d", "." }, info.ArgumentList.ToArray());
    }

    [Theory]
    [InlineData(@"C:\PeelTest\safe; new-tab powershell -Command calc")]
    [InlineData(@"C:\PeelTest\x;timeout 25")]
    [InlineData(@"C:\PeelTest\a b & c ^ %PATH%")]
    public void TheFolderNeverReachesAnyCommandLine(string folder)
    {
        // Windows Terminal parses ';' in its arguments as a new subcommand, so
        // the folder may only travel as the working directory (lucy round 1).
        foreach (var info in new[] { TerminalAction.Primary(folder), TerminalAction.Fallback(folder) })
        {
            Assert.Equal(folder, info.WorkingDirectory);
            Assert.Equal(string.Empty, info.Arguments);
            foreach (var arg in info.ArgumentList)
            {
                Assert.DoesNotContain(";", arg);
                Assert.DoesNotContain("PeelTest", arg);
            }
        }
        Assert.Equal(TerminalAction.PrimaryArguments, TerminalAction.Primary(folder).ArgumentList.ToArray());
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

    // --- Move: physical containment and cross-device -------------------------
    // The destination below is the moved folder's own descendant, reached by a
    // spelling path strings cannot see through. Every case must refuse and
    // leave the folder whole. They run on a real NTFS volume on George.

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "peel-move-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>A folder "album" holding a precious file and a subfolder "out".</summary>
    private static (string Album, string Out, string Keep) MakeAlbum(string root)
    {
        var album = Path.Combine(root, "album");
        var into = Path.Combine(album, "out");
        Directory.CreateDirectory(into);
        var keep = Path.Combine(album, "photo.txt");
        File.WriteAllText(keep, "precious");
        return (album, into, keep);
    }

    private static void AssertRefusedAndWhole(string album, string keep, string destination)
    {
        // The refusal must come from the containment test seeing the alias,
        // not from some other IOException on the way.
        Assert.True(FileIdentity.IsSameOrBeneath(destination, album), $"{destination} not seen inside {album}");
        Assert.Throws<IOException>(() => MoveAction.MoveInto(album, destination));
        Assert.True(Directory.Exists(album));
        Assert.Equal("precious", File.ReadAllText(keep));
    }

    /// <summary>The same local path through the administrative share.</summary>
    private static string UncAlias(string local)
    {
        var full = Path.GetFullPath(local);
        return $@"\\localhost\{full[0]}$" + full.Substring(2);
    }

    private static void RunCmd(string arguments)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c " + arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"cmd /c {arguments} failed: {process.StandardError.ReadToEnd()}");
    }

    private static void Cleanup(string root)
    {
        // Remove junctions first so a recursive delete can never reach through one.
        foreach (var dir in Directory.GetDirectories(root, "*", SearchOption.AllDirectories))
        {
            if (Directory.Exists(dir) && new DirectoryInfo(dir).Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                Directory.Delete(dir);
            }
        }
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void ADescendantReachedThroughAUncAliasIsRefused()
    {
        var root = NewRoot();
        var (album, into, keep) = MakeAlbum(root);
        try
        {
            var alias = UncAlias(into);
            Assert.True(Directory.Exists(alias), $"admin share not reachable: {alias}");
            AssertRefusedAndWhole(album, keep, alias);
            // And the other way round: the source named by its alias.
            AssertRefusedAndWhole(UncAlias(album), keep, into);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void ADescendantReachedThroughAJunctionIsRefused()
    {
        var root = NewRoot();
        var (album, into, keep) = MakeAlbum(root);
        var link = Path.Combine(root, "link");
        try
        {
            RunCmd($"mklink /J \"{link}\" \"{into}\"");
            AssertRefusedAndWhole(album, keep, link);
            // A junction to the folder itself, too.
            var self = Path.Combine(root, "self");
            RunCmd($"mklink /J \"{self}\" \"{album}\"");
            AssertRefusedAndWhole(album, keep, self);
            AssertRefusedAndWhole(album, keep, Path.Combine(self, "out"));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void ADescendantSpelledInAnotherCaseIsRefused()
    {
        var root = NewRoot();
        var (album, into, keep) = MakeAlbum(root);
        try
        {
            AssertRefusedAndWhole(album, keep, into.ToUpperInvariant());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void ADescendantThroughAShortNameIsRefused()
    {
        var root = NewRoot();
        var album = Path.Combine(root, "a long album name");
        var into = Path.Combine(album, "out");
        Directory.CreateDirectory(into);
        var keep = Path.Combine(album, "photo.txt");
        File.WriteAllText(keep, "precious");
        try
        {
            // fsutil may refuse to set a short name without admin; when the
            // volume already made one (or lets us set one) the case is real.
            var shortAlbum = Path.Combine(root, "ALBUM~1");
            if (!Directory.Exists(shortAlbum))
            {
                var info = new System.Diagnostics.ProcessStartInfo("fsutil.exe", $"file setshortname \"{album}\" ALBUM~1")
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                };
                using var process = System.Diagnostics.Process.Start(info)!;
                process.WaitForExit();
            }
            if (Directory.Exists(shortAlbum))
            {
                AssertRefusedAndWhole(album, keep, Path.Combine(shortAlbum, "out"));
            }
            // Always holds: the plain descendant is refused too.
            AssertRefusedAndWhole(album, keep, into);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void ADescendantThroughASubstDriveIsRefused()
    {
        var root = NewRoot();
        var (album, _, keep) = MakeAlbum(root);
        var free = "PQRSTUVWXYZ".Select(c => $"{c}:").FirstOrDefault(d => !Directory.Exists(d + @"\"));
        Assert.NotNull(free);
        try
        {
            RunCmd($"subst {free} \"{album}\"");
            try
            {
                AssertRefusedAndWhole(album, keep, free + @"\out");
                AssertRefusedAndWhole(album, keep, free + @"\");
            }
            finally
            {
                RunCmd($"subst {free} /d");
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void AFolderMovesToASiblingNamedThroughAUncAlias()
    {
        // Same disk, but Windows calls a local path and its UNC alias different
        // devices, so the rename fails with ERROR_NOT_SAME_DEVICE and the move
        // must fall back to copy then delete, keeping every byte.
        var root = NewRoot();
        var (album, _, _) = MakeAlbum(root);
        File.WriteAllText(Path.Combine(album, "out", "deep.txt"), "deep");
        var sibling = Path.Combine(root, "sibling");
        Directory.CreateDirectory(sibling);
        try
        {
            Assert.False(FileIdentity.IsSameOrBeneath(UncAlias(sibling), album));
            var landed = MoveAction.MoveInto(album, UncAlias(sibling));
            Assert.False(Directory.Exists(album));
            Assert.Equal("precious", File.ReadAllText(Path.Combine(sibling, "album", "photo.txt")));
            Assert.Equal("deep", File.ReadAllText(Path.Combine(sibling, "album", "out", "deep.txt")));
            Assert.EndsWith(@"sibling\album", landed);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void AFolderMovesToASiblingOnTheSameVolumeByRename()
    {
        var root = NewRoot();
        var (album, _, _) = MakeAlbum(root);
        var sibling = Path.Combine(root, "sibling");
        Directory.CreateDirectory(sibling);
        try
        {
            var before = FileIdentity.Of(album);
            var landed = MoveAction.MoveInto(album, sibling);
            // A rename keeps the folder's identity; a copy would not.
            Assert.Equal(before, FileIdentity.Of(landed));
            Assert.Equal("precious", File.ReadAllText(Path.Combine(landed, "photo.txt")));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void ACopyFallbackRefusesATreeHoldingALinkBeforeCopying()
    {
        var root = NewRoot();
        var (album, into, keep) = MakeAlbum(root);
        var elsewhere = Path.Combine(root, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        var target = Path.Combine(root, "copy");
        try
        {
            RunCmd($"mklink /J \"{Path.Combine(into, "link")}\" \"{elsewhere}\"");
            Assert.Throws<IOException>(() => CrossVolumeMove.Move(album, target));
            Assert.False(Directory.Exists(target));
            Assert.Equal("precious", File.ReadAllText(keep));
        }
        finally
        {
            Cleanup(root);
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
