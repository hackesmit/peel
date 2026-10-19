// Adversarial review checks for hq-plct.9 (round 1). Written by the reviewer,
// not the author. A failing test here is a blocking finding; a passing one
// records an attack that the change survives.
using System;
using System.IO;
using System.Linq;
using Peel.Actions;
using Xunit;

namespace Peel.Tests;

public class ReviewActionsTests
{
    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "peel-review-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string UncAlias(string local)
    {
        var full = Path.GetFullPath(local);
        return $@"\\localhost\{full[0]}$" + full.Substring(2);
    }

    // --- Terminal ---------------------------------------------------------
    //
    // Attacked live on George (wt 1.24.12741): a folder named "x;timeout 25"
    // passed as the -d value did not start a second wt subcommand, so the
    // ArgumentList quoting holds. No unit check is needed beyond ActionTests.

    // --- Move: never overwrite, never lose data -------------------------------

    [Fact]
    public void ACaseOnlyCollisionIsNotOverwritten()
    {
        var root = NewRoot();
        try
        {
            var src = Directory.CreateDirectory(Path.Combine(root, "src")).FullName;
            var dst = Directory.CreateDirectory(Path.Combine(root, "dst")).FullName;
            File.WriteAllText(Path.Combine(src, "report.txt"), "new");
            File.WriteAllText(Path.Combine(dst, "REPORT.TXT"), "old");
            var target = MoveAction.MoveInto(Path.Combine(src, "report.txt"), dst);
            Assert.Equal("old", File.ReadAllText(Path.Combine(dst, "REPORT.TXT")));
            Assert.Equal("new", File.ReadAllText(target));
            Assert.NotEqual(Path.Combine(dst, "report.txt"), target, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ARecentFolderThatNoLongerExistsLeavesTheFilesInPlace()
    {
        var root = NewRoot();
        try
        {
            var file = Path.Combine(root, "keep.txt");
            var folder = Directory.CreateDirectory(Path.Combine(root, "album")).FullName;
            File.WriteAllText(file, "precious");
            File.WriteAllText(Path.Combine(folder, "in.txt"), "inside");
            var gone = Path.Combine(root, "deleted-destination");
            var outcome = MoveAction.MoveAll(new[] { file, folder }, gone);
            Assert.Equal(0, outcome.Moved);
            Assert.Equal(2, outcome.Failures.Count);
            Assert.Equal("precious", File.ReadAllText(file));
            Assert.Equal("inside", File.ReadAllText(Path.Combine(folder, "in.txt")));
            Assert.False(Directory.Exists(gone));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ACrossDeviceFolderMoveWithALockedFileLosesNothing()
    {
        // The UNC alias forces ERROR_NOT_SAME_DEVICE, so the copy then delete
        // fallback runs. One file is held open with no sharing: the copy must
        // fail on it and the source must stay whole.
        var root = NewRoot();
        try
        {
            var album = Directory.CreateDirectory(Path.Combine(root, "album")).FullName;
            var dst = Directory.CreateDirectory(Path.Combine(root, "dst")).FullName;
            for (var i = 0; i < 20; i++)
            {
                File.WriteAllText(Path.Combine(album, $"f{i:D2}.txt"), $"data {i}");
            }
            var locked = Path.Combine(album, "f10.txt");
            using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.ThrowsAny<Exception>(() => MoveAction.MoveInto(album, UncAlias(dst)));
            }
            for (var i = 0; i < 20; i++)
            {
                Assert.Equal($"data {i}", File.ReadAllText(Path.Combine(album, $"f{i:D2}.txt")));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ACrossDeviceFolderMoveWhoseDeleteFailsMidWayLosesNothing()
    {
        // The file is readable (the copy succeeds) but cannot be deleted, so
        // Directory.Delete fails part way. Every byte must still exist in the
        // source or the copy.
        var root = NewRoot();
        try
        {
            var album = Directory.CreateDirectory(Path.Combine(root, "album")).FullName;
            var dst = Directory.CreateDirectory(Path.Combine(root, "dst")).FullName;
            for (var i = 0; i < 20; i++)
            {
                File.WriteAllText(Path.Combine(album, $"f{i:D2}.txt"), $"data {i}");
            }
            var held = Path.Combine(album, "f10.txt");
            // Round 1 fix: a delete that fails no longer fails the move. The copy
            // is complete and verified, so it is reported as a partial move
            // naming the source file that stayed.
            using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                MoveAction.MoveInto(album, UncAlias(dst), out var remaining);
                Assert.Equal(new[] { "f10.txt" }, remaining.ToArray());
            }
            var copy = Path.Combine(dst, "album");
            for (var i = 0; i < 20; i++)
            {
                var name = $"f{i:D2}.txt";
                var inSource = Path.Combine(album, name);
                var inCopy = Path.Combine(copy, name);
                Assert.True(
                    (File.Exists(inSource) && File.ReadAllText(inSource) == $"data {i}") ||
                    (File.Exists(inCopy) && File.ReadAllText(inCopy) == $"data {i}"),
                    $"{name} lost");
            }
        }
        finally
        {
            foreach (var f in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(f, FileAttributes.Normal);
            }
            Directory.Delete(root, recursive: true);
        }
    }

    // --- Copy: CF_HDROP list ---------------------------------------------------

    [Fact]
    public void TheCopyListKeepsAThousandUnicodePaths()
    {
        var files = Enumerable.Range(0, 1000).Select(i => $@"C:\fotos\año {i} 写真 \U0001F600.png").ToArray();
        var list = CopyAction.FileList(files).Cast<string>().ToArray();
        Assert.Equal(files, list);
    }
}
