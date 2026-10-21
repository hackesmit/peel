// hq-plct.17, from lucy's final pass on hq-plct.7:
//   1. Between CreateProcessW(CREATE_SUSPENDED) and AssignProcessToJobObject a
//      Peel crash left a suspended engine outside any kill on close job. The
//      engine is now created inside its job (PROC_THREAD_ATTRIBUTE_JOB_LIST),
//      so killing the launcher in that window ends it. The launcher is a real
//      separate process (tests\JobLauncher), killed the way a crash would be.
//   2. A call cancelled before the engine is created, or while it is still
//      suspended, never resumes it.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Peel.Engine;
using Peel.Jobs;
using Xunit;

namespace Peel.Tests;

[Collection("environment")]
public sealed class ContainedProcessJobListTests : IDisposable
{
    private readonly string? _previousCli = Environment.GetEnvironmentVariable("PEEL_CLI");
    private readonly string _pids = Path.Combine(Path.GetTempPath(), $"peel-joblist-pids-{Guid.NewGuid():N}.txt");
    private readonly List<int> _created = new();

    public ContainedProcessJobListTests()
    {
        Environment.SetEnvironmentVariable("PEEL_CLI", EngineProcessTests.FakeEngine);
        Environment.SetEnvironmentVariable("PEEL_FAKE_PIDS", _pids);
        Environment.SetEnvironmentVariable("PEEL_FAKE_MODE", "hang");
    }

    public void Dispose()
    {
        // Whatever a failing run left behind, suspended or running.
        foreach (var pid in Pids().Concat(_created))
        {
            try { Process.GetProcessById(pid).Kill(); } catch (Exception) { }
        }
        Environment.SetEnvironmentVariable("PEEL_CLI", _previousCli);
        Environment.SetEnvironmentVariable("PEEL_FAKE_MODE", null);
        Environment.SetEnvironmentVariable("PEEL_FAKE_PIDS", null);
        try { File.Delete(_pids); } catch (IOException) { }
    }

    /// <summary>tests\JobLauncher\bin\&lt;config&gt;\&lt;tfm&gt;\Peel.JobLauncher.exe, next to this build.</summary>
    private static string JobLauncher
    {
        get
        {
            var here = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
            // shell\Peel.Tests\bin\<config>\<tfm>
            var config = here.Parent!.Name;
            var shell = here.Parent!.Parent!.Parent!.Parent!.FullName;
            var path = Path.Combine(shell, "tests", "JobLauncher", "bin", config, here.Name, "Peel.JobLauncher.exe");
            Assert.True(File.Exists(path), $"no job launcher at {path}");
            return path;
        }
    }

    /// <summary>Pids the fake engine wrote: each one is an engine that ran.</summary>
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

    private static async Task<bool> ExitsWithin(int pid, TimeSpan limit)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < limit)
        {
            if (Exited(pid))
            {
                return true;
            }
            await Task.Delay(50);
        }
        return Exited(pid);
    }

    [Fact]
    public async Task KillingTheLauncherBeforeResumeEndsTheSuspendedEngine()
    {
        using var launcher = Process.Start(new ProcessStartInfo(JobLauncher, $"\"{EngineProcessTests.FakeEngine}\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        })!;
        try
        {
            var line = await launcher.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
            if (!int.TryParse(line, out var engine))
            {
                // Only now: the launcher holds its stderr open while it waits.
                launcher.Kill();
                Assert.Fail($"the launcher printed '{line}': {await launcher.StandardError.ReadToEndAsync()}");
            }
            _created.Add(engine);
            Assert.False(Exited(engine), "the engine should be alive and suspended");

            // Only the launcher, as a crash would: its handles close, nothing else.
            launcher.Kill(entireProcessTree: false);
            await launcher.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));

            Assert.True(await ExitsWithin(engine, TimeSpan.FromSeconds(5)),
                "a suspended engine outlived its launcher: it was not in the kill on close job");
            Assert.Empty(Pids()); // and it never ran a line
        }
        finally
        {
            try { launcher.Kill(entireProcessTree: true); } catch (Exception) { }
        }
    }

    [Fact]
    public async Task TheEngineIsInItsJobFromCreation()
    {
        ProcessJob? job = null;
        bool? inJob = null;
        using var cancel = new CancellationTokenSource();
        var engine = new EngineClient
        {
            CreateJob = () => job = ProcessJob.Create(),
            AfterCreate = pid =>
            {
                _created.Add(pid);
                using var process = Process.GetProcessById(pid);
                inJob = job!.Contains(process.Handle);
                cancel.Cancel();
            },
        };
        var result = await engine.RunAsync("x.y", new[] { @"C:\a.txt" }, null, cancel.Token);
        Assert.False(result.Ok);
        Assert.True(inJob, "the suspended engine was not in its job before afterCreate ran");
    }

    [Fact]
    public async Task APreCancelledCallNeverCreatesTheEngine()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var engine = new EngineClient { AfterCreate = pid => _created.Add(pid) };

        var run = await engine.RunAsync("x.y", new[] { @"C:\a.txt" }, null, cancel.Token);
        var options = await engine.OptionsAsync(new[] { @"C:\a.txt" }, cancel.Token);

        Assert.False(run.Ok);
        Assert.Contains("cancelled", run.Message);
        Assert.False(options.Ok);
        Assert.Contains("cancelled", options.Message);
        Assert.Empty(_created);
        await Task.Delay(500);
        Assert.Empty(Pids());
    }

    [Fact]
    public async Task ACallCancelledWhileSuspendedNeverResumes()
    {
        using var cancel = new CancellationTokenSource();
        var engine = new EngineClient
        {
            AfterCreate = pid =>
            {
                _created.Add(pid);
                cancel.Cancel();
            },
        };

        var result = await engine.RunAsync("x.y", new[] { @"C:\a.txt" }, null, cancel.Token);

        Assert.False(result.Ok);
        Assert.Contains("cancelled", result.Message);
        Assert.Single(_created);
        Assert.True(await ExitsWithin(_created[0], TimeSpan.FromSeconds(5)), "the cancelled engine is still alive");
        await Task.Delay(500);
        Assert.Empty(Pids());
    }

    [Fact]
    public async Task QuittingWhileSuspendedNeverResumes()
    {
        EngineClient? engine = null;
        engine = new EngineClient
        {
            AfterCreate = pid =>
            {
                _created.Add(pid);
                engine!.KillAll();
            },
        };

        var result = await engine.RunAsync("x.y", new[] { @"C:\a.txt" });

        Assert.False(result.Ok);
        Assert.Contains("quitting", result.Message);
        Assert.Single(_created);
        Assert.True(await ExitsWithin(_created[0], TimeSpan.FromSeconds(5)), "the engine outlived KillAll");
        await Task.Delay(500);
        Assert.Empty(Pids());
    }
}
