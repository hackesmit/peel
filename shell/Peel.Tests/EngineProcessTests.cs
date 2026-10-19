// The engine client against a real process: the fake engine in
// tests\FakeEngine, built with this project. Review round 1 for hq-plct.7:
// long file lists (Opus blocker), process lifetime (lucy 2) and the timeout
// covering the pipes as well as the exit (lucy 3).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Peel.Engine;
using Xunit;

namespace Peel.Tests;

[Collection("environment")]
public sealed class EngineProcessTests : IDisposable
{
    private readonly string? _previousCli = Environment.GetEnvironmentVariable("PEEL_CLI");
    private readonly string _pids = Path.Combine(Path.GetTempPath(), $"peel-fake-pids-{Guid.NewGuid():N}.txt");

    public EngineProcessTests()
    {
        Environment.SetEnvironmentVariable("PEEL_CLI", FakeEngine);
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

    /// <summary>tests\FakeEngine\bin\&lt;config&gt;\net8.0\FakeEngine.exe, next to this build.</summary>
    internal static string FakeEngine
    {
        get
        {
            var here = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
            // shell\Peel.Tests\bin\<config>\net8.0-windows
            var config = here.Parent!.Name;
            var shell = here.Parent!.Parent!.Parent!.Parent!.FullName;
            var path = Path.Combine(shell, "tests", "FakeEngine", "bin", config, "net8.0", "FakeEngine.exe");
            Assert.True(File.Exists(path), $"the fake engine is not built: {path}");
            return path;
        }
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
        for (var i = 0; i < 200; i++)
        {
            var pids = Pids();
            if (pids.Count >= count)
            {
                return pids;
            }
            await Task.Delay(50);
        }
        return Pids();
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

    private static List<string> Photos(int count) => Enumerable.Range(0, count)
        .Select(i => $@"C:\Users\danie\Pictures\Vacaciones 2026 Ensenada\IMG_{i:00000}.JPG")
        .ToList();

    [Fact]
    public async Task SevenHundredPathsReachTheEngineThroughAListFileThatIsThenDeleted()
    {
        Environment.SetEnvironmentVariable("PEEL_FAKE_MODE", "echo");
        var files = Photos(700);
        Assert.True(files.Sum(f => f.Length + 3) > 32767);

        var options = await new EngineClient().OptionsAsync(files);
        Assert.True(options.Ok, options.Message);
        Assert.Equal(files, options.Files);
        var listFile = options.Message;
        Assert.EndsWith(".txt", listFile);
        Assert.False(File.Exists(listFile), $"the list file was left behind: {listFile}");

        var run = await new EngineClient().RunAsync("docs.doc_to_md", files, new Dictionary<string, string> { ["q"] = "1" });
        Assert.True(run.Ok, run.Message);
        Assert.False(File.Exists(run.Message));
    }

    [Fact]
    public async Task AFewPathsStayOnTheCommandLine()
    {
        Environment.SetEnvironmentVariable("PEEL_FAKE_MODE", "echo");
        var files = Photos(3).Append(@"C:\tmp\a&calc&b ñ 🍊.txt").ToList();
        var options = await new EngineClient().OptionsAsync(files);
        Assert.True(options.Ok, options.Message);
        Assert.Equal(files, options.Files);
        Assert.Equal(string.Empty, options.Message);
    }

    [Fact]
    public void TheListFileHoldsOnePathPerLineInUtf8()
    {
        var location = new EngineLocation(@"C:\x\peel.exe", Array.Empty<string>(), "test");
        var arguments = new List<string>();
        var files = Photos(2).Append(@"C:\tmp\ñandú 日本語.txt").ToList();
        var list = EngineClient.Arrange(location, new[] { "run", "a.b" }, files, new[] { "--param", "k=v" }, 10, arguments);
        try
        {
            Assert.NotNull(list);
            Assert.Equal(new[] { "run", "a.b", "--files-from", list!, "--param", "k=v" }, arguments);
            var bytes = File.ReadAllBytes(list!);
            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF, "no byte order mark");
            Assert.Equal(files, System.Text.Encoding.UTF8.GetString(bytes).Split('\n').Where(l => l.Length > 0));
        }
        finally
        {
            if (list is not null) File.Delete(list);
        }
    }

    [Fact]
    public async Task TheTimeoutCoversPipesABackendKeepsOpenAfterTheEngineExits()
    {
        Environment.SetEnvironmentVariable("PEEL_FAKE_MODE", "orphan");
        var engine = new EngineClient { RunTimeout = TimeSpan.FromSeconds(4) };
        var watch = Stopwatch.StartNew();

        var result = await engine.RunAsync("x.y", new[] { @"C:\a.txt" });

        Assert.False(result.Ok);
        Assert.Contains("took longer", result.Message);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), $"took {watch.Elapsed}");
        var pids = Pids();
        Assert.Equal(2, pids.Count);
        Assert.True(await AllExited(pids), "the backend that held the pipes is still running");
    }

    [Fact]
    public async Task KillAllEndsARunningEngineAndEverythingItStarted()
    {
        Environment.SetEnvironmentVariable("PEEL_FAKE_MODE", "hang");
        var engine = new EngineClient();
        var run = engine.RunAsync("x.y", new[] { @"C:\a.txt" });
        var pids = await WaitForPids(2);
        Assert.Equal(2, pids.Count);
        Assert.False(run.IsCompleted);

        engine.KillAll();

        var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(run, finished);
        Assert.False((await run).Ok);
        Assert.True(await AllExited(pids), "an engine process outlived KillAll");
        var after = await engine.RunAsync("x.y", new[] { @"C:\a.txt" });
        Assert.False(after.Ok);
        Assert.Contains("quitting", after.Message);
    }

    [Fact]
    public async Task CancellingARunKillsTheWholeTree()
    {
        Environment.SetEnvironmentVariable("PEEL_FAKE_MODE", "hang");
        using var cancel = new System.Threading.CancellationTokenSource();
        var run = new EngineClient().RunAsync("x.y", new[] { @"C:\a.txt" }, null, cancel.Token);
        var pids = await WaitForPids(2);
        cancel.Cancel();
        var result = await run;
        Assert.False(result.Ok);
        Assert.Contains("cancelled", result.Message);
        Assert.True(await AllExited(pids), "an engine process outlived its cancellation");
    }

    [Fact]
    public void ACmdOverrideIsNeverUsed()
    {
        var cmd = Path.Combine(Path.GetTempPath(), $"peel-{Guid.NewGuid():N}.cmd");
        File.WriteAllText(cmd, "@echo {}");
        try
        {
            Environment.SetEnvironmentVariable("PEEL_CLI", cmd);
            var location = new EngineClient().Location;
            Assert.True(location is null || location.Description != "PEEL_CLI");
        }
        finally
        {
            File.Delete(cmd);
        }
    }
}
