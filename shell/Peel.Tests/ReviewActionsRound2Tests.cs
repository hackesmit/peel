// Round 2 adversarial review checks for the Alt wheel actions (hq-plct.9).
// Cross-volume Move under concurrent writers and with attributes, Terminal's
// PowerShell fallback with hostile folder names, and the Float cap. The cross
// device cases go through the \\localhost\C$ alias (ERROR_NOT_SAME_DEVICE on
// the same disk), so they run the real CrossVolumeMove fallback.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Peel.Actions;
using Xunit;

namespace Peel.Tests;

public class ReviewActionsRound2Tests
{
    private static string NewRoot()
    {
        var root = Path.Combine(@"C:\peel-dev\review2-tmp", "r2-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string UncAlias(string local)
    {
        var full = Path.GetFullPath(local);
        return $@"\\localhost\{full[0]}$" + full.Substring(2);
    }

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
        try
        {
            foreach (var f in Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories))
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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(string link, string existing, IntPtr security);

    // --- Attributes, timestamps -------------------------------------------

    [Fact]
    public void AReadOnlyFileIsMovedAcrossVolumesNotLeftBehind()
    {
        var root = NewRoot();
        try
        {
            var album = MakeAlbum(root);
            File.SetAttributes(Path.Combine(album, "a.txt"), FileAttributes.ReadOnly);
            var dst = Directory.CreateDirectory(Path.Combine(root, "dst")).FullName;
            MoveAction.MoveInto(album, UncAlias(dst), out var remaining);
            Assert.Equal("alpha", File.ReadAllText(Path.Combine(dst, "album", "a.txt")));
            Assert.True(File.GetAttributes(Path.Combine(dst, "album", "a.txt")).HasFlag(FileAttributes.ReadOnly));
            Assert.Empty(remaining);
            Assert.False(Directory.Exists(album), "the source folder should be gone after a clean move");
        }
        finally
        {
            Wipe(root);
        }
    }

    [Fact]
    public void AHiddenFileKeepsItsAttributeAndLastWriteTimeAcrossVolumes()
    {
        var root = NewRoot();
        try
        {
            var album = MakeAlbum(root);
            var b = Path.Combine(album, "b.txt");
            var when = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(b, when);
            File.SetAttributes(b, FileAttributes.Hidden);
            var dst = Directory.CreateDirectory(Path.Combine(root, "dst")).FullName;
            MoveAction.MoveInto(album, UncAlias(dst), out var remaining);
            var moved = Path.Combine(dst, "album", "b.txt");
            Assert.Empty(remaining);
            Assert.True(File.GetAttributes(moved).HasFlag(FileAttributes.Hidden));
            Assert.Equal(when, File.GetLastWriteTimeUtc(moved));
        }
        finally
        {
            Wipe(root);
        }
    }

    [Fact(Skip = "review round 2 follow-up: CrossVolumeMove recreates folders without their attributes or timestamps")]
    public void AHiddenSubfolderStaysHiddenAcrossVolumes()
    {
        var root = NewRoot();
        try
        {
            var album = MakeAlbum(root);
            var sub = new DirectoryInfo(Path.Combine(album, "sub"));
            sub.Attributes |= FileAttributes.Hidden;
            var dst = Directory.CreateDirectory(Path.Combine(root, "dst")).FullName;
            MoveAction.MoveInto(album, UncAlias(dst), out _);
            Assert.True(new DirectoryInfo(Path.Combine(dst, "album", "sub")).Attributes.HasFlag(FileAttributes.Hidden),
                "a hidden folder (for example .git) became visible after the cross-volume move");
        }
        finally
        {
            Wipe(root);
        }
    }

    // --- Concurrent holders and writers ------------------------------------

    [Fact]
    public void AWriterHoldingAFileOpenKeepsThatFileInTheSourceAndTheReportSaysSo()
    {
        var root = NewRoot();
        try
        {
            var album = MakeAlbum(root);
            var dst = Directory.CreateDirectory(Path.Combine(root, "dst")).FullName;
            MoveOutcome outcome;
            using (var writer = new FileStream(Path.Combine(album, "b.txt"), FileMode.Open, FileAccess.ReadWrite,
                       FileShare.ReadWrite | FileShare.Delete))
            {
                outcome = MoveAction.MoveAll(new[] { album }, UncAlias(dst));
                writer.Seek(0, SeekOrigin.End);
                writer.Write(new byte[] { 0x21 });
            }
            Assert.Empty(outcome.Failures);
            Assert.Single(outcome.Partials);
            Assert.Contains("b.txt", outcome.Partials[0]);
            Assert.Equal("bravo!", File.ReadAllText(Path.Combine(album, "b.txt")));
            Assert.Equal("bravo", File.ReadAllText(Path.Combine(dst, "album", "b.txt")));
            Assert.False(File.Exists(Path.Combine(album, "a.txt")));
        }
        finally
        {
            Wipe(root);
        }
    }

    [Fact(Skip = "review round 2 follow-up: a delete-pending file is reported as stayed in the source")]
    public void AReaderWithShareDeleteDoesNotLoseDataAndTheFileGoesWhenItCloses()
    {
        var root = NewRoot();
        try
        {
            var album = MakeAlbum(root);
            var dst = Directory.CreateDirectory(Path.Combine(root, "dst")).FullName;
            IReadOnlyList<string> remaining;
            using (var reader = new FileStream(Path.Combine(album, "a.txt"), FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete))
            {
                MoveAction.MoveInto(album, UncAlias(dst), out remaining);
                var buffer = new byte[5];
                Assert.Equal(5, reader.Read(buffer, 0, 5));
            }
            Assert.Equal("alpha", File.ReadAllText(Path.Combine(dst, "album", "a.txt")));
            Assert.False(File.Exists(Path.Combine(album, "a.txt")));
            // The report must not name a file that is in fact gone once the reader closed.
            Assert.DoesNotContain(remaining, r => r.Equals("a.txt", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Wipe(root);
        }
    }

    [Fact]
    public void AnAppenderRunningDuringTheMoveNeverLosesBytes()
    {
        var root = NewRoot();
        try
        {
            var album = MakeAlbum(root);
            var log = Path.Combine(album, "log.bin");
            File.WriteAllBytes(log, new byte[8 * 1024 * 1024]);
            var dst = Directory.CreateDirectory(Path.Combine(root, "dst")).FullName;
            using var stop = new CancellationTokenSource();
            long written = 8 * 1024 * 1024;
            var appender = new Thread(() =>
            {
                while (!stop.IsCancellationRequested)
                {
                    try
                    {
                        using var s = new FileStream(log, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                        s.Write(new byte[4096]);
                        Interlocked.Add(ref written, 4096);
                    }
                    catch (IOException)
                    {
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                }
            });
            appender.Start();
            MoveAction.MoveInto(album, UncAlias(dst), out var remaining);
            stop.Cancel();
            appender.Join();
            var inSource = File.Exists(log) ? new FileInfo(log).Length : -1;
            var inDest = File.Exists(Path.Combine(dst, "album", "log.bin")) ? new FileInfo(Path.Combine(dst, "album", "log.bin")).Length : -1;
            // Either the full file is still in the source, or the moved copy holds every byte written.
            Assert.True(inSource == Interlocked.Read(ref written) || (inSource == -1 && inDest == Interlocked.Read(ref written)),
                $"written {written}, source {inSource}, dest {inDest}, remaining [{string.Join(",", remaining)}]");
        }
        finally
        {
            Wipe(root);
        }
    }

    [Fact]
    public void AFileRenamedWhileTheCopyRunsLeavesTheSourceWhole()
    {
        var root = NewRoot();
        try
        {
            var album = Path.Combine(root, "album");
            Directory.CreateDirectory(album);
            File.WriteAllBytes(Path.Combine(album, "a-big.bin"), new byte[64 * 1024 * 1024]);
            File.WriteAllText(Path.Combine(album, "b.txt"), "bravo");
            var dst = Directory.CreateDirectory(Path.Combine(root, "dst")).FullName;
            var target = Path.Combine(dst, "album");
            var renamed = false;
            var watcher = new Thread(() =>
            {
                var deadline = DateTime.UtcNow.AddSeconds(20);
                while (DateTime.UtcNow < deadline && !File.Exists(Path.Combine(target, "a-big.bin")))
                {
                    Thread.SpinWait(1000);
                }
                try
                {
                    File.Move(Path.Combine(album, "b.txt"), Path.Combine(album, "b2.txt"));
                    renamed = true;
                }
                catch
                {
                }
            });
            watcher.Start();
            Exception? error = null;
            IReadOnlyList<string> remaining = Array.Empty<string>();
            try
            {
                MoveAction.MoveInto(album, UncAlias(dst), out remaining);
            }
            catch (Exception e)
            {
                error = e;
            }
            watcher.Join();
            Assert.True(renamed, "the rename did not happen during the move");
            // Nothing may be lost: bravo lives in the source as b2.txt or at the destination.
            var bravo = new[] { Path.Combine(album, "b2.txt"), Path.Combine(album, "b.txt"), Path.Combine(target, "b.txt"), Path.Combine(target, "b2.txt") }
                .Any(p => File.Exists(p) && File.ReadAllText(p) == "bravo");
            Assert.True(bravo, $"bravo lost; error {error?.Message}; remaining [{string.Join(",", remaining)}]");
            Assert.True(File.Exists(Path.Combine(album, "a-big.bin")) || File.Exists(Path.Combine(target, "a-big.bin")));
        }
        finally
        {
            Wipe(root);
        }
    }

    [Fact]
    public void AHardLinkFromOutsideTheTreeKeepsItsContent()
    {
        var root = NewRoot();
        try
        {
            var album = MakeAlbum(root);
            var outside = Path.Combine(root, "outside-link.txt");
            Assert.True(CreateHardLinkW(outside, Path.Combine(album, "a.txt"), IntPtr.Zero));
            var dst = Directory.CreateDirectory(Path.Combine(root, "dst")).FullName;
            MoveAction.MoveInto(album, UncAlias(dst), out var remaining);
            Assert.Equal("alpha", File.ReadAllText(outside));
            Assert.Equal("alpha", File.ReadAllText(Path.Combine(dst, "album", "a.txt")));
            Assert.Empty(remaining);
        }
        finally
        {
            Wipe(root);
        }
    }

    [Fact(Skip = "review round 2 follow-up: P/Invoke paths lack the long path prefix, so a tree over 260 chars is refused with a misleading stream error")]
    public void ALongPathInsideTheTreeEitherMovesOrIsRefusedWithTheSourceWhole()
    {
        var root = NewRoot();
        try
        {
            var album = Path.Combine(root, "album");
            var deep = album;
            while (deep.Length < 300)
            {
                deep = Path.Combine(deep, new string('d', 40));
            }
            Directory.CreateDirectory(deep);
            var file = Path.Combine(deep, "deep.txt");
            File.WriteAllText(file, "deep");
            var dst = Directory.CreateDirectory(Path.Combine(root, "dst")).FullName;
            Exception? error = null;
            try
            {
                MoveAction.MoveInto(album, UncAlias(dst), out _);
            }
            catch (Exception e)
            {
                error = e;
            }
            var moved = Path.Combine(dst, "album") + deep.Substring(album.Length);
            var safe = File.Exists(file) || File.Exists(Path.Combine(moved, "deep.txt"));
            Assert.True(safe, $"deep.txt lost; error {error?.Message}");
            Assert.True(error is null, $"long path move refused: {error?.Message}");
        }
        finally
        {
            Wipe(root);
        }
    }

    [Fact]
    public void ASameVolumeFolderMoveIsStillOneRenameKeepingAttributes()
    {
        var root = NewRoot();
        try
        {
            var album = MakeAlbum(root);
            File.SetAttributes(Path.Combine(album, "a.txt"), FileAttributes.ReadOnly);
            new DirectoryInfo(Path.Combine(album, "sub")).Attributes |= FileAttributes.Hidden;
            var id = CrossVolumeMove.Stat(album).Id;
            var dst = Directory.CreateDirectory(Path.Combine(root, "dst")).FullName;
            var landed = MoveAction.MoveInto(album, dst, out var remaining);
            Assert.Empty(remaining);
            Assert.Equal(id, CrossVolumeMove.Stat(landed).Id);
            Assert.True(new DirectoryInfo(Path.Combine(landed, "sub")).Attributes.HasFlag(FileAttributes.Hidden));
        }
        finally
        {
            Wipe(root);
        }
    }

    // --- Terminal fallback with hostile folder names -----------------------

    public static IEnumerable<object[]> HostileNames() => new[]
    {
        new object[] { "semi; new-tab powershell -Command calc" },
        new object[] { "tick`n`$(New-Item pwned1)" },
        new object[] { "dollar $(New-Item pwned2) end" },
        new object[] { "pct %USERNAME% caret ^& amp & x" },
        new object[] { "-leading dash" },
        new object[] { "unicode \u00e9\u4e2d\U0001F600" },
    };

    [Theory]
    [MemberData(nameof(HostileNames))]
    public void ThePowerShellFallbackLandsInTheFolderAndRunsNothingElse(string name)
    {
        var root = NewRoot();
        try
        {
            var folder = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
            var info = TerminalAction.Fallback(folder);
            Assert.DoesNotContain(info.ArgumentList, a => a.Contains(name, StringComparison.Ordinal));
            var primary = TerminalAction.Primary(folder);
            Assert.Equal(new[] { "-d", "." }, primary.ArgumentList.ToArray());
            Assert.Equal(folder, primary.WorkingDirectory);
            info.RedirectStandardInput = true;
            info.RedirectStandardOutput = true;
            info.CreateNoWindow = true;
            var outFile = Path.Combine(root, "where.txt");
            using var p = Process.Start(info)!;
            p.StandardInput.WriteLine($"(Get-Location).ProviderPath | Set-Content -LiteralPath '{outFile.Replace("'", "''")}' -Encoding UTF8");
            p.StandardInput.WriteLine("exit");
            Assert.True(p.WaitForExit(30000), "powershell did not exit");
            Assert.Equal(folder, File.ReadAllText(outFile).Trim());
            Assert.Empty(Directory.GetFiles(root, "pwned*", SearchOption.AllDirectories));
            Assert.Empty(Directory.GetFiles(folder, "pwned*"));
        }
        finally
        {
            Wipe(root);
        }
    }

    [Fact(Skip = "review round 2 follow-up: Windows PowerShell 5.1 starts in $PSHOME when its working directory has [ ], and Set-Location -LiteralPath $PWD keeps it there; use [Environment]::CurrentDirectory")]
    public void ThePowerShellFallbackLandsInABracketFolder() => ThePowerShellFallbackLandsInTheFolderAndRunsNothingElse("brackets [ab] and 'quote'");

    // --- Float cap ------------------------------------------------------------

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(20, 20)]
    [InlineData(21, 20)]
    [InlineData(5000, 20)]
    public void FloatCapHolds(int dropped, int shown) => Assert.Equal(shown, FloatAction.WindowsFor(dropped));
}
