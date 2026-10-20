// Review round 3 checks for the Alt wheel actions (hq-plct.9).
//
// A move between a FAT32/exFAT drive (a USB stick, an SD card) and an NTFS
// drive is the common cross-volume case. These checks need a FAT volume: set
// PEEL_REVIEW_FAT to a folder on one (for example R:\ on a FAT32 VHD made with
// diskpart). Without it they return early and pass.
using System;
using System.IO;
using Peel.Actions;
using Xunit;

namespace Peel.Tests;

public class Review3ActionsTests
{
    private static string? FatRoot()
    {
        var fat = Environment.GetEnvironmentVariable("PEEL_REVIEW_FAT");
        if (string.IsNullOrEmpty(fat) || !Directory.Exists(fat))
        {
            return null;
        }
        var root = Path.Combine(fat, "peel-rv3-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string NtfsRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "peel-rv3-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string MakeAlbum(string parent)
    {
        var album = Path.Combine(parent, "album");
        Directory.CreateDirectory(Path.Combine(album, "sub"));
        File.WriteAllText(Path.Combine(album, "a.txt"), "alpha");
        File.WriteAllText(Path.Combine(album, "sub", "c.txt"), "charlie");
        return album;
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

    [Fact]
    public void AMoveFromAFatDriveToNtfsEmptiesTheSource()
    {
        var fat = FatRoot();
        if (fat is null)
        {
            return;
        }
        var ntfs = NtfsRoot();
        try
        {
            var album = MakeAlbum(fat);
            var target = Path.Combine(ntfs, "album");
            var result = CrossVolumeMove.Move(album, target);
            Assert.Equal("charlie", File.ReadAllText(Path.Combine(target, "sub", "c.txt")));
            Assert.Empty(result.Remaining);
            Assert.False(Directory.Exists(album));
        }
        finally
        {
            Wipe(fat);
            Wipe(ntfs);
        }
    }

    [Fact]
    public void AMoveFromNtfsToAFatDriveEmptiesTheSource()
    {
        var fat = FatRoot();
        if (fat is null)
        {
            return;
        }
        var ntfs = NtfsRoot();
        try
        {
            var album = MakeAlbum(ntfs);
            var target = Path.Combine(fat, "album");
            var result = CrossVolumeMove.Move(album, target);
            Assert.Equal("charlie", File.ReadAllText(Path.Combine(target, "sub", "c.txt")));
            Assert.Empty(result.Remaining);
            Assert.False(Directory.Exists(album));
        }
        finally
        {
            Wipe(fat);
            Wipe(ntfs);
        }
    }

    /// <summary>The cause: StreamNames(handle) throws on FAT, where a file has
    /// no named streams, so every copy fails verification (destination on
    /// FAT) or every source fails its delete check (source on FAT).</summary>
    [Fact]
    public void StreamNamesOfAFatFileIsEmptyNotAnError()
    {
        var fat = FatRoot();
        if (fat is null)
        {
            return;
        }
        try
        {
            var file = Path.Combine(fat, "a.txt");
            File.WriteAllText(file, "alpha");
            using var handle = File.OpenHandle(file);
            Assert.Empty(CrossVolumeMove.StreamNames(handle));
        }
        finally
        {
            Wipe(fat);
        }
    }
}
