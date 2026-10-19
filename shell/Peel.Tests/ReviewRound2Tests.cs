// Review round 2 for hq-plct.7 (fresh context reviewer). Attacks on the
// round 1 fix for the Opus blocker (many files through --files-from).
//
// The last test drives the BRANCH engine, not the installed one (the
// installed venv is from main and has no --files-from). Point
// PEEL_REVIEW2_ENGINE at a peel.exe built from this branch's engine, for
// example: python -m venv C:\peel-dev\review2-4500\venv, then
// venv\Scripts\pip install --no-build-isolation C:\peel-dev\review2-4500\engine.
// Without the variable that test returns at once.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Peel.Engine;
using Xunit;

namespace Peel.Tests;

[Collection("environment")]
public sealed class ReviewRound2Tests : IDisposable
{
    private readonly string? _previousCli = Environment.GetEnvironmentVariable("PEEL_CLI");
    private readonly string _pids = Path.Combine(Path.GetTempPath(), $"peel-r2-pids-{Guid.NewGuid():N}.txt");
    private static readonly string ListFolder = Path.Combine(Path.GetTempPath(), "Peel");

    public ReviewRound2Tests()
    {
        Environment.SetEnvironmentVariable("PEEL_CLI", EngineProcessTests.FakeEngine);
        Environment.SetEnvironmentVariable("PEEL_FAKE_PIDS", _pids);
    }

    public void Dispose()
    {
        try
        {
            foreach (var line in File.Exists(_pids) ? File.ReadAllLines(_pids) : Array.Empty<string>())
            {
                if (int.TryParse(line.Trim(), out var pid))
                {
                    try { Process.GetProcessById(pid).Kill(); } catch (Exception) { }
                }
            }
            File.Delete(_pids);
        }
        catch (IOException) { }
        Environment.SetEnvironmentVariable("PEEL_CLI", _previousCli);
        Environment.SetEnvironmentVariable("PEEL_FAKE_MODE", null);
        Environment.SetEnvironmentVariable("PEEL_FAKE_PIDS", null);
    }

    private static HashSet<string> Lists() => Directory.Exists(ListFolder)
        ? Directory.GetFiles(ListFolder, "files-*.txt").ToHashSet(StringComparer.OrdinalIgnoreCase)
        : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private static List<string> Odd(int count) => Enumerable.Range(0, count)
        .Select(i => $@"C:\Users\danie\Pictures\ñandú café 日本語 🍊\IMG_{i:00000} a&b %PATH%.JPG")
        .ToList();

    [Fact]
    public async Task ManyUnicodeFilesReachTheEngineThroughTheList()
    {
        Environment.SetEnvironmentVariable("PEEL_FAKE_MODE", "echo");
        var files = Odd(700);
        var before = Lists();
        var options = await new EngineClient().OptionsAsync(files);
        Assert.True(options.Ok, options.Message);
        Assert.Equal(files, options.Files);
        Assert.NotEqual(string.Empty, options.Message);
        Assert.Empty(Lists().Except(before));
    }

    [Fact]
    public async Task TheListFileIsDeletedWhenTheEngineTimesOut()
    {
        Environment.SetEnvironmentVariable("PEEL_FAKE_MODE", "hang");
        var before = Lists();
        var engine = new EngineClient { OptionsTimeout = TimeSpan.FromSeconds(2), CommandLineBudget = 10 };
        var result = await engine.OptionsAsync(Odd(5));
        Assert.False(result.Ok);
        Assert.Contains("took longer", result.Message);
        Assert.Empty(Lists().Except(before));
    }

    [Fact]
    public async Task TheListFileIsDeletedWhenTheEngineFails()
    {
        Environment.SetEnvironmentVariable("PEEL_FAKE_MODE", null);
        var before = Lists();
        var engine = new EngineClient { CommandLineBudget = 10 };
        var result = await engine.RunAsync("x.y", Odd(5));
        Assert.False(result.Ok);
        Assert.Empty(Lists().Except(before));
    }

    [Fact]
    public async Task TheListFileIsDeletedWhenTheSessionCancelsTheCall()
    {
        Environment.SetEnvironmentVariable("PEEL_FAKE_MODE", "hang");
        var before = Lists();
        using var cancel = new CancellationTokenSource();
        var engine = new EngineClient { CommandLineBudget = 10 };
        var call = engine.OptionsAsync(Odd(5), cancel.Token);
        await Task.Delay(1500);
        cancel.Cancel();
        var result = await call;
        Assert.False(result.Ok);
        Assert.Empty(Lists().Except(before));
    }

    /// <summary>
    /// The real (branch) engine reading the list the shell writes. NTFS allows
    /// U+2028, U+2029 and U+0085 in a name; the engine splits the list with
    /// str.splitlines(), which breaks on all three.
    /// </summary>
    [Fact]
    public async Task TheBranchEngineSeesAPathWithALineSeparatorInALongDrag()
    {
        var engineExe = Environment.GetEnvironmentVariable("PEEL_REVIEW2_ENGINE");
        if (string.IsNullOrEmpty(engineExe))
        {
            return;
        }
        Environment.SetEnvironmentVariable("PEEL_CLI", engineExe);
        Environment.SetEnvironmentVariable("PEEL_FAKE_MODE", null);
        var folder = Path.Combine(Path.GetTempPath(), $"peel-r2 ñandú 🍊 {Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var files = new List<string>();
            foreach (var name in new[] { "acta\u2028final.txt", "nota\u2029b.txt", "x\u0085y.txt", "plain café.txt" })
            {
                var path = Path.Combine(folder, name);
                File.WriteAllText(path, "x");
                files.Add(path);
            }

            // Control: inline, under the budget, every path survives.
            var inline = await new EngineClient().OptionsAsync(files);
            Assert.True(inline.Ok, inline.Message);
            Assert.Equal(files, inline.Files);

            // Same files through the list file, as a long drag sends them.
            var listed = await new EngineClient { CommandLineBudget = 10 }.OptionsAsync(files);
            Assert.True(listed.Ok, listed.Message);
            Assert.Equal(files, listed.Files);
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }
    }
}
