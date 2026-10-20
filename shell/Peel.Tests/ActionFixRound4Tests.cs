// Round 4 fixes for the Alt wheel actions (hq-plct.9), one regression test per
// finding of the round 3 reviews: a named stream added to a source folder just
// before it is deleted, a copied folder that is missing or replaced, and a
// share sheet Peel cannot prove its own. The FAT32/exFAT fix is checked by
// Review3ActionsTests with PEEL_REVIEW_FAT set.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    // --- 3. Share closes only a sheet proven Peel's ---------------------------

    /// <summary>Sheets on screen, each with the window that owns it now.</summary>
    private sealed class FakeDesktop
    {
        public readonly Dictionary<IntPtr, IntPtr> OwnerOf = new();
        public readonly List<IntPtr> Closed = new();

        /// <summary>Runs inside close, after the check and before the close.</summary>
        public Action? WhileClosing;

        public ShareOwner Owner() => new(
            () => OwnerOf.Keys.ToList(),
            (sheet, anchor) => OwnerOf.TryGetValue(sheet, out var o) && o == anchor,
            (sheet, mayClose) =>
            {
                WhileClosing?.Invoke();
                if (OwnerOf.ContainsKey(sheet) && mayClose(sheet))
                {
                    Closed.Add(sheet);
                    OwnerOf.Remove(sheet);
                }
                return !OwnerOf.ContainsKey(sheet);
            });
    }

    private static readonly IntPtr Anchor = new(0x0A01);
    private static readonly IntPtr Explorer = new(0x0E01);
    private static readonly IntPtr SheetA = new(0x1001);
    private static readonly IntPtr SheetB = new(0x2002);

    [Fact]
    public void PeelClosesASheetItsAnchorOwns()
    {
        var desk = new FakeDesktop();
        desk.OwnerOf[SheetA] = Anchor;
        Assert.Equal(0, desk.Owner().Dismiss(Anchor));
        Assert.Equal(new[] { SheetA }, desk.Closed);
    }

    [Fact]
    public void AReusedSheetWindowWithNoLookInBetweenIsNotClosed()
    {
        var desk = new FakeDesktop();
        var owner = desk.Owner();
        // Peel's sheet is up in window A; nothing polls. The user dismisses it
        // and Explorer opens its own sheet in the same window A.
        desk.OwnerOf[SheetA] = Anchor;
        desk.OwnerOf[SheetA] = Explorer;
        owner.Dismiss(Anchor);
        Assert.Empty(desk.Closed);
    }

    [Fact]
    public void AForeignSheetThatAppearsBeforePeelsIsNotClosed()
    {
        var desk = new FakeDesktop();
        // Peel raises its sheet; before any look, the user dismisses it and
        // another app's sheet is the only new one on screen.
        desk.OwnerOf[SheetB] = Explorer;
        desk.Owner().Dismiss(Anchor);
        Assert.Empty(desk.Closed);
    }

    [Fact]
    public void ASheetWithNoOwnerIsLeftOpen()
    {
        // If Windows gives the sheet no owner, nothing proves it is Peel's.
        var desk = new FakeDesktop();
        desk.OwnerOf[SheetA] = IntPtr.Zero;
        desk.Owner().Dismiss(Anchor);
        Assert.Empty(desk.Closed);
    }

    [Fact]
    public void OnlyTheProvenSheetIsClosedWhenSeveralAreUp()
    {
        var desk = new FakeDesktop();
        desk.OwnerOf[SheetA] = Anchor;
        desk.OwnerOf[SheetB] = Explorer;
        desk.Owner().Dismiss(Anchor);
        Assert.Equal(new[] { SheetA }, desk.Closed);
        Assert.True(desk.OwnerOf.ContainsKey(SheetB));
    }

    [Fact]
    public void ASheetThatChangesHandsWhileClosingIsLeftOpen()
    {
        var desk = new FakeDesktop();
        desk.OwnerOf[SheetA] = Anchor;
        desk.WhileClosing = () => desk.OwnerOf[SheetA] = Explorer;
        desk.Owner().Dismiss(Anchor);
        Assert.Empty(desk.Closed);
    }

    [Fact]
    public void WithNoAnchorNothingIsClosed()
    {
        var desk = new FakeDesktop();
        desk.OwnerOf[SheetA] = IntPtr.Zero;
        desk.Owner().Dismiss(IntPtr.Zero);
        Assert.Empty(desk.Closed);
    }

    [Fact]
    public void ARealWindowIsNotOwnedByADeadAnchor()
    {
        // OwnedBy refuses an anchor that is not a live window, so a closed
        // anchor whose handle was reused proves nothing.
        Assert.False(ShareSheet.OwnedBy(SheetA, new IntPtr(0x7FFF0001)));
        Assert.False(ShareSheet.OwnedBy(SheetA, IntPtr.Zero));
    }
}
