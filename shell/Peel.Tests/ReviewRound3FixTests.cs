// Round 3 fixes for hq-plct.7 (the final pass Daniel approved): the Opus
// round 2 blocker (list file split) and lucy round 2 findings 1 to 5 and 7.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Peel.Engine;
using Peel.Hooks;
using Peel.Jobs;
using Xunit;

namespace Peel.Tests;

[Collection("environment")]
public sealed class ReviewRound3FixTests : IDisposable
{
    private readonly string? _previousCli = Environment.GetEnvironmentVariable("PEEL_CLI");
    private readonly string _pids = Path.Combine(Path.GetTempPath(), $"peel-r3-pids-{Guid.NewGuid():N}.txt");

    public ReviewRound3FixTests()
    {
        Environment.SetEnvironmentVariable("PEEL_CLI", EngineProcessTests.FakeEngine);
        Environment.SetEnvironmentVariable("PEEL_FAKE_PIDS", _pids);
    }

    public void Dispose()
    {
        foreach (var pid in Pids())
        {
            try { Process.GetProcessById(pid).Kill(); } catch (Exception) { }
        }
        Environment.SetEnvironmentVariable("PEEL_CLI", _previousCli);
        Environment.SetEnvironmentVariable("PEEL_FAKE_MODE", null);
        Environment.SetEnvironmentVariable("PEEL_FAKE_PIDS", null);
        try { File.Delete(_pids); } catch (IOException) { }
    }

    private List<int> Pids()
    {
        try
        {
            return File.Exists(_pids)
                ? File.ReadAllLines(_pids).Where(l => l.Trim().Length > 0).Select(int.Parse).ToList()
                : new List<int>();
        }
        catch (IOException)
        {
            return new List<int>();
        }
    }

    private async Task<List<int>> WaitForPids(int count)
    {
        for (var i = 0; i < 200 && Pids().Count < count; i++)
        {
            await Task.Delay(50);
        }
        return Pids();
    }

    private static bool Exited(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private static async Task<bool> AllExited(IEnumerable<int> pids)
    {
        for (var i = 0; i < 100; i++)
        {
            if (pids.All(Exited))
            {
                return true;
            }
            await Task.Delay(50);
        }
        return false;
    }

    // ---- Opus round 2 blocker: only "\n" ends a line of the list ----

    [Fact]
    public async Task UnicodeLineSeparatorsInANameSurviveTheListFile()
    {
        Environment.SetEnvironmentVariable("PEEL_FAKE_MODE", "echo");
        var files = new[] { "acta\u2028final.txt", "nota\u2029b.txt", "x\u0085y.txt", "plain.txt" }
            .Select(name => $@"C:\Users\danie\Documents\{name}")
            .ToList();
        var listed = await new EngineClient { CommandLineBudget = 10 }.OptionsAsync(files);
        Assert.True(listed.Ok, listed.Message);
        Assert.NotEqual(string.Empty, listed.Message); // it really went through a list file
        Assert.Equal(files, listed.Files);
    }

    // ---- lucy 1 and 2: suspended start, fail closed, shutdown token ----

    [Fact]
    public void TheCommandLineQuotesLikeArgumentList()
    {
        Assert.Equal("\"C:\\p q\\e.exe\" a \"b c\" \"\" d\\ \"e\\\\\\\"f\" \"x\\\"y\"",
            ContainedProcess.CommandLine(@"C:\p q\e.exe", new[] { "a", "b c", "", "d\\", "e\\\"f", "x\"y" }));
    }

    [Fact]
    public async Task HostileArgumentsRoundTripThroughTheSuspendedLaunch()
    {
        Environment.SetEnvironmentVariable("PEEL_FAKE_MODE", "echo");
        var files = new List<string>
        {
            @"C:\a b\c.txt", @"C:\trailing\", @"C:\x\""quoted"".txt", @"C:\two\\slashes\\", "a&calc&b",
            "%PATH%", "tab\there", "\u2028line", "ñandú 🍊", "-rf", "x\\\"y",
        };
        var result = await new EngineClient().OptionsAsync(files);
        Assert.True(result.Ok, result.Message);
        Assert.Equal(string.Empty, result.Message); // inline, no list file
        Assert.Equal(files, result.Files);
    }

    [Fact]
    public async Task TheEngineDoesNotRunUntilItIsInItsJob()
    {
        Environment.SetEnvironmentVariable("PEEL_FAKE_MODE", "hang");
        var ranWhileHeld = -1;
        var heldPid = 0;
        using var cancel = new CancellationTokenSource();
        var engine = new EngineClient
        {
            AfterCreate = pid =>
            {
                heldPid = pid;
                // The old code ran the engine here already, and anything it
                // started in this window escaped the job.
                Thread.Sleep(1500);
                ranWhileHeld = Pids().Count;
            },
        };
        var run = engine.RunAsync("x.y", new[] { @"C:\a.txt" }, null, cancel.Token);
        var pids = await WaitForPids(2);
        Assert.Equal(0, ranWhileHeld);
        Assert.Equal(2, pids.Count);
        Assert.Contains(heldPid, pids);
        cancel.Cancel();
        Assert.False((await run).Ok);
        Assert.True(await AllExited(pids), "an engine process outlived its job");
    }

    [Fact]
    public async Task NoJobMeansTheEngineNeverRuns()
    {
        Environment.SetEnvironmentVariable("PEEL_FAKE_MODE", "hang");
        var engine = new EngineClient { CreateJob = () => null, RunTimeout = TimeSpan.FromSeconds(5) };
        var result = await engine.RunAsync("x.y", new[] { @"C:\a.txt" });
        Assert.False(result.Ok);
        Assert.Contains("did not run the engine", result.Message);
        await Task.Delay(500);
        Assert.Empty(Pids());
    }

    [Fact]
    public async Task AFailedJobAssignmentEndsTheSuspendedEngine()
    {
        Environment.SetEnvironmentVariable("PEEL_FAKE_MODE", "hang");
        var created = 0;
        var engine = new EngineClient
        {
            // A job whose handle is already closed: no process can be born
            // in it, so none is created (hq-plct.17).
            CreateJob = () =>
            {
                var job = ProcessJob.Create();
                job?.Dispose();
                return job;
            },
            AfterCreate = pid => created = pid,
            RunTimeout = TimeSpan.FromSeconds(5),
        };
        var result = await engine.RunAsync("x.y", new[] { @"C:\a.txt" });
        Assert.False(result.Ok);
        Assert.Contains("job", result.Message);
        Assert.Equal(0, created);
        Assert.Empty(Pids());
    }

    [Fact]
    public async Task QuittingEndsAnOptionsCallToo()
    {
        Environment.SetEnvironmentVariable("PEEL_FAKE_MODE", "hang");
        var engine = new EngineClient { OptionsTimeout = TimeSpan.FromMinutes(5) };
        var call = engine.OptionsAsync(new[] { @"C:\a.txt" });
        var pids = await WaitForPids(2);
        Assert.Equal(2, pids.Count);

        engine.KillAll();

        Assert.Same(call, await Task.WhenAny(call, Task.Delay(TimeSpan.FromSeconds(10))));
        var result = await call;
        Assert.False(result.Ok);
        Assert.Contains("Peel quit", result.Message);
        Assert.True(await AllExited(pids), "an options engine outlived KillAll");
    }

    // ---- lucy 3: nothing of the call runs on the caller's thread ----

    [Fact]
    public async Task ListAndLaunchRunOffTheCallingThread()
    {
        Environment.SetEnvironmentVariable("PEEL_FAKE_MODE", "echo");
        using var gate = new ManualResetEventSlim(false);
        var caller = Environment.CurrentManagedThreadId;
        var preparedOn = 0;
        var engine = new EngineClient
        {
            CommandLineBudget = 10,
            BeforeLaunch = () =>
            {
                preparedOn = Environment.CurrentManagedThreadId;
                gate.Wait(TimeSpan.FromSeconds(10));
            },
        };
        var watch = Stopwatch.StartNew();
        var call = engine.OptionsAsync(new[] { @"C:\a.txt", @"C:\b.txt" });
        var returnedAfter = watch.Elapsed;
        Assert.False(call.IsCompleted);
        Assert.True(returnedAfter < TimeSpan.FromMilliseconds(500), $"the caller was held {returnedAfter}");
        gate.Set();
        var result = await call;
        Assert.True(result.Ok, result.Message);
        Assert.NotEqual(0, preparedOn);
        Assert.NotEqual(caller, preparedOn);
    }

    // ---- lucy 7: no list file is left behind ----

    [Fact]
    public void APartiallyWrittenListIsDeleted()
    {
        var folder = EngineClient.ListFolder;
        var before = Directory.Exists(folder) ? Directory.GetFiles(folder).ToHashSet() : new HashSet<string>();
        var location = new EngineLocation(@"C:\peel.exe", Array.Empty<string>(), "test");
        string? written = null;
        Assert.Throws<IOException>(() => EngineClient.Arrange(location, new[] { "options" },
            new[] { @"C:\a.txt", @"C:\b.txt" }, Array.Empty<string>(), 10, new List<string>(),
            (path, text) =>
            {
                written = path;
                File.WriteAllText(path, text[..3]);
                throw new IOException("There is not enough space on the disk.");
            }));
        Assert.NotNull(written);
        Assert.False(File.Exists(written), "the partial list file was left in %TEMP%");
        Assert.Empty(Directory.GetFiles(folder).Except(before));
    }

    [Fact]
    public void StaleListsAreSweptAndFreshOnesKept()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"peel-r3-sweep-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var stale = Path.Combine(folder, "files-old.txt");
            var fresh = Path.Combine(folder, "files-new.txt");
            var other = Path.Combine(folder, "notes.txt");
            foreach (var path in new[] { stale, fresh, other })
            {
                File.WriteAllText(path, "C:\\a.txt\n");
            }
            File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-2));
            File.SetLastWriteTimeUtc(other, DateTime.UtcNow.AddDays(-2));

            Assert.Equal(1, EngineClient.ScavengeListFiles(TimeSpan.FromDays(1), folder));
            Assert.False(File.Exists(stale));
            Assert.True(File.Exists(fresh));
            Assert.True(File.Exists(other));
            Assert.Equal(0, EngineClient.ScavengeListFiles(TimeSpan.FromDays(1), Path.Combine(folder, "missing")));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    // ---- lucy 5: each hook is judged on its own ----

    private static HookSample Sample(uint now, uint input, uint mouse, uint key, bool moved = false, bool installed = true) =>
        new(now, installed, input, mouse, key, moved);

    [Fact]
    public void ADeadMouseHookIsFoundWhileTheKeyboardHookStillRuns()
    {
        var liveness = new HookLiveness(lagMs: 3000, refreshMs: uint.MaxValue / 4);
        // Typing keeps the keyboard hook fresh; the cursor moves, the mouse hook is silent.
        Assert.Null(liveness.Check(Sample(20000, input: 19900, mouse: 10000, key: 19900, moved: true)));
        Assert.NotNull(liveness.Check(Sample(25000, input: 24900, mouse: 10000, key: 24900, moved: true)));
        // Typing alone, cursor still: not mouse evidence, no churn.
        var calm = new HookLiveness(lagMs: 3000, refreshMs: uint.MaxValue / 4);
        for (uint t = 20000; t < 60000; t += 5000)
        {
            Assert.Null(calm.Check(Sample(t, input: t - 100, mouse: 10000, key: t - 100)));
        }
    }

    [Fact]
    public void ADeadKeyboardHookIsReplacedAtTheNextIdlePause()
    {
        var liveness = new HookLiveness(lagMs: 3000, idleMs: 15000, refreshMs: 600000);
        // The mouse keeps moving, so the shared view looks healthy: no live reinstall.
        for (uint t = 5000; t < 700000; t += 5000)
        {
            Assert.Null(liveness.Check(Sample(t, input: t - 50, mouse: t - 50, key: 1000, moved: true)));
        }
        // The user pauses: both hooks are reinstalled once.
        Assert.NotNull(liveness.Check(Sample(720000, input: 700000, mouse: 700000, key: 1000)));
        // And not again for the refresh period.
        Assert.Null(liveness.Check(Sample(740000, input: 700000, mouse: 700000, key: 1000)));
        Assert.Null(liveness.Check(Sample(1300000, input: 1280000, mouse: 1280000, key: 1290000)));
        Assert.NotNull(liveness.Check(Sample(1330000, input: 1300000, mouse: 1300000, key: 1300000)));
    }

    [Fact]
    public void AFailedReinstallIsRetriedLater()
    {
        var liveness = new HookLiveness(retryMs: 30000, refreshMs: uint.MaxValue / 4);
        Assert.NotNull(liveness.Check(Sample(10000, input: 9000, mouse: 9000, key: 9000, installed: false)));
        Assert.Null(liveness.Check(Sample(20000, input: 19000, mouse: 9000, key: 9000, installed: false)));
        Assert.NotNull(liveness.Check(Sample(40000, input: 39000, mouse: 9000, key: 9000, installed: false)));
        Assert.Null(liveness.Check(Sample(45000, input: 44000, mouse: 9000, key: 9000, installed: false)));
        Assert.NotNull(liveness.Check(Sample(70000, input: 69000, mouse: 9000, key: 9000, installed: false)));
    }

    // ---- lucy 4: the clipboard write never waits ----

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr owner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [Fact]
    public void ABusyClipboardFailsAtOnceInsteadOfBlocking()
    {
        using var held = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var opened = false;
        var holder = new Thread(() =>
        {
            opened = OpenClipboard(IntPtr.Zero);
            held.Set();
            release.Wait(TimeSpan.FromSeconds(10));
            if (opened)
            {
                CloseClipboard();
            }
        });
        holder.Start();
        held.Wait(TimeSpan.FromSeconds(5));
        try
        {
            if (!opened)
            {
                return; // no clipboard in this session (a service logon): nothing to measure
            }
            Exception? failure = null;
            var elapsed = TimeSpan.Zero;
            var writer = new Thread(() =>
            {
                var watch = Stopwatch.StartNew();
                try
                {
                    ClipboardWriter.SetOnce("peel round 3");
                }
                catch (Exception error)
                {
                    failure = error;
                }
                elapsed = watch.Elapsed;
            });
            writer.SetApartmentState(ApartmentState.STA);
            writer.Start();
            writer.Join(TimeSpan.FromSeconds(10));
            Assert.NotNull(failure);
            Assert.True(elapsed < TimeSpan.FromMilliseconds(300), $"one clipboard try held the thread {elapsed}");
        }
        finally
        {
            release.Set();
            holder.Join(TimeSpan.FromSeconds(10));
        }
    }
}
